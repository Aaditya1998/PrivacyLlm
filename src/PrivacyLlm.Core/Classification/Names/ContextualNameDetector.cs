namespace PrivacyLlm.Core.Classification.Names;

public sealed class NameScoringOptions
{
    public const string Section = "NameScoring";

    /// <summary>Score at or above which a word is treated as a possible name (text stays off the frontier tier).</summary>
    public double LowThreshold { get; set; } = 0.30;

    /// <summary>Score at or above which a word is treated as a definite name (personal data, consent required).</summary>
    public double HighThreshold { get; set; } = 0.70;

    /// <summary>Text in Indic scripts is not covered by the Latin-script lists, so treat it as a possible name.</summary>
    public bool TreatIndicScriptAsUncertain { get; set; } = true;

    /// <summary>Clamped so the bands cannot be configured into uselessness; there is deliberately no "off" switch.</summary>
    public void Validate()
    {
        if (LowThreshold is < 0.2 or > 0.5) throw new ArgumentOutOfRangeException(nameof(LowThreshold), LowThreshold, "must be between 0.2 and 0.5");
        if (HighThreshold is < 0.5 or > 0.85) throw new ArgumentOutOfRangeException(nameof(HighThreshold), HighThreshold, "must be between 0.5 and 0.85");
        if (LowThreshold >= HighThreshold) throw new ArgumentException("LowThreshold must be below HighThreshold");
    }
}

/// <summary>
/// Finds person names that have no label in front of them ("Ravi said...", "Hi Priya,", "Anil bhai ka
/// order"), which the regex detector cannot see. Each word gets evidence from curated name lists and
/// from its context, and the score puts it in one of three bands:
/// definite (personal data), possible (kept on the self-hosted tier) or none.
/// Deterministic and in-process: no model, no network, nothing leaves the machine.
/// </summary>
public sealed class ContextualNameDetector : IPiiDetector
{
    private readonly NameLexicon _lexicon;
    private readonly NameScoringOptions _options;
    private readonly Dictionary<string, List<string[]>> _phrasesByFirstWord;

