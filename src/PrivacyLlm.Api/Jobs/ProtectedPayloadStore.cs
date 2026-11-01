using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PrivacyLlm.Core.Storage;

namespace PrivacyLlm.Api.Jobs;

/// <summary>
/// Hangfire serialises job arguments into its storage (SQL Server tables, dashboard views). Passing
/// an <c>LlmTask</c> directly would persist raw customer names and phone numbers there indefinitely.
/// Instead, payloads are encrypted with ASP.NET Core Data Protection and Hangfire only sees an id.
/// The ciphertext goes to an <see cref="IExpiringBlobStore"/> (in-memory or database), so the
/// store itself never holds plaintext. Payloads are deleted once processed and results expire
/// after <see cref="ResultTtl"/> (DPDP s.8(7)).
/// </summary>
public sealed class ProtectedPayloadStore(IDataProtectionProvider provider, IExpiringBlobStore blobs, TimeProvider clock)
{
    public static readonly TimeSpan ResultTtl = TimeSpan.FromHours(24);

    private readonly IDataProtector _protector = provider.CreateProtector("PrivacyLlm.Payloads.v1");

    public string Put<T>(T value, TimeSpan ttl, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        blobs.Put(id, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value)), clock.GetUtcNow() + ttl);
        return id;
    }

    public T? Get<T>(string id)
    {
        var item = blobs.Get(id);
        if (item is null) return default;
        if (item.ExpiresAt <= clock.GetUtcNow())
        {
            blobs.Delete(id);
            return default;
        }

        return JsonSerializer.Deserialize<T>(_protector.Unprotect(item.Data));
    }

    public void Delete(string id) => blobs.Delete(id);

    public int PurgeExpired() => blobs.PurgeExpired(clock.GetUtcNow());
}
