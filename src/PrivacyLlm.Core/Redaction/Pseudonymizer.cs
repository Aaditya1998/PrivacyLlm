using System.Text;
using PrivacyLlm.Core.Classification;

namespace PrivacyLlm.Core.Redaction;

/// <summary>
/// Request-scoped token vault. Personal values are replaced with stable placeholders such as
/// <c>[[PHONE_1]]</c>; the mapping never leaves process memory and is discarded with the request.
/// </summary>
public sealed class PseudonymVault
{
    private readonly Dictionary<string, string> _tokenToValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _valueToToken = new(StringComparer.Ordinal);
    private readonly Dictionary<PiiType, int> _counters = new();

    public int Count => _tokenToValue.Count;

    public string TokenFor(PiiType type, string value)
    {
        if (_valueToToken.TryGetValue(value, out var existing)) return existing;
        _counters[type] = _counters.GetValueOrDefault(type) + 1;
        var token = $"[[{Label(type)}_{_counters[type]}]]";
        _tokenToValue[token] = value;
        _valueToToken[value] = token;
        return token;
    }

    public string Rehydrate(string text)
    {
        if (_tokenToValue.Count == 0 || string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text);
        foreach (var (token, value) in _tokenToValue) sb.Replace(token, value);
        return sb.ToString();
    }

    private static string Label(PiiType type) => type switch
    {
        PiiType.PersonName => "NAME",
        PiiType.Phone => "PHONE",
        PiiType.Email => "EMAIL",
        PiiType.Address => "ADDRESS",
        PiiType.Pincode => "PINCODE",
        PiiType.Aadhaar => "AADHAAR",
        PiiType.Pan => "PAN",
        PiiType.BankAccount => "ACCOUNT",
        PiiType.Ifsc => "IFSC",
        PiiType.UpiId => "UPI",
        PiiType.DateOfBirth => "DOB",
        _ => "PII",
    };
}

public static class Pseudonymizer
{
    public static string Apply(string text, IReadOnlyList<PiiMatch> matches, PseudonymVault vault)
    {
        if (matches.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var m in matches.OrderBy(m => m.Start))
        {
            if (m.Start < cursor) continue;
            sb.Append(text, cursor, m.Start - cursor);
            sb.Append(vault.TokenFor(m.Type, text.Substring(m.Start, m.Length)));
            cursor = m.End;
        }

        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }
}
