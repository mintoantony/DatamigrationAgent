using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Analysis;

/// <summary>Runs every rule and assembles the script draft (v0) of the analysis artifact.</summary>
public static class Analyzer
{
    public const double RowsPerSecond = 50_000;

    public static AnalysisPayload Analyze(CatalogSnapshot src, CatalogSnapshot tgt, IReadOnlyDictionary<string, long>? orphanCounts = null)
    {
        var ctx = new RuleContext { Src = src, Tgt = tgt, OrphanCounts = orphanCounts ?? new Dictionary<string, long>() };
        var findings = new Dictionary<string, Finding>(StringComparer.Ordinal);
        var n = 0;
        foreach (var rule in Rules.All)
        {
            var ordered = rule.Evaluate(ctx)
                .OrderBy(f => SideRank(f.Side))
                .ThenBy(f => f.Object, StringComparer.OrdinalIgnoreCase);
            foreach (var finding in ordered)
                findings[$"F{(++n).ToString("000", CultureInfo.InvariantCulture)}"] = finding;
        }
        return new AnalysisPayload
        {
            Source = DbSummary.From(src),
            Target = DbSummary.From(tgt),
            Findings = findings,
            Estimates = Estimate(src),
        };
    }

    public static Estimates Estimate(CatalogSnapshot src)
    {
        var rows = src.Tables.Sum(t => t.Rows);
        var sizeMb = Math.Round(src.Tables.Sum(t => t.SizeMb), 2);
        var minutes = Math.Ceiling(rows / RowsPerSecond / 60.0 * 10.0) / 10.0;
        return new Estimates(rows, sizeMb, minutes,
            "source rows ÷ 50,000 rows/s (nominal bulk-copy rate); network, row width and target indexes change the real figure");
    }

    /// <summary>"N findings (x critical, y high)".</summary>
    public static string Summary(AnalysisPayload payload)
    {
        var critical = payload.Findings.Values.Count(f => f.Severity == Severity.Critical);
        var high = payload.Findings.Values.Count(f => f.Severity == Severity.High);
        return $"{payload.Findings.Count} findings ({critical} critical, {high} high)";
    }

    private static int SideRank(string side) => side switch { "src" => 0, "tgt" => 1, _ => 2 };
}
