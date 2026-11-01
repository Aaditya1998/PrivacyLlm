namespace PrivacyLlm.Core.Inference;

public enum InferenceTier
{
    /// <summary>Open-weight model (Qwen 3.x 30B-class) on vLLM, on GPUs we rent and control.</summary>
    SelfHosted,

    /// <summary>Third-party frontier API. Never receives raw personal data.</summary>
    Frontier,
}

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
}

public sealed record LlmRequest(IReadOnlyList<ChatMessage> Messages, int MaxTokens = 1024, double Temperature = 0.2, bool JsonMode = false);

public sealed record LlmResponse(string Content, string Model, int InputTokens, int OutputTokens, TimeSpan Latency);

public interface ILlmClient
{
    InferenceTier Tier { get; }
    string Name { get; }

    /// <summary>Region the inference host physically runs in (ISO country code), used for transfer checks.</summary>
    string HostRegion { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}

public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
