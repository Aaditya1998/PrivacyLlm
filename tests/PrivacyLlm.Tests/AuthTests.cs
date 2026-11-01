using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PrivacyLlm.Api.Auth;

namespace PrivacyLlm.Tests;

[Collection(ApiHostCollection.Name)]
public sealed class AuthTests : IDisposable
{
    private const string AliceKey = "alice-key-0123456789abcdef0123456789";
    private const string BobKey = "bob-key-0123456789abcdef0123456789ab";
    private const string ReaderKey = "reader-key-0123456789abcdef01234567";

    private readonly WebApplicationFactory<Program> _factory;

    public AuthTests() => _factory = Boot(tasksPerMinute: 100);

    private static WebApplicationFactory<Program> Boot(int tasksPerMinute, Action<IWebHostBuilder>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("SelfHostedLlm:BaseUrl", "http://127.0.0.1:9");
            b.UseSetting("FrontierLlm:Enabled", "false");
            b.UseSetting("RateLimiting:TasksPerMinute", tasksPerMinute.ToString());
            b.WithClient(0, "alice", AliceKey, Scopes.All);
            b.WithClient(1, "bob", BobKey, Scopes.Tasks);
            b.WithClient(2, "reader", ReaderKey, Scopes.ConsentRead, Scopes.AuditRead);
            extra?.Invoke(b);
        });

    public void Dispose() => _factory.Dispose();

    private static object Task(string id = "t1") => new { taskId = id, taskType = "x", purpose = "p", systemPrompt = "s", input = "write a tagline" };

    public static TheoryData<string, string> Endpoints => new()
    {
        { "POST", "/api/classify" },
        { "POST", "/api/tasks" },
        { "GET", "/api/tasks/abc" },
        { "POST", "/api/tasks/sync" },
        { "POST", "/api/consents" },
        { "DELETE", "/api/consents/cust-1/p" },
        { "GET", "/api/consents/cust-1" },
        { "GET", "/api/audit" },
    };

    private static HttpRequestMessage Request(string method, string path, string? key)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") req.Content = JsonContent.Create(new { text = "x" });
        if (key is not null) req.Headers.Add("X-Api-Key", key);
        return req;
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_api_endpoint_rejects_missing_and_wrong_keys(string method, string path)
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Request(method, path, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Request(method, path, "not-a-real-key"))).StatusCode);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var r = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Keys_only_reach_endpoints_in_their_scopes()
    {
        var reader = _factory.CreateClient().WithKey(ReaderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/tasks", Task())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/consents", new { dataPrincipalId = "cust-1", purpose = "p" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/consents/cust-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/audit")).StatusCode);

        var bob = _factory.CreateClient().WithKey(BobKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync("/api/consents/cust-1/p")).StatusCode);
    }

    [Fact]
    public async Task A_client_cannot_see_another_clients_task()
    {
        var alice = _factory.CreateClient().WithKey(AliceKey);
        var bob = _factory.CreateClient().WithKey(BobKey);

        var queued = await alice.PostAsJsonAsync("/api/tasks", Task());
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payloadId").GetString();

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/tasks/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/tasks/{id}")).StatusCode);
    }

    [Fact]
    public async Task Task_creation_is_rate_limited_per_client()
    {
        using var factory = Boot(tasksPerMinute: 2);
        var alice = factory.CreateClient().WithKey(AliceKey);
        var bob = factory.CreateClient().WithKey(BobKey);

        Assert.Equal(HttpStatusCode.Accepted, (await alice.PostAsJsonAsync("/api/tasks", Task("a1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await alice.PostAsJsonAsync("/api/tasks", Task("a2"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.PostAsJsonAsync("/api/tasks", Task("a3"))).StatusCode);

        // Another client has its own allowance.
        Assert.Equal(HttpStatusCode.Accepted, (await bob.PostAsJsonAsync("/api/tasks", Task("b1"))).StatusCode);
    }

    [Fact]
    public void Generated_keys_hash_to_their_configured_value()
    {
        var (key, hash) = ApiKeys.Generate();
        Assert.StartsWith("pllm_", key);
        Assert.True(key.Length >= 40);
        Assert.True(ApiKeys.HashesEqual(ApiKeys.Hash(key), hash));
        Assert.False(ApiKeys.HashesEqual(ApiKeys.Hash(key + "x"), hash));
    }

    [Theory]
    [InlineData("not-hex", "tasks")]
    [InlineData("ABCD", "tasks")]
    [InlineData(null, "everything")]
    public void Bad_auth_configuration_stops_startup(string? hash, string scope)
    {
        var options = new ApiKeyOptions
        {
            Clients = { new ApiClient { Name = "x", KeySha256 = hash ?? ApiKeys.Hash("k"), Scopes = { scope } } },
        };
        Assert.Throws<InvalidOperationException>(() => ApiKeys.Validate(options));
    }

    [Fact]
    public void Two_clients_with_the_same_key_stop_startup()
    {
        var options = new ApiKeyOptions
        {
            Clients =
            {
                new ApiClient { Name = "a", KeySha256 = ApiKeys.Hash("k"), Scopes = { Scopes.Tasks } },
                new ApiClient { Name = "b", KeySha256 = ApiKeys.Hash("k"), Scopes = { Scopes.AuditRead } },
            },
        };
        Assert.Throws<InvalidOperationException>(() => ApiKeys.Validate(options));
    }

    /// <summary>Each endpoint and the one scope it needs. Checked from both sides so no route relies on the fallback policy alone.</summary>
    public static TheoryData<string, string, string> EndpointScopes => new()
    {
        { "POST", "/api/classify", Scopes.Tasks },
        { "POST", "/api/tasks", Scopes.Tasks },
        { "GET", "/api/tasks/abc", Scopes.Tasks },
        { "POST", "/api/tasks/sync", Scopes.Tasks },
        { "POST", "/api/consents", Scopes.ConsentWrite },
        { "DELETE", "/api/consents/cust-1/p", Scopes.ConsentWrite },
        { "GET", "/api/consents/cust-1", Scopes.ConsentRead },
        { "GET", "/api/audit", Scopes.AuditRead },
    };

    [Theory]
    [MemberData(nameof(EndpointScopes))]
    public async Task Each_endpoint_requires_exactly_its_scope(string method, string path, string scope)
    {
        const string withoutKey = "all-but-one-0123456789abcdef0123456789";
        const string onlyKey = "only-this-0123456789abcdef0123456789ab";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("SelfHostedLlm:BaseUrl", "http://127.0.0.1:9");
            b.UseSetting("FrontierLlm:Enabled", "false");
            b.WithClient(0, "without", withoutKey, Scopes.All.Where(s => s != scope).ToArray());
            b.WithClient(1, "only", onlyKey, scope);
        });

        var denied = await factory.CreateClient().SendAsync(Request(method, path, withoutKey));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var allowed = await factory.CreateClient().SendAsync(Request(method, path, onlyKey));
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
    }

    [Fact]
    public async Task A_finished_result_is_only_returned_to_its_owner()
    {
        // The job cannot finish here (no model), so write the finished result the way LlmTaskJob does.
        var store = _factory.Services.GetRequiredService<PrivacyLlm.Api.Jobs.ProtectedPayloadStore>();
        var result = new PrivacyLlm.Core.Pipeline.LlmTaskResult("t1", PrivacyLlm.Core.Pipeline.TaskOutcome.Completed, "Dear Ravi, ...",
            PrivacyLlm.Core.Inference.InferenceTier.SelfHosted, "qwen", false, PrivacyLlm.Core.Classification.DataSensitivity.Personal, "ok");
        store.Put(new PrivacyLlm.Api.Jobs.OwnedResult("alice", result), TimeSpan.FromMinutes(5), PrivacyLlm.Api.Jobs.LlmTaskJob.ResultKey("done-1"));

        var alice = await _factory.CreateClient().WithKey(AliceKey).GetAsync("/api/tasks/done-1");
        Assert.Equal(HttpStatusCode.OK, alice.StatusCode);
        Assert.Contains("Dear Ravi", await alice.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().WithKey(BobKey).GetAsync("/api/tasks/done-1")).StatusCode);
    }

    [Fact]
    public async Task Rotating_a_key_keeps_access_to_tasks_queued_under_the_old_one()
    {
        const string oldKey = "svc-old-0123456789abcdef0123456789abcd";
        const string newKey = "svc-new-0123456789abcdef0123456789abcd";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("SelfHostedLlm:BaseUrl", "http://127.0.0.1:9");
            b.UseSetting("FrontierLlm:Enabled", "false");
            b.WithClient(0, "order-service", oldKey, Scopes.Tasks);
            b.WithClient(1, "order-service", newKey, Scopes.Tasks);
        });

        var queued = await factory.CreateClient().WithKey(oldKey).PostAsJsonAsync("/api/tasks", Task());
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payloadId").GetString();

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().WithKey(newKey).GetAsync($"/api/tasks/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_payload_queued_before_ownership_existed_is_dropped_not_retried_forever()
    {
        var store = _factory.Services.GetRequiredService<PrivacyLlm.Api.Jobs.ProtectedPayloadStore>();
        var legacyId = store.Put(new PrivacyLlm.Core.Pipeline.LlmTask("t", "x", "p", "s", "hi"), TimeSpan.FromMinutes(5)); // old format: no owner

        var job = _factory.Services.GetRequiredService<PrivacyLlm.Api.Jobs.LlmTaskJob>();
        await job.RunAsync(legacyId, CancellationToken.None); // must not throw

        Assert.Null(store.Get<PrivacyLlm.Api.Jobs.QueuedTask>(legacyId));
    }
}
