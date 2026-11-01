using System.Collections.Concurrent;

namespace PrivacyLlm.Core.Storage;

public sealed record StoredBlob(byte[] Data, DateTimeOffset ExpiresAt);

/// <summary>
/// Opaque bytes with an expiry. Callers encrypt before storing, so implementations never see
/// plaintext personal data and can live in a shared database.
/// </summary>
public interface IExpiringBlobStore
{
    /// <summary>Insert or replace.</summary>
    void Put(string id, byte[] data, DateTimeOffset expiresAt);

    StoredBlob? Get(string id);
    void Delete(string id);
    int PurgeExpired(DateTimeOffset now);
}

public sealed class InMemoryBlobStore : IExpiringBlobStore
{
    private readonly ConcurrentDictionary<string, StoredBlob> _items = new();

    public void Put(string id, byte[] data, DateTimeOffset expiresAt) => _items[id] = new StoredBlob(data, expiresAt);

    public StoredBlob? Get(string id) => _items.TryGetValue(id, out var b) ? b : null;

    public void Delete(string id) => _items.TryRemove(id, out _);

    public int PurgeExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (key, item) in _items)
            if (item.ExpiresAt <= now && _items.TryRemove(key, out _)) removed++;
        return removed;
    }
}
