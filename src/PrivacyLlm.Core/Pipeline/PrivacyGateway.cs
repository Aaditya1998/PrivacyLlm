using System.Text.RegularExpressions;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Policy;
using PrivacyLlm.Core.Redaction;

namespace PrivacyLlm.Core.Pipeline;

/// <summary>A unit of LLM work, typically created by a Hangfire job from a business record.</summary>
public sealed record LlmTask(
    string TaskId,
    string TaskType,
    string Purpose,
    string SystemPrompt,
    string Input,
    string? DataPrincipalId = null,
    IReadOnlyList<string>? DeclaredFields = null,
    int MaxTokens = 1024,
    bool JsonMode = false);

public enum TaskOutcome
{
    Completed,
    RejectedNoConsent,
    RejectedRegion,
    Unavailable,
}

public sealed record LlmTaskResult(
    string TaskId,
    TaskOutcome Outcome,
    string? Output,
    InferenceTier? Tier,
    string? Model,
    bool Pseudonymized,
    DataSensitivity Sensitivity,
    string Reason);

public interface IPrivacyGateway
{
    Task<LlmTaskResult> ExecuteAsync(LlmTask task, CancellationToken ct = default);
}

public sealed record GatewaySecrets(byte[] AuditHashKey);

/// <summary>
/// Classify → check lawful basis → route → (pseudonymise) → infer → rehydrate → audit.
/// Every path writes exactly one audit event.
/// </summary>
public sealed partial class PrivacyGateway(
    IDataClassifier classifier,
    IRoutingPolicy routing,
    IConsentRegistry consents,
    IEnumerable<ILlmClient> clients,
    IAuditLog audit,
    RoutingOptions routingOptions,
    GatewaySecrets secrets,
    TimeProvider clock) : IPrivacyGateway
{
    private readonly Dictionary<InferenceTier, ILlmClient> _clients = clients.ToDictionary(c => c.Tier);

    [GeneratedRegex(@"<think>.*?</think>\s*", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    public async Task<LlmTaskResult> ExecuteAsync(LlmTask task, CancellationToken ct = default)
    {
        var classification = classifier.Classify(task.SystemPrompt + "\n" + task.Input, task.DeclaredFields);

        if (classification.ContainsPersonalData)
        {
            var basisProblem = CheckLawfulBasis(task);
            if (basisProblem is not null)
                return Finish(task, classification, TaskOutcome.RejectedNoConsent, basisProblem, null, null, false, null);
        }

        var decision = routing.Decide(task.TaskType, classification);
        var tiers = decision.Fallback is { } fb ? new[] { decision.Tier, fb } : new[] { decision.Tier };
        var errors = new List<string>();

        foreach (var tier in tiers)
        {
            if (!_clients.TryGetValue(tier, out var client))
            {
                errors.Add($"{tier}: not configured");
                continue;
            }

            // Pseudonymise whenever raw personal data would otherwise reach a non-self-hosted model.
            var pseudonymize = classification.ContainsPersonalData && tier != InferenceTier.SelfHosted;
            if (pseudonymize && !decision.Pseudonymize)
            {
                // Defence in depth: the policy never produces this, but never trust a single check.
                errors.Add($"{tier}: refused, personal data without pseudonymisation approval");
                continue;
            }

            if (classification.ContainsPersonalData && !pseudonymize &&
                !routingOptions.AllowedPersonalDataRegions.Contains(client.HostRegion, StringComparer.OrdinalIgnoreCase))
            {
                return Finish(task, classification, TaskOutcome.RejectedRegion,
                    $"{client.Name} runs in region '{client.HostRegion}', not in allowed personal-data regions", tier, null, false, client.HostRegion);
            }

            var vault = new PseudonymVault();
            var system = pseudonymize ? Pseudonymize(task.SystemPrompt, vault) : task.SystemPrompt;
            var input = pseudonymize ? Pseudonymize(task.Input, vault) : task.Input;

            try
            {
                var response = await client.CompleteAsync(
                    new LlmRequest(new[] { ChatMessage.System(system), ChatMessage.User(input) }, task.MaxTokens, JsonMode: task.JsonMode), ct);

                var output = ThinkBlock().Replace(response.Content, string.Empty).Trim();
                if (pseudonymize) output = vault.Rehydrate(output);

                var reason = tier == decision.Tier ? decision.Reason : $"{decision.Reason}; fell back after: {string.Join("; ", errors)}";
                return Finish(task, classification, TaskOutcome.Completed, reason, tier, response.Model, pseudonymize, client.HostRegion, output);
            }
            catch (Exception ex) when (ex is LlmUnavailableException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{tier}: {ex.Message}");
            }
        }

        // Personal-data tasks have no fallback, so this is the fail-closed path: Hangfire will retry.
        return Finish(task, classification, TaskOutcome.Unavailable, string.Join("; ", errors), decision.Tier, null, false, null);
    }

    private string? CheckLawfulBasis(LlmTask task)
    {
        if (string.IsNullOrWhiteSpace(task.DataPrincipalId))
            return "personal data present but no DataPrincipalId supplied; cannot verify lawful basis";

        var record = consents.Find(task.DataPrincipalId, task.Purpose);
        if (record is null) return $"no consent or legitimate-use record for purpose '{task.Purpose}'";
        if (!record.IsActive) return $"consent for purpose '{task.Purpose}' was withdrawn at {record.WithdrawnAt:O}";
        return null;
    }

    private string Pseudonymize(string text, PseudonymVault vault) =>
        Pseudonymizer.Apply(text, classifier.Classify(text).Matches, vault);

    private LlmTaskResult Finish(
        LlmTask task, ClassificationResult c, TaskOutcome outcome, string reason,
        InferenceTier? tier, string? model, bool pseudonymized, string? region, string? output = null)
    {
        audit.Write(new AuditEvent(
            Guid.NewGuid(), clock.GetUtcNow(), task.TaskId, task.TaskType, task.Purpose, task.DataPrincipalId,
            c.Sensitivity, c.CountsByType, tier, model, region, pseudonymized, outcome.ToString(), reason,
            AuditHash.Compute(task.Input, secrets.AuditHashKey)));

        return new LlmTaskResult(task.TaskId, outcome, output, tier, model, pseudonymized, c.Sensitivity, reason);
    }
}
