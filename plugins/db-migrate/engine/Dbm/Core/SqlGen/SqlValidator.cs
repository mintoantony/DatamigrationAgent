using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.SqlGen;

/// <summary>Validates a plan against the live databases.
/// <para><b>Safety claim:</b> no plan SQL — generated or agent-written — is ever executed. Validation may create an engine-authored,
/// session-local temp table (<c>#stg</c>) in tempdb, dropped at connection close.</para>
/// <para>Every plan statement, on either side, is only ever sent as the <c>@tsql</c> string argument of
/// <c>sp_describe_first_result_set</c>, which compiles without executing. A plan statement is never sent as a batch. The only
/// batches this class sends are the engine-built scaffold <c>CREATE TABLE #stg</c> (<see cref="SqlGenerator.StagingDdl"/>) and its
/// <c>DROP</c>. (The previous SET PARSEONLY sandbox was void: a statement can switch PARSEONLY off with a spelling a text filter
/// cannot recognise, and the rest of its batch executed.)</para>
/// <para>Each statement is checked on its own, never batched with another: sp_describe reports errors in later statements of a
/// batch inconsistently. Limits (reported, not hidden): a statement that depends on an object an earlier statement creates cannot be
/// server-checked without executing the creator, and a single stored field holding several statements gets sp_describe's
/// inconsistent later-statement checking.</para></summary>
public static class SqlValidator
{
    /// <summary>Prefix of validator warnings stored in TaskPlan.Warnings (lets a re-validation replace them).</summary>
    public const string WarningPrefix = "validate: ";

    /// <summary>Middle of a "not checked" warning: "&lt;field&gt;: not checked: &lt;reason&gt;".</summary>
    public const string NotCheckedMarker = ": not checked: ";

    /// <summary>The GlobalWarnings line of a single-task run: the global statements were not checked. Report/CLI only, never stored.</summary>
    public const string SingleTaskGlobalWarning = "global preSql/postSql" + NotCheckedMarker + "single-task validation";

