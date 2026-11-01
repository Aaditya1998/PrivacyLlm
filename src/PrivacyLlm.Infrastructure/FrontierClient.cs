using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PrivacyLlm.Core.Inference;

namespace PrivacyLlm.Infrastructure;

/// <summary>Anthropic Messages API client for the frontier tier.</summary>
public sealed class FrontierClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly FrontierLlmOptions _options;

    public FrontierClient(HttpClient http, IOptions<FrontierLlmOptions> options)
    {
        _options = options.Value;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        if (!string.IsNullOrEmpty(_options.ApiKey)) _http.DefaultRequestHeaders.Add("x-api-key", _options.ApiKey);
    }

    public InferenceTier Tier => InferenceTier.Frontier;
    public string Name => $"frontier:{_options.Model}";
    public string HostRegion => _options.HostRegion;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey)) throw new LlmUnavailableException("frontier API key not configured");

        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["messages"] = new JsonArray(request.Messages.Where(m => m.Role != "system")
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        if (system.Length > 0) body["system"] = system;

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("v1/messages", body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"frontier API unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new LlmUnavailableException($"frontier API returned HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var text = string.Concat(json.GetProperty("content").EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "text")
                .Select(b => b.GetProperty("text").GetString()));
            var usage = json.GetProperty("usage");
            return new LlmResponse(text, json.GetProperty("model").GetString() ?? _options.Model,
                usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32(), sw.Elapsed);
        }
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct = default) =>
        Task.FromResult(_options.Enabled && !string.IsNullOrEmpty(_options.ApiKey));
}
