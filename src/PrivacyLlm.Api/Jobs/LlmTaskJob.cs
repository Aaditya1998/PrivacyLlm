using Hangfire;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Pipeline;

namespace PrivacyLlm.Api.Jobs;

public sealed class LlmTaskJob(IPrivacyGateway gateway, ProtectedPayloadStore store, ILogger<LlmTaskJob> logger)
{
    public static string ResultKey(string payloadId) => "result:" + payloadId;

    /// <summary>
    /// Hangfire entry point. Only the opaque payload id is serialised into Hangfire storage.
    /// When the self-hosted tier is down the job throws so Hangfire retries with back-off,
    /// rather than the gateway silently sending personal data elsewhere.
    /// </summary>
    [Queue("llm")]
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 30, 60, 120, 300, 600 })]
    [JobDisplayName("LLM task {0}")]
    public async Task RunAsync(string payloadId, CancellationToken ct)
    {
        var task = store.Get<LlmTask>(payloadId);
        if (task is null)
        {
            logger.LogWarning("Payload {PayloadId} missing or expired; nothing to do", payloadId);
            return;
        }

        var result = await gateway.ExecuteAsync(task, ct);
        logger.LogInformation("Task {TaskId} ({TaskType}) -> {Outcome} via {Tier}", task.TaskId, task.TaskType, result.Outcome, result.Tier);

        if (result.Outcome == TaskOutcome.Unavailable)
            throw new LlmUnavailableException($"Task {task.TaskId}: {result.Reason}");

        store.Put(result, ProtectedPayloadStore.ResultTtl, ResultKey(payloadId));
        store.Delete(payloadId);
    }
}

public sealed class MaintenanceJobs(ProtectedPayloadStore store, IEnumerable<ILlmClient> clients, ILogger<MaintenanceJobs> logger)
{
    public void PurgeExpiredPayloads()
    {
        var n = store.PurgeExpired();
        if (n > 0) logger.LogInformation("Purged {Count} expired payloads/results", n);
    }

    public async Task CheckInferenceHealthAsync(CancellationToken ct)
    {
        foreach (var c in clients)
        {
            var ok = await c.IsHealthyAsync(ct);
            if (ok) logger.LogInformation("{Client} healthy", c.Name);
            else if (c.Tier == InferenceTier.SelfHosted)
                logger.LogWarning("{Client} UNHEALTHY; personal-data jobs will retry until it recovers", c.Name);
            else
                logger.LogWarning("{Client} UNHEALTHY or not configured; public-data jobs will fall back to self-hosted", c.Name);
        }
    }
}