    static readonly Regex GoLine = new(@"^\s*GO\s*(\d+\s*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    const int InvalidObjectName = 208;
    const int InvalidColumnName = 207;

    public sealed record ResultColumn(string Name, string SystemTypeName, bool IsNullable, int Precision, int Scale);

    /// <summary>Outcome of checking one target statement: <see cref="Error"/> = a real error; <see cref="NotChecked"/> = the server
    /// could not check it and why; both null = it compiled. <see cref="Errors"/> are the underlying server errors (wrappers removed).</summary>
    public sealed record TargetCheck(string? Error, string? NotChecked, IReadOnlyList<SqlError> Errors);

    /// <summary>Start of the global error a report carries when it could not open that connection.</summary>
    public const string SourceConnectionFailed = "source connection failed: ";
    /// <summary>Start of the global error a report carries when it could not open that connection.</summary>
    public const string TargetConnectionFailed = "target connection failed: ";

    /// <summary>Open item 8 L3: start of the global error a report carries when a connection dropped mid-validation
    /// (InvalidOperationException, never SqlException - e.g. the server closed an idle connection). Never a cancellation: a
    /// cancellation the caller requested still propagates rather than being reported here.</summary>
    public const string ValidationStoppedPrefix = "validation stopped: ";

    /// <summary>True when the line records a connection this validation could not open.</summary>
    public static bool IsConnectionFailure(string? line) => line is not null
        && (line.StartsWith(SourceConnectionFailed, StringComparison.Ordinal) || line.StartsWith(TargetConnectionFailed, StringComparison.Ordinal));

    /// <summary>Review L3: a report is evidence of live validation only when it reached both databases; otherwise nothing compiled.</summary>
    public static bool Connected(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return !report.GlobalErrors.Any(IsConnectionFailure);
    }

    public static Task<ValidationReport> ValidateAsync(SqlPlanPayload plan, string sourceCs, string targetCs, CatalogSnapshot tgt,
        string? onlyTaskId, CancellationToken ct) =>
        ValidateAsync(plan, sourceCs, targetCs, tgt, onlyTaskId, SqlConnect.OpenAsync, ct);

    /// <param name="open">Opens a connection; tests substitute a failing opener to pin the secret scrubbing of connection errors.</param>
    internal static async Task<ValidationReport> ValidateAsync(SqlPlanPayload plan, string sourceCs, string targetCs, CatalogSnapshot tgt,
        string? onlyTaskId, Func<string, CancellationToken, Task<SqlConnection>> open, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceCs);
        ArgumentNullException.ThrowIfNull(targetCs);
        ArgumentNullException.ThrowIfNull(tgt);
        ArgumentNullException.ThrowIfNull(open);
        var taskErrors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var taskWarnings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var global = new List<string>();
        var globalWarnings = new List<string>();
        if (onlyTaskId is not null && !plan.Tasks.ContainsKey(onlyTaskId))
            return new ValidationReport(false, taskErrors, taskWarnings, [$"unknown task {onlyTaskId}"], [SingleTaskGlobalWarning]);

        var ids = onlyTaskId is not null ? [onlyTaskId]
            : plan.Order.Where(plan.Tasks.ContainsKey).Concat(plan.Tasks.Keys.Where(k => !plan.Order.Contains(k)).OrderBy(k => k, StringComparer.Ordinal)).Distinct().ToList();
        foreach (var id in ids) { taskErrors[id] = new List<string>(); taskWarnings[id] = new List<string>(); }
        var secrets = Redactor.SecretsOf(sourceCs).Concat(Redactor.SecretsOf(targetCs)).ToList();
        string Scrub(string text) => Redactor.Scrub(text, secrets);

        SqlConnection? source = null, target = null;
        try
        {
            try { source = await open(sourceCs, ct); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
            { global.Add(SourceConnectionFailed + Scrub(ex.Message)); }
            // Not pooled: the session, and with it the #stg scaffold, ends when this connection closes.
            try { target = await open(WithoutPooling(targetCs), ct); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
            { global.Add(TargetConnectionFailed + Scrub(ex.Message)); }

            // Open item 8 L3, decision I-2: a connection dropped mid-run (InvalidOperationException - never a cancellation, since
            // nothing here cancels ct) ends the run with a report instead of an unhandled exception, so every caller (job, CLI,
            // endpoint) gets ok:false and a named reason rather than a crash or a job with no stored evidence. Tasks the break
            // reaches are marked "not checked" rather than left with empty error/warning lists that would silently read as clean.
            var stoppedAt = ids.Count;
            for (var i = 0; i < ids.Count; i++)
            {
                try
                {
                    await ValidateTaskAsync(plan.Tasks[ids[i]], source, target, tgt, taskErrors[ids[i]], taskWarnings[ids[i]], Scrub, ct);
                }
                catch (InvalidOperationException ex)
                {
                    global.Add(ValidationStoppedPrefix + Scrub(ex.Message));
                    stoppedAt = i;
                    break;
                }
            }
            for (var i = stoppedAt; i < ids.Count; i++)
                taskWarnings[ids[i]].Add("task" + NotCheckedMarker + "validation stopped before this task could be checked");

            if (stoppedAt == ids.Count && onlyTaskId is null)
            {
                var statements = plan.PreSql.Select((sql, i) => ($"preSql[{i}]", sql))
                    .Concat(plan.PostSql.Select((sql, i) => ($"postSql[{i}]", sql))).ToList();
                foreach (var (field, sql) in statements)
                    if (GoLine.IsMatch(sql)) global.Add($"{field}: GO batch separators are not allowed");
                if (target is not null)
                {
                    try
                    {
                        await CheckSequenceAsync(target, statements, customMerge: false, global, globalWarnings, Scrub, ct);
                    }
                    catch (InvalidOperationException ex)
                    {
                        global.Add(ValidationStoppedPrefix + Scrub(ex.Message));
                        globalWarnings.Add("global preSql/postSql" + NotCheckedMarker + "validation stopped before these could be checked");
                    }
                }
            }
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
            if (target is not null) await target.DisposeAsync();
        }
        var ok = global.Count == 0 && taskErrors.Values.All(l => l.Count == 0);
        return new ValidationReport(ok, taskErrors, taskWarnings, global, onlyTaskId is null ? globalWarnings : [SingleTaskGlobalWarning]);
    }

    /// <summary>Copies a report into the plan: task Errors are replaced, validator warnings (prefixed) replace the previous
    /// validator warnings; generator warnings stay. <paramref name="full"/> = the report covers the whole plan: only then are
    /// plan.Errors and the prefixed lines of plan.Warnings replaced. A partial run checked no global statement, so it leaves both
    /// alone. Lines of plan.Warnings that do not START with the prefix (generator notices, e.g. discarded custom SQL) always survive.</summary>
    public static void Apply(SqlPlanPayload plan, ValidationReport report, bool full = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);
        foreach (var (id, errors) in report.TaskErrors)
            if (plan.Tasks.TryGetValue(id, out var task)) task.Errors = errors.ToList();
        foreach (var (id, warnings) in report.TaskWarnings)
            if (plan.Tasks.TryGetValue(id, out var task)) task.Warnings = ReplacePrefixed(task.Warnings, warnings);
        if (!full) return;
        plan.Errors = report.GlobalErrors.ToList();
        plan.Warnings = ReplacePrefixed(plan.Warnings, report.GlobalWarnings ?? []);
    }

    static List<string> ReplacePrefixed(List<string> existing, IEnumerable<string> validatorLines) =>
        existing.Where(w => !w.StartsWith(WarningPrefix, StringComparison.Ordinal)).Concat(validatorLines.Select(w => WarningPrefix + w)).ToList();

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
        return errors;
    }

