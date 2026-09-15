using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Sql;

namespace Dbm.Core.SqlGen;

/// <summary>Turns an approved mapping into a migration plan: one task per non-skip TableMap, ordered parents-first.</summary>
public static class SqlGenerator
{
    public const int LobChunkSize = 5000;
    public const string NoKeyWarning = "no key: single-transaction load";
    public const string ReviewMergeWarning = "review merge SQL";
    public const string CarriedWarning = "custom SQL carried over from the previous plan";
    public const string DiscardedWarning = "custom SQL discarded: the mapping for this table changed";

    /// <summary>Rendered as "&lt;Col&gt;: " + this for a column whose typeRisk is <see cref="TypeCompat.UnevaluatedRisk"/>: the conversion
    /// could not be evaluated, which is a different claim from a conversion evaluated and found lossy.</summary>
    public const string UnevaluatedWarning = "conversion not verified: custom expression";

    public static SqlPlanPayload Generate(MappingPayload mapping, CatalogSnapshot src, CatalogSnapshot tgt, SqlPlanPayload? carryOver = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(tgt);
        var plan = new SqlPlanPayload();

        // 1. Mapped targets (exact catalog case), skipping "skip" maps and unknown tables.
        var maps = new Dictionary<string, (TableInfo Table, TableMap Map)>(StringComparer.Ordinal);
        foreach (var (key, map) in mapping.Tables.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (string.Equals(map.Kind, "skip", StringComparison.OrdinalIgnoreCase)) continue;
            var table = tgt.FindTable(key);
            if (table is null) { plan.Warnings.Add($"{key}: target table not found in the target catalog; no task generated"); continue; }
            maps[table.Key] = (table, map);
        }

        // 2. FK edges (child -> parent) among mapped targets.
        var fksByEdge = new Dictionary<(string Child, string Parent), List<ForeignKeyInfo>>();
        foreach (var (key, (table, _)) in maps)
            foreach (var fk in table.ForeignKeys)
            {
                var parent = tgt.FindTable(fk.RefKey)?.Key;
                if (parent is null || !maps.ContainsKey(parent)) continue;
                if (!fksByEdge.TryGetValue((key, parent), out var list)) fksByEdge[(key, parent)] = list = new List<ForeignKeyInfo>();
                list.Add(fk);
            }
        var topo = TopoSort.Sort(maps.Keys.ToList(), fksByEdge.Keys.ToList(),
            (child, parent) => fksByEdge[(child, parent)].All(fk => fk.Columns.All(c => maps[child].Table.FindColumn(c)?.IsNullable == true)));
        var cycleEdges = new HashSet<(string Child, string Parent)>(topo.CycleEdges);

        // 3. Task ids in execution order.
        var width = Math.Max(2, topo.Order.Count.ToString(CultureInfo.InvariantCulture).Length);
        var idFormat = "D" + width.ToString(CultureInfo.InvariantCulture);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < topo.Order.Count; i++) ids[topo.Order[i]] = "T" + (i + 1).ToString(idFormat, CultureInfo.InvariantCulture);

        // 4. Tasks.
        foreach (var target in topo.Order)
        {
            var (table, map) = maps[target];
            var task = BuildTask(table, map, src, out var notes);
            task.DependsOn = fksByEdge.Keys
                .Where(e => e.Child == target && e.Parent != target && !cycleEdges.Contains(e))
                .Select(e => ids[e.Parent]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var old = carryOver?.Tasks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).FirstOrDefault(p => p is not null && p.Custom
                && string.Equals(p.Target, task.Target, StringComparison.OrdinalIgnoreCase)
                && p.MappingHash is not null && p.MappingHash == task.MappingHash);
            if (old is not null) CarryOver(task, old);
            // A custom task for this target that was NOT carried over (its mapping hash differs) is hand-written SQL being dropped: say so.
            var discarded = old is null && carryOver is not null && carryOver.Tasks.Values.Any(p => p is not null && p.Custom
                && string.Equals(p.Target, task.Target, StringComparison.OrdinalIgnoreCase));
            task.Warnings = BuildWarnings(task, table, map, notes);
            if (discarded) task.Warnings.Add(DiscardedWarning);
            plan.Tasks[ids[target]] = task;
            plan.Order.Add(ids[target]);
        }

