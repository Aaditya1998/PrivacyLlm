using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Core.Storage;

namespace PrivacyLlm.Persistence;

internal static class Ticks
{
    public static long Of(DateTimeOffset t) => t.UtcTicks;
    public static DateTimeOffset From(long ticks) => new(ticks, TimeSpan.Zero);
}

internal static class Exact
{
    // Defence in depth: the database's comparison may be looser than ordinal (e.g. SQL Server pads
    // trailing spaces even under a binary collation), so re-check every match in memory.
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    public static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>
/// The Core interfaces are synchronous and the services are singletons, so each call opens its own
/// short-lived context from the factory rather than holding one across threads.
/// </summary>
public sealed class EfConsentRegistry(IDbContextFactory<PrivacyLlmDbContext> factory, TimeProvider clock) : IConsentRegistry
{
    public ConsentRecord Grant(string dataPrincipalId, string purpose, LawfulBasis basis, string? consentManagerRef = null)
    {
        var key = Normalize(purpose);
        var now = clock.GetUtcNow();

        // Two concurrent grants can both see "no row" and both insert; the loser retries as an update.
        for (var attempt = 0; ; attempt++)
        {
            using var db = factory.CreateDbContext();
            var row = db.Consents.Find(dataPrincipalId, key);
            if (row is not null && !(Exact.Same(row.DataPrincipalId, dataPrincipalId) && Exact.Same(row.Purpose, key)))
                throw new InvalidOperationException("Consent key collides with an existing principal under the database collation.");
            if (row is null)
            {
                row = new ConsentRow { DataPrincipalId = dataPrincipalId, Purpose = key };
                db.Consents.Add(row);
            }

            row.Basis = basis.ToString();
            row.GrantedAtUtcTicks = Ticks.Of(now);
            row.WithdrawnAtUtcTicks = null;
            row.ConsentManagerRef = consentManagerRef;

            try
            {
                db.SaveChanges();
                return ToRecord(row);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
            }
        }
    }

    public bool Withdraw(string dataPrincipalId, string purpose)
    {
        using var db = factory.CreateDbContext();
        var key = Normalize(purpose);
        var row = db.Consents.Find(dataPrincipalId, key);
        if (row is null || !Exact.Same(row.DataPrincipalId, dataPrincipalId) || !Exact.Same(row.Purpose, key)) return false;
        if (row.WithdrawnAtUtcTicks is not null) return false;
        row.WithdrawnAtUtcTicks = Ticks.Of(clock.GetUtcNow());
        db.SaveChanges();
        return true;
    }

    public ConsentRecord? Find(string dataPrincipalId, string purpose)
    {
        var key = Normalize(purpose);
        using var db = factory.CreateDbContext();
        var row = db.Consents.AsNoTracking().SingleOrDefault(c => c.DataPrincipalId == dataPrincipalId && c.Purpose == key);
        return row is not null && Exact.Same(row.DataPrincipalId, dataPrincipalId) && Exact.Same(row.Purpose, key) ? ToRecord(row) : null;
    }

    public IReadOnlyList<ConsentRecord> ForPrincipal(string dataPrincipalId)
    {
        using var db = factory.CreateDbContext();
        return db.Consents.AsNoTracking()
            .Where(c => c.DataPrincipalId == dataPrincipalId)
            .OrderBy(c => c.GrantedAtUtcTicks)
            .AsEnumerable()
            .Where(c => Exact.Same(c.DataPrincipalId, dataPrincipalId))
            .Select(ToRecord)
            .ToList();
    }

    private static ConsentRecord ToRecord(ConsentRow r) => new(
        r.DataPrincipalId, r.Purpose, Enum.Parse<LawfulBasis>(r.Basis), Ticks.From(r.GrantedAtUtcTicks),
        r.WithdrawnAtUtcTicks is { } w ? Ticks.From(w) : null, r.ConsentManagerRef);

