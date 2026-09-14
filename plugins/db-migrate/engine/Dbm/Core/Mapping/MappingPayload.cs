namespace Dbm.Core.Mapping;

public enum MapMethod { Exact, Fuzzy, Vector, Agent, Human, Carried }

public sealed class MappingPayload
{
    public Dictionary<string, TableMap> Tables { get; set; } = new();        // key = target "schema.table" (exact catalog case)
    public Dictionary<string, DropDecision> Drops { get; set; } = new();     // key = source "schema.table" (whole table) or "schema.table.column"
    public List<string> Notes { get; set; } = new();
}

public sealed class TableMap
{
    public string Kind { get; set; } = "direct";          // "direct" | "merge" | "lookup" | "skip"  (a split = several TableMaps sharing a source)
    public List<string> Sources { get; set; } = new();    // source table keys; Sources[0] is the primary source, aliased "s"
    public string? From { get; set; }                     // optional full FROM clause (without FROM)
    public string? Filter { get; set; }                   // optional WHERE predicate (without WHERE)
    public double Confidence { get; set; }
    public MapMethod Method { get; set; }
    public string? Rationale { get; set; }
    public Dictionary<string, ColumnMap> Columns { get; set; } = new();     // key = target column name
    public List<Candidate>? Candidates { get; set; }
}

public sealed class ColumnMap
{
    public string? Expr { get; set; }                     // T-SQL expression over the FROM aliases; null = unmapped
    public List<string> SourceColumns { get; set; } = new();   // "schema.table.column" values referenced by Expr (drives coverage)
    public string? Default { get; set; }                  // T-SQL expression used when Expr is null
    public double Confidence { get; set; }
    public MapMethod Method { get; set; }
    public string? Rationale { get; set; }
    public string? TypeRisk { get; set; }                 // engine-owned: computed, recomputed and restored by dbm, never by an author
    public List<Candidate>? Candidates { get; set; }
}


public sealed record Candidate(string Source, double Score, string Why);

public sealed record DropDecision(string Reason, MapMethod Method);