    /// <summary>Middle of a bare-CR error: "&lt;field&gt;: bare carriage return at line &lt;N&gt; (…)".</summary>
    public const string BareCarriageReturnMarker = ": bare carriage return at line ";

    const string BareCarriageReturnAdvice = " (SQL Server treats it as a line break; use CRLF or LF)";

    static readonly Regex BareCarriageReturnLine = new(
        @"^(?:preSql\[\d+\]|postSql\[\d+\]|sourceQuery|stagingDdl|mergeSql): bare carriage return at line \d+ \(SQL Server treats it as a line break; use CRLF or LF\)$",
        RegexOptions.CultureInvariant);

    /// <summary>Every lone <c>\r</c> (a CR not followed by LF) in every SQL body of the plan, one line per occurrence, offline. SQL Server
    /// ends a <c>--</c> comment at a lone CR while <see cref="TaskListing"/> keeps it inside one line, so one listed line can hold two
    /// executable lines. Scope "plan" (global preSql then postSql) first, then each task in <see cref="SqlPlanPayload.Order"/>, then any
    /// task Order misses (ordinal). Line = "&lt;field&gt;: bare carriage return at line &lt;N&gt; (…)".
    /// <para>N for a task field is its <see cref="TaskListing"/> number. N for a global statement is its number in the screen's global
    /// card, which lists the statements joined by a <c>GO</c> line and numbers them by the same rules; the field names the statement.</para></summary>
    public static List<(string Scope, string Line)> FindBareCarriageReturns(SqlPlanPayload plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var found = new List<(string, string)>();
        foreach (var (name, statements) in new[] { ("preSql", plan.PreSql), ("postSql", plan.PostSql) })
        {
            var offset = 0;
            var list = statements ?? [];
            for (var i = 0; i < list.Count; i++)
            {
                var own = OwnListing(list[i]);
                foreach (var l in own) AddOccurrences("plan", $"{name}[{i}]", offset + l.No, l.Text);
                offset += Math.Max(own.Count, 1) + 1;   // an empty statement is still one card line; "GO" separates statements
            }
        }
        var tasks = plan.Tasks ?? [];
        var ids = (plan.Order ?? []).Where(tasks.ContainsKey)
            .Concat(tasks.Keys.OrderBy(k => k, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var task = tasks[id];
            if (task is null) continue;
            var offset = 0;
            foreach (var (field, text) in ListingFields(task))
            {
                var own = OwnListing(text);
                foreach (var l in own) AddOccurrences(id, field, offset + l.No, l.Text);
                offset += own.Count;
            }
            if (offset != TaskListing.Build(task).Count)
                throw new InvalidOperationException("bare carriage return check: the field walk no longer matches TaskListing");
        }
        return found;

        void AddOccurrences(string scope, string field, int line, string text)
        {
            foreach (var c in text)
                if (c == '\r') found.Add((scope, FormattableString.Invariant($"{field}{BareCarriageReturnMarker}{line}{BareCarriageReturnAdvice}")));
        }
    }

    /// <summary>Replaces the bare-CR lines stored in plan.Errors and each task's Errors with the current ones (re-derived on every run:
    /// a fixed CR clears) and returns them as "&lt;scope&gt;: &lt;line&gt;". Other stored errors are left alone.</summary>
    public static List<string> RecordBareCarriageReturns(SqlPlanPayload plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var found = FindBareCarriageReturns(plan);
        plan.Errors = [.. (plan.Errors ?? []).Where(e => !IsBareCarriageReturnLine(e)), .. found.Where(f => f.Scope == "plan").Select(f => f.Line)];
        foreach (var (id, task) in plan.Tasks ?? [])
            if (task is not null)
                task.Errors = [.. (task.Errors ?? []).Where(e => !IsBareCarriageReturnLine(e)), .. found.Where(f => f.Scope == id).Select(f => f.Line)];
        return found.Select(f => $"{f.Scope}: {f.Line}").ToList();
    }

    static bool IsBareCarriageReturnLine(string? error) => error is not null && BareCarriageReturnLine.IsMatch(error);

    /// <summary>Open item 17: <paramref name="report"/> (live, or null when live validation could not run) with this plan's bare-CR lines
    /// added as errors — plan scope to GlobalErrors, task scope to that task's TaskErrors (only <paramref name="onlyTaskId"/>'s when
    /// given) — and Ok false whenever there is one. Live validation compiles the SQL, and SQL Server reads a bare CR as a line break, so a
    /// patch whose only defect is one compiles cleanly; the offline scan is what refuses it, exactly as <c>SqlModule.Validate</c> does.
    /// With no live report, GlobalWarnings carries <paramref name="skipped"/>, never [] (which would claim the globals were checked).
    /// Returns null when there is no live report and nothing to report offline.</summary>
    public static ValidationReport? WithBareCarriageReturns(ValidationReport? report, SqlPlanPayload plan, string? onlyTaskId, string skipped)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var found = FindBareCarriageReturns(plan).Where(f => f.Scope == "plan" || onlyTaskId is null || f.Scope == onlyTaskId).ToList();
        if (report is null && found.Count == 0) return null;
        var taskErrors = report?.TaskErrors.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        var globalErrors = report?.GlobalErrors.ToList() ?? [];
        foreach (var (scope, line) in found)
        {
            if (scope == "plan") globalErrors.Add(line);
            else (taskErrors.TryGetValue(scope, out var list) ? list : taskErrors[scope] = []).Add(line);
        }
        return new ValidationReport((report?.Ok ?? true) && found.Count == 0, taskErrors,
            report?.TaskWarnings ?? new Dictionary<string, List<string>>(StringComparer.Ordinal), globalErrors, report?.GlobalWarnings ?? [skipped]);
    }

