using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrivacyLlm.Core.Audit;
using PrivacyLlm.Core.Dpdp;
using PrivacyLlm.Core.Storage;

namespace PrivacyLlm.Persistence;

public enum StorageProvider
{
    InMemory,
    Sqlite,
    SqlServer,
}

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "PrivacyLlm";

    /// <summary>
    /// Switches consent, audit and payload storage to a database when <c>Storage:Provider</c> is
    /// <c>Sqlite</c> or <c>SqlServer</c>, and persists the Data Protection key ring alongside them.
    /// Call after <c>AddPrivacyLlm</c>; with <c>InMemory</c> (the default) nothing changes.
    /// </summary>
    public static StorageProvider AddPrivacyLlmStorage(this IServiceCollection services, IConfiguration config)
    {
        var provider = config.GetValue("Storage:Provider", StorageProvider.InMemory);
        if (provider == StorageProvider.InMemory) return provider;

        var connectionString = config.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Storage:Provider is {provider} but ConnectionStrings:{ConnectionStringName} is empty.");

        services.AddDbContextFactory<PrivacyLlmDbContext>(o =>
        {
            if (provider == StorageProvider.Sqlite) o.UseSqlite(connectionString).AddInterceptors(new SqliteSecureDeleteInterceptor());
            else o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
        });

        services.Replace(ServiceDescriptor.Singleton<IConsentRegistry, EfConsentRegistry>());
        services.Replace(ServiceDescriptor.Singleton<IAuditLog, EfAuditLog>());
        services.Replace(ServiceDescriptor.Singleton<IExpiringBlobStore, EfBlobStore>());

        services.AddDataProtection()
            .SetApplicationName("PrivacyLlm")
            .PersistKeysToDbContext<PrivacyLlmDbContext>();

        return provider;
    }

    /// <summary>
    /// Creates the database and this app's tables if they are missing. Unlike <c>EnsureCreated</c>,
    /// this also works in a database that already holds other tables (e.g. Hangfire's), and tolerates
    /// several instances starting at once. Switch to EF migrations before the schema has to evolve.
    /// </summary>
    public static void EnsurePrivacyLlmStorageCreated(this IServiceProvider services)
    {
        var factory = services.GetService<IDbContextFactory<PrivacyLlmDbContext>>();
        if (factory is null) return;

        using var db = factory.CreateDbContext();
        var creator = db.GetService<IRelationalDatabaseCreator>();

        if (!creator.Exists())
        {
            try { creator.Create(); }
            catch (DbException) when (creator.Exists()) { } // another instance created it first
        }

        if (OwnTablesExist(db)) return;

        try { creator.CreateTables(); }
        catch (DbException) when (OwnTablesExist(db)) { } // another instance created them first
    }

    private static bool OwnTablesExist(PrivacyLlmDbContext db)
    {
        try
        {
            _ = db.Consents.Any() | db.AuditEvents.Any() | db.Blobs.Any() | db.DataProtectionKeys.Any();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }
}

/// <summary>
/// SQLite only marks deleted rows' pages free, so purged payload ciphertext would stay readable in the
/// file. <c>secure_delete</c> overwrites it, which matters for DPDP s.8(7) erasure.
/// </summary>
internal sealed class SqliteSecureDeleteInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA secure_delete = ON;";
        cmd.ExecuteNonQuery();
    }
}
