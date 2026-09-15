using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.SqlGen;

/// <summary>Validates a plan against the live databases without changing either of them.
/// Source: <c>sp_describe_first_result_set</c> (parameterised) for every SourceQuery.
/// Target: catalog checks plus a syntax check of every target-side statement with SET PARSEONLY sent as three
/// separate batches (ON / statement / OFF). Verified on SQL Server 2025 LocalDB: a single batch
/// "SET PARSEONLY ON; stmt; SET PARSEONLY OFF;" EXECUTES stmt (both SETs act at parse time), so never combine them.</summary>
public static class SqlValidator
{
    /// <summary>Prefix of validator warnings stored in TaskPlan.Warnings (lets a re-validation replace them).</summary>
    public const string WarningPrefix = "validate: ";

    /// <summary>SET PARSEONLY takes effect while the batch is parsed, so a statement containing "SET PARSEONLY OFF" would switch
    /// the check off and the rest of its batch would EXECUTE (verified on LocalDB). Any mention of PARSEONLY - comments
    /// included, deliberately - is refused without being sent.</summary>
    static readonly Regex ParseOnlyWord = new(@"\bPARSEONLY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    const string ParseOnlyError = "SET PARSEONLY is not allowed";

    /// <summary>The one PARSEONLY test, shared by CheckShape (offline report) and ParseCheckAsync (refuses to send).</summary>
    static bool MentionsParseOnly(string? sql) => sql is not null && ParseOnlyWord.IsMatch(sql);

    static readonly Regex GoLine = new(@"^\s*GO\s*(\d+\s*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record ResultColumn(string Name, string SystemTypeName, bool IsNullable, int Precision, int Scale);

    public static async Task<ValidationReport> ValidateAsync(SqlPlanPayload plan, string sourceCs, string targetCs, CatalogSnapshot tgt,
        string? onlyTaskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceCs);
        ArgumentNullException.ThrowIfNull(targetCs);
        ArgumentNullException.ThrowIfNull(tgt);
        var taskErrors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var taskWarnings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var global = new List<string>();
        if (onlyTaskId is not null && !plan.Tasks.ContainsKey(onlyTaskId))
            return new ValidationReport(false, taskErrors, taskWarnings, [$"unknown task {onlyTaskId}"]);

        var ids = onlyTaskId is not null ? [onlyTaskId]
            : plan.Order.Where(plan.Tasks.ContainsKey).Concat(plan.Tasks.Keys.Where(k => !plan.Order.Contains(k)).OrderBy(k => k, StringComparer.Ordinal)).Distinct().ToList();
        foreach (var id in ids) { taskErrors[id] = new List<string>(); taskWarnings[id] = new List<string>(); }
        var secrets = Redactor.SecretsOf(sourceCs).Concat(Redactor.SecretsOf(targetCs)).ToList();
        string Scrub(string text) => Redactor.Scrub(text, secrets);

        SqlConnection? source = null, target = null;
        try
        {
            try { source = await SqlConnect.OpenAsync(sourceCs, ct); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
            { global.Add("source connection failed: " + Scrub(ex.Message)); }
            try { target = await SqlConnect.OpenAsync(WithoutPooling(targetCs), ct); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
            { global.Add("target connection failed: " + Scrub(ex.Message)); }

            foreach (var id in ids)
                await ValidateTaskAsync(plan.Tasks[id], source, target, tgt, taskErrors[id], taskWarnings[id], Scrub, ct);

            if (onlyTaskId is null)
            {
                CheckGlobal("preSql", plan.PreSql);
                CheckGlobal("postSql", plan.PostSql);
                if (target is not null)
                {
                    // Statements naming PARSEONLY were reported by CheckGlobal; ParseCheckAsync would refuse them anyway.
                    for (var i = 0; i < plan.PreSql.Count; i++)
                        if (!MentionsParseOnly(plan.PreSql[i]) && await ParseCheckAsync(target, plan.PreSql[i], ct) is { } e) global.Add($"preSql[{i}]: {Scrub(e)}");
                    for (var i = 0; i < plan.PostSql.Count; i++)
                        if (!MentionsParseOnly(plan.PostSql[i]) && await ParseCheckAsync(target, plan.PostSql[i], ct) is { } e) global.Add($"postSql[{i}]: {Scrub(e)}");
                }
            }
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
            if (target is not null) await target.DisposeAsync();
        }
        var ok = global.Count == 0 && taskErrors.Values.All(l => l.Count == 0);
        return new ValidationReport(ok, taskErrors, taskWarnings, global);

        void CheckGlobal(string field, List<string> statements)
        {
            for (var i = 0; i < statements.Count; i++)
            {
                if (GoLine.IsMatch(statements[i])) global.Add($"{field}[{i}]: GO batch separators are not allowed");
                if (MentionsParseOnly(statements[i])) global.Add($"{field}[{i}]: {ParseOnlyError}");
            }
        }
    }

    /// <summary>Copies a report into the plan: task Errors are replaced, validator warnings (prefixed) replace the previous
    /// validator warnings; generator warnings stay. <paramref name="full"/> = the report covers the whole plan (sets plan.Errors).</summary>
    public static void Apply(SqlPlanPayload plan, ValidationReport report, bool full = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);
        foreach (var (id, errors) in report.TaskErrors)
            if (plan.Tasks.TryGetValue(id, out var task)) task.Errors = errors.ToList();
        foreach (var (id, warnings) in report.TaskWarnings)
            if (plan.Tasks.TryGetValue(id, out var task))
                task.Warnings = task.Warnings.Where(w => !w.StartsWith(WarningPrefix, StringComparison.Ordinal))
                    .Concat(warnings.Select(w => WarningPrefix + w)).ToList();
        if (full) plan.Errors = report.GlobalErrors.ToList();
    }

    /// <summary>Offline checks of one task (no database needed): mode, required fields, bindings, GO separators.</summary>
    public static List<string> CheckShape(TaskPlan task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(task.SourceQuery)) errors.Add("sourceQuery is empty");
        if (task.Mode is not ("direct" or "staging_merge")) errors.Add($"mode '{task.Mode}' must be direct or staging_merge");
        if (task.Mode == "staging_merge")
        {
            if (string.IsNullOrWhiteSpace(task.StagingDdl)) errors.Add("staging_merge requires stagingDdl");
            if (string.IsNullOrWhiteSpace(task.MergeSql)) errors.Add("staging_merge requires mergeSql");
        }
        if (task.Columns.Count == 0) errors.Add("no column bindings");
        foreach (var dup in task.Columns.GroupBy(b => b.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"{dup.Key}: bound more than once");
        foreach (var (field, sql) in AllFields(task))
            if (GoLine.IsMatch(sql)) errors.Add($"{field}: GO batch separators are not allowed");
        foreach (var (field, sql) in TargetFields(task))
            if (MentionsParseOnly(sql)) errors.Add($"{field}: {ParseOnlyError}");
        return errors;
    }

    static async Task ValidateTaskAsync(TaskPlan task, SqlConnection? source, SqlConnection? target, CatalogSnapshot tgt,
        List<string> errors, List<string> warnings, Func<string, string> scrub, CancellationToken ct)
    {
        errors.AddRange(CheckShape(task));
        var table = tgt.FindTable(task.Target);
        if (table is null) errors.Add($"target table {task.Target} not found in the target catalog");

        // Target columns: exist and insertable.
        var targetCols = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        if (table is not null)
        {
            foreach (var b in task.Columns)
            {
                var col = table.FindColumn(b.Target);
                if (col is null) errors.Add($"{b.Target}: not a column of {task.Target}");
                else if (col.IsComputed) errors.Add($"{b.Target}: computed column cannot be loaded");
                else if (col.IsRowVersion) errors.Add($"{b.Target}: rowversion column cannot be loaded");
                else targetCols[b.Target] = col;
            }
            var identity = table.Columns.FirstOrDefault(c => c.IsIdentity);
            var identityBound = identity is not null && targetCols.ContainsKey(identity.Name);
            if (identityBound && !task.IdentityInsert)
                warnings.Add($"{identity!.Name}: identity column is bound but identityInsert is false; the target will generate new values");
            if (!identityBound && task.IdentityInsert)
                warnings.Add("identityInsert is true but no identity column is bound");
        }

        // Source query shape and types.
        if (source is not null && !string.IsNullOrWhiteSpace(task.SourceQuery))
        {
            var (cols, error) = await DescribeAsync(source, task.SourceQuery, ct);
            if (error is not null) errors.Add("sourceQuery: " + scrub(error));
            else if (cols!.Count == 0) errors.Add("sourceQuery: returns no result set");
            else
            {
                var byName = new Dictionary<string, ResultColumn>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in cols)
                {
                    if (c.Name.Length == 0) { errors.Add("sourceQuery: every result column needs an alias"); continue; }
                    if (!byName.TryAdd(c.Name, c)) errors.Add($"sourceQuery: duplicate result column '{c.Name}'");
                }
                foreach (var key in task.KeyColumns)
                    if (!byName.ContainsKey(key)) errors.Add($"key column '{key}' is not in the source query result");
                foreach (var b in task.Columns)
                {
                    if (!byName.TryGetValue(b.Source, out var rc)) { errors.Add($"{b.Target}: column '{b.Source}' is not in the source query result"); continue; }
                    if (!targetCols.TryGetValue(b.Target, out var col)) continue;
                    var srcType = SqlTypeText.Parse(rc.SystemTypeName, rc.Precision, rc.Scale);
                    var compat = TypeCompat.Check(srcType, ColumnType.From(col));
                    var arrow = $"{SqlTypeText.Format(srcType)} -> {SqlTypeText.Format(col)}";
                    if (compat.Level == CompatLevel.Incompatible) errors.Add($"{b.Target}: {arrow}: {compat.Risk ?? "incompatible types"}");
                    else if (compat.Level == CompatLevel.Risky) warnings.Add($"{b.Target}: {arrow}: {compat.Risk ?? "risky conversion"}");
                    if (rc.IsNullable && !col.IsNullable) warnings.Add($"{b.Target}: source may be NULL but the target column is NOT NULL");
                }
            }
        }

        // Target-side statements: syntax only.
        if (target is not null)
            foreach (var (field, sql) in TargetFields(task))
                if (!MentionsParseOnly(sql) && await ParseCheckAsync(target, sql, ct) is { } e) errors.Add($"{field}: {scrub(e)}");   // PARSEONLY: reported by CheckShape
    }

    /// <summary>Result columns of the first result set, or the compile error message(s).</summary>
    public static async Task<(List<ResultColumn>? Columns, string? Error)> DescribeAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(sql);
        await using var cmd = new SqlCommand("EXEC sp_describe_first_result_set @tsql = @q;", conn) { CommandTimeout = 60 };
        cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, -1) { Value = sql });
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            int oHidden = r.GetOrdinal("is_hidden"), oName = r.GetOrdinal("name"), oNull = r.GetOrdinal("is_nullable"),
                oType = r.GetOrdinal("system_type_name"), oPrec = r.GetOrdinal("precision"), oScale = r.GetOrdinal("scale");
            var list = new List<ResultColumn>();
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(oHidden) && r.GetBoolean(oHidden)) continue;
                list.Add(new ResultColumn(
                    r.IsDBNull(oName) ? "" : r.GetString(oName),
                    r.IsDBNull(oType) ? "sql_variant" : r.GetString(oType),
                    !r.IsDBNull(oNull) && r.GetBoolean(oNull),
                    r.IsDBNull(oPrec) ? 0 : Convert.ToInt32(r.GetValue(oPrec), CultureInfo.InvariantCulture),
                    r.IsDBNull(oScale) ? 0 : Convert.ToInt32(r.GetValue(oScale), CultureInfo.InvariantCulture)));
            }
            return (list, null);
        }
        catch (SqlException ex) { return (null, Messages(ex)); }
    }

    /// <summary>Syntax check on the target: SET PARSEONLY ON / statement / SET PARSEONLY OFF as three batches. Nothing executes.
    /// A statement mentioning PARSEONLY is never sent, whether or not CheckShape ran first (see <see cref="ParseOnlyWord"/>).</summary>
    public static async Task<string?> ParseCheckAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (string.IsNullOrWhiteSpace(sql)) return null;
        if (MentionsParseOnly(sql)) return ParseOnlyError;   // security boundary: must not depend on any earlier check
        await ExecAsync(conn, "SET PARSEONLY ON;", ct);
        try
        {
            await ExecAsync(conn, sql, ct);
            return null;
        }
        catch (SqlException ex) { return Messages(ex); }
        finally { await ExecAsync(conn, "SET PARSEONLY OFF;", CancellationToken.None); }
    }

    static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };   // no parameters => sent as a plain SQL batch
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static string Messages(SqlException ex)
    {
        var parts = ex.Errors.Cast<SqlError>().Select(e => e.Message)
            .Where(m => !m.StartsWith("The batch could not be analyzed", StringComparison.Ordinal)
                     && !m.StartsWith("The metadata could not be determined", StringComparison.Ordinal))
            .Distinct().ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : ex.Message;
    }

    static string WithoutPooling(string cs)
    {
        try { return new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString; }
        catch (ArgumentException) { return cs; }
    }

    static IEnumerable<(string Field, string Sql)> TargetFields(TaskPlan task)
    {
        for (var i = 0; i < task.PreSql.Count; i++) yield return ($"preSql[{i}]", task.PreSql[i]);
        if (!string.IsNullOrWhiteSpace(task.StagingDdl)) yield return ("stagingDdl", task.StagingDdl);
        if (!string.IsNullOrWhiteSpace(task.MergeSql)) yield return ("mergeSql", task.MergeSql);
        for (var i = 0; i < task.PostSql.Count; i++) yield return ($"postSql[{i}]", task.PostSql[i]);
    }

    static IEnumerable<(string Field, string Sql)> AllFields(TaskPlan task) =>
        TargetFields(task).Prepend(("sourceQuery", task.SourceQuery));
}
