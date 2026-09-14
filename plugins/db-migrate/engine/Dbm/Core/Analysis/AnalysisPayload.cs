using Dbm.Core.Catalog;

namespace Dbm.Core.Analysis;

/// <summary>The Analysis artifact (phase "analysis"). v0 is written by the analyze job; the agent adds /narrative and commentary.</summary>
public sealed class AnalysisPayload
{
    public DbSummary Source { get; set; } = null!;
    public DbSummary Target { get; set; } = null!;
    public Dictionary<string, Finding> Findings { get; set; } = new();   // "F001", "F002", … in rule order, then object order
    public Estimates Estimates { get; set; } = null!;
    public Narrative? Narrative { get; set; }
}

public sealed record DbSummary(string Server, string Database, string Version, string Edition, string Collation, int CompatLevel,
    int Tables, int Columns, long Rows, double SizeMb, int Views, int Procedures, int Functions, int Triggers)
{
    public int MajorVersion { get; init; }
    public string? ProductVersion { get; init; }

    public static DbSummary From(CatalogSnapshot s) => new(
        s.Server.Server,
        s.Server.Database,
        FirstLine(s.Server.Version),
        s.Server.Edition,
        s.Server.DatabaseCollation,
        s.Server.CompatLevel,
        s.Tables.Count,
        s.Tables.Sum(t => t.Columns.Count),
        s.Tables.Sum(t => t.Rows),
        Math.Round(s.Tables.Sum(t => t.SizeMb), 2),
        s.Objects.Views,
        s.Objects.Procedures,
        s.Objects.Functions,
        s.Objects.Triggers)
    {
        MajorVersion = s.Server.MajorVersion,
        ProductVersion = s.Server.ProductVersion,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 80 ? line : line[..80];
    }
}

/// <summary>EstimatedMinutes = source rows / 50 000 rows per second, in minutes, rounded up to 0.1.</summary>
public sealed record Estimates(long TotalRows, double TotalSizeMb, double EstimatedMinutes, string Basis);

public sealed class Narrative
{
    public string Summary { get; set; } = "";                     // <= 150 words
    public List<string> Risks { get; set; } = new();              // <= 12 bullets
    public List<string> Recommendations { get; set; } = new();    // 1..8 imperative bullets
}
