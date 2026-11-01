using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PrivacyLlm.Core.Classification.Names;

public sealed record NameToken(int Start, int Length, string Text, string Lower, string Folded)
{
    public int End => Start + Length;

    // Computed once: the detector asks these many times per word.
    public bool IsTitleCase { get; } = Text.Length > 1 && char.IsUpper(Text[0]) && Text.AsSpan(1).IndexOfAnyInRange('A', 'Z') < 0 && !HasUpperBeyondAscii(Text);
    public bool IsLatin { get; } = Text.AsSpan().IndexOfAnyInRange('ɐ', '￿') < 0;
    public bool IsAllUpperWord { get; } = Text.Length > 1 && Text.AsSpan().IndexOfAnyInRange('a', 'z') < 0;

    /// <summary>First word of a sentence or line, where capitalisation says nothing about names.</summary>
    public bool SentenceStart { get; init; }
    public bool LineStart { get; init; }

    /// <summary>Text between the previous token and this one (punctuation, spaces, newlines).</summary>
    public string Before { get; init; } = "";
    public string After { get; internal set; } = "";

    /// <summary>Inside a URL, e-mail, code identifier or a [[TOKEN_1]] placeholder: never a name.</summary>
    public bool Skip { get; init; }

    private static bool HasUpperBeyondAscii(string s)
    {
        for (var i = 1; i < s.Length; i++)
            if (s[i] > 0x7F && char.IsUpper(s[i])) return true;
        return false;
    }
}

public static partial class NameTokenizer
{
    [GeneratedRegex(@"\p{L}[\p{L}\p{M}]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Chunk();

    [GeneratedRegex(@"\[\[[A-Z]+_\d+\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static IReadOnlyList<NameToken> Tokenize(string text)
    {
        // Mark characters inside technical chunks (URLs, e-mails, code) and [[TOKEN_1]] placeholders.
        var skip = new bool[text.Length];
        foreach (Match c in Chunk().Matches(text))
            if (IsTechnical(c.ValueSpan)) Array.Fill(skip, true, c.Index, c.Length);
        foreach (Match p in Placeholder().Matches(text))
            Array.Fill(skip, true, p.Index, p.Length);

        var tokens = new List<NameToken>();
        var prevEnd = 0;
        foreach (Match m in Word().Matches(text))
        {
            var before = text[prevEnd..m.Index];
            var lineStart = tokens.Count == 0 || before.Contains('\n');
            var sentenceStart = lineStart || before.AsSpan().IndexOfAny(".!?") >= 0 && !IsInitial(tokens[^1], before);
            var lower = StripMarks(m.Value).ToLowerInvariant();
            if (tokens.Count > 0) tokens[^1].After = before;
            tokens.Add(new NameToken(m.Index, m.Length, m.Value, lower, Transliteration.Fold(lower))
            {
                Before = before,
                LineStart = lineStart,
                SentenceStart = sentenceStart,
                Skip = skip[m.Index],
            });
            prevEnd = m.Index + m.Length;
        }

        if (tokens.Count > 0) tokens[^1].After = text[prevEnd..];
        return tokens;
    }

    /// <summary>Capitalisation is evidence only in mixed-case text, not in ALL-CAPS SMS or all-lowercase chat.</summary>
    public static bool CasingInformative(string text)
    {
        int upper = 0, lower = 0;
        foreach (var c in text)
        {
            if (char.IsUpper(c)) upper++;
            else if (char.IsLower(c)) lower++;
        }

        // Ordinary sentences are mostly lowercase with a few capitals, which is exactly when capitals mean something.
        var letters = upper + lower;
        return upper > 0 && lower > 0 && upper / (double)letters < 0.6;
    }

    private static bool IsInitial(NameToken prev, string between) => prev.Text.Length == 1 && char.IsUpper(prev.Text[0]) && between.TrimEnd() == ".";

    /// <summary>
    /// Only chunks that really are URLs, e-mails, paths or code. Names glued to punctuation in ordinary
    /// writing ("Mr.Ravi", "R.Suresh", "@Priya", "Ravi/Sunita", "Ravi_Kumar") must still be scored:
    /// skipping them would send the name to the frontier unmasked.
    /// </summary>
    private static bool IsTechnical(ReadOnlySpan<char> chunk)
    {
        if (chunk.IndexOf("://") >= 0 || chunk.StartsWith("www.") || chunk.IndexOfAny("=\\{<") >= 0 || chunk.IndexOf("()") >= 0) return true;

        // e-mail: text@domain.tld (a leading "@Priya" is a mention)
        var at = chunk.IndexOf('@');
        if (at > 0 && chunk[(at + 1)..].IndexOf('.') > 0) return true;

        // paths: two or more slashes, or a leading slash ("Ravi/Sunita" is two names)
        if (chunk.Count('/') >= 2 || chunk.StartsWith("/")) return true;

        // snake_case identifiers are lowercase ("user_id"); "Ravi_Kumar" is a name
        if (chunk.IndexOf('_') >= 0 && !ContainsUpper(chunk)) return true;

        // file.ext / domain.com: a dot followed by 2-4 lowercase letters at the end ("Mr.Ravi", "R.Suresh" stay words)
        var dot = chunk.TrimEnd(".,;:!?)").LastIndexOf('.');
        if (dot > 0 && char.IsLetter(chunk[dot - 1]))
        {
            var ext = chunk[(dot + 1)..].TrimEnd(".,;:!?)");
            if (ext.Length is >= 2 and <= 4 && !ContainsUpper(ext) && ext.IndexOfAnyExceptInRange('a', 'z') < 0) return true;
        }

        // camelCase identifiers start lowercase ("getRaviOrders", "iPhone"); "McDonald" starts uppercase
        if (chunk.Length > 1 && char.IsLower(chunk[0]))
            for (var i = 1; i < chunk.Length; i++)
                if (char.IsLower(chunk[i - 1]) && char.IsUpper(chunk[i])) return true;

        return false;
    }

    private static bool ContainsUpper(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (char.IsUpper(c)) return true;
        return false;
    }

    private static string StripMarks(string s)
    {
        if (Ascii.IsValid(s)) return s; // the common case: nothing to strip
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark || c >= 0x900) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