    private static string Normalize(string purpose) => purpose.Trim().ToLowerInvariant();
}

public sealed class EfAuditLog(IDbContextFactory<PrivacyLlmDbContext> factory) : IAuditLog
{
    // Column limits from PrivacyLlmDbContext. An audit write must never fail after the model call
    // (the Hangfire retry would call the model again), so over-long values are clipped, not rejected.
    private const int IdMax = 128, ModelMax = 256, RegionMax = 8, DetailMax = 2000;

    public void Write(AuditEvent e)
    {
        using var db = factory.CreateDbContext();
        db.AuditEvents.Add(new AuditRow
        {
            Id = e.Id,
            AtUtcTicks = Ticks.Of(e.At),
            TaskId = Exact.Clip(e.TaskId, IdMax)!,
            TaskType = Exact.Clip(e.TaskType, IdMax)!,
            Purpose = Exact.Clip(e.Purpose, IdMax)!,
            DataPrincipalId = Exact.Clip(e.DataPrincipalId, IdMax),
            Sensitivity = e.Sensitivity.ToString(),
            PiiCountsJson = JsonSerializer.Serialize(e.PiiCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)),
            Tier = e.Tier?.ToString(),
            Model = Exact.Clip(e.Model, ModelMax),
            HostRegion = Exact.Clip(e.HostRegion, RegionMax),
            Pseudonymized = e.Pseudonymized,
            Outcome = e.Outcome,
            Detail = Exact.Clip(e.Detail, DetailMax)!,
            InputHash = e.InputHash,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<AuditEvent> Query(string? dataPrincipalId = null, int take = 100)
    {
        using var db = factory.CreateDbContext();
        var q = db.AuditEvents.AsNoTracking();
        if (dataPrincipalId is not null) q = q.Where(a => a.DataPrincipalId == dataPrincipalId);
        return q.OrderByDescending(a => a.AtUtcTicks).Take(Math.Clamp(take, 1, 1000))
            .AsEnumerable()
            .Where(a => dataPrincipalId is null || Exact.Same(a.DataPrincipalId, dataPrincipalId))
            .Select(ToEvent)
            .ToList();
    }

    private static AuditEvent ToEvent(AuditRow r) => new(
        r.Id, Ticks.From(r.AtUtcTicks), r.TaskId, r.TaskType, r.Purpose, r.DataPrincipalId,
        Enum.Parse<DataSensitivity>(r.Sensitivity),
        JsonSerializer.Deserialize<Dictionary<string, int>>(r.PiiCountsJson)!
            .ToDictionary(kv => Enum.Parse<PiiType>(kv.Key), kv => kv.Value),
        r.Tier is null ? null : Enum.Parse<InferenceTier>(r.Tier),
        r.Model, r.HostRegion, r.Pseudonymized, r.Outcome, r.Detail, r.InputHash);
}

public sealed class EfBlobStore(IDbContextFactory<PrivacyLlmDbContext> factory) : IExpiringBlobStore
{
    public void Put(string id, byte[] data, DateTimeOffset expiresAt)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var db = factory.CreateDbContext();
            var row = db.Blobs.Find(id);
            if (row is null) db.Blobs.Add(row = new BlobRow { Id = id });
            row.Data = data;
            row.ExpiresAtUtcTicks = Ticks.Of(expiresAt);

            try
            {
                db.SaveChanges();
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
            }
        }
    }

    public StoredBlob? Get(string id)
    {
        using var db = factory.CreateDbContext();
        var row = db.Blobs.AsNoTracking().SingleOrDefault(b => b.Id == id);
        return row is not null && Exact.Same(row.Id, id) ? new StoredBlob(row.Data, Ticks.From(row.ExpiresAtUtcTicks)) : null;
    }

    public void Delete(string id)
    {
        using var db = factory.CreateDbContext();
        db.Blobs.Where(b => b.Id == id).ExecuteDelete();
    }

    public int PurgeExpired(DateTimeOffset now)
    {
        var cutoff = Ticks.Of(now);
        using var db = factory.CreateDbContext();
        return db.Blobs.Where(b => b.ExpiresAtUtcTicks <= cutoff).ExecuteDelete();
    }
}
