using System.Globalization;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.Mapping;

public static class MappingValidator
{
    public static readonly IReadOnlyList<string> Kinds = ["direct", "merge", "lookup", "skip"];
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static List<string> Errors(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        var errors = new List<string>();
        foreach (var (key, map) in m.Tables)
        {
            var table = tgt.FindTable(key);
            if (table is null) { errors.Add($"unknown target table '{key}'"); continue; }
            if (table.Key != key) errors.Add($"target table '{key}' must be written as '{table.Key}' (catalog case)");
            if (!Kinds.Contains(map.Kind)) errors.Add($"{table.Key}: invalid kind '{map.Kind}' (use direct, merge, lookup or skip)");
            foreach (var source in map.Sources)
                if (src.FindTable(source) is null) errors.Add($"{table.Key}: unknown source table '{source}'");
            foreach (var (name, col) in map.Columns)
            {
                var tc = table.FindColumn(name);
                if (tc is null) { errors.Add($"unknown target column '{table.Key}.{name}'"); continue; }
                if (tc.Name != name) errors.Add($"target column '{table.Key}.{name}' must be written as '{tc.Name}' (catalog case)");
                if ((tc.IsComputed || tc.IsRowVersion) && (col.Expr is not null || col.Default is not null))
                    errors.Add($"{table.Key}.{tc.Name} is {(tc.IsComputed ? "computed" : "a rowversion")} and cannot be written");
                if (col.Expr is not null && string.IsNullOrWhiteSpace(col.Expr))
                    errors.Add($"{table.Key}.{tc.Name}: expr is empty (use null for unmapped)");
                foreach (var sc in col.SourceColumns)
                    if (ResolveSourceColumn(src, sc) is null) errors.Add($"{table.Key}.{tc.Name}: unknown source column '{sc}'");
            }
        }
        foreach (var (key, drop) in m.Drops)
        {
            if (src.FindTable(key) is null && ResolveSourceColumn(src, key) is null)
                errors.Add($"unknown drop '{key}' (use schema.table or schema.table.column)");
            if (string.IsNullOrWhiteSpace(drop.Reason)) errors.Add($"drop '{key}' needs a reason");
        }
        return errors;
    }

    public static List<string> Blockers(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        var blockers = new List<string>();
        foreach (var table in tgt.Tables)
        {
            var map = FindTableMap(m, table.Key);
            if (map is null) { blockers.Add($"{table.Key}: no table mapping (map a source table or set kind \"skip\")"); continue; }
            if (map.Kind == "skip") continue;
            if (map.Sources.Count == 0) blockers.Add($"{table.Key}: no source table (choose one or set kind \"skip\")");
            foreach (var col in table.Columns)
            {
                if (col.IsNullable || col.IsIdentity || col.IsComputed || col.IsRowVersion || col.DefaultDefinition is not null) continue;
                var cm = FindColumnMap(map, col.Name);
                if (cm is null || string.IsNullOrWhiteSpace(cm.Expr) && string.IsNullOrWhiteSpace(cm.Default))
                    blockers.Add($"{table.Key}.{col.Name}: NOT NULL without default needs an expression or default");
            }
        }
        foreach (var (table, missing) in UncoveredByTable(m, src))
        {
            if (missing.Count == table.Columns.Count)
                blockers.Add($"source table {table.Key} is not mapped or dropped ({missing.Count} columns)");
            else
                blockers.AddRange(missing.Select(c => $"source column {table.Key}.{c.Name} is not mapped or dropped"));
        }
        return blockers;
    }

    public static List<string> Attention(MappingPayload m, MatchOptions options)
    {
        var list = new List<string>();
        foreach (var (key, map) in m.Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (map.Kind == "skip") continue;
            if (NeedsReview(map.Method, map.Confidence, options))
                list.Add(map.Sources.Count == 0
                    ? Inv($"{key}: no confident source table (best {map.Confidence:0.00})")
                    : Inv($"{key}: source {map.Sources[0]} needs review (confidence {map.Confidence:0.00}, {EnumText.ToText(map.Method)})"));
            foreach (var (name, cm) in map.Columns)
            {
                if (!NeedsReview(cm.Method, cm.Confidence, options)) continue;
                list.Add(cm.Expr is null
                    ? Inv($"{key}.{name}: unmapped (best candidate {cm.Confidence:0.00})")
                    : Inv($"{key}.{name}: {cm.Expr} needs review (confidence {cm.Confidence:0.00}, {EnumText.ToText(cm.Method)})"));
            }
        }
        return list;
    }

    /// <summary>True for script-proposed items (fuzzy/vector) below the auto-accept band.</summary>
    public static bool NeedsReview(MapMethod method, double confidence, MatchOptions options) =>
        method is MapMethod.Fuzzy or MapMethod.Vector && confidence < options.AutoAccept;

    /// <summary>Source columns ("schema.table.column") neither referenced by a non-skip map nor dropped.</summary>
    public static List<string> UncoveredSourceColumns(MappingPayload m, CatalogSnapshot src) =>
        UncoveredByTable(m, src).SelectMany(x => x.Missing.Select(c => $"{x.Table.Key}.{c.Name}")).ToList();

    /// <summary>Resolves "schema.table.column" (split on the last '.') against a catalog.</summary>
    public static (TableInfo Table, ColumnInfo Column)? ResolveSourceColumn(CatalogSnapshot catalog, string key)
    {
        var dot = key.LastIndexOf('.');
        if (dot <= 0 || dot == key.Length - 1) return null;
        var table = catalog.FindTable(key[..dot]);
        var column = table?.FindColumn(key[(dot + 1)..]);
        return table is null || column is null ? null : (table, column);
    }

    public static TableMap? FindTableMap(MappingPayload m, string targetKey) =>
        m.Tables.TryGetValue(targetKey, out var exact) ? exact
        : m.Tables.FirstOrDefault(kv => Ci.Equals(kv.Key, targetKey)).Value;

    public static ColumnMap? FindColumnMap(TableMap map, string column) =>
        map.Columns.TryGetValue(column, out var exact) ? exact
        : map.Columns.FirstOrDefault(kv => Ci.Equals(kv.Key, column)).Value;

    private static IEnumerable<(TableInfo Table, List<ColumnInfo> Missing)> UncoveredByTable(MappingPayload m, CatalogSnapshot src)
    {
        var covered = new HashSet<string>(Ci);
        foreach (var map in m.Tables.Values.Where(t => t.Kind != "skip"))
            foreach (var cm in map.Columns.Values)
                foreach (var sc in cm.SourceColumns) covered.Add(sc);
        var dropped = new HashSet<string>(m.Drops.Keys, Ci);
        foreach (var table in src.Tables)
        {
            if (dropped.Contains(table.Key)) continue;
            var missing = table.Columns
                .Where(c => !covered.Contains($"{table.Key}.{c.Name}") && !dropped.Contains($"{table.Key}.{c.Name}"))
                .ToList();
            if (missing.Count > 0) yield return (table, missing);
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
