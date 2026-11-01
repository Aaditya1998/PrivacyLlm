using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Inference;

namespace PrivacyLlm.Core.Policy;

public sealed class RoutingOptions
{
    public const string Section = "Routing";

    /// <summary>
    /// When true, Personal (not HighRisk) payloads may go to the frontier tier after pseudonymisation,
    /// but only for task types listed in <see cref="PseudonymizedFrontierTaskTypes"/>. Off by default.
    /// </summary>
    public bool AllowPseudonymizedFrontier { get; set; }

    public List<string> PseudonymizedFrontierTaskTypes { get; set; } = new();

    /// <summary>Non-personal tasks go to the frontier tier unless listed here.</summary>
    public List<string> AlwaysSelfHostedTaskTypes { get; set; } = new();

    /// <summary>If the frontier tier is down, may non-personal tasks fall back to self-hosted?</summary>
    public bool FallbackToSelfHostedForPublicData { get; set; } = true;

    /// <summary>ISO country codes where personal data may be processed (DPDP s.16 transfer controls).</summary>
    public List<string> AllowedPersonalDataRegions { get; set; } = new() { "IN" };
}

public sealed record RoutingDecision(InferenceTier Tier, bool Pseudonymize, InferenceTier? Fallback, string Reason);

public interface IRoutingPolicy
{
    RoutingDecision Decide(string taskType, ClassificationResult classification);
}

/// <summary>
/// The single place that decides where a prompt may go. Invariant: raw personal data, definite or
/// possible, is only ever sent to the self-hosted tier, and such tasks never fall back to the frontier tier.
/// </summary>
public sealed class PrivacyRoutingPolicy(RoutingOptions options) : IRoutingPolicy
{
    public RoutingDecision Decide(string taskType, ClassificationResult classification)
    {
        switch (classification.Sensitivity)
        {
            case DataSensitivity.HighRiskPersonal:
                return new(InferenceTier.SelfHosted, false, null, "high-risk personal data: self-hosted only, no fallback");

            case DataSensitivity.Personal when options.AllowPseudonymizedFrontier && Contains(options.PseudonymizedFrontierTaskTypes, taskType):
                return new(InferenceTier.Frontier, true, null, $"personal data, task '{taskType}' approved for pseudonymised frontier processing");

            case DataSensitivity.Personal:
                return new(InferenceTier.SelfHosted, false, null, "personal data: self-hosted only, no fallback");

            // Before the AlwaysSelfHosted branch, which would otherwise allow a frontier fallback.
            case DataSensitivity.Public when classification.SuspectedPersonal:
                return new(InferenceTier.SelfHosted, false, null, "possible personal name: self-hosted only, no fallback");

            default:
                if (Contains(options.AlwaysSelfHostedTaskTypes, taskType))
                    return new(InferenceTier.SelfHosted, false, InferenceTier.Frontier, $"task '{taskType}' pinned to self-hosted");

                return new(InferenceTier.Frontier, false,
                    options.FallbackToSelfHostedForPublicData ? InferenceTier.SelfHosted : null,
                    "no personal data: frontier tier");
        }
    }

    private static bool Contains(IEnumerable<string> list, string value) => list.Contains(value, StringComparer.OrdinalIgnoreCase);
}
