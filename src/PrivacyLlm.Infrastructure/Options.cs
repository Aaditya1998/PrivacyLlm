namespace PrivacyLlm.Infrastructure;

/// <summary>vLLM OpenAI-compatible server running Qwen 3.x on rented Vast.ai GPUs.</summary>
public sealed class SelfHostedLlmOptions
{
    public const string Section = "SelfHostedLlm";

    /// <summary>e.g. https://vllm.internal.example.in (behind a TLS-terminating proxy or WireGuard tunnel).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Value of vLLM's <c>--api-key</c>. Load from a secret store, never commit it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Must match vLLM's <c>--served-model-name</c> (or the HF repo id if that flag is not set).</summary>
    public string Model { get; set; } = "qwen3-30b-a3b";

    /// <summary>Country of the GPU host. Vast.ai lets you filter offers by geolocation; pin it and declare it here.</summary>
    public string HostRegion { get; set; } = "IN";

    /// <summary>Qwen3 hybrid-thinking checkpoints: disable reasoning for extraction/normalisation tasks.</summary>
    public bool EnableThinking { get; set; }

    /// <summary>Liveness path relative to <see cref="BaseUrl"/>. vLLM: <c>health</c>. Ollama (local dev): <c>v1/models</c>.</summary>
    public string HealthPath { get; set; } = "health";

    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>Frontier API used only for tasks with no personal data (or approved pseudonymised tasks).</summary>
public sealed class FrontierLlmOptions
{
    public const string Section = "FrontierLlm";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Where the provider processes requests; recorded in the audit log.</summary>
    public string HostRegion { get; set; } = "US";

    public int TimeoutSeconds { get; set; } = 120;
}
