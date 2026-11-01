using System.Collections.Concurrent;

namespace PrivacyLlm.Core.Dpdp;

/// <summary>Lawful basis under the DPDP Act 2023.</summary>
public enum LawfulBasis
{
    /// <summary>s.6: free, specific, informed, unconditional and unambiguous consent for a stated purpose.</summary>
    Consent,

    /// <summary>s.7(a): data voluntarily provided for a specified purpose where the principal has not objected (e.g. a delivery address for that delivery).</summary>
    VoluntarilyProvided,
}

public sealed record ConsentRecord(
    string DataPrincipalId,
    string Purpose,
    LawfulBasis Basis,
    DateTimeOffset GrantedAt,
    DateTimeOffset? WithdrawnAt = null,
    string? ConsentManagerRef = null)
{
    public bool IsActive => WithdrawnAt is null;
}

public interface IConsentRegistry
{
    ConsentRecord Grant(string dataPrincipalId, string purpose, LawfulBasis basis, string? consentManagerRef = null);

    /// <summary>s.6(4): withdrawal must be as easy as giving consent; processing stops thereafter.</summary>
    bool Withdraw(string dataPrincipalId, string purpose);

    ConsentRecord? Find(string dataPrincipalId, string purpose);
    IReadOnlyList<ConsentRecord> ForPrincipal(string dataPrincipalId);
}

/// <summary>In-memory registry for development. Production would back this with a DB or a registered Consent Manager.</summary>
public sealed class InMemoryConsentRegistry(TimeProvider clock) : IConsentRegistry
{
    private readonly ConcurrentDictionary<(string, string), ConsentRecord> _records = new();

    public ConsentRecord Grant(string dataPrincipalId, string purpose, LawfulBasis basis, string? consentManagerRef = null)
    {
        var record = new ConsentRecord(dataPrincipalId, Normalize(purpose), basis, clock.GetUtcNow(), null, consentManagerRef);
        _records[(dataPrincipalId, Normalize(purpose))] = record;
        return record;
    }

    public bool Withdraw(string dataPrincipalId, string purpose)
    {
        var key = (dataPrincipalId, Normalize(purpose));
        if (!_records.TryGetValue(key, out var r) || !r.IsActive) return false;
        _records[key] = r with { WithdrawnAt = clock.GetUtcNow() };
        return true;
    }

    public ConsentRecord? Find(string dataPrincipalId, string purpose) =>
        _records.TryGetValue((dataPrincipalId, Normalize(purpose)), out var r) ? r : null;

    public IReadOnlyList<ConsentRecord> ForPrincipal(string dataPrincipalId) =>
        _records.Values.Where(r => r.DataPrincipalId == dataPrincipalId).OrderBy(r => r.GrantedAt).ToList();

    private static string Normalize(string purpose) => purpose.Trim().ToLowerInvariant();
}
