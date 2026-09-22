namespace Dbm.Core.SqlGen;

/// <summary>The SQL phase artifact (contract C13). Serialised with <c>Dbm.Core.Json.Options</c> (camelCase).</summary>
public sealed class SqlPlanPayload
{
    public List<string> PreSql { get; set; } = new();
    public List<string> PostSql { get; set; } = new();
    public List<string> Order { get; set; } = new();                        // task ids in execution order
    public Dictionary<string, TaskPlan> Tasks { get; set; } = new();        // key = task id "T01", "T02", ... (numbered in Order)
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();                       // M4 addition: global errors from the last validation

    /// <summary>Ruling 204 (open item 10): positive evidence that live validation ran over THIS version's SQL, written by every writer
    /// that validates (SqlGenJob, SqlModule.Validate - agent patch, dry-run and UI edit alike) and by nobody else; a patch may not
    /// change it. Null - an offline run, or a version stored before the evidence existed (the field is then absent) - means NOT
    /// validated, whatever the warnings say.</summary>
    public SqlValidation? Validation { get; set; }

    /// <summary>Ruling 204: why this version is NOT validated - empty only when it carries <see cref="Validation"/> evidence and no
    /// stored <see cref="SqlPlanSource.SkippedPrefix"/> line (the engine never writes both; a version that has both is refused too).
    /// The stored skipped line(s) when there are any, else <see cref="SqlModule.NoEvidence"/>. Read by ApprovalBlockers, the script
    /// pack and, through the same payload, the SQL screen.</summary>
    public List<string> NotValidatedReasons()
    {
        var skipped = (Warnings ?? []).Where(w => w is not null && w.StartsWith(SqlPlanSource.SkippedPrefix, StringComparison.Ordinal)).ToList();
        if (Validation is not null && skipped.Count == 0) return [];
        return skipped.Count > 0 ? skipped : [SqlModule.NoEvidence];
    }

    public int ErrorCount() => Errors.Count + Tasks.Values.Sum(t => t.Errors.Count);
    public int WarningCount() => Warnings.Count + Tasks.Values.Sum(t => t.Warnings.Count);

    /// <summary>Every way <see cref="Order"/> fails to list each task exactly once, in a stable order: "order[2]: T99 is not a task",
    /// "order[6]: T01 is listed more than once", then (ordinal) "T03: task is missing from order". Empty = consistent.</summary>
    public List<string> OrderProblems()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Order.Count; i++)
        {
            var id = Order[i];
            if (!Tasks.ContainsKey(id)) problems.Add(FormattableString.Invariant($"order[{i}]: {id} is not a task"));
            else if (!seen.Add(id)) problems.Add(FormattableString.Invariant($"order[{i}]: {id} is listed more than once"));
        }
        foreach (var id in Tasks.Keys.Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            problems.Add($"{id}: task is missing from order");
        return problems;
    }
}

public sealed class TaskPlan
{
    public string Target { get; set; } = "";              // "app.Orders"
    public string Mode { get; set; } = "direct";          // "direct" | "staging_merge"
    public string SourceQuery { get; set; } = "";         // "SELECT <expr> AS [TargetCol], ..., s.[k] AS [__k0] FROM ... [WHERE ...]" (no ORDER BY)
    public List<string> KeyColumns { get; set; } = new(); // key aliases in SourceQuery ("__k0", "__k1"); empty = single-transaction load
    public List<ColumnBinding> Columns { get; set; } = new();
    public bool IdentityInsert { get; set; }
    public string? StagingDdl { get; set; }               // staging_merge only: "CREATE TABLE #stg (...)"
    public string? MergeSql { get; set; }                 // staging_merge only: moves #stg rows into Target
    public List<string> PreSql { get; set; } = new();
    public List<string> PostSql { get; set; } = new();
    public string CountSql { get; set; } = "";            // "SELECT COUNT_BIG(*) FROM (\n<SourceQuery>\n) AS q"
    public List<string> DependsOn { get; set; } = new();  // FK-parent task ids (cycle edges excluded)
    public int? ChunkSize { get; set; }                   // generator sets 5000 when any mapped column is LOB/(max)
    public bool Custom { get; set; }                      // true once an agent/human edited this task's SQL
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();     // from the last validation
    public string? MappingHash { get; set; }              // M4 addition: SHA-256 (lower hex) of the TableMap JSON this task was generated from
}

/// <summary>When live validation ran over the version (UTC) and whether it left the plan without errors. JSON <c>{"at": …, "ok": …}</c>.</summary>
public sealed record SqlValidation(DateTimeOffset At, bool Ok);

/// <summary>Source = alias in SourceQuery (equals the target column name for generated tasks); Target = target column name.</summary>
public sealed record ColumnBinding(string Source, string Target);

/// <summary>Result of SqlValidator.ValidateAsync (T4.3). Dictionaries contain an entry (possibly empty) for every validated task.
/// <see cref="GlobalWarnings"/> is always present: "not checked" notices for global preSql/postSql, or, on a single-task run, the one
/// line <c>SqlValidator.SingleTaskGlobalWarning</c> — so an empty list always means "checked, nothing to report".</summary>
public sealed record ValidationReport(bool Ok, Dictionary<string, List<string>> TaskErrors, Dictionary<string, List<string>> TaskWarnings, List<string> GlobalErrors,
    List<string> GlobalWarnings);
