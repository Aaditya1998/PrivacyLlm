namespace PrivacyLlm.Core.Classification;

/// <summary>Kinds of personal data the pipeline recognises (DPDP Act 2023, s.2(t) "personal data").</summary>
public enum PiiType
{
    PersonName,
    Phone,
    Email,
    Address,
    Pincode,
    Aadhaar,
    Pan,
    BankAccount,
    Ifsc,
    UpiId,
    DateOfBirth,
}

/// <summary>
/// Risk tier used for routing. DPDP does not define a "sensitive" class, but financial and
/// government identifiers carry higher breach penalties in practice, so we keep them apart.
/// </summary>
public enum DataSensitivity
{
    Public = 0,
    Personal = 1,
    HighRiskPersonal = 2,
}

/// <param name="Tentative">
/// A possible match (e.g. a word that may or may not be a person's name). Tentative matches keep the
/// text off the frontier tier and are still pseudonymised, but do not count as personal data for
/// the consent check, so an uncertain guess never rejects a task.
/// </param>
/// <param name="Source">Which detector produced the match: "Regex" or "Name".</param>
public sealed record PiiMatch(PiiType Type, int Start, int Length, string Value, double Confidence, bool Tentative = false, string Source = "Regex")
{
    public int End => Start + Length;
}

public sealed record ClassificationResult(
    DataSensitivity Sensitivity,
    IReadOnlyList<PiiMatch> Matches,
    IReadOnlyList<string> Reasons,
    bool SuspectedPersonal = false,
    double MaxNameScore = 0)
{
    /// <summary>Definite personal data: requires a lawful basis and the self-hosted tier.</summary>
    public bool ContainsPersonalData => Sensitivity >= DataSensitivity.Personal;

    /// <summary>Definite or possible personal data: either way it must not reach the frontier tier unmasked.</summary>
    public bool MayContainPersonalData => ContainsPersonalData || SuspectedPersonal;

    public IReadOnlyDictionary<PiiType, int> CountsByType =>
        Matches.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// Matches inside [start, start + length), shifted to offsets local to that range and clipped at its edges.
    /// Lets one classification of "system + \n + input" be applied to each part separately.
    /// </summary>
    public IReadOnlyList<PiiMatch> MatchesWithin(int start, int length)
    {
        var end = start + length;
        var result = new List<PiiMatch>();
        foreach (var m in Matches)
        {
            var s = Math.Max(m.Start, start);
            var e = Math.Min(m.End, end);
            if (e <= s) continue;
            var offset = s - m.Start;
            result.Add(m with { Start = s - start, Length = e - s, Value = m.Value.Substring(Math.Min(offset, m.Value.Length), Math.Min(e - s, Math.Max(0, m.Value.Length - offset))) });
        }

        return result;
    }
}
