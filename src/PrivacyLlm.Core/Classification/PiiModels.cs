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

public sealed record PiiMatch(PiiType Type, int Start, int Length, string Value, double Confidence)
{
    public int End => Start + Length;
}

public sealed record ClassificationResult(DataSensitivity Sensitivity, IReadOnlyList<PiiMatch> Matches, IReadOnlyList<string> Reasons)
{
    public bool ContainsPersonalData => Sensitivity >= DataSensitivity.Personal;

    public IReadOnlyDictionary<PiiType, int> CountsByType =>
        Matches.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());
}
