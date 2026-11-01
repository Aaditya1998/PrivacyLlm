using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PrivacyLlm.Core.Pipeline;

namespace PrivacyLlm.Tests;

public sealed class ApiValidationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;

    public ApiValidationTests(WebApplicationFactory<Program> factory) =>
        _http = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("SelfHostedLlm:BaseUrl", "http://127.0.0.1:9");
            b.UseSetting("FrontierLlm:Enabled", "false");
        }).CreateClient();

    [Theory]
    [InlineData("cust-42", true)]
    [InlineData("user@example.in", true)]
    [InlineData("CUST-ab12 ", false)] // trailing space: SQL Server would treat it as "CUST-ab12"
    [InlineData(" cust", false)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    public void Identifier_rules(string id, bool valid) => Assert.Equal(valid, InputLimits.IsValidIdentifier(id));

    [Fact]
    public void Identifiers_longer_than_the_column_are_rejected()
    {
        Assert.True(InputLimits.IsValidIdentifier(new string('a', 128)));
        Assert.False(InputLimits.IsValidIdentifier(new string('a', 129)));
    }

    [Fact]
    public async Task Enqueue_rejects_invalid_ids_before_any_job_is_created()
    {
        var r = await _http.PostAsJsonAsync("/api/tasks", new
        {
            taskId = new string('t', 200), taskType = "x", purpose = "order-support", dataPrincipalId = "cust-1", systemPrompt = "s", input = "hi",
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("TaskId", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Consent_with_padded_principal_is_rejected()
    {
        var r = await _http.PostAsJsonAsync("/api/consents", new { dataPrincipalId = "cust-1 ", purpose = "order-support" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
