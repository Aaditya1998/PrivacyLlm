using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Inference;
using PrivacyLlm.Persistence;

namespace PrivacyLlm.Tests;

/// <summary>
/// Runs the EF stores against a real SQLite file. Each context opens its own connection, as in
/// production; a single shared in-memory connection would not be safe for the concurrency test.
/// </summary>
public sealed class PersistenceTests : IDisposable
{
    private sealed class Factory(DbContextOptions<PrivacyLlmDbContext> options) : IDbContextFactory<PrivacyLlmDbContext>
    {
        public PrivacyLlmDbContext CreateDbContext() => new(options);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"privacyllm-unit-{Guid.NewGuid():N}.db");
    private readonly Factory _factory;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));

    public PersistenceTests()
    {
        _factory = new Factory(new DbContextOptionsBuilder<PrivacyLlmDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Consent_survives_new_registry_instances_and_withdrawal_sticks()
    {
        new EfConsentRegistry(_factory, _clock).Grant("cust-1", " Order-Support ", LawfulBasis.VoluntarilyProvided, "cm-123");

        var fresh = new EfConsentRegistry(_factory, _clock);
        var r = fresh.Find("cust-1", "order-support");
        Assert.NotNull(r);
        Assert.Equal(LawfulBasis.VoluntarilyProvided, r.Basis);
        Assert.Equal("cm-123", r.ConsentManagerRef);
        Assert.Equal(_clock.Now, r.GrantedAt);

        _clock.Now = _clock.Now.AddHours(1);
        Assert.True(fresh.Withdraw("cust-1", "ORDER-SUPPORT"));
        Assert.False(fresh.Withdraw("cust-1", "order-support"));

        var after = new EfConsentRegistry(_factory, _clock).Find("cust-1", "order-support")!;
        Assert.False(after.IsActive);
        Assert.Equal(_clock.Now, after.WithdrawnAt);
    }

    [Fact]
    public void Regranting_after_withdrawal_reactivates_consent()
    {
        var reg = new EfConsentRegistry(_factory, _clock);
        reg.Grant("cust-1", "p", LawfulBasis.Consent);
        reg.Withdraw("cust-1", "p");
        reg.Grant("cust-1", "p", LawfulBasis.Consent);

        Assert.True(reg.Find("cust-1", "p")!.IsActive);
        Assert.Single(reg.ForPrincipal("cust-1"));
    }

    [Fact]
    public async Task Concurrent_grants_for_the_same_key_do_not_fail()
    {
        var reg = new EfConsentRegistry(_factory, _clock);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => reg.Grant("cust-9", "p", LawfulBasis.Consent))));
        Assert.Single(reg.ForPrincipal("cust-9"));
    }

    [Fact]
    public void Audit_round_trips_and_filters_newest_first()
    {
        var log = new EfAuditLog(_factory);
        for (var i = 0; i < 3; i++)
        {
            log.Write(new AuditEvent(Guid.NewGuid(), _clock.Now.AddMinutes(i), $"t{i}", "support-reply", "order-support",
                i == 1 ? "cust-other" : "cust-1", DataSensitivity.HighRiskPersonal,
                new Dictionary<PiiType, int> { [PiiType.Phone] = 2, [PiiType.Pan] = 1 },
                InferenceTier.SelfHosted, "qwen3", "IN", false, "Completed", "ok", "abc123"));
        }

        var events = new EfAuditLog(_factory).Query("cust-1");
        Assert.Equal(new[] { "t2", "t0" }, events.Select(e => e.TaskId));
        var e = events[0];
        Assert.Equal(DataSensitivity.HighRiskPersonal, e.Sensitivity);
        Assert.Equal(2, e.PiiCounts[PiiType.Phone]);
        Assert.Equal(InferenceTier.SelfHosted, e.Tier);
        Assert.Equal(_clock.Now.AddMinutes(2), e.At);

        Assert.Single(log.Query(take: 1));
    }

    [Fact]
    public void Audit_truncates_overlong_detail_instead_of_failing()
    {
        var log = new EfAuditLog(_factory);
        log.Write(new AuditEvent(Guid.NewGuid(), _clock.Now, "t", "x", "p", null, DataSensitivity.Public,
            new Dictionary<PiiType, int>(), null, null, null, false, "Unavailable", new string('x', 5000), "h"));
        Assert.Equal(2000, log.Query().Single().Detail.Length);
    }

    [Fact]
    public void Audit_clips_overlong_ids_instead_of_failing_after_the_model_call()
    {
        var log = new EfAuditLog(_factory);
        log.Write(new AuditEvent(Guid.NewGuid(), _clock.Now, new string('t', 300), "x", "p", null, DataSensitivity.Public,
            new Dictionary<PiiType, int>(), null, new string('m', 400), "INDIA-SOUTH", false, "Completed", "ok", "h"));
        var e = log.Query().Single();
        Assert.Equal(128, e.TaskId.Length);
        Assert.Equal(256, e.Model!.Length);
        Assert.Equal(8, e.HostRegion!.Length);
    }

    [Fact]
    public void Schema_is_created_in_a_database_that_already_has_other_tables()
    {
        // e.g. ConnectionStrings:PrivacyLlm pointing at the database that already holds Hangfire's tables,
        // where plain EnsureCreated would see "has tables" and create nothing.
        var path = Path.Combine(Path.GetTempPath(), $"privacyllm-shared-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE HangfireJob (Id INTEGER PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            var sp = BuildStorage(path);
            sp.EnsurePrivacyLlmStorageCreated();
            sp.EnsurePrivacyLlmStorageCreated(); // idempotent

            var reg = sp.GetRequiredService<IConsentRegistry>();
            reg.Grant("cust-1", "p", LawfulBasis.Consent);
            Assert.NotNull(reg.Find("cust-1", "p"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Sqlite_connections_overwrite_deleted_content()
    {
        var sp = BuildStorage(_dbPath);
        using var db = sp.GetRequiredService<IDbContextFactory<PrivacyLlmDbContext>>().CreateDbContext();
        db.Database.OpenConnection();
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "PRAGMA secure_delete";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    private static ServiceProvider BuildStorage(string path)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Sqlite",
            ["ConnectionStrings:PrivacyLlm"] = $"Data Source={path}",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddPrivacyLlmStorage(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Blobs_upsert_expire_and_purge()
    {
        var store = new EfBlobStore(_factory);
        store.Put("a", new byte[] { 1 }, _clock.Now.AddMinutes(5));
        store.Put("a", new byte[] { 2, 3 }, _clock.Now.AddMinutes(10));
        store.Put("b", new byte[] { 9 }, _clock.Now.AddMinutes(-1));

        var a = store.Get("a")!;
        Assert.Equal(new byte[] { 2, 3 }, a.Data);
        Assert.Equal(_clock.Now.AddMinutes(10), a.ExpiresAt);

        Assert.Equal(1, store.PurgeExpired(_clock.Now));
        Assert.Null(store.Get("b"));

        store.Delete("a");
        Assert.Null(store.Get("a"));
    }
}
