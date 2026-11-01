using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Classification.Names;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Pipeline;
using PrivacyLlm.Core.Policy;

namespace PrivacyLlm.Tests;

public class NameDetectorTests
{
    private static ContextualNameDetector Names => NameEvalTests.Detectors.Value.Names;
    private static DataClassifier Classifier => NameEvalTests.Detectors.Value.EndToEnd;

    [Theory]
    [InlineData("Ravi said the parcel was late", "Ravi")]
    [InlineData("Hi Priya, your order has shipped.", "Priya")]
    [InlineData("I spoke to suresh yesterday about the refund", "suresh")]
    [InlineData("Thanks for your help.\nRegards,\nKavita Rao", "Kavita Rao")]
    [InlineData("Anil bhai ka order kal aayega", "Anil")]
    [InlineData("ANIL BHAI KA ORDER KAL AAYEGA", "ANIL")]
    [InlineData("Sharma ji called twice about the invoice", "Sharma")]
    [InlineData("Fatima ne bola ki payment ho gaya", "Fatima")]
    [InlineData("my friend Gurpreet wants to return the jacket", "Gurpreet")]
    public void Unlabelled_names_are_kept_off_the_frontier(string text, string expected)
    {
        var found = Names.Detect(text);
        Assert.Contains(found, m => m.Value.Contains(expected, StringComparison.Ordinal));
        Assert.True(Classifier.Classify(text).MayContainPersonalData);
    }

    [Theory]
    [InlineData("Ravi said the parcel was late")]
    [InlineData("Anil bhai ka order kal aayega")]
    [InlineData("Thanks for your help.\nRegards,\nKavita Rao")]
    public void Strong_context_makes_a_name_definite(string text) =>
        Assert.Contains(Names.Detect(text), m => !m.Tentative);

    [Theory]
    [InlineData("Lakshmi Nagar metro station is closed for repairs")]
    [InlineData("Ganesh Chaturthi sale: flat 20% off on all idols")]
    [InlineData("Tata Motors and Maruti Suzuki both raised prices")]
    [InlineData("Water levels in the Krishna river are rising")]
    [InlineData("mujhe asha hai ki delivery time pe hogi")]
    [InlineData("Hi team, the warehouse closes early on Friday.")]
    [InlineData("Regards,\nSupport Team")]
    [InlineData("Lord Shiva brass idol, 6 inch, gold finish")]
    [InlineData("Write a product description for a stainless steel water bottle.")]
    public void Places_brands_festivals_and_ordinary_words_are_not_names(string text) =>
        Assert.Empty(Names.Detect(text));

    [Fact]
    public void Ambiguous_word_needs_context()
    {
        Assert.Empty(Names.Detect("There is still some asha left for a timely delivery."));
        Assert.NotEmpty(Names.Detect("Asha said she will collect the parcel."));
    }

    [Fact]
    public void Spelling_variants_fold_together()
    {
        Assert.Equal(Transliteration.Fold("preethi"), Transliteration.Fold("preeti"));
        Assert.Equal(Transliteration.Fold("shiva"), Transliteration.Fold("siva"));
        Assert.Equal(Transliteration.Fold("vikkram"), Transliteration.Fold("vikram"));
        Assert.Equal(Transliteration.Fold("preeti"), Transliteration.Fold(Transliteration.Fold("preeti")));
    }

    [Fact]
    public void A_clear_name_propagates_to_later_bare_mentions()
    {
        var found = Names.Detect("Ravi called this morning. Later ravi emailed the invoice.");
        Assert.Equal(2, found.Count);
        Assert.All(found, m => Assert.False(m.Tentative));
    }

    [Fact]
    public void Urls_emails_code_and_placeholders_are_ignored()
    {
        Assert.Empty(Names.Detect("See https://example.com/priya/orders and call getRaviOrders() in user_ravi.cs"));
        Assert.Empty(Names.Detect("Hi [[NAME_1]], your order [[PHONE_1]] has shipped."));
    }