    /// <summary>One text numbered on its own by <see cref="TaskListing"/>'s rules (the skip rule included).</summary>
    static List<ListingLine> OwnListing(string? text) => TaskListing.Build(new TaskPlan { SourceQuery = text ?? "" });

    /// <summary>The task's SQL fields in <see cref="TaskListing"/> input order.</summary>
    static IEnumerable<(string Field, string? Text)> ListingFields(TaskPlan task)
    {
        var pre = task.PreSql ?? [];
        for (var i = 0; i < pre.Count; i++) yield return ($"preSql[{i}]", pre[i]);
        yield return ("sourceQuery", task.SourceQuery);
        yield return ("stagingDdl", task.StagingDdl);
        yield return ("mergeSql", task.MergeSql);
        var post = task.PostSql ?? [];
        for (var i = 0; i < post.Count; i++) yield return ($"postSql[{i}]", post[i]);
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

        // Target statements, one at a time, in execution order. mergeSql is checked against the engine-built #stg scaffold.
        if (target is null) return;
        var fields = TargetFields(task).ToList();
        try
        {
            // #stg drops: task start (here), immediately before the scaffold create, and in finally. In the happy path the three are
            // MUTUALLY REDUNDANT: the previous task's finally drop already clears #stg before this one; removing any single drop
            // breaks no test (analysed equivalent mutants). They are kept as defence in depth, because DropScaffoldAsync swallows its
            // own exceptions, so each drop backstops another's SILENT failure. Together they guarantee this task's statements (a
            // custom direct task may name #stg) never bind against an earlier task's scaffold.
            await DropScaffoldAsync(target);
            var beforeMerge = fields.TakeWhile(f => f.Field != "mergeSql").ToList();
            await CheckSequenceAsync(target, beforeMerge, task.Custom, errors, warnings, scrub, ct);
            if (fields.Count == beforeMerge.Count) return;

            // Rule 10: drop-then-create IMMEDIATELY before this mergeSql check. The connection is shared by every task and #stg is a
            // fixed name, so a wider scaffold left by an earlier task would let a narrower task's merge bind and false-pass.
            // DELIBERATELY REDUNDANT today (see the task-start comment): nothing executes between the task-start drop and here
            // (sp_describe executes nothing). Keep it, so a future executing step added before the merge check, or a silently failed
            // earlier drop, cannot reintroduce the stale-scaffold false pass.
            await DropScaffoldAsync(target);
            string? scaffoldFailure = null;
            if (table is not null)
            {
                var columns = task.Columns.Select(b => targetCols.GetValueOrDefault(b.Target)).OfType<ColumnInfo>()
                    .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
                var keys = task.KeyColumns.Select(k => (k, (ColumnInfo?)null));   // key types live in the source catalog: sql_variant
                try { await ExecAsync(target, SqlGenerator.StagingDdl(columns, keys), ct); }
                catch (SqlException ex) { scaffoldFailure = Messages(ex); }
            }
            if (scaffoldFailure is not null)
                warnings.Add($"mergeSql{NotCheckedMarker}the staging scaffold could not be created ({scrub(scaffoldFailure)})");
            else
                await CheckSequenceAsync(target, fields, task.Custom, errors, warnings, scrub, ct, skip: beforeMerge.Count);
        }
        finally
        {
            await DropScaffoldAsync(target);   // third of the mutually redundant #stg drops (see the task-start comment)
        }
    }

