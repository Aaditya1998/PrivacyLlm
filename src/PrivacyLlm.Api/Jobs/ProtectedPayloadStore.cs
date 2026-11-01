using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace PrivacyLlm.Api.Jobs;

/// <summary>
/// Hangfire serialises job arguments into its storage (SQL Server tables, dashboard views). Passing
/// an <c>LlmTask</c> directly would persist raw customer names and phone numbers there indefinitely.
/// Instead, payloads are encrypted with ASP.NET Core Data Protection and Hangfire only sees an id.
/// Payloads are deleted once processed and results expire after <see cref="ResultTtl"/> (DPDP s.8(7)).
/// </summary>
public sealed class ProtectedPayloadStore(IDataProtectionProvider provider, TimeProvider clock)
{
    public static readonly TimeSpan ResultTtl = TimeSpan.FromHours(24);

    private readonly IDataProtector _protector = provider.CreateProtector("PrivacyLlm.Payloads.v1");
    private readonly ConcurrentDictionary<string, (byte[] Cipher, DateTimeOffset ExpiresAt)> _items = new();

    public string Put<T>(T value, TimeSpan ttl, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        _items[id] = (_protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value)), clock.GetUtcNow() + ttl);
        return id;
    }

    public T? Get<T>(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return default;
        if (item.ExpiresAt <= clock.GetUtcNow())
        {
            _items.TryRemove(id, out _);
            return default;
        }

        return JsonSerializer.Deserialize<T>(_protector.Unprotect(item.Cipher));
    }

    public void Delete(string id) => _items.TryRemove(id, out _);

    public int PurgeExpired()
    {
        var now = clock.GetUtcNow();
        var removed = 0;
        foreach (var (key, item) in _items)
            if (item.ExpiresAt <= now && _items.TryRemove(key, out _)) removed++;
        return removed;
    }
}
