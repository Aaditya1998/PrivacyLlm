namespace PrivacyLlm.Core.Classification;

public interface IDataClassifier
{
    ClassificationResult Classify(string text, IEnumerable<string>? declaredFields = null);
}

/// <summary>
/// Combines content scanning with schema hints. Callers that know a payload carries customer
/// fields (e.g. a Hangfire job built from an orders table) declare them, and that alone is
/// enough to classify the payload as personal, regardless of what the regexes find.
/// </summary>
public sealed class DataClassifier(IPiiDetector detector) : IDataClassifier
{
    private static readonly HashSet<PiiType> HighRisk = new() { PiiType.Aadhaar, PiiType.Pan, PiiType.BankAccount, PiiType.UpiId, PiiType.Ifsc };

    private static readonly string[] PersonalFieldHints =
    {
        "name", "phone", "mobile", "email", "address", "pincode", "pin_code", "zip", "dob", "birth", "customer", "consignee", "contact",
    };

    private static readonly string[] HighRiskFieldHints = { "aadhaar", "aadhar", "uid", "pan", "account", "ifsc", "upi", "card" };

    public ClassificationResult Classify(string text, IEnumerable<string>? declaredFields = null)
    {
        var matches = detector.Detect(text);
        var reasons = new List<string>();
        var sensitivity = DataSensitivity.Public;

        foreach (var field in declaredFields ?? Enumerable.Empty<string>())
        {
            var f = field.ToLowerInvariant();
            if (HighRiskFieldHints.Any(f.Contains))
            {
                sensitivity = DataSensitivity.HighRiskPersonal;
                reasons.Add($"declared field '{field}' is high-risk personal data");
            }
            else if (PersonalFieldHints.Any(f.Contains))
            {
                sensitivity = Max(sensitivity, DataSensitivity.Personal);
                reasons.Add($"declared field '{field}' is personal data");
            }
        }

        foreach (var group in matches.GroupBy(m => m.Type))
        {
            var level = HighRisk.Contains(group.Key) ? DataSensitivity.HighRiskPersonal : DataSensitivity.Personal;
            sensitivity = Max(sensitivity, level);
            reasons.Add($"detected {group.Count()} x {group.Key}");
        }

        if (reasons.Count == 0) reasons.Add("no personal data detected");
        return new ClassificationResult(sensitivity, matches, reasons);
    }

    private static DataSensitivity Max(DataSensitivity a, DataSensitivity b) => a > b ? a : b;
}
