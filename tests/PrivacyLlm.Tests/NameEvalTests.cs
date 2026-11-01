using System.Text;
using System.Text.Json;
using PrivacyLlm.Core.Classification;
using PrivacyLlm.Core.Classification.Names;
using Xunit.Abstractions;

namespace PrivacyLlm.Tests;

/// <summary>
/// Measures the name detector on labelled synthetic messages. name-eval.jsonl is the development set
/// (used while tuning weights); name-eval-holdout.jsonl was written independently and is only scored,
/// never tuned against, so its numbers are the honest ones.
/// </summary>
public sealed class NameEvalTests(ITestOutputHelper output)
{
    private sealed record Example(string Id, string Text, string[] Names, string Category);

    public sealed record Metrics(
        int Positives, int Negatives,
        double OffFrontierRecall, double DefiniteRecall, double NameRecall,
        double NegativesFrontierEligible, double NegativesFrontierEligibleEndToEnd,
        string Report);

    internal static readonly Lazy<(ContextualNameDetector Names, DataClassifier EndToEnd)> Detectors = new(() =>
    {
        var names = new ContextualNameDetector(new NameLexicon(), new NameScoringOptions());
        return (names, new DataClassifier(new CompositePiiDetector(new RegexPiiDetector(), names)));
    });

    public static Metrics Evaluate(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", file);
        var examples = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l =>
        {
            var e = JsonDocument.Parse(l).RootElement;
            return new Example(e.GetProperty("id").GetString()!, e.GetProperty("text").GetString()!,
                e.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(), e.GetProperty("category").GetString()!);
        }).ToList();

        var (names, endToEnd) = Detectors.Value;
        var report = new StringBuilder();
        int pos = 0, neg = 0, offFrontier = 0, definite = 0, namesTotal = 0, namesFound = 0, negClean = 0, negCleanE2E = 0;
        var misses = new List<string>();
        var falseAlarms = new List<string>();
        var byCategory = new Dictionary<string, (int Total, int Ok)>();

        foreach (var ex in examples)
        {
            var found = names.Detect(ex.Text);
            var ok = ex.Names.Length > 0 ? found.Count > 0 : found.Count == 0;
            var c = byCategory.GetValueOrDefault(ex.Category);
            byCategory[ex.Category] = (c.Total + 1, c.Ok + (ok ? 1 : 0));

            if (ex.Names.Length > 0)
            {
                pos++;
                if (found.Count > 0) offFrontier++;
                if (found.Any(m => !m.Tentative)) definite++;
                foreach (var n in ex.Names)
                {
                    namesTotal++;
                    var at = ex.Text.IndexOf(n, StringComparison.Ordinal);
                    if (found.Any(m => m.Start < at + n.Length && at < m.End)) namesFound++;
                    else misses.Add($"  {ex.Id} [{ex.Category}] missed '{n}': {Trim(ex.Text)}");
                }
            }
            else
            {
                neg++;
                if (found.Count == 0) negClean++;
                else falseAlarms.Add($"  {ex.Id} [{ex.Category}] flagged {string.Join(", ", found.Select(m => $"'{m.Value}' {m.Confidence:0.00}"))}: {Trim(ex.Text)}");
                if (!endToEnd.Classify(ex.Text).MayContainPersonalData) negCleanE2E++;
            }
        }

        var m = new Metrics(pos, neg,
            Ratio(offFrontier, pos), Ratio(definite, pos), Ratio(namesFound, namesTotal),
            Ratio(negClean, neg), Ratio(negCleanE2E, neg), "");

        report.AppendLine($"{file}: {pos} positives, {neg} negatives");
        report.AppendLine($"  positives kept off frontier (name detector): {m.OffFrontierRecall:P1}");
        report.AppendLine($"  positives with a definite name:              {m.DefiniteRecall:P1}");
        report.AppendLine($"  labelled names covered by a span:            {m.NameRecall:P1}");
        report.AppendLine($"  negatives left frontier-eligible (names):    {m.NegativesFrontierEligible:P1}");
        report.AppendLine($"  negatives left frontier-eligible (all PII):  {m.NegativesFrontierEligibleEndToEnd:P1}");
        report.AppendLine("  by category (correct/total):");
        foreach (var (cat, (total, okCount)) in byCategory.OrderBy(kv => kv.Key))
            report.AppendLine($"    {cat,-28} {okCount}/{total}");
        report.AppendLine($"  missed names ({misses.Count}):");
        misses.ForEach(x => report.AppendLine(x));
        report.AppendLine($"  false alarms on negatives ({falseAlarms.Count}):");
        falseAlarms.ForEach(x => report.AppendLine(x));

        return m with { Report = report.ToString() };
    }

    [Fact]
    public void Development_set()
    {
        var m = Evaluate("name-eval.jsonl");
        output.WriteLine(m.Report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "name-eval-report.txt"), m.Report);

        Assert.True(m.OffFrontierRecall >= 0.95, $"off-frontier recall {m.OffFrontierRecall:P1}");
        Assert.True(m.NegativesFrontierEligible >= 0.85, $"negatives frontier-eligible {m.NegativesFrontierEligible:P1}");
    }

    /// <summary>
    /// The first held-out set. It scored 87.3% off-frontier recall on first sight (below its 90% gate),
    /// and its misses were then used to find general rule gaps, so it now counts as development data.
    /// </summary>
    [Fact]
    public void Second_development_set()
    {
        var m = Evaluate("name-eval-holdout.jsonl");
        output.WriteLine(m.Report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "name-eval-dev2-report.txt"), m.Report);

        Assert.True(m.OffFrontierRecall >= 0.90, $"off-frontier recall {m.OffFrontierRecall:P1}");
        Assert.True(m.NegativesFrontierEligible >= 0.85, $"negatives frontier-eligible {m.NegativesFrontierEligible:P1}");
    }

    /// <summary>
    /// Written independently after the rules were tuned, and scored once. Thresholds were fixed before
    /// the first score; this set must not be used to tune anything.
    /// </summary>
    [Fact]
    public void Held_out_set()
    {
        var m = Evaluate("name-eval-holdout-v2.jsonl");
        output.WriteLine(m.Report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "name-eval-holdout-report.txt"), m.Report);

        Assert.True(m.OffFrontierRecall >= 0.90, $"off-frontier recall {m.OffFrontierRecall:P1}");
        Assert.True(m.NegativesFrontierEligible >= 0.85, $"negatives frontier-eligible {m.NegativesFrontierEligible:P1}");
    }

    private static double Ratio(int a, int b) => b == 0 ? 1 : a / (double)b;
    private static string Trim(string s) => (s.Length > 110 ? s[..110] + "…" : s).Replace('\n', ' ');
}
