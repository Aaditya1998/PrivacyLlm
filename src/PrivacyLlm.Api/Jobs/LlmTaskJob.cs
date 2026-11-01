using Hangfire;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Pipeline;

namespace PrivacyLlm.Api.Jobs;

/// <summary>A queued task and the API client that queued it; only that client may read the result.</summary>
public sealed record QueuedTask(string Owner, LlmTask Task);

/// <summary>A finished task's result, still tied to its owner: results contain personal data.</summary>
public sealed record OwnedResult(string Owner, LlmTaskResult Result);

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
        var queued = store.Get<QueuedTask>(payloadId);
        if (queued is null)
        {
            logger.LogWarning("Payload {PayloadId} missing or expired; nothing to do", payloadId);
            return;
        }

        // A payload stored before task ownership existed deserialises with no owner. Nobody could ever read its
        // result, so drop it instead of failing on every retry.
        if (queued.Task is null || string.IsNullOrEmpty(queued.Owner))
        {
            logger.LogWarning("Payload {PayloadId} has no owner (queued before API auth); deleting it unprocessed", payloadId);
            store.Delete(payloadId);
            return;
        }

        var task = queued.Task;
        var result = await gateway.ExecuteAsync(task, ct);
        logger.LogInformation("Task {TaskId} ({TaskType}) for {Client} -> {Outcome} via {Tier}", task.TaskId, task.TaskType, queued.Owner, result.Outcome, result.Tier);

        if (result.Outcome == TaskOutcome.Unavailable)
            throw new LlmUnavailableException($"Task {task.TaskId}: {result.Reason}");

        store.Put(new OwnedResult(queued.Owner, result), ProtectedPayloadStore.ResultTtl, ResultKey(payloadId));
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