    /// <summary>Checks <paramref name="statements"/>[skip..] one by one; earlier statements (all of them) feed the rule-8 lookup.</summary>
    static async Task CheckSequenceAsync(SqlConnection target, List<(string Field, string Sql)> statements, bool customMerge,
        List<string> errors, List<string> warnings, Func<string, string> scrub, CancellationToken ct, int skip = 0)
    {
        for (var i = skip; i < statements.Count; i++)
        {
            var (field, sql) = statements[i];
            if (string.IsNullOrWhiteSpace(sql)) continue;
            var check = await CheckTargetStatementAsync(target, sql, ct);
            if (check.NotChecked is not null) { warnings.Add(field + NotCheckedMarker + scrub(check.NotChecked)); continue; }
            if (MayHoldSeveralStatements(sql)) warnings.Add(field + NotCheckedMarker + MultipleStatementsReason);   // plus any error below
            if (check.Error is null) continue;
            if (Unverifiable(check.Errors, field, customMerge, statements.Take(i)) is { } reason)
                warnings.Add(field + NotCheckedMarker + scrub(reason));
            else
                errors.Add($"{field}: {scrub(check.Error)}");
        }
    }

    // Rules 6-8 classify a SERVER-AUTHORED error message to choose a report label ("error" or "not checked"). This is not the text
    // filter that was voided as a security boundary: nothing here decides what is sent or executed — every statement is only ever
    // described, never run — so gaming these rules can only hide the author's own broken SQL from themselves (it then fails at the
    // real migration). They carry no security load. Rule 8 is an acknowledged heuristic: worst case a false positive or a missed typo.
    static string? Unverifiable(IReadOnlyList<SqlError> serverErrors, string field, bool customTask, IEnumerable<(string Field, string Sql)> earlier)
    {
        var earlierList = earlier.ToList();
        var reasons = new List<string>();
        foreach (var e in serverErrors)
        {
            if (e.Number == InvalidObjectName && QuotedName(e.Message) is { } name)
            {
                // Rule 6: a '#' name is provably a temp object (a permanent table cannot be named with a leading '#').
                if (name.StartsWith('#')) { reasons.Add($"temp table '{name}' does not exist during validation"); continue; }
                // Rule 8: created by an earlier statement of the same sequence, which validation never executes.
                if (earlierList.FirstOrDefault(s => Creates(s.Sql, name)) is { Field: not null } creator)
                { reasons.Add($"'{name}' is created by {creator.Field}, which validation does not execute"); continue; }
            }
            // Rule 7: [207] never names its object. Only a generated task's mergeSql is known to match the scaffold exactly.
            if (e.Number == InvalidColumnName && field == "mergeSql" && customTask)
            { reasons.Add($"custom staging columns are unknown to validation ({e.Message})"); continue; }
            return null;   // at least one real error: report the statement as an error
        }
        return reasons.Count == 0 ? null : string.Join("; ", reasons.Distinct(StringComparer.Ordinal));
    }

    const string MultipleStatementsReason = "binding not verified: the field may hold multiple statements, and later statements are not reliably checked";