    public ContextualNameDetector(NameLexicon lexicon, NameScoringOptions options)
    {
        options.Validate();
        _lexicon = lexicon;
        _options = options;
        _phrasesByFirstWord = lexicon.ExclusionPhrases
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Length).ToList(), StringComparer.Ordinal);
    }

    private enum Kind { None, Given, Surname, Ambiguous, Unknown }

    private sealed class Scored
    {
        public Kind Kind;
        public double Score;

        /// <summary>Part of an exclusion phrase or a deity reference: never a name, even by propagation.</summary>
        public bool Suppressed;

        /// <summary>Followed by a place/brand word ("Ravi Road"); a clearly named "Ravi" elsewhere still propagates here.</summary>
        public bool SuffixSuppressed;
    }

    public IReadOnlyList<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<PiiMatch>();

        var tokens = NameTokenizer.Tokenize(text);
        var casing = NameTokenizer.CasingInformative(text);
        var excluded = ExcludedByPhrase(tokens);
        var scored = new Scored[tokens.Count];

        for (var i = 0; i < tokens.Count; i++)
            scored[i] = Score(tokens, i, casing, excluded[i]);

        // Name propagation: once "Ravi" is clearly a name, a later bare "ravi" is the same person.
        var bestByKey = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
            if (scored[i].Score >= _options.LowThreshold && scored[i].Kind != Kind.None)
                bestByKey[tokens[i].Folded] = Math.Max(bestByKey.GetValueOrDefault(tokens[i].Folded), scored[i].Score);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (scored[i].Suppressed || tokens[i].Skip || scored[i].Kind == Kind.None || !bestByKey.TryGetValue(tokens[i].Folded, out var best)) continue;
            if (scored[i].SuffixSuppressed && best < _options.HighThreshold) continue;
            scored[i].Score = Math.Max(scored[i].Score, best);
        }

        var matches = new List<PiiMatch>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (scored[i].Score < _options.LowThreshold) continue;

            // Merge adjacent names into one span: "Kavita Rao", "Mohd Irfan Ansari" (at most 4 words).
            var j = i;
            var score = scored[i].Score;
            while (j + 1 < tokens.Count && j - i < 3 && scored[j + 1].Score >= _options.LowThreshold && IsSpaceOnly(tokens[j].After))
            {
                j++;
                score = Math.Max(score, scored[j].Score);
            }

            var start = tokens[i].Start;
            var length = tokens[j].End - start;
            matches.Add(new PiiMatch(PiiType.PersonName, start, length, text.Substring(start, length), Math.Round(score, 3),
                Tentative: score < _options.HighThreshold, Source: "Name"));
            i = j;
        }

        return matches;
    }

    private Scored Score(IReadOnlyList<NameToken> tokens, int i, bool casing, bool excluded)
    {
        var t = tokens[i];
        var s = new Scored();
        if (t.Skip) return s;

        if (!t.IsLatin)
        {
            // The word lists are Latin-script, so a word in any other script (Devanagari, Tamil, Urdu...) cannot be
            // checked: treat it as a possible name rather than let it through.
            if (_options.TreatIndicScriptAsUncertain)
            {
                s.Kind = Kind.Unknown;
                s.Score = (_options.LowThreshold + _options.HighThreshold) / 2;
            }

            return s;
        }

        var kind = KindOf(t, out var reason);
        if (kind == Kind.None) return s;
        s.Kind = kind;

        var cues = Cues(tokens, i, casing, out var strongCue, out var kinship, out var adjacent);
        var lowercaseInMixedCase = casing && !char.IsUpper(t.Text[0]);

        // "Ravi will call": "will" is next to a name but is not one. Ambiguous words only get the
        // adjacency bonus when written as a name (capitalised in mixed-case text: "Ravi Will").
        if (adjacent && kind == Kind.Ambiguous && !(casing && t.IsTitleCase)) cues -= NameSignals.AdjacentName;

        // Hard suppression: the word is part of a place, festival, brand or deity reference.
        // Only a kinship word overrides it ("Ganesh bhai" is a person); an honorific does not,
        // because places are often named "Sardar Patel Road" or "Sri Krishna Sweets".
        if (!kinship && (excluded || reason == AmbiguityReason.Deity && i > 0 && NameSignals.DeityPrefixes.Contains(tokens[i - 1].Lower)))
        {
            s.Suppressed = true;
            return s;
        }

        if (!kinship && FollowedByNonPersonSuffix(tokens, i, casing, kind))
        {
            s.SuffixSuppressed = true;
            return s;
        }

        // An ambiguous word written in lowercase inside properly capitalised text is the ordinary word
        // ("there is still some asha"). Score 0 but not Suppressed, so a clear "Asha" elsewhere still propagates.
        if (kind == Kind.Ambiguous && lowercaseInMixedCase && strongCue == 0) return s;

        if (kind == Kind.Unknown)
        {
            // A word in no list is a possible name only when something strongly says so: "Hi Thejaswini,",
            // "Sutapa asked", "Sunaina ko refund", "assign the ticket to Neikhrielie." Kept in the possible band:
            // the cue alone cannot tell a rare name from "Hi Zomato", and possible only means "stay self-hosted".
            var written = !casing || t.IsTitleCase;
            if (written && !LooksInflected(t.Lower) && !_lexicon.Places.Contains(t.Lower)
                && (strongCue > 0 || t.IsTitleCase && (ActsAsSubject(tokens, i) || IsRecipient(tokens, i))))
                s.Score = (_options.LowThreshold + _options.HighThreshold) / 2;
            return s;
        }

        var exact = _lexicon.IsExactName(t.Lower);
        var lexical = kind switch
        {
            Kind.Given => exact ? NameSignals.GivenName : NameSignals.FoldedGivenName,
            Kind.Surname => exact ? NameSignals.Surname : NameSignals.FoldedSurname,
            _ => NameSignals.AmbiguousName,
        };
        if (lowercaseInMixedCase && strongCue == 0) lexical += NameSignals.LowercaseInMixedCase;

        s.Score = Sigmoid(lexical + cues - NameSignals.Offset);
        return s;
    }

    private Kind KindOf(NameToken t, out AmbiguityReason? reason)
    {
        reason = null;

        // Cue words are never names themselves ("the customer said", "Hi team").
        if (IsCueWord(t.Lower)) return Kind.None;

        if (_lexicon.Ambiguous.TryGetValue(t.Lower, out var r))
        {
            reason = r;
            return Kind.Ambiguous;
        }

        var given = _lexicon.IsGivenName(t.Lower, t.Folded);
        var surname = _lexicon.IsSurname(t.Lower, t.Folded);
        var common = _lexicon.CommonWords.Contains(t.Lower);

        if (given || surname)
            return common ? Kind.Ambiguous : given ? Kind.Given : Kind.Surname;

        if (common || t.Text.Length < 3) return Kind.None;
        return Kind.Unknown;
    }

    private static bool IsCueWord(string lower) =>
        NameSignals.KinshipSuffixes.Contains(lower) || NameSignals.HonorificPrefixes.Contains(lower) || NameSignals.Greetings.Contains(lower)
        || NameSignals.SignOffs.Contains(lower) || NameSignals.RelationNouns.Contains(lower) || NameSignals.SpeechVerbs.Contains(lower)
        || NameSignals.InteractionWords.Contains(lower) || NameSignals.HindiPostpositions.Contains(lower)
        || NameSignals.NonPersonSuffixes.Contains(lower) || NameSignals.DeityPrefixes.Contains(lower) || NameSignals.Calendar.Contains(lower);

    /// <summary>"confirms", "delivered", "booking": an inflected common word, not an unknown name.</summary>
    private bool LooksInflected(string lower)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s", "ly", "er", "ers" })
        {
            if (lower.Length <= suffix.Length + 2 || !lower.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var stem = lower[..^suffix.Length];
            if (_lexicon.CommonWords.Contains(stem) || _lexicon.CommonWords.Contains(stem + "e") || IsCueWord(stem)) return true;
        }

        return false;
    }

    private double Cues(IReadOnlyList<NameToken> tokens, int i, bool casing, out double strongCue, out bool kinship, out bool adjacent)
    {
        var t = tokens[i];
        var prev = i > 0 ? tokens[i - 1] : null;
        var prev2 = i > 1 ? tokens[i - 2] : null;
        var next = i + 1 < tokens.Count ? tokens[i + 1] : null;

        double strong = 0, sum = 0;

        kinship = next is not null && NameSignals.KinshipSuffixes.Contains(next.Lower) && IsJoiner(t.After);
        // "Smt Lakshmi", "Dr. Reddy", "Mr.Ravi" (no space after the dot)
        var honorific = prev is not null && NameSignals.HonorificPrefixes.Contains(prev.Lower) && (t.Before == "." || IsJoiner(t.Before.Replace(".", "")));
        if (kinship) strong = Math.Max(strong, NameSignals.KinshipSuffix);
        if (honorific) strong = Math.Max(strong, NameSignals.HonorificPrefix);

        // The look-backs below handle the second word of one name ("Hello Kavita Rao"), so the word must be
        // space-joined to the previous name word: "Hi Ravi, Tata refund..." must not make "Tata" a name.
        var secondWordOfName = IsNameish(prev) && IsSpaceOnly(t.Before);

        // Greeting: "Hi Priya", "Thank you Ravi", "Hello Kavita Rao".
        var nameEnd = next is not null && IsNameish(next) && IsSpaceOnly(t.After) ? i + 1 : i;
        if (IsGreetingBefore(tokens, i, nameEnd) || secondWordOfName && IsGreetingBefore(tokens, i - 1, nameEnd))
            strong = Math.Max(strong, NameSignals.Greeting);

        // Sign-off: "Regards,\nKavita Rao", "Thanks, Anil", "From: Ravi", or a "- Meena" signature.
        if (IsSignOffBefore(tokens, i, nameEnd) || secondWordOfName && IsSignOffBefore(tokens, i - 1, nameEnd) || IsDashSignature(tokens, i))
            strong = Math.Max(strong, NameSignals.SignOff);

        if (prev is not null && NameSignals.RelationNouns.Contains(prev.Lower) && IsSpaceOrColon(t.Before))
            strong = Math.Max(strong, NameSignals.RelationNoun);

        sum += strong;

        adjacent = prev is not null && IsNameish(prev) && IsSpaceOnly(t.Before) || next is not null && IsNameish(next) && IsSpaceOnly(t.After);
        if (adjacent) sum += NameSignals.AdjacentName;

        // "Sneha, Rahul and Ibrahim": listed alongside a clear name. Not for ambiguous words, which a comma
        // after a name does not make a name ("Hi Ravi, Tata refund...").
        if (!adjacent && !_lexicon.Ambiguous.ContainsKey(t.Lower) && !_lexicon.CommonWords.Contains(t.Lower)
            && (prev is not null && IsNameish(prev) && IsListSeparator(t.Before) || next is not null && IsNameish(next) && IsListSeparator(t.After)))
            sum += NameSignals.CoordinatedName;

        var verbAfter = next is not null && IsVerb(next) && IsSpaceOnly(t.After)
            || next is not null && IsNameish(next) && IsSpaceOnly(t.After) && i + 2 < tokens.Count && IsVerb(tokens[i + 2]);
        if (verbAfter) sum += NameSignals.SpeechVerbAfter;

        if (next is not null && next.Lower == "from" && IsSpaceOnly(t.After) && (!casing || t.IsTitleCase))
            sum += NameSignals.FromAfter;

        if (prev is not null && IsSpaceOrComma(t.Before)
            && (NameSignals.InteractionWords.Contains(prev.Lower) || prev.Lower == "to" && prev2 is not null && NameSignals.VerbsBeforeTo.Contains(prev2.Lower))
            || (!casing || t.IsTitleCase) && IsRecipient(tokens, i))
            sum += NameSignals.InteractionBefore;

        if (prev is not null && prev.Text.Length == 1 && char.IsUpper(prev.Text[0]) && prev.Text is not ("A" or "I") && t.Before.Trim() is "" or ".")
            sum += NameSignals.Initial;

        if (next is not null && NameSignals.HindiPostpositions.Contains(next.Lower) && IsSpaceOnly(t.After))
            sum += NameSignals.HindiPostposition;

        if (casing && t.IsTitleCase && !t.SentenceStart)
            sum += NameSignals.TitleCaseMidSentence;

        if (t.LineStart && t.After.StartsWith(','))
            sum += NameSignals.Vocative;

        // "Sagar's laptop" yes, "Haldiram's bhujia" no: brands, places and deities take possessives too.
        if (IsPossessive(t.After) && !(_lexicon.Ambiguous.TryGetValue(t.Lower, out var why) && why is AmbiguityReason.Brand or AmbiguityReason.Place or AmbiguityReason.Deity))
            sum += NameSignals.Possessive;

        strongCue = strong;
        return sum;
    }

    private bool IsNameish(NameToken? t) =>
        t is not null && !t.Skip && t.IsLatin && (_lexicon.IsGivenName(t.Lower, t.Folded) || _lexicon.IsSurname(t.Lower, t.Folded))
        && !_lexicon.CommonWords.Contains(t.Lower) && !_lexicon.Ambiguous.ContainsKey(t.Lower) && !IsCueWord(t.Lower);

    /// <param name="nameEnd">Index of the last word of the name, to check what follows it.</param>
    private static bool IsGreetingBefore(IReadOnlyList<NameToken> tokens, int i, int nameEnd)
    {
        if (i < 1) return false;
        var prev = tokens[i - 1];
        if (!IsSpaceOrComma(tokens[i].Before)) return false;

        var greeting = prev.Lower;
        if (greeting == "you" && i >= 2 && tokens[i - 2].Lower == "thank") greeting = "thank you";
        else if (!NameSignals.Greetings.Contains(greeting) || greeting is "good" or "thank" or "you") return false;

        if (NameSignals.PureGreetings.Contains(greeting)) return true;

        // "Thanks Anil!" / "Good morning Priya," count; "We welcome frank feedback" does not.
        var after = tokens[nameEnd].After.TrimStart(' ');
        return nameEnd + 1 >= tokens.Count && after.Trim().Length <= 1 || after.Length > 0 && after[0] is ',' or '!' or '.' or ':' or '\n' or '\r';
    }

    /// <param name="nameEnd">Index of the last word of the name, to check what follows it.</param>
    private static bool IsSignOffBefore(IReadOnlyList<NameToken> tokens, int i, int nameEnd)
    {
        if (i < 1) return false;
        var prev = tokens[i - 1];
        var between = tokens[i].Before;

        // "sent by Ravi"
        if (prev.Lower == "by") return i >= 2 && tokens[i - 2].Lower == "sent" && IsSpaceOnly(between);

        // "From: Ravi" / "From Ravi" only at the start of a line (an e-mail header or signature), never "from April".
        if (prev.Lower == "from") return prev.LineStart && (IsSpaceOnly(between) || between.Trim() is ":" or ",");

        if (!NameSignals.SignOffs.Contains(prev.Lower)) return false;

        // "Regards,\nKavita" / "Thanks, Anil" / "Cheers - Ravi"
        if (between.Contains('\n') || between.Trim().TrimEnd('\r') is "," or "-") return true;

        // "Thanks Anil" / "Regards Kavita Rao" with only a space: only when the name ends the line,
        // so "the best Tata deal" or "love Ganga water" are not sign-offs.
        return IsSpaceOnly(between) && EndsLine(tokens, nameEnd);
    }

    private static bool EndsLine(IReadOnlyList<NameToken> tokens, int i)
    {
        if (i + 1 >= tokens.Count) return true;
        var after = tokens[i].After.TrimStart(' ');
        return after.Length > 0 && (after[0] is '\n' or '\r' || after[0] is '!' or '.' or ',' && after.TrimStart(" !.,".ToCharArray()).StartsWith('\n'));
    }

    /// <summary>"Attached the roster. - Dhanashree", "ok will do. -lakshmikanth": a dash after a sentence or line start, then the name to the end of the line.</summary>
    private bool IsDashSignature(IReadOnlyList<NameToken> tokens, int i)
    {
        var start = i;
        if (i > 0 && !EndsWithDash(tokens[i].Before))
        {
            // second word of "- Kavita Rao": only when the first word is itself a name, so a bullet like "- Visit Ganga" is not a signature
            if (!IsNameish(tokens[i - 1])) return false;
            start = i - 1;
        }

        if (!EndsWithDash(tokens[start].Before)) return false;
        if (start != i && !IsSpaceOnly(tokens[i].Before)) return false;

        var before = tokens[start].Before.TrimEnd(' ');
        var lead = before[..^1].TrimEnd(' ');
        var opensSignature = tokens[start].LineStart || lead.Length == 0 || lead[^1] is '.' or '!' or '?' or '\n' or '\r';
        var endsLine = i + 1 >= tokens.Count || tokens[i].After.Contains('\n');
        return opensSignature && endsLine;
    }

    private static bool EndsWithDash(string before)
    {
        var b = before.TrimEnd(' ');
        return b.Length > 0 && b[^1] is '-' or '—' or '~';
    }

    /// <summary>A lowercase verb after the word: listed speech verbs, or any "-ed" past tense ("escalated", "dropped").</summary>
    private static bool IsVerb(NameToken t) =>
        NameSignals.SpeechVerbs.Contains(t.Lower) || t.Text.Length > 4 && t.Text.EndsWith("ed", StringComparison.Ordinal) && char.IsLower(t.Text[0]);

    /// <summary>"Shubhrajit escalated…", "Ibanshaphrang will collect…", "Sunaina ko refund…".</summary>
    private static bool ActsAsSubject(IReadOnlyList<NameToken> tokens, int i)
    {
        if (i + 1 >= tokens.Count || !IsSpaceOnly(tokens[i].After)) return false;
        var next = tokens[i + 1];
        if (NameSignals.HindiPostpositions.Contains(next.Lower) || NameSignals.StrongSpeechVerbs.Contains(next.Lower)) return true;
        return IsVerb(next) && next.Lower is not ("is" or "was" or "has" or "had" or "can" or "could" or "would" or "should" or "did" or "does");
    }

    /// <summary>"assign the ticket to Neikhrielie.", "route the call to Hiranmayee in billing", "approved the claim for Shivranjani."</summary>
    private static bool IsRecipient(IReadOnlyList<NameToken> tokens, int i)
    {
        if (i < 1 || tokens[i].SentenceStart || !IsSpaceOnly(tokens[i].Before)) return false;
        if (!NameSignals.RecipientPrepositions.Contains(tokens[i - 1].Lower)) return false;
        if (i + 1 >= tokens.Count) return true;
        var after = tokens[i].After.TrimStart(' ');
        return after.Length > 0 && after[0] is ',' or '.' or '!' or '?' or ';' or ':' or '\n' or '\r'
            || IsSpaceOnly(tokens[i].After) && NameSignals.ClauseEnders.Contains(tokens[i + 1].Lower);
    }

    private static bool IsListSeparator(string s) => s is ", " or "," or " and " or " & " or " aur ";

    private bool FollowedByNonPersonSuffix(IReadOnlyList<NameToken> tokens, int i, bool casing, Kind kind)
    {
        // Extend over the rest of a capitalised/name run: "Indira Gandhi International Airport".
        var j = i;
        while (j + 1 < tokens.Count && j - i < 4 && IsSpaceOnly(tokens[j].After)
               && (IsNameish(tokens[j + 1]) || casing && tokens[j + 1].IsTitleCase && !NameSignals.NonPersonSuffixes.Contains(tokens[j + 1].Lower)))
            j++;
        if (j + 1 >= tokens.Count || !IsSpaceOnly(tokens[j].After) || !NameSignals.NonPersonSuffixes.Contains(tokens[j + 1].Lower)) return false;

        // In properly capitalised text a place or product name capitalises its suffix ("Ravi Road", "Tata Motors");
        // "Kavita set up the meeting" or "Ravi cross-checked" are a person and a verb. Ambiguous words
        // ("Ganga aarti") and text without informative casing still count.
        var suffix = tokens[j + 1];
        return !casing || suffix.IsTitleCase || suffix.IsAllUpperWord || kind is Kind.Ambiguous or Kind.Unknown;
    }

    private bool[] ExcludedByPhrase(IReadOnlyList<NameToken> tokens)
    {
        var excluded = new bool[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!_phrasesByFirstWord.TryGetValue(tokens[i].Lower, out var phrases)) continue;
            foreach (var phrase in phrases)
            {
                if (i + phrase.Length > tokens.Count) continue;
                var ok = true;
                for (var k = 0; k < phrase.Length && ok; k++)
                    ok = tokens[i + k].Lower == phrase[k] && (k == 0 || IsSpaceOnly(tokens[i + k].Before) || tokens[i + k].Before.Trim() is "-" or "&");
                if (!ok) continue;
                for (var k = 0; k < phrase.Length; k++) excluded[i + k] = true;
                break;
            }
        }

        return excluded;
    }

    private static bool IsSpaceOnly(string s) => s.Length > 0 && s.All(c => c == ' ');
    private static bool IsJoiner(string s) => s.Length > 0 && s.All(c => c is ' ' or '-');
    private static bool IsSpaceOrComma(string s) => s.Length > 0 && s.All(c => c is ' ' or ',');
    private static bool IsSpaceOrColon(string s) => s.Length > 0 && s.All(c => c is ' ' or ':' or '-');
    // The tokenizer splits "Ravi's" into "Ravi" + "s", so the possessive shows up as an apostrophe right after the word.
    private static bool IsPossessive(string after) => after is "'" or "’";
    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));
}
