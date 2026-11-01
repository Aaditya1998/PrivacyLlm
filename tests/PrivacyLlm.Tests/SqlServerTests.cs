using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Persistence;

namespace PrivacyLlm.Tests;

/// <summary>
/// SQL Server behaviour that SQLite cannot reproduce (case-insensitive default collation, enforced
/// column lengths). Runs only when PRIVACYLLM_SQLSERVER_TEST holds a server connection string, e.g.
/// <c>Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true</c>.
/// Each run creates and drops its own database.
/// </summary>
public sealed class SqlServerTests : IDisposable
{
    private readonly string? _server = Environment.GetEnvironmentVariable("PRIVACYLLM_SQLSERVER_TEST");
    private readonly string _dbName = $"PrivacyLlmTest_{Guid.NewGuid():N}";
    private ServiceProvider? _sp;

    private string ConnectionString => new SqlConnectionStringBuilder(_server) { InitialCatalog = _dbName }.ConnectionString;

    private ServiceProvider Storage()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "SqlServer",
            ["ConnectionStrings:PrivacyLlm"] = ConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddPrivacyLlmStorage(config);
        _sp = services.BuildServiceProvider();
        _sp.EnsurePrivacyLlmStorageCreated();
        return _sp;
    }

    public void Dispose()
    {
        if (_sp is null) return;
        using (var db = _sp.GetRequiredService<IDbContextFactory<PrivacyLlmDbContext>>().CreateDbContext())
            db.Database.EnsureDeleted();
        _sp.Dispose();
    }

    [Fact]
    public void Principal_ids_differing_only_by_case_are_separate_principals()
    {
        if (_server is null) return;
        var reg = Storage().GetRequiredService<IConsentRegistry>();

        reg.Grant("CUST-ab12", "support", LawfulBasis.Consent);
        Assert.Null(reg.Find("cust-AB12", "support"));

        reg.Grant("cust-AB12", "support", LawfulBasis.Consent);
        Assert.True(reg.Withdraw("cust-AB12", "support"));
        Assert.True(reg.Find("CUST-ab12", "support")!.IsActive);
        Assert.Single(reg.ForPrincipal("CUST-ab12"));
    }

    [Fact]
    public void Audit_write_with_overlong_values_succeeds()
    {
        if (_server is null) return;
        var log = Storage().GetRequiredService<IAuditLog>();
        log.Write(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, new string('t', 300), "x", "p", "CUST-ab12", DataSensitivity.Public,
            new Dictionary<PiiType, int>(), null, new string('m', 400), "INDIA-SOUTH", false, "Completed", new string('d', 5000), "h"));
        Assert.Single(log.Query("CUST-ab12"));
        Assert.Empty(log.Query("cust-ab12"));
    }

    [Fact]
    public void Tables_are_created_in_a_database_that_already_has_other_tables()
    {
        if (_server is null) return;
        using (var c = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{_dbName}]; EXEC('USE [{_dbName}]; CREATE TABLE dbo.HangfireJob (Id int PRIMARY KEY)');";
            cmd.ExecuteNonQuery();
        }

        var reg = Storage().GetRequiredService<IConsentRegistry>();
        reg.Grant("cust-1", "p", LawfulBasis.Consent);
        Assert.NotNull(reg.Find("cust-1", "p"));
    }
}
