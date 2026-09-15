using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.SqlGen;

/// <summary>Phase module for the SQL review loop (agent "sql-engineer", job "sqlgen").</summary>
public sealed class SqlModule : IPhaseModule
{
    static readonly TimeSpan LiveTimeout = TimeSpan.FromMinutes(3);

    /// <summary>ApprovalBlockers line for a plan whose stored warnings record that live validation did not run.</summary>
    public const string NotValidatedBlocker = "plan is not validated: ";

    public PhaseName Phase => PhaseName.Sql;
    public string Agent => "sql-engineer";
    public string JobKind => "sqlgen";

    public bool NeedsAgent(JsonNode draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var plan = Json.FromNode<SqlPlanPayload>(draft);
        return plan.Errors.Count > 0 || plan.Tasks.Values.Any(NeedsWork);
    }

    static bool NeedsWork(TaskPlan t) => t.Errors.Count > 0 || (t.Mode == "staging_merge" && !t.Custom);

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var plan = Json.Deserialize<SqlPlanPayload>(ctx.Current.PayloadJson);
        var mapping = SqlPlanSource.ApprovedMapping(ctx.Services);
        var tgt = ctx.Services.Catalog.Get(Side.Tgt);
        var data = new JsonObject
        {
            ["legend"] = Legend(),
            ["servers"] = new JsonObject
            {
                ["src"] = ServerNode(ctx.Services.Connections.GetMeta(Side.Src)),
                ["tgt"] = ServerNode(ctx.Services.Connections.GetMeta(Side.Tgt)),
            },
            ["order"] = Strings(plan.Order),
            ["pre"] = Strings(plan.PreSql),
            ["post"] = Strings(plan.PostSql),
            ["planErrors"] = Strings(plan.Errors),
        };

        var detailed = new HashSet<string>(StringComparer.Ordinal);
        if (mode == PacketMode.Rework)
        {
            var context = new JsonObject();
            foreach (var fb in ctx.OpenFeedback)
            {
                var key = fb.Id.ToString(CultureInfo.InvariantCulture);
                var (taskId, line) = ParseAnchor(fb.Anchor);
                if (taskId is null || !plan.Tasks.TryGetValue(taskId, out var task))
                {
                    context[key] = new JsonObject { ["anchor"] = fb.Anchor ?? "general", ["scope"] = "whole plan: see order/pre/post and the task summaries" };
                    continue;
                }
                var item = new JsonObject { ["anchor"] = fb.Anchor, ["task"] = Detail(taskId, task, mapping, tgt) };
                if (line is not null)
                {
                    var listed = TaskListing.Build(task).FirstOrDefault(l => l.No == line);
                    item["line"] = line;
                    item["section"] = listed?.Section ?? "(line no longer exists)";
                    item["listing"] = TaskListing.Render(task, line);
                }
                context[key] = item;
                detailed.Add(taskId);
            }
            data["context"] = context;
        }

        var work = new JsonArray();
        foreach (var id in plan.Order)
            if (plan.Tasks.TryGetValue(id, out var t) && !detailed.Contains(id) && NeedsWork(t))
            {
                work.Add(Detail(id, t, mapping, tgt));
                detailed.Add(id);
            }
        data["work"] = work;

