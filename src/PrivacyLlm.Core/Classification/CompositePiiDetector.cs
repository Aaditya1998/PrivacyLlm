namespace PrivacyLlm.Core.Classification;

/// <summary>
/// Runs the regex detector, then the contextual name detector, and merges the results. Regex
/// matches win overlaps, so labelled names ("Customer: Priya Sharma") stay definite.
/// Each detector fails closed on its own: if the name detector breaks, the whole text is treated as
/// a possible name and stays on the self-hosted tier.
/// </summary>
public sealed class CompositePiiDetector(IPiiDetector regex, IPiiDetector names) : IPiiDetector
{
    public IReadOnlyList<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<PiiMatch>();

        var regexMatches = regex.Detect(text);

        IReadOnlyList<PiiMatch> nameMatches;
        try
        {
            nameMatches = names.Detect(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            nameMatches = new[] { new PiiMatch(PiiType.PersonName, 0, text.Length, text, 1.0, Tentative: true, Source: "Name") };
        }

        return PiiMatchMerger.ResolveOverlaps(regexMatches.Concat(nameMatches));
    }
}
