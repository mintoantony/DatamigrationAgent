using System.Globalization;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.Mapping;

public static class MappingValidator
{
    public static readonly IReadOnlyList<string> Kinds = ["direct", "merge", "lookup", "skip"];
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>Errors reject a payload. INVARIANT: any payload with no errors can be processed by MappingPacket (Summary, Draft,
    /// Rework), the MappingModule and the carry-over without throwing — so every malformed shape those dereference (a null
    /// collection, a null element inside candidates) is reported here rather than tolerated. Null tolerance in Blockers and
    /// Attention only keeps the validator itself from throwing; it is not acceptance.</summary>
    public static List<string> Errors(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(tgt);
        var errors = new List<string>();
        if (m.Tables is null) errors.Add("tables must be an object");
        if (m.Drops is null) errors.Add("drops must be an object");
        if (m.Notes is null) errors.Add("notes must be an array");
        foreach (var (key, map) in m.Tables ?? new())
        {
            var table = tgt.FindTable(key);
            if (table is null) { errors.Add($"unknown target table '{key}'"); continue; }
            if (table.Key != key) errors.Add($"target table '{key}' must be written as '{table.Key}' (catalog case)");
            if (map is null) { errors.Add($"target table '{key}' has no mapping (expected an object)"); continue; }
            if (!Kinds.Contains(map.Kind)) errors.Add($"{table.Key}: invalid kind '{map.Kind}' (use direct, merge, lookup or skip)");
            if (map.Sources is null) errors.Add($"{table.Key}: sources must be an array");
            if (map.Columns is null) errors.Add($"{table.Key}: columns must be an object");
            CandidateErrors(map.Candidates, table.Key, errors);
            foreach (var source in SourcesOf(map))
                if (src.FindTable(source) is null) errors.Add($"{table.Key}: unknown source table '{source}'");
            foreach (var (name, col) in ColumnsOf(map))
            {
                var tc = table.FindColumn(name);
                if (tc is null) { errors.Add($"unknown target column '{table.Key}.{name}'"); continue; }
                if (tc.Name != name) errors.Add($"target column '{table.Key}.{name}' must be written as '{tc.Name}' (catalog case)");
                if (col is null) { errors.Add($"{table.Key}.{tc.Name}: no column mapping (expected an object)"); continue; }
                if (col.SourceColumns is null) errors.Add($"{table.Key}.{tc.Name}: sourceColumns must be an array");
                CandidateErrors(col.Candidates, $"{table.Key}.{tc.Name}", errors);
                if ((tc.IsComputed || tc.IsRowVersion) && (col.Expr is not null || col.Default is not null))
                    errors.Add($"{table.Key}.{tc.Name} is {(tc.IsComputed ? "computed" : "a rowversion")} and cannot be written");
                if (col.Expr is not null && string.IsNullOrWhiteSpace(col.Expr))
                    errors.Add($"{table.Key}.{tc.Name}: expr is empty (use null for unmapped)");
                foreach (var sc in SourceColumnsOf(col))
                    if (ResolveSourceColumn(src, sc) is null) errors.Add($"{table.Key}.{tc.Name}: unknown source column '{sc}'");
            }
        }
        foreach (var (key, drop) in m.Drops ?? new())
        {
            if (src.FindTable(key) is null && ResolveSourceColumn(src, key) is null)
                errors.Add($"unknown drop '{key}' (use schema.table or schema.table.column)");
            if (drop is null) { errors.Add($"drop '{key}' has no decision (expected an object)"); continue; }
            if (string.IsNullOrWhiteSpace(drop.Reason)) errors.Add($"drop '{key}' needs a reason");
        }
        return errors;
    }

    private static void CandidateErrors(List<Candidate>? candidates, string owner, List<string> errors)
    {
        if (candidates is null) return;   // an absent candidate list is normal
        foreach (var c in candidates)
        {
            if (c is null) errors.Add($"{owner}: candidates must not contain null (expected an object)");
            else if (string.IsNullOrWhiteSpace(c.Source)) errors.Add($"{owner}: candidate source is missing");
        }
    }

    public static List<string> Blockers(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(tgt);
        var blockers = new List<string>();
        foreach (var table in tgt.Tables)
        {
            var map = FindTableMap(m, table.Key);
            if (map is null) { blockers.Add($"{table.Key}: no table mapping (map a source table or set kind \"skip\")"); continue; }
            if (map.Kind == "skip") continue;
            if (SourcesOf(map).Count == 0) blockers.Add($"{table.Key}: no source table (choose one or set kind \"skip\")");
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

    /// <summary>One entry per table and per column that needs a reviewer's eye. A column is attention when
    /// <see cref="ColumnNeedsAttention"/> is true; the JS mapping view mirrors that predicate.</summary>
    public static List<string> Attention(MappingPayload m, MatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(options);
        var list = new List<string>();
        foreach (var (key, map) in (m.Tables ?? new()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (map is null || map.Kind == "skip") continue;
            if (NeedsReview(map.Method, map.Confidence, options))
            {
                var sources = SourcesOf(map);
                list.Add(sources.Count == 0
                    ? Inv($"{key}: no confident source table (best {map.Confidence:0.00})")
                    : Inv($"{key}: source {sources[0]} needs review (confidence {map.Confidence:0.00}, {EnumText.ToText(map.Method)})"));
            }
            foreach (var (name, cm) in ColumnsOf(map).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (cm is null || !ColumnNeedsAttention(cm, options)) continue;
                if (NeedsReview(cm.Method, cm.Confidence, options))
                    list.Add(cm.Expr is null
                        ? Inv($"{key}.{name}: unmapped (best candidate {cm.Confidence:0.00})")
                        : Inv($"{key}.{name}: {cm.Expr} needs review (confidence {cm.Confidence:0.00}, {EnumText.ToText(cm.Method)})"));
                else
                    list.Add($"{key}.{name}: type risk: {cm.TypeRisk}");
            }
        }
        return list;
    }

    /// <summary>True for script-proposed items (fuzzy/vector) below the auto-accept band.</summary>
    public static bool NeedsReview(MapMethod method, double confidence, MatchOptions options) =>
        method is MapMethod.Fuzzy or MapMethod.Vector && confidence < options.AutoAccept;

    /// <summary>The column-level attention predicate (JS parity): a script proposal below the auto-accept band, OR a carried
    /// type risk. The type-risk half deliberately ignores method and confidence — an exact match or a human edit carrying a
    /// data-loss hazard is attention.</summary>
    public static bool ColumnNeedsAttention(ColumnMap cm, MatchOptions options) =>
        NeedsReview(cm.Method, cm.Confidence, options) || HasTypeRisk(cm);

    /// <summary>THE one "carries a type risk" predicate, used by attention, packet confidence and the packet's typeRisk field:
    /// typeRisk is present and not whitespace. JS mirror: <c>typeof cm.typeRisk === 'string' &amp;&amp; cm.typeRisk.trim() !== ''</c>.</summary>
    public static bool HasTypeRisk(ColumnMap? cm) => !string.IsNullOrWhiteSpace(cm?.TypeRisk);

    /// <summary>When <paramref name="expr"/> is a bare reference to one column — <c>[Name]</c>, <c>Name</c>, <c>alias.[Name]</c>
    /// or <c>alias.Name</c>, surrounding whitespace ignored, <c>]]</c> unescaped — returns that column name; otherwise null.
    /// A null or blank expression is not a reference (callers decide what an empty expression means).</summary>
    public static string? BareColumnName(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        var match = BareReference.Match(expr);
        if (!match.Success) return null;
        return match.Groups["b"].Success ? match.Groups["b"].Value.Replace("]]", "]") : match.Groups["p"].Value;
    }

    private static readonly System.Text.RegularExpressions.Regex BareReference = new(
        @"^\s*(?:(?:\[(?:[^\]]|\]\])+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?:\[(?<b>(?:[^\]]|\]\])+)\]|(?<p>[A-Za-z_@#][A-Za-z0-9_@#$]*))\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Source columns ("schema.table.column") neither referenced by a non-skip map nor dropped.</summary>
    public static List<string> UncoveredSourceColumns(MappingPayload m, CatalogSnapshot src)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(src);
        return UncoveredByTable(m, src).SelectMany(x => x.Missing.Select(c => $"{x.Table.Key}.{c.Name}")).ToList();
    }

    /// <summary>Resolves "schema.table.column" (split on the last '.') against a catalog. A null or empty
    /// key (e.g. a null element inside a "sourceColumns" JSON array) resolves to nothing, like an unknown key.</summary>
    public static (TableInfo Table, ColumnInfo Column)? ResolveSourceColumn(CatalogSnapshot catalog, string? key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrEmpty(key)) return null;
        var dot = key.LastIndexOf('.');
        if (dot <= 0 || dot == key.Length - 1) return null;
        var table = catalog.FindTable(key[..dot]);
        var column = table?.FindColumn(key[(dot + 1)..]);
        return table is null || column is null ? null : (table, column);
    }

    public static TableMap? FindTableMap(MappingPayload m, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(m);
        var tables = m.Tables ?? new();
        return tables.TryGetValue(targetKey, out var exact) ? exact
            : tables.FirstOrDefault(kv => Ci.Equals(kv.Key, targetKey)).Value;
    }

    public static ColumnMap? FindColumnMap(TableMap map, string column)
    {
        ArgumentNullException.ThrowIfNull(map);
        var columns = ColumnsOf(map);
        return columns.TryGetValue(column, out var exact) ? exact
            : columns.FirstOrDefault(kv => Ci.Equals(kv.Key, column)).Value;
    }

    /// <summary>Null-safe: a null <see cref="TableMap"/> (a malformed JSON entry) has no sources.</summary>
    private static List<string> SourcesOf(TableMap? map) => map?.Sources ?? [];

    /// <summary>Null-safe: a null <see cref="TableMap"/> or a null "columns" object has no columns.</summary>
    private static Dictionary<string, ColumnMap> ColumnsOf(TableMap? map) => map?.Columns ?? new();

    /// <summary>Null-safe: a null <see cref="ColumnMap"/> or a null "sourceColumns" array references no source columns.</summary>
    private static List<string> SourceColumnsOf(ColumnMap? col) => col?.SourceColumns ?? [];

    private static IEnumerable<(TableInfo Table, List<ColumnInfo> Missing)> UncoveredByTable(MappingPayload m, CatalogSnapshot src)
    {
        var covered = new HashSet<string>(Ci);
        foreach (var map in (m.Tables ?? new()).Values.Where(t => t?.Kind != "skip"))
            foreach (var cm in ColumnsOf(map).Values)
                foreach (var sc in SourceColumnsOf(cm)) covered.Add(sc);
        var dropped = new HashSet<string>((m.Drops ?? new()).Keys, Ci);
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
