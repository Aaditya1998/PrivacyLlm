using System.Text.RegularExpressions;

namespace PrivacyLlm.Core.Classification;

public interface IPiiDetector
{
    IReadOnlyList<PiiMatch> Detect(string text);
}

/// <summary>
/// Deterministic, India-focused PII detector. It is deliberately recall-biased: a false positive
/// only costs us a self-hosted call instead of a frontier call, a false negative leaks data.
/// </summary>
public sealed partial class RegexPiiDetector : IPiiDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailRegex();

    // UPI VPA: handle@bank, no TLD (e.g. ravi.k@okaxis, 9876543210@ybl)
    [GeneratedRegex(@"\b[A-Za-z0-9.\-_]{2,256}@(?:ok[a-z]+|ybl|ibl|axl|paytm|upi|apl|icici|sbi|hdfcbank|axisbank|kotak|yesbank|fbl|ptyes|ptaxis|pthdfc|ptsbi)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex UpiRegex();

    // Indian mobile: optional +91 / 91 / 0 prefix, starts 6-9, 10 digits, tolerant of spaces/dashes.
    [GeneratedRegex(@"(?<![\d])(?:\+?91[\s\-]?|0)?[6-9]\d{4}[\s\-]?\d{5}(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex MobileRegex();

    // Landline with STD code, e.g. 022-2345 6789, 080 23456789
    [GeneratedRegex(@"(?<![\d])0\d{2,4}[\s\-]\d{3,4}[\s\-]?\d{4}(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex LandlineRegex();

    [GeneratedRegex(@"(?<![\d])[2-9]\d{3}[\s\-]?\d{4}[\s\-]?\d{4}(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex AadhaarRegex();

    [GeneratedRegex(@"\b[A-Z]{3}[ABCFGHLJPT][A-Z]\d{4}[A-Z]\b", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PanRegex();

    [GeneratedRegex(@"\b[A-Z]{4}0[A-Z0-9]{6}\b", RegexOptions.CultureInvariant, 250)]
    private static partial Regex IfscRegex();

    [GeneratedRegex(@"(?:a/?c|account|acct)\s*(?:no\.?|number|#)?\s*[:\-]?\s*(\d{9,18})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex BankAccountRegex();

    [GeneratedRegex(@"(?<![\d])[1-9]\d{2}\s?\d{3}(?![\d])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PincodeRegex();

    [GeneratedRegex(@"\b(?:dob|d\.o\.b\.?|date of birth|born on)\s*[:\-]?\s*(\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex DobRegex();

    // Honorific / label-introduced names: "Mr. Ravi Kumar", "Smt Lakshmi", "Name: Priya Sharma", "customer Arjun Mehta"
    // Labels are case-insensitive; the name itself must be Title Case so "at the gate" is not captured.
    [GeneratedRegex(@"(?:\b(?i:name|customer|consignee|recipient|deliver to|attn)\s*[:\-]?\s*(?:(?i:Mr|Mrs|Ms|Miss|Dr|Shri|Sri|Smt|Kumari|Km)\.?\s+)?|\b(?i:Mr|Mrs|Ms|Miss|Dr|Shri|Sri|Smt|Kumari|Km)\.?\s+)((?:[A-Z][a-z]+)(?:\s+[A-Z][a-z]+){0,3})", RegexOptions.CultureInvariant, 250)]
    private static partial Regex NameRegex();

    // Address lines: house/flat markers or typical Indian locality suffixes.
    [GeneratedRegex(@"(?:\b(?:flat|house|h\.?\s?no|plot|door|shop|block|wing|floor)\b[^\n,]{0,40}|\b[\w.\-/#]+\s+(?:[A-Z][a-z]+\s+){0,3}(?:road|rd|street|st|marg|nagar|colony|layout|sector|lane|gali|chowk|apartments?|apts?|society|enclave|vihar|puram|halli|pet|ganj|bazaar|cross|main)\b[^\n]{0,60})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex AddressRegex();

    private static readonly HashSet<string> NameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "service", "support", "care", "team", "the", "is", "has", "wants", "said", "complained", "called", "id", "number",
    };

    public IReadOnlyList<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<PiiMatch>();

        var matches = new List<PiiMatch>();
        try
        {
            // Order matters: stronger, more specific patterns first so overlap resolution keeps them.
            Add(matches, EmailRegex(), text, PiiType.Email, 0.99);
            Add(matches, UpiRegex(), text, PiiType.UpiId, 0.95);
            Add(matches, PanRegex(), text, PiiType.Pan, 0.97);
            Add(matches, IfscRegex(), text, PiiType.Ifsc, 0.9);

            foreach (Match m in AadhaarRegex().Matches(text))
            {
                var digits = Digits(m.Value);
                if (Verhoeff.Validate(digits)) matches.Add(new(PiiType.Aadhaar, m.Index, m.Length, m.Value, 0.98));
            }

            AddGroup(matches, BankAccountRegex(), text, PiiType.BankAccount, 0.9);
            Add(matches, MobileRegex(), text, PiiType.Phone, 0.95);
            Add(matches, LandlineRegex(), text, PiiType.Phone, 0.8);
            AddGroup(matches, DobRegex(), text, PiiType.DateOfBirth, 0.9);

            foreach (Match m in NameRegex().Matches(text))
            {
                var g = m.Groups[1];
                var name = TrimStopWords(g.Value);
                if (name.Length >= 2) matches.Add(new(PiiType.PersonName, g.Index, name.Length, name, 0.75));
            }

            Add(matches, PincodeRegex(), text, PiiType.Pincode, 0.6);
            Add(matches, AddressRegex(), text, PiiType.Address, 0.7);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: if we cannot finish scanning, treat the whole text as personal data.
            return new[] { new PiiMatch(PiiType.Address, 0, text.Length, text, 0.0) };
        }

        return ResolveOverlaps(matches);
    }

    private static void Add(List<PiiMatch> list, Regex regex, string text, PiiType type, double confidence)
    {
        foreach (Match m in regex.Matches(text))
            list.Add(new(type, m.Index, m.Length, m.Value.TrimEnd(), confidence));
    }

    private static void AddGroup(List<PiiMatch> list, Regex regex, string text, PiiType type, double confidence)
    {
        foreach (Match m in regex.Matches(text))
        {
            var g = m.Groups[1];
            list.Add(new(type, g.Index, g.Length, g.Value, confidence));
        }
    }

    private static string TrimStopWords(string candidate)
    {
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => !NameStopWords.Contains(w));
        return string.Join(' ', words);
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>
    /// Keeps the earliest-added (most specific) match when spans overlap. Address spans are the
    /// exception: they often contain a phone or pincode, so the uncovered fragments are kept as
    /// separate Address matches rather than discarding the rest of the address.
    /// </summary>
    private static IReadOnlyList<PiiMatch> ResolveOverlaps(List<PiiMatch> matches)
    {
        var kept = new List<PiiMatch>();
        foreach (var m in matches)
        {
            var overlaps = kept.Where(k => m.Start < k.End && k.Start < m.End).OrderBy(k => k.Start).ToList();
            if (overlaps.Count == 0)
            {
                kept.Add(m);
                continue;
            }

            if (m.Type != PiiType.Address) continue;

            var cursor = m.Start;
            foreach (var k in overlaps.Append(new PiiMatch(PiiType.Address, m.End, 0, "", 0)))
            {
                var fragment = m.Value.Substring(Math.Min(cursor - m.Start, m.Value.Length), Math.Max(0, Math.Min(k.Start, m.End) - cursor));
                var trimmed = fragment.Trim(' ', ',', '-', '.');
                if (trimmed.Length >= 4)
                    kept.Add(m with { Start = cursor + fragment.IndexOf(trimmed, StringComparison.Ordinal), Length = trimmed.Length, Value = trimmed });
                cursor = Math.Max(cursor, k.End);
            }
        }

        return kept.OrderBy(m => m.Start).ToList();
    }
}
