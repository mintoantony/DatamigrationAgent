namespace Dbm.Core.Analysis;

public enum Severity { Critical, High, Medium, Low, Info }   // JSON: "critical" | "high" | "medium" | "low" | "info"

/// <summary>One rule hit. Stored in AnalysisPayload.Findings under its id ("F001"…); only Commentary may be patched by the agent.</summary>
public sealed record Finding
{
    public required string Rule { get; init; }        // "R01".."R16"
    public required string Title { get; init; }       // rule title, e.g. "No primary or unique key"
    public required Severity Severity { get; init; }
    public required string Side { get; init; }        // "src" | "tgt" | "both"
    public required string Object { get; init; }      // "dbo.AUDIT_LOG", "dbo.CUST.NOTES", "app.Addresses ↔ app.Customers" or "database"
    public required string Message { get; init; }     // one or two sentences, evidence-based
    public string? Anchor { get; init; }              // "table:src:dbo.AUDIT_LOG" | "column:src:dbo.CUST.NOTES" | null
    public long? Count { get; init; }                 // e.g. orphan rows
    public string? Commentary { get; init; }          // written by the schema-analyst (critical/high only)
}
