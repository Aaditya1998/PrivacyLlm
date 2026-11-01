namespace PrivacyLlm.Core.Classification;

internal static class PiiMatchMerger
{
    /// <summary>
    /// Keeps the earliest-added (most specific) match when spans overlap, with two exceptions so that no
    /// part of personal data is left unmasked:
    /// a PersonName overlapping only other PersonNames is merged into one span covering both
    /// ("Mr. Ravi" from the regex + "Ravi kumar" from the name detector = "Ravi kumar"), and
    /// Address or PersonName spans keep their uncovered fragments when they overlap other types.
    /// </summary>
    public static IReadOnlyList<PiiMatch> ResolveOverlaps(IEnumerable<PiiMatch> matches)
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

            if (m.Type == PiiType.PersonName && overlaps.All(k => k.Type == PiiType.PersonName))
            {
                var start = Math.Min(m.Start, overlaps.Min(k => k.Start));
                var end = Math.Max(m.End, overlaps.Max(k => k.End));
                var first = overlaps[0];
                var merged = first with
                {
                    Start = start,
                    Length = end - start,
                    Value = MergedValue(m, overlaps, start, end),
                    Confidence = Math.Max(m.Confidence, overlaps.Max(k => k.Confidence)),
                    Tentative = m.Tentative && overlaps.All(k => k.Tentative),
                };
                foreach (var k in overlaps) kept.Remove(k);
                kept.Add(merged);
                continue;
            }

            if (m.Type is not (PiiType.Address or PiiType.PersonName)) continue;

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

    /// <summary>Rebuilds the merged span's text from the pieces we have (the original text is not passed in).</summary>
    private static string MergedValue(PiiMatch m, List<PiiMatch> others, int start, int end)
    {
        var chars = new char[end - start];
        Array.Fill(chars, ' ');
        foreach (var x in others.Append(m))
            for (var i = 0; i < x.Value.Length && x.Start + i < end; i++)
                chars[x.Start - start + i] = x.Value[i];
        return new string(chars);
    }
}