        // 5. FK cycles: disable the cut constraints for the load, re-enable WITH CHECK afterwards.
        foreach (var edge in topo.CycleEdges)
        {
            var child = maps[edge.Child].Table;
            var tableSql = SqlQuote.Table(child.Schema, child.Name);
            foreach (var fk in fksByEdge[edge].OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                plan.PreSql.Add($"ALTER TABLE {tableSql} NOCHECK CONSTRAINT {SqlQuote.Ident(fk.Name)};");
                plan.PostSql.Add($"ALTER TABLE {tableSql} WITH CHECK CHECK CONSTRAINT {SqlQuote.Ident(fk.Name)};");
            }
            plan.Warnings.Add($"FK cycle broken at {edge.Child} -> {edge.Parent}: constraint(s) disabled during the load and re-enabled WITH CHECK afterwards");
        }
        return plan;
    }

    /// <summary>The row-count query for a source query. The newline before ")" keeps a trailing "--" comment harmless.</summary>
    public static string CountSql(string sourceQuery)
    {
        ArgumentNullException.ThrowIfNull(sourceQuery);
        return "SELECT COUNT_BIG(*) FROM (\n" + sourceQuery.TrimEnd() + "\n) AS q";
    }

    /// <summary>Lower-hex SHA-256 of the <see cref="SqlRelevant"/> projection of the TableMap, serialised with Json.Options (compact).
    /// Only fields that change the generated SQL are hashed, so a custom task survives a regeneration that merely recomputed
    /// typeRisk or touched candidates, rationale, confidence or method.</summary>
    public static string MappingHash(TableMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(SqlRelevant.Of(map))))).ToLowerInvariant();
    }

    /// <summary>THE set of mapping fields that affect generated SQL, and so the only fields <see cref="MappingHash"/> covers.
    /// A mapping field that changes the SQL is added here and nowhere else. Columns are keyed ordinally so the hash does not
    /// depend on dictionary insertion order.</summary>
    sealed record SqlRelevant(string Kind, List<string> Sources, string? From, string? Filter, SortedDictionary<string, SqlRelevantColumn> Columns)
    {
        public static SqlRelevant Of(TableMap m)
        {
            var columns = new SortedDictionary<string, SqlRelevantColumn>(StringComparer.Ordinal);
            foreach (var (name, c) in m.Columns ?? [])
                columns[name] = new SqlRelevantColumn(c?.Expr, c?.SourceColumns ?? [], c?.Default);
            return new SqlRelevant(m.Kind, m.Sources ?? [], m.From, m.Filter, columns);
        }
    }

    sealed record SqlRelevantColumn(string? Expr, List<string> SourceColumns, string? Default);

    static TaskPlan BuildTask(TableInfo table, TableMap map, CatalogSnapshot src, out List<string> notes)
    {
        notes = new List<string>();
        var task = new TaskPlan
        {
            Target = table.Key,
            Mode = string.Equals(map.Kind, "lookup", StringComparison.OrdinalIgnoreCase) ? "staging_merge" : "direct",
            MappingHash = MappingHash(map),
        };

        var selectLines = new List<string>();
        var bound = new List<ColumnInfo>();
        var referencesLob = false;
        foreach (var col in table.Columns.OrderBy(c => c.Ordinal))
        {
            var cm = FindColumnMap(map, col.Name);
            if (cm is null) continue;
            var expr = !string.IsNullOrWhiteSpace(cm.Expr) ? cm.Expr.Trim()
                : !string.IsNullOrWhiteSpace(cm.Default) ? cm.Default.Trim() : null;
            if (expr is null) continue;
            if (col.IsComputed || col.IsRowVersion)
            {
                notes.Add($"{col.Name}: target column is computed or rowversion; its mapping is ignored");
                continue;
            }
            selectLines.Add($"{expr} AS {SqlQuote.Ident(col.Name)}");
            bound.Add(col);
            task.Columns.Add(new ColumnBinding(col.Name, col.Name));
            referencesLob |= cm.SourceColumns.Any(sc => FindSourceColumn(src, sc) is { } s && SqlTypeText.IsLob(s));
        }

        TableInfo? primary = map.Sources.Count > 0 ? src.FindTable(map.Sources[0]) : null;
        if (map.Sources.Count > 0 && primary is null) notes.Add($"primary source {map.Sources[0]} not found in the source catalog");
        var keyColumns = primary?.BestKey() ?? [];
        var keyLines = new List<string>();
        var keyDdl = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var alias = "__k" + i.ToString(CultureInfo.InvariantCulture);
            keyLines.Add($"s.{SqlQuote.Ident(keyColumns[i])} AS {SqlQuote.Ident(alias)}");
            task.KeyColumns.Add(alias);
            var keyCol = primary!.FindColumn(keyColumns[i]);
            keyDdl.Add($"    {SqlQuote.Ident(alias)} {(keyCol is null ? "sql_variant" : TypeWithCollation(keyCol))} NULL");
        }

        var from = !string.IsNullOrWhiteSpace(map.From) ? map.From.Trim()
            : primary is not null ? SqlQuote.Table(primary.Schema, primary.Name) + " AS s"
            : map.Sources.Count > 0 ? SqlQuote.TableKey(map.Sources[0]) + " AS s"
            : null;
        if (from is null) notes.Add("no source table: the query returns a single row");

        var sb = new StringBuilder("SELECT");
        var items = selectLines.Concat(keyLines).ToList();
        for (var i = 0; i < items.Count; i++)
            sb.Append("\n    ").Append(items[i]).Append(i < items.Count - 1 ? "," : "");
        if (from is not null) sb.Append("\nFROM ").Append(from);
        if (!string.IsNullOrWhiteSpace(map.Filter)) sb.Append("\nWHERE (").Append(map.Filter.Trim()).Append(')');
        task.SourceQuery = sb.ToString();
        task.CountSql = CountSql(task.SourceQuery);
        task.IdentityInsert = bound.Any(c => c.IsIdentity);
        if (referencesLob || bound.Any(SqlTypeText.IsLob)) task.ChunkSize = LobChunkSize;

        if (task.Mode == "staging_merge")
        {
            var ddl = bound.Select(c => $"    {SqlQuote.Ident(c.Name)} {TypeWithCollation(c)} NULL").Concat(keyDdl);
            task.StagingDdl = "CREATE TABLE #stg (\n" + string.Join(",\n", ddl) + "\n);";
            var cols = string.Join(", ", bound.Select(c => SqlQuote.Ident(c.Name)));
            task.MergeSql = $"INSERT INTO {SqlQuote.Table(table.Schema, table.Name)} ({cols})\nSELECT {cols}\nFROM #stg;";
        }
        return task;
    }

    static void CarryOver(TaskPlan task, TaskPlan old)
    {
        task.Mode = old.Mode;
        task.SourceQuery = old.SourceQuery;
        task.StagingDdl = old.StagingDdl;
        task.MergeSql = old.MergeSql;
        task.PreSql = old.PreSql.ToList();
        task.PostSql = old.PostSql.ToList();
        task.ChunkSize = old.ChunkSize;
        task.KeyColumns = old.KeyColumns.ToList();
        task.Columns = old.Columns.ToList();
        task.IdentityInsert = old.IdentityInsert;
        task.CountSql = CountSql(task.SourceQuery);
        task.Custom = true;
    }

    static List<string> BuildWarnings(TaskPlan task, TableInfo table, TableMap map, List<string> notes)
    {
        var w = new List<string>();
        foreach (var binding in task.Columns)
            if (FindColumnMap(map, binding.Target) is { TypeRisk: { Length: > 0 } risk })
            {
                // Sentinel first, and exclusive: an unevaluated conversion is not a data-loss claim, so it never renders as the loss line.
                if (string.Equals(risk, TypeCompat.UnevaluatedRisk, StringComparison.Ordinal))
                    w.Add($"{binding.Target}: {UnevaluatedWarning}");
                else
                    w.Add($"{binding.Target}: {risk}");
            }
        w.AddRange(notes);
        if (task.KeyColumns.Count == 0) w.Add(NoKeyWarning);
        if (table.TriggerCount > 0) w.Add(string.Create(CultureInfo.InvariantCulture, $"target has {table.TriggerCount} trigger(s); not fired unless FireTriggers"));
        if (task.Mode == "staging_merge" && !task.Custom) w.Add(ReviewMergeWarning);
        if (task.Custom) w.Add(CarriedWarning);
        return w.Distinct(StringComparer.Ordinal).ToList();
    }

    static ColumnMap? FindColumnMap(TableMap map, string column)
    {
        if (map.Columns.TryGetValue(column, out var exact)) return exact;
        foreach (var (k, v) in map.Columns.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    /// <summary>"schema.table.column" → the catalog column (split on the last '.').</summary>
    static ColumnInfo? FindSourceColumn(CatalogSnapshot src, string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        if (dot <= 0) return null;
        return src.FindTable(qualified[..dot])?.FindColumn(qualified[(dot + 1)..]);
    }

    static string TypeWithCollation(ColumnInfo c) =>
        string.IsNullOrEmpty(c.Collation) ? SqlTypeText.Format(c) : $"{SqlTypeText.Format(c)} COLLATE {c.Collation}";
}
