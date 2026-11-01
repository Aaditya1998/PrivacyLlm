using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Pipeline;
using PrivacyLlm.Core.Policy;
using PrivacyLlm.Core.Redaction;

namespace PrivacyLlm.Tests;

public class RoutingAndGatewayTests
{
    private sealed class FakeClient(InferenceTier tier, string region = "IN", bool down = false, Func<string, string>? reply = null) : ILlmClient
    {
        public List<LlmRequest> Received { get; } = new();
        public InferenceTier Tier => tier;
        public string Name => $"fake-{tier}";
        public string HostRegion => region;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Received.Add(request);
            if (down) throw new LlmUnavailableException($"{tier} down");
            var user = request.Messages.Last().Content;
            return Task.FromResult(new LlmResponse(reply?.Invoke(user) ?? "ok", Name, 1, 1, TimeSpan.Zero));
        }

        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(!down);
    }

    private readonly DataClassifier _classifier = new(new RegexPiiDetector());
    private readonly InMemoryConsentRegistry _consents = new(TimeProvider.System);
    private readonly InMemoryAuditLog _audit = new();

    private PrivacyGateway Gateway(RoutingOptions options, params ILlmClient[] clients) =>
        new(_classifier, new PrivacyRoutingPolicy(options), _consents, clients, _audit, options, new GatewaySecrets(new byte[32]), TimeProvider.System);

    private static LlmTask NewTask(string input, string? principal = "cust-1", string type = "support-reply") =>
        new(Guid.NewGuid().ToString(), type, "order-support", "You are a support assistant.", input, principal);

    [Fact]
    public void Personal_data_routes_self_hosted_with_no_fallback()
    {
        var d = new PrivacyRoutingPolicy(new RoutingOptions()).Decide("x", _classifier.Classify("call 9876543210"));
        Assert.Equal(InferenceTier.SelfHosted, d.Tier);
        Assert.Null(d.Fallback);
    }

    [Fact]
    public void High_risk_data_ignores_pseudonymisation_opt_in()
    {
        var opts = new RoutingOptions { AllowPseudonymizedFrontier = true, PseudonymizedFrontierTaskTypes = { "x" } };
        var d = new PrivacyRoutingPolicy(opts).Decide("x", _classifier.Classify("PAN ABCPK1234F"));
        Assert.Equal(InferenceTier.SelfHosted, d.Tier);
    }

    [Fact]
    public void Declared_fields_force_personal_classification()
    {
        var c = _classifier.Classify("lorem ipsum", new[] { "customer_name" });
        Assert.Equal(DataSensitivity.Personal, c.Sensitivity);
    }

    [Fact]
    public async Task Frontier_never_sees_personal_data_even_when_self_hosted_is_down()
    {
        var self = new FakeClient(InferenceTier.SelfHosted, down: true);
        var frontier = new FakeClient(InferenceTier.Frontier, "US");
        _consents.Grant("cust-1", "order-support", LawfulBasis.Consent);

        var result = await Gateway(new RoutingOptions(), self, frontier).ExecuteAsync(NewTask("Customer Ravi Kumar, phone 9876543210, wants a refund"));

        Assert.Equal(TaskOutcome.Unavailable, result.Outcome);
        Assert.Empty(frontier.Received);
    }

    [Fact]
    public async Task Public_data_goes_to_frontier_and_falls_back_to_self_hosted()
    {
        var self = new FakeClient(InferenceTier.SelfHosted);
        var frontier = new FakeClient(InferenceTier.Frontier, "US", down: true);

        var result = await Gateway(new RoutingOptions(), self, frontier).ExecuteAsync(NewTask("Write a tagline for a steel bottle", principal: null));

        Assert.Equal(TaskOutcome.Completed, result.Outcome);
        Assert.Equal(InferenceTier.SelfHosted, result.Tier);
        Assert.Single(frontier.Received);
    }

    [Fact]
    public async Task Missing_or_withdrawn_consent_blocks_processing()
    {
        var self = new FakeClient(InferenceTier.SelfHosted);
        var gw = Gateway(new RoutingOptions(), self);

        Assert.Equal(TaskOutcome.RejectedNoConsent, (await gw.ExecuteAsync(NewTask("phone 9876543210"))).Outcome);

        _consents.Grant("cust-1", "order-support", LawfulBasis.Consent);
        Assert.Equal(TaskOutcome.Completed, (await gw.ExecuteAsync(NewTask("phone 9876543210"))).Outcome);

        _consents.Withdraw("cust-1", "order-support");
        Assert.Equal(TaskOutcome.RejectedNoConsent, (await gw.ExecuteAsync(NewTask("phone 9876543210"))).Outcome);
        Assert.Single(self.Received);
    }

    [Fact]
    public async Task Self_hosted_outside_allowed_region_is_rejected_for_personal_data()
    {
        var self = new FakeClient(InferenceTier.SelfHosted, region: "US");
        _consents.Grant("cust-1", "order-support", LawfulBasis.Consent);

        var result = await Gateway(new RoutingOptions(), self).ExecuteAsync(NewTask("phone 9876543210"));

        Assert.Equal(TaskOutcome.RejectedRegion, result.Outcome);
        Assert.Empty(self.Received);
    }

    [Fact]
    public async Task Approved_task_is_pseudonymised_for_frontier_and_rehydrated()
    {
        var frontier = new FakeClient(InferenceTier.Frontier, "US", reply: input => $"Dear {input[(input.IndexOf("[[NAME_1]]"))..(input.IndexOf("[[NAME_1]]") + 10)]}, we will call you.");
        var opts = new RoutingOptions { AllowPseudonymizedFrontier = true, PseudonymizedFrontierTaskTypes = { "support-reply" } };
        _consents.Grant("cust-1", "order-support", LawfulBasis.Consent);

        var result = await Gateway(opts, new FakeClient(InferenceTier.SelfHosted), frontier)
            .ExecuteAsync(NewTask("Customer: Ravi Kumar, phone 9876543210, parcel late"));

        var sent = frontier.Received.Single().Messages.Last().Content;
        Assert.DoesNotContain("Ravi", sent);
        Assert.DoesNotContain("9876543210", sent);
        Assert.Contains("[[PHONE_1]]", sent);
        Assert.True(result.Pseudonymized);
        Assert.Equal("Dear Ravi Kumar, we will call you.", result.Output);
    }

    [Fact]
    public async Task Qwen_think_blocks_are_stripped_and_audit_holds_no_raw_text()
    {
        var self = new FakeClient(InferenceTier.SelfHosted, reply: _ => "<think>reasoning about 9876543210</think>\nDone.");
        _consents.Grant("cust-1", "order-support", LawfulBasis.Consent);
        var input = "phone 9876543210";

        var result = await Gateway(new RoutingOptions(), self).ExecuteAsync(NewTask(input));

        Assert.Equal("Done.", result.Output);
        var e = Assert.Single(_audit.Query());
        Assert.Equal(1, e.PiiCounts[PiiType.Phone]);
        Assert.DoesNotContain("9876543210", System.Text.Json.JsonSerializer.Serialize(e));
    }

    [Fact]
    public void Pseudonymizer_round_trips_and_reuses_tokens()
    {
        const string text = "Call 9876543210. Again: 9876543210. Email a@b.com";
        var vault = new PseudonymVault();
        var masked = Pseudonymizer.Apply(text, new RegexPiiDetector().Detect(text), vault);

        Assert.Equal("Call [[PHONE_1]]. Again: [[PHONE_1]]. Email [[EMAIL_1]]", masked);
        Assert.Equal(text, vault.Rehydrate(masked));
    }
}
