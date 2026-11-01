using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Inference;

namespace PrivacyLlm.Core.Audit;

/// <summary>
/// One processing event. Holds no raw prompt or response text: only a keyed hash of the input
/// (so a breach investigation can match a payload without the log itself becoming personal data),
/// PII type counts, the routing decision and the outcome.
/// </summary>
public sealed record AuditEvent(
    Guid Id,
    DateTimeOffset At,
    string TaskId,
    string TaskType,
    string Purpose,
    string? DataPrincipalId,
    DataSensitivity Sensitivity,
    IReadOnlyDictionary<PiiType, int> PiiCounts,
    InferenceTier? Tier,
    string? Model,
    string? HostRegion,
    bool Pseudonymized,
    string Outcome,
    string Detail,
    string InputHash);

public interface IAuditLog
{
    void Write(AuditEvent e);
    IReadOnlyList<AuditEvent> Query(string? dataPrincipalId = null, int take = 100);
}

public sealed class InMemoryAuditLog : IAuditLog
{
    private const int Capacity = 10_000;
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public void Write(AuditEvent e)
    {
        _events.Enqueue(e);
        while (_events.Count > Capacity && _events.TryDequeue(out _)) { }
    }

    public IReadOnlyList<AuditEvent> Query(string? dataPrincipalId = null, int take = 100) =>
        _events.Where(e => dataPrincipalId is null || e.DataPrincipalId == dataPrincipalId)
            .OrderByDescending(e => e.At)
            .Take(take)
            .ToList();
}

public static class AuditHash
{
    public static string Compute(string text, byte[] key)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
    }
}
