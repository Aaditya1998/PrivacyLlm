using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PrivacyLlm.Persistence;

// Timestamps are stored as UTC ticks (long): SQLite cannot compare or order DateTimeOffset columns
// in SQL, and ticks behave the same on every provider.

public sealed class ConsentRow
{
    public string DataPrincipalId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Basis { get; set; } = "";
    public long GrantedAtUtcTicks { get; set; }
    public long? WithdrawnAtUtcTicks { get; set; }
    public string? ConsentManagerRef { get; set; }
}

/// <summary>Mirrors <c>AuditEvent</c>; like it, holds no raw prompt or response text.</summary>
public sealed class AuditRow
{
    public Guid Id { get; set; }
    public long AtUtcTicks { get; set; }
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? DataPrincipalId { get; set; }
    public string Sensitivity { get; set; } = "";
    public string PiiCountsJson { get; set; } = "{}";
    public string? Tier { get; set; }
    public string? Model { get; set; }
    public string? HostRegion { get; set; }
    public bool Pseudonymized { get; set; }
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
    public string InputHash { get; set; } = "";
}

/// <summary>Data Protection ciphertext only. See <c>ProtectedPayloadStore</c>.</summary>
public sealed class BlobRow
{
    public string Id { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public long ExpiresAtUtcTicks { get; set; }
}

public sealed class PrivacyLlmDbContext(DbContextOptions<PrivacyLlmDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<ConsentRow> Consents => Set<ConsentRow>();
    public DbSet<AuditRow> AuditEvents => Set<AuditRow>();
    public DbSet<BlobRow> Blobs => Set<BlobRow>();

    /// <summary>The Data Protection key ring. Without it, a restart (or a second instance) could not decrypt queued payloads.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// SQL Server's default collation is case-insensitive, which would let principal "CUST-ab12" match
    /// "cust-AB12"'s consent. Identifier columns use a binary collation there so every provider compares
    /// them exactly, like the in-memory stores. (SQLite already compares with BINARY.)
    /// </summary>
    private const string SqlServerBinaryCollation = "Latin1_General_100_BIN2";

    protected override void OnModelCreating(ModelBuilder b)
    {
        var sqlServer = Database.IsSqlServer();

        void Exact<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> p)
        {
            if (sqlServer) p.UseCollation(SqlServerBinaryCollation);
        }

        b.Entity<ConsentRow>(e =>
        {
            e.ToTable("Consents");
            e.HasKey(x => new { x.DataPrincipalId, x.Purpose });
            Exact(e.Property(x => x.DataPrincipalId).HasMaxLength(128));
            Exact(e.Property(x => x.Purpose).HasMaxLength(128));
            e.Property(x => x.Basis).HasMaxLength(32);
            e.Property(x => x.ConsentManagerRef).HasMaxLength(256);
        });

        b.Entity<AuditRow>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AtUtcTicks);
            e.HasIndex(x => new { x.DataPrincipalId, x.AtUtcTicks });
            e.Property(x => x.TaskId).HasMaxLength(128);
            e.Property(x => x.TaskType).HasMaxLength(128);
            e.Property(x => x.Purpose).HasMaxLength(128);
            Exact(e.Property(x => x.DataPrincipalId).HasMaxLength(128));
            e.Property(x => x.Sensitivity).HasMaxLength(32);
            e.Property(x => x.Tier).HasMaxLength(32);
            e.Property(x => x.Model).HasMaxLength(256);
            e.Property(x => x.HostRegion).HasMaxLength(8);
            e.Property(x => x.Outcome).HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.InputHash).HasMaxLength(64);
        });

        b.Entity<BlobRow>(e =>
        {
            e.ToTable("ProtectedBlobs");
            e.HasKey(x => x.Id);
            Exact(e.Property(x => x.Id).HasMaxLength(128));
            e.HasIndex(x => x.ExpiresAtUtcTicks);
        });
    }
}