    [Fact]
    public void Thresholds_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NameScoringOptions { LowThreshold = 0.05 }.Validate());
        Assert.Throws<ArgumentException>(() => new NameScoringOptions { LowThreshold = 0.5, HighThreshold = 0.5 }.Validate());
    }

    private sealed class ThrowingNames : IPiiDetector
    {
        public IReadOnlyList<PiiMatch> Detect(string text) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Possible_name_only_routes_self_hosted_without_fallback_or_consent()
    {
        // A bare name with no context: possible, not definite ("ask ramesh" or "for ramesh" would be definite).
        var c = Classifier.Classify("ramesh file pending");
        Assert.Equal(DataSensitivity.Public, c.Sensitivity);
        Assert.True(c.SuspectedPersonal);

        var d = new PrivacyRoutingPolicy(new RoutingOptions { AlwaysSelfHostedTaskTypes = { "x" } }).Decide("x", c);
        Assert.Equal(InferenceTier.SelfHosted, d.Tier);
        Assert.Null(d.Fallback);
    }

    private sealed class FakeClient(InferenceTier tier, bool down = false) : ILlmClient
    {
        public List<LlmRequest> Received { get; } = new();
        public InferenceTier Tier => tier;
        public string Name => $"fake-{tier}";
        public string HostRegion => tier == InferenceTier.SelfHosted ? "IN" : "US";

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Received.Add(request);
            if (down) throw new LlmUnavailableException("down");
            return Task.FromResult(new LlmResponse("ok", Name, 1, 1, TimeSpan.Zero));
        }

        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(!down);
    }

    private static PrivacyGateway Gateway(RoutingOptions o, DataClassifier c, params ILlmClient[] clients) =>
        new(c, new PrivacyRoutingPolicy(o), new InMemoryConsentRegistry(TimeProvider.System), clients, new InMemoryAuditLog(), o,
            new GatewaySecrets(new byte[32]), TimeProvider.System);

    [Fact]
    public async Task Possible_name_completes_on_self_hosted_without_a_principal()
    {
        var self = new FakeClient(InferenceTier.SelfHosted);
        var frontier = new FakeClient(InferenceTier.Frontier);
        var r = await Gateway(new RoutingOptions(), Classifier, self, frontier)
            .ExecuteAsync(new LlmTask("t", "x", "p", "Summarise.", "please ask ramesh to check"));

        Assert.Equal(TaskOutcome.Completed, r.Outcome);
        Assert.Equal(InferenceTier.SelfHosted, r.Tier);
        Assert.Empty(frontier.Received);
    }

    [Fact]
    public async Task Possible_name_with_self_hosted_down_never_reaches_frontier()
    {
        var frontier = new FakeClient(InferenceTier.Frontier);
        var r = await Gateway(new RoutingOptions(), Classifier, new FakeClient(InferenceTier.SelfHosted, down: true), frontier)
            .ExecuteAsync(new LlmTask("t", "x", "p", "Summarise.", "please ask ramesh to check"));

        Assert.Equal(TaskOutcome.Unavailable, r.Outcome);
        Assert.Empty(frontier.Received);
    }

    [Fact]
    public async Task Name_cued_in_the_prompt_is_masked_in_the_input_too()
    {
        var opts = new RoutingOptions { AllowPseudonymizedFrontier = true, PseudonymizedFrontierTaskTypes = { "reply" } };
        var consents = new InMemoryConsentRegistry(TimeProvider.System);
        consents.Grant("cust-1", "p", LawfulBasis.Consent);
        var frontier = new FakeClient(InferenceTier.Frontier);
        var gw = new PrivacyGateway(Classifier, new PrivacyRoutingPolicy(opts), consents, new ILlmClient[] { new FakeClient(InferenceTier.SelfHosted), frontier },
            new InMemoryAuditLog(), opts, new GatewaySecrets(new byte[32]), TimeProvider.System);

        // On its own the input has nothing detectable (lowercase "asha" in a capitalised sentence reads as the
        // word "hope"); only the combined classification, sliced per part, knows it is the customer from the prompt.
        const string input = "Please check the asha parcel again.";
        Assert.Empty(Classifier.Classify(input).Matches);

        await gw.ExecuteAsync(new LlmTask("t", "reply", "p", "Customer: Asha Rao. Draft a reply.", input, "cust-1"));

        var sent = string.Join("\n", frontier.Received.Single().Messages.Select(m => m.Content));
        Assert.DoesNotContain("asha", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rao", sent);
    }

    private sealed class LeakyDetector : IPiiDetector
    {
        // Finds the name in the original text but not in the masked text's leftover "Kumar".
        public IReadOnlyList<PiiMatch> Detect(string text)
        {
            var i = text.IndexOf("Ravi", StringComparison.Ordinal);
            var k = text.IndexOf("Kumar", StringComparison.Ordinal);
            var list = new List<PiiMatch>();
            if (i >= 0) list.Add(new PiiMatch(PiiType.PersonName, i, 4, "Ravi", 0.9, Source: "Name"));
            else if (k >= 0) list.Add(new PiiMatch(PiiType.PersonName, k, 5, "Kumar", 0.5, Tentative: true, Source: "Name"));
            return list;
        }
    }

    [Fact]
    public async Task Residual_personal_data_after_masking_refuses_the_frontier()
    {
        var opts = new RoutingOptions { AllowPseudonymizedFrontier = true, PseudonymizedFrontierTaskTypes = { "reply" } };
        var consents = new InMemoryConsentRegistry(TimeProvider.System);
        consents.Grant("cust-1", "p", LawfulBasis.Consent);
        var frontier = new FakeClient(InferenceTier.Frontier);
        var gw = new PrivacyGateway(new DataClassifier(new LeakyDetector()), new PrivacyRoutingPolicy(opts), consents,
            new ILlmClient[] { new FakeClient(InferenceTier.SelfHosted), frontier }, new InMemoryAuditLog(), opts, new GatewaySecrets(new byte[32]), TimeProvider.System);

        var r = await gw.ExecuteAsync(new LlmTask("t", "reply", "p", "Draft a reply.", "Ravi Kumar says hi", "cust-1"));

        Assert.Empty(frontier.Received);
        Assert.Equal(TaskOutcome.Unavailable, r.Outcome);
        Assert.Contains("residual", r.Reason);
    }

    [Fact]
    public void A_failing_name_detector_fails_closed()
    {
        var classifier = new DataClassifier(new CompositePiiDetector(new RegexPiiDetector(), new ThrowingNames()));
        var c = classifier.Classify("Write a tagline for a steel bottle");
        Assert.True(c.SuspectedPersonal);
        Assert.Equal(InferenceTier.SelfHosted, new PrivacyRoutingPolicy(new RoutingOptions()).Decide("x", c).Tier);
    }

    // Regression tests for review findings.

    [Theory]
    [InlineData("Please call Mr.Ravi tomorrow about the delivery.")]
    [InlineData("Spoke to Smt.Lakshmi about the refund.")]
    [InlineData("R.Suresh said the parcel was late")]
    [InlineData("Assign this to Ravi/Sunita before Friday.")]
    [InlineData("@Ravi can you check the order status?")]
    [InlineData("Ravi_Kumar sent the file")]
    public void Names_glued_to_punctuation_are_still_seen(string text) =>
        Assert.True(Classifier.Classify(text).MayContainPersonalData, text);

    [Fact]
    public void Text_in_scripts_the_lists_cannot_check_is_a_possible_name()
    {
        Assert.True(Classifier.Classify("روی نے کہا").SuspectedPersonal);   // Urdu
        Assert.True(Classifier.Classify("रवि ने कहा").SuspectedPersonal);    // Devanagari
    }

    [Fact]
    public void A_name_followed_by_an_ordinary_verb_is_not_a_place()
    {
        Assert.NotEmpty(Names.Detect("Kavita set up the meeting with the vendor."));
        Assert.Equal(2, Names.Detect("Please ask Kavita.\nKavita set the agenda.").Count);
        Assert.Empty(Names.Detect("Pickup moved to Ravi Road near the bus depot."));
    }

    [Theory]
    [InlineData("This is the best Tata deal this year.")]
    [InlineData("Notes:\n- Visit Ganga\n- Pay rent")]
    [InlineData("Steps:\n- Pay bill")]
    public void Sign_off_and_signature_cues_need_their_real_shape(string text) =>
        Assert.Empty(Names.Detect(text));

    [Fact]
    public void Greeting_look_back_does_not_cross_a_comma()
    {
        var found = Names.Detect("Hi Ravi, Tata refund is pending.");
        Assert.DoesNotContain(found, m => m.Value.Contains("Tata"));
        Assert.Contains(found, m => m.Value == "Ravi");
    }

    [Fact]
    public void Overlapping_name_spans_merge_so_the_surname_is_masked_too()
    {
        var text = "Mr. Ravi kumar called from 9876543210 about the order.";
        var c = Classifier.Classify(text);
        var masked = PrivacyLlm.Core.Redaction.Pseudonymizer.Apply(text, c.Matches, new PrivacyLlm.Core.Redaction.PseudonymVault());
        Assert.DoesNotContain("kumar", masked);
        Assert.DoesNotContain("Ravi", masked);
    }

    [Fact]
    public void Ten_thousand_characters_classify_quickly()
    {
        var text = string.Concat(Enumerable.Repeat("Hi Priya, Ravi said the Tata Salt parcel to Lakshmi Nagar was late. ", 150));
        Classifier.Classify(text); // warm up

        // Best of several runs, so other tests running in parallel do not make this flaky.
        var best = double.MaxValue;
        for (var i = 0; i < 7; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Classifier.Classify(text);
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        Assert.True(best < 50, $"{best:0.0} ms per 10k chars");
    }

    [Fact]
    public void Lexicon_lists_are_loaded_and_consistent()
    {
        var lex = new NameLexicon();
        Assert.True(lex.GivenNames.Count > 5000);
        Assert.True(lex.Places.Count > 1000);
        // Every listed name that is also an everyday word must need context (be treated as ambiguous).
        foreach (var w in lex.GivenNames.Where(lex.CommonWords.Contains))
            Assert.False(NameSignals.KinshipSuffixes.Contains(w), w);
        Assert.DoesNotContain(lex.CommonWords, NameSignals.KinshipSuffixes.Contains);
    }

    [Fact]
    public void Matches_within_slices_and_shifts_offsets()
    {
        var c = new ClassificationResult(DataSensitivity.Personal,
            new[] { new PiiMatch(PiiType.PersonName, 2, 4, "Ravi", 0.9), new PiiMatch(PiiType.Phone, 10, 10, "9876543210", 0.9) },
            Array.Empty<string>());
        var second = c.MatchesWithin(8, 20);
        Assert.Equal(2, Assert.Single(second).Start);
        Assert.Equal("9876543210", second[0].Value);
    }
}
