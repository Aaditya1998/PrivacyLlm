using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PrivacyLlm.Core.Inference;

namespace PrivacyLlm.Infrastructure;

/// <summary>
/// Client for vLLM's OpenAI-compatible <c>/v1/chat/completions</c> endpoint. Uses raw JSON rather
/// than an OpenAI SDK so vLLM-specific fields (<c>chat_template_kwargs</c>, <c>guided_json</c>) are easy to add.
/// </summary>
public sealed class VllmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly SelfHostedLlmOptions _options;

    public VllmClient(HttpClient http, IOptions<SelfHostedLlmOptions> options)
    {
        _options = options.Value;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        if (!string.IsNullOrEmpty(_options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public InferenceTier Tier => InferenceTier.SelfHosted;
    public string Name => $"vllm:{_options.Model}";
    public string HostRegion => _options.HostRegion;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = _options.EnableThinking },
        };
        if (request.JsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        var sw = Stopwatch.StartNew();
        using var response = await SendAsync(() => _http.PostAsJsonAsync("v1/chat/completions", body, ct));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var usage = json.TryGetProperty("usage", out var u) ? u : default;
        return new LlmResponse(
            content,
            json.TryGetProperty("model", out var model) ? model.GetString() ?? _options.Model : _options.Model,
            usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("prompt_tokens").GetInt32() : 0,
            usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("completion_tokens").GetInt32() : 0,
            sw.Elapsed);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            // vLLM exposes /health (200 when the engine is up). Vast.ai instances get preempted or
            // reprovisioned, so this is polled before routing and by the Hangfire health job.
            using var r = await _http.GetAsync(_options.HealthPath.TrimStart('/'), ct);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"self-hosted endpoint unreachable: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = (int)response.StatusCode;
        response.Dispose();
        // Do not include the response body: vLLM echoes request fragments in some validation errors.
        throw new LlmUnavailableException($"self-hosted endpoint returned HTTP {status}");
    }
}
