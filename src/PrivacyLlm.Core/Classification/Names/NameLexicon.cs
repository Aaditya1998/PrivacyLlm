using System.Reflection;

namespace PrivacyLlm.Core.Classification.Names;

/// <summary>Why a name needs a context cue before it is treated as a person.</summary>
public enum AmbiguityReason
{
    English,
    HindiWord,
    Deity,
    Place,
    Brand,
}

/// <summary>
/// Curated word lists embedded in the assembly (see Data/SOURCES.md). Loaded once; the constructor
/// throws if a list is missing or suspiciously small, so a broken build cannot silently stop
/// detecting names and let them through to the frontier tier.
/// </summary>
public sealed class NameLexicon
{
    private const string Prefix = "PrivacyLlm.Core.Classification.Names.Data.";

    public IReadOnlySet<string> GivenNames { get; }
    public IReadOnlySet<string> Surnames { get; }
    public IReadOnlySet<string> CommonWords { get; }
    public IReadOnlyDictionary<string, AmbiguityReason> Ambiguous { get; }

    /// <summary>Lowercase multi-word phrases (place, festival, brand...) whose name-like words are not people.</summary>
    public IReadOnlyList<string[]> ExclusionPhrases { get; }

    /// <summary>Single-word Indian places (cities, states, localities, rivers). Multi-word places are added to <see cref="ExclusionPhrases"/>.</summary>
    public IReadOnlySet<string> Places { get; }

    private readonly HashSet<string> _givenFolded;
    private readonly HashSet<string> _surnamesFolded;

    public NameLexicon()
    {
        var given = Load("given_names.txt", min: 1500);
        var surnames = Load("surnames.txt", min: 500);
        GivenNames = given.ToHashSet(StringComparer.Ordinal);
        Surnames = surnames.ToHashSet(StringComparer.Ordinal);
        CommonWords = Load("common_words.txt", min: 2000).ToHashSet(StringComparer.Ordinal);

        var ambiguous = new Dictionary<string, AmbiguityReason>(StringComparer.Ordinal);
        foreach (var line in Load("ambiguous.txt", min: 200))
        {
            var parts = line.Split('|');
            if (parts.Length != 2 || !TryParseReason(parts[1], out var reason))
                throw new InvalidDataException($"ambiguous.txt: bad line '{line}'");
            ambiguous[parts[0]] = reason;
        }

        var places = Load("places.txt", min: 500);
        Places = places.Where(p => !p.Contains(' ')).ToHashSet(StringComparer.Ordinal);

        // A name that is also a well-known place (Dwarka, Somnath) needs a context cue, like any ambiguous word.
        foreach (var p in Places)
            if (!ambiguous.ContainsKey(p) && (GivenNames.Contains(p) || Surnames.Contains(p))) ambiguous[p] = AmbiguityReason.Place;

        Ambiguous = ambiguous;
        ExclusionPhrases = Load("exclusion_phrases.txt", min: 300)
            .Concat(places.Where(p => p.Contains(' ')))
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .ToList();

        _givenFolded = given.Select(Transliteration.Fold).ToHashSet(StringComparer.Ordinal);
        _surnamesFolded = surnames.Select(Transliteration.Fold).ToHashSet(StringComparer.Ordinal);
    }

    public bool IsGivenName(string lower, string folded) => GivenNames.Contains(lower) || _givenFolded.Contains(folded);
    public bool IsSurname(string lower, string folded) => Surnames.Contains(lower) || _surnamesFolded.Contains(folded);

    /// <summary>True when the word itself is listed, not just a spelling variant of a listed name.</summary>
    public bool IsExactName(string lower) => GivenNames.Contains(lower) || Surnames.Contains(lower);

    private static string[] Load(string file, int min)
    {
        using var stream = typeof(NameLexicon).Assembly.GetManifestResourceStream(Prefix + file)
            ?? throw new InvalidDataException($"Embedded name list '{file}' is missing.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd()
            .Split('\n')
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length < min)
            throw new InvalidDataException($"Embedded name list '{file}' has {lines.Length} entries; expected at least {min}.");
        return lines;
    }

    private static bool TryParseReason(string s, out AmbiguityReason reason)
    {
        reason = s.Trim() switch
        {
            "english" => AmbiguityReason.English,
            "hindi-word" => AmbiguityReason.HindiWord,
            "deity" => AmbiguityReason.Deity,
            "place" => AmbiguityReason.Place,
            "brand" => AmbiguityReason.Brand,
            _ => (AmbiguityReason)(-1),
        };
        return Enum.IsDefined(reason);
    }
}