        var others = new JsonArray();
        foreach (var id in plan.Order)
            if (plan.Tasks.TryGetValue(id, out var t) && !detailed.Contains(id))
                others.Add(new JsonObject
                {
                    ["id"] = id,
                    ["target"] = t.Target,
                    ["mode"] = t.Mode,
                    ["cols"] = Strings(t.Columns.Select(ColumnText)),
                    ["keys"] = Strings(t.KeyColumns),
                    ["warns"] = t.Warnings.Count,
                    ["custom"] = t.Custom,
                });
        data["others"] = others;
        return data;
    }

    /// <summary>Normalises <paramref name="payload"/> IN PLACE (Custom flags, CountSql, validation results): WorkflowEngine.Apply
    /// stores this same node, so a copy would silently lose the flags.
    /// <para>The patch surface may write SQL, never validation evidence: a payload that changes <c>errors</c>, <c>warnings</c>
    /// (plan or task) or <c>custom</c> against the base is rejected before anything is normalised. The engine then writes its own
    /// lines — including, when live validation cannot run, the stored <see cref="SqlPlanSource.SkippedPrefix"/> marker that blocks
    /// approval (re-derived on every run: added offline, removed live).</para></summary>
    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload is not JsonObject) return new PayloadCheck(["payload is not a SQL plan: expected a JSON object"], []);
        SqlPlanPayload plan;
        try { plan = Json.FromNode<SqlPlanPayload>(payload); }
        catch (JsonException ex) { return new PayloadCheck([$"payload is not a SQL plan: {ex.Message}"], []); }
        if (StructureErrors(plan) is { Count: > 0 } structure) return new PayloadCheck(structure, []);
        SqlPlanPayload? basePlan = null;
        try { basePlan = Json.Deserialize<SqlPlanPayload>(ctx.Current.PayloadJson); } catch (JsonException) { }
        if (EngineOwnedChanges(plan, basePlan) is { Count: > 0 } owned) return new PayloadCheck(owned, []);

        foreach (var (id, task) in plan.Tasks)
        {
            var before = basePlan?.Tasks.GetValueOrDefault(id);
            task.Custom = before is null || before.Custom || SqlChanged(task, before);
            task.CountSql = SqlGenerator.CountSql(task.SourceQuery);
        }

        var errors = plan.OrderProblems();
        var warnings = new List<string>();
        var skipped = SqlPlanSource.SkippedWarning(ctx.Services);
        if (skipped is null)
        {
            using var cts = new CancellationTokenSource(LiveTimeout);
            var report = SqlPlanSource.ValidateLiveAsync(ctx.Services, plan, null, cts.Token).GetAwaiter().GetResult();
            SqlValidator.Apply(plan, report);
            plan.Warnings = WithoutSkippedMarker(plan.Warnings);
            errors.AddRange(report.GlobalErrors);
            var ids = plan.Order.Concat(plan.Tasks.Keys.OrderBy(k => k, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var id in ids)
                if (report.TaskErrors.TryGetValue(id, out var list)) errors.AddRange(list.Select(e => $"{id}: {e}"));
            // Dry-run is where an agent checks its own patch, and the plan it holds predates this validation: every "not checked"
            // line of THIS run must reach it, global and task alike. Other task warnings stay in the stored plan.
            warnings.AddRange(report.GlobalWarnings);
            foreach (var id in ids)
                if (report.TaskWarnings.TryGetValue(id, out var list))
                    warnings.AddRange(list.Where(w => w.Contains(SqlValidator.NotCheckedMarker, StringComparison.Ordinal)).Select(w => $"{id}: {w}"));
        }
        else
        {
            plan.Warnings = [.. WithoutSkippedMarker(plan.Warnings), skipped];
            warnings.Add(skipped);
            foreach (var (id, task) in plan.Tasks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                errors.AddRange(SqlValidator.CheckShape(task).Select(e => $"{id}: {e}"));
        }

        ReplaceContent(payload.AsObject(), plan);
        return new PayloadCheck(errors, warnings);
    }

    static List<string> WithoutSkippedMarker(List<string> warnings) =>
        warnings.Where(w => !w.StartsWith(SqlPlanSource.SkippedPrefix, StringComparison.Ordinal)).ToList();

    /// <summary>Errors, warnings and custom are engine-authored evidence. Compared exactly (ordinal, order-sensitive, whole list) against
    /// the base, BEFORE Validate writes anything. Tasks only in the base were deleted (allowed; their lines go with them); tasks only in
    /// the payload are new and must arrive with no errors and no warnings (their custom flag is set by the engine regardless).</summary>
    static List<string> EngineOwnedChanges(SqlPlanPayload plan, SqlPlanPayload? basePlan)
    {
        const string ByValidation = "recorded by validation";
        const string ByEngine = "recorded by the generator and validation";
        static bool Same(List<string> a, List<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);
        static string Owned(string scope, string field, string owner) => $"{scope}: {field} is engine-owned ({owner}); a patch may not change it";

        var errors = new List<string>();
        if (!Same(plan.Errors, basePlan?.Errors ?? [])) errors.Add(Owned("plan", "errors", ByValidation));
        if (!Same(plan.Warnings, basePlan?.Warnings ?? [])) errors.Add(Owned("plan", "warnings", ByEngine));
        foreach (var (id, task) in plan.Tasks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var before = basePlan?.Tasks.GetValueOrDefault(id);
            if (!Same(task.Errors, before?.Errors ?? [])) errors.Add(Owned(id, "errors", ByValidation));
            if (!Same(task.Warnings, before?.Warnings ?? [])) errors.Add(Owned(id, "warnings", ByEngine));
            if (before is not null && task.Custom != before.Custom) errors.Add(Owned(id, "custom", "set when the task's SQL changes"));
        }
        return errors;
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var plan = Json.FromNode<SqlPlanPayload>(payload);
        var blockers = plan.Errors.Select(e => $"plan: {e}").ToList();
        // Absence of errors is not evidence of validation: a plan nothing compiled cannot be approved.
        blockers.AddRange(plan.Warnings.Where(w => w.StartsWith(SqlPlanSource.SkippedPrefix, StringComparison.Ordinal)).Select(w => NotValidatedBlocker + w));
        foreach (var id in plan.Order.Where(plan.Tasks.ContainsKey).Distinct(StringComparer.Ordinal))
            if (plan.Tasks[id].Errors.Count > 0) blockers.Add($"{id} ({plan.Tasks[id].Target}): {plan.Tasks[id].Errors.Count} validation error(s)");
        var orderSet = new HashSet<string>(plan.Order, StringComparer.Ordinal);
        if (plan.Order.Count != plan.Tasks.Count || !orderSet.SetEquals(plan.Tasks.Keys))
            blockers.Add("order must list every task id exactly once");
        return blockers;
    }

    public string Summarize(JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var plan = Json.FromNode<SqlPlanPayload>(payload);
        var errors = plan.ErrorCount();
        return FormattableString.Invariant($"{plan.Tasks.Count} tasks, {plan.Tasks.Values.Count(t => t.Custom)} custom, {plan.WarningCount()} warnings")
            + (errors > 0 ? FormattableString.Invariant($", {errors} errors") : "");
    }

    /// <summary>"task:T04" → (T04, null); "sql:T04:12" → (T04, 12); anything else → (null, null).
    /// The line must be plain ASCII decimal digits (no sign, no spaces) and ≥ 1 — the numbering of <see cref="TaskListing"/>.</summary>
    public static (string? TaskId, int? Line) ParseAnchor(string? anchor)
    {
        if (anchor is null) return (null, null);
        if (anchor.StartsWith("task:", StringComparison.Ordinal)) return (anchor[5..], null);
        if (anchor.StartsWith("sql:", StringComparison.Ordinal))
        {
            var rest = anchor[4..];
            var colon = rest.LastIndexOf(':');
            if (colon > 0 && int.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line >= 1)
                return (rest[..colon], line);
        }
        return (null, null);
    }

    static JsonObject Detail(string id, TaskPlan t, MappingPayload? mapping, CatalogSnapshot? tgt)
    {
        var p = $"/tasks/{id}";
        var o = new JsonObject
        {
            ["id"] = id,
            ["target"] = t.Target,
            ["mode"] = t.Mode,
            ["keys"] = Strings(t.KeyColumns),
            ["identityInsert"] = t.IdentityInsert,
            ["chunkSize"] = t.ChunkSize,
            ["custom"] = t.Custom,
            ["dependsOn"] = Strings(t.DependsOn),
            ["cols"] = Strings(t.Columns.Select(ColumnText)),
            ["sourceQuery"] = t.SourceQuery,
            ["stagingDdl"] = t.StagingDdl,
            ["mergeSql"] = t.MergeSql,
            ["preSql"] = Strings(t.PreSql),
            ["postSql"] = Strings(t.PostSql),
            ["errors"] = Strings(t.Errors),
            ["warnings"] = Strings(t.Warnings),
            ["paths"] = new JsonObject
            {
                ["sourceQuery"] = p + "/sourceQuery", ["stagingDdl"] = p + "/stagingDdl", ["mergeSql"] = p + "/mergeSql",
                ["preSql"] = p + "/preSql", ["postSql"] = p + "/postSql", ["columns"] = p + "/columns", ["keyColumns"] = p + "/keyColumns",
            },
        };
        if (mapping is not null && MappingValidator.FindTableMap(mapping, t.Target) is { } map) o["tableMap"] = WithoutCandidates(map);
        if (tgt?.FindTable(t.Target) is { } table)
            o["targetColumns"] = Strings(table.Columns.OrderBy(c => c.Ordinal).Select(c =>
                $"{c.Name} {SqlTypeText.Format(c)} {(c.IsNullable ? "NULL" : "NOT NULL")}"
                + (c.IsIdentity ? " IDENTITY" : "") + (c.IsComputed ? " COMPUTED" : "") + (c.IsRowVersion ? " ROWVERSION" : "")
                + (c.DefaultDefinition is { Length: > 0 } d ? " DEFAULT " + d : "")));
        return o;
    }

    static JsonObject Legend() => new()
    {
        ["work"] = "tasks you must fix or complete, in full",
        ["others"] = "tasks that validated cleanly, summarised; `dbm artifact sql --path /tasks/<id>` prints one in full",
        ["context"] = "rework only: per feedback id, the anchored task in full; for sql:<id>:<line> anchors also the numbered listing ('>>' marks the line)",
        ["servers"] = "source/target product version, major version, edition, database compatibility level and collation; write T-SQL both accept",
        ["cols"] = "column bindings; 'alias->Target' when the SourceQuery alias differs from the target column",
        ["keys"] = "key aliases (__k0, __k1, ...) selected in SourceQuery; empty = single-transaction load",
        ["sourceQuery"] = "SELECT <expr> AS [TargetColumn], ..., s.[k] AS [__kN] FROM ... [WHERE ...]; runs on the SOURCE; no ORDER BY, no TOP",
        ["stagingDdl"] = "staging_merge only: CREATE TABLE #stg (...); runs on the TARGET before each chunk is bulk-copied into #stg",
        ["mergeSql"] = "staging_merge only: moves #stg rows into the target (INSERT...SELECT or MERGE ... ;), same transaction as the chunk",
        ["pre"] = "global statements on the TARGET before the first task (patch path /preSql)",
        ["post"] = "global statements on the TARGET after the last task (patch path /postSql)",
        ["targetColumns"] = "name type NULL|NOT NULL [IDENTITY|COMPUTED|ROWVERSION] [DEFAULT expr]",
        ["tableMap"] = "the approved mapping for this target; expressions are over the FROM aliases (s = primary source)",
        ["paths"] = "JSON pointers for patch ops on this task",
    };

    /// <summary>Only these five fields of ServerMeta: never Server, Database or AuthSummary (parts of a connection string), and never
    /// the connection string itself — the packet goes to an agent, into logs and into exports.</summary>
    static JsonNode? ServerNode(ServerMeta? m) => m is null ? null : new JsonObject
    {
        ["version"] = m.ProductVersion,
        ["major"] = m.MajorVersion,
        ["edition"] = m.Edition,
        ["compatLevel"] = m.CompatLevel,
        ["collation"] = m.DatabaseCollation,
    };

    static string ColumnText(ColumnBinding b) => b.Source == b.Target ? b.Target : $"{b.Source}->{b.Target}";

    static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    static JsonNode WithoutCandidates(TableMap map)
    {
        var node = Json.ToNode(map).AsObject();
        node.Remove("candidates");
        if (node["columns"] is JsonObject cols)
            foreach (var (_, col) in cols)
                (col as JsonObject)?.Remove("candidates");
        return node;
    }

    /// <summary>Explicit JSON nulls where the model has no null (a patch can write them) would crash the checks below.</summary>
    static List<string> StructureErrors(SqlPlanPayload plan)
    {
        var errors = new List<string>();
        if (plan.PreSql is null || plan.PostSql is null || plan.Order is null || plan.Tasks is null || plan.Warnings is null || plan.Errors is null)
            errors.Add("preSql, postSql, order, tasks, warnings and errors must not be null");
        else if (plan.Order.Any(s => s is null) || plan.Warnings.Any(s => s is null) || plan.Errors.Any(s => s is null)
                 || plan.PreSql.Any(s => s is null) || plan.PostSql.Any(s => s is null))
            errors.Add("preSql, postSql, order, warnings and errors must not contain null");
        foreach (var (id, t) in (plan.Tasks ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (t is null) { errors.Add($"{id}: task must not be null"); continue; }
            if (t.Target is null || t.Mode is null || t.SourceQuery is null || t.KeyColumns is null || t.Columns is null || t.PreSql is null
                || t.PostSql is null || t.DependsOn is null || t.Warnings is null || t.Errors is null
                || t.Columns.Any(c => c is null || c.Source is null || c.Target is null)
                || t.PreSql.Any(s => s is null) || t.PostSql.Any(s => s is null) || t.KeyColumns.Any(s => s is null)
                || t.DependsOn.Any(s => s is null) || t.Warnings.Any(s => s is null) || t.Errors.Any(s => s is null))
                errors.Add($"{id}: only stagingDdl, mergeSql, chunkSize and mappingHash may be null");
        }
        return errors;
    }

    static bool SqlChanged(TaskPlan a, TaskPlan b)
    {
        static string N(string? s) => (s ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        static bool Same(List<string> x, List<string> y) => x.Select(N).SequenceEqual(y.Select(N), StringComparer.Ordinal);
        return N(a.SourceQuery) != N(b.SourceQuery) || N(a.StagingDdl) != N(b.StagingDdl) || N(a.MergeSql) != N(b.MergeSql)
            || !Same(a.PreSql, b.PreSql) || !Same(a.PostSql, b.PostSql) || a.Mode != b.Mode
            || !a.KeyColumns.SequenceEqual(b.KeyColumns, StringComparer.Ordinal) || !a.Columns.SequenceEqual(b.Columns)
            || a.IdentityInsert != b.IdentityInsert || a.ChunkSize != b.ChunkSize;
    }

    /// <summary>Replaces the node's content with the normalised plan, keeping the node itself (the one the engine stores).</summary>
    static void ReplaceContent(JsonObject target, SqlPlanPayload plan)
    {
        var fresh = Json.ToNode(plan).AsObject();
        target.Clear();
        foreach (var key in fresh.Select(p => p.Key).ToList())
        {
            var value = fresh[key];
            fresh.Remove(key);
            target[key] = value;
        }
    }
}
