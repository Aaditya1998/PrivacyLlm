using System.Text;

namespace PrivacyLlm.Core.Classification.Names;

/// <summary>
/// Conservative folding of romanised Indian names so common spelling variants share one key:
/// Preethi/Preeti, Shiva/Siva, Priyaa/Priya, Vikkram/Vikram, Mohammed/Mohamed.
/// Used only for gazetteer lookups. Common-word checks use the unfolded word, so folding can
/// never turn an ordinary word into a name.
/// </summary>
public static class Transliteration
{
    /// <summary>
    /// Single pass, applied left to right: aa→a, ee/ii→i, oo/uu→u, w→v, and an aspirate h after
    /// t/d/s/p/k/g/b is dropped (th→t, sh→s, ph→p...); then doubled letters collapse and a final h is dropped.
    /// </summary>
    public static string Fold(string lower)
    {
        var sb = new StringBuilder(lower.Length);
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            var next = i + 1 < lower.Length ? lower[i + 1] : '\0';

            if (c == 'e' && next == 'e' || c == 'i' && next == 'i') { Append(sb, 'i'); i++; continue; }
            if (c == 'o' && next == 'o' || c == 'u' && next == 'u') { Append(sb, 'u'); i++; continue; }
            if (c == 'a' && next == 'a') { Append(sb, 'a'); i++; continue; }
            if (next == 'h' && c is 't' or 'd' or 's' or 'p' or 'k' or 'g' or 'b') { Append(sb, c == 'p' ? 'f' : c); i++; continue; }
            Append(sb, c == 'w' ? 'v' : c);
        }

        if (sb.Length > 3 && sb[^1] == 'h') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Collapses doubled consonants (vikkram → vikram); vowels were already merged above.</summary>
    private static void Append(StringBuilder sb, char c)
    {
        if (sb.Length > 0 && sb[^1] == c && c is not ('a' or 'e' or 'i' or 'o' or 'u')) return;
        sb.Append(c);
    }
}