    // Heuristic with NO security load, same class as rules 6-8: it only chooses to ADD a disclosure. sp_describe reports parse errors
    // for the whole string but may silently skip binding errors in a later statement, so a field that may hold several statements
    // gets any server error reported AND a visible "binding not verified" line. Conservative on purpose: a ';' inside a literal or
    // comment over-flags, which only adds an honest warning, never an error. Generated SQL is always one statement.
    static bool MayHoldSeveralStatements(string sql)
    {
        var trimmed = sql.TrimEnd();
        if (trimmed.EndsWith(';')) trimmed = trimmed[..^1];
        return trimmed.Contains(';', StringComparison.Ordinal);
    }

    static string? QuotedName(string message)
    {
        var first = message.IndexOf('\'');
        var last = message.LastIndexOf('\'');
        return first >= 0 && last > first ? message[(first + 1)..last] : null;
    }

    /// <summary>Heuristic (rule 8): does <paramref name="sql"/> textually create <paramref name="name"/> ("schema.table" or "table")?</summary>
    static bool Creates(string sql, string name)
    {
        var target = SplitName(name);
        foreach (Match m in CreatesObject.Matches(sql.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal)))
        {
            var created = SplitName(m.Groups["name"].Value);
            if (!string.Equals(created.Name, target.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (created.Schema is null || target.Schema is null || string.Equals(created.Schema, target.Schema, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static readonly Regex CreatesObject = new(@"\b(?:CREATE\s+(?:TABLE|VIEW|SYNONYM)|INTO)\s+(?<name>[^\s(;,]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static (string? Schema, string Name) SplitName(string qualified)
    {
        var parts = qualified.Split('.');
        return parts.Length == 1 ? (null, parts[0]) : (parts[^2], parts[^1]);
    }

    /// <summary>Result columns of the first result set, or the compile error message(s). Never executes <paramref name="sql"/>:
    /// it is passed only as the <c>@tsql</c> argument of <c>sp_describe_first_result_set</c>.</summary>
    public static async Task<(List<ResultColumn>? Columns, string? Error)> DescribeAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(sql);
        try
        {
            return (await DescribeColumnsAsync(conn, sql, ct), null);
        }
        catch (SqlException ex) { return (null, Messages(ex)); }
    }

    /// <summary>Checks one target statement without executing it, via <c>sp_describe_first_result_set</c>. For a target statement an
    /// empty result set means VALID (DML and DDL return none) — unlike a source query. When the server gives only a "could not be
    /// analyzed / determined" wrapper with no underlying error (e.g. dynamic SQL), the statement is reported as not checked, never
    /// as passing or failing. Context-free: the task-level rules (temp tables, earlier creators, custom staging) are applied by
    /// ValidateAsync, which is the entry point to use for a plan.</summary>
    public static async Task<TargetCheck> CheckTargetStatementAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql)) return new TargetCheck(null, null, []);
        try
        {
            await DescribeColumnsAsync(conn, sql, ct);
            return new TargetCheck(null, null, []);
        }
        catch (SqlException ex)
        {
            var real = RealErrors(ex);
            return real.Count == 0
                ? new TargetCheck(null, ex.Message, [])
                : new TargetCheck(string.Join(" ", real.Select(e => e.Message).Distinct(StringComparer.Ordinal)), null, real);
        }
    }

    static async Task<List<ResultColumn>> DescribeColumnsAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("EXEC sp_describe_first_result_set @tsql = @q;", conn) { CommandTimeout = 60 };
        cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, -1) { Value = sql });
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
        return list;
    }

    /// <summary>Engine-authored batches only (the scaffold and its drop). Never called with plan SQL.</summary>
    static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Best effort and never masks an earlier error: a failure here leaves at most a session-local temp table that the
    /// non-pooled connection drops on close.</summary>
    static async Task DropScaffoldAsync(SqlConnection conn)
    {
        try { await ExecAsync(conn, "IF OBJECT_ID('tempdb..#stg') IS NOT NULL DROP TABLE #stg;", CancellationToken.None); }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException) { /* see summary */ }
    }

    static List<SqlError> RealErrors(SqlException ex) =>
        ex.Errors.Cast<SqlError>()
            .Where(e => !e.Message.StartsWith("The batch could not be analyzed", StringComparison.Ordinal)
                     && !e.Message.StartsWith("The metadata could not be determined", StringComparison.Ordinal))
            .ToList();

    static string Messages(SqlException ex)
    {
        var parts = RealErrors(ex).Select(e => e.Message).Distinct().ToList();
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
