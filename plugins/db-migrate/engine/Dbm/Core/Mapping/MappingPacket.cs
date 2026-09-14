using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;

namespace Dbm.Core.Mapping;

/// <summary>Builds the compact "data" section of mapping work packets: confident tables summarised, uncertain tables detailed,
/// rework feedback resolved to the slice each anchor points at. Never contains connection strings.</summary>
public static class MappingPacket
{
    public const int MaxSourceTables = 3;
    public const int MaxSamples = 3;

    // Legend and hint avoid characters that System.Text.Json escapes by default (quotes, apostrophes, backticks, angle brackets, plus).
    public const string Hint =
        "Verify names before using them: dbm show schema.table --side src|tgt prints one table; " +
        "dbm search words --side src -k 8 finds columns by meaning. Never invent tables or columns.";

    public static JsonObject Legend() => new()
    {
        ["confidence"] = "0..1; script proposals below options.autoAccept need review",
        ["method"] = "exact|fuzzy|vector = script proposal; agent|human|carried = decided",
        ["expr"] = "T-SQL over the FROM aliases: s = sources[0]; other aliases are declared in from",
        ["sourceColumns"] = "every schema.table.column the expr or its join reads (drives coverage)",
        ["candidates"] = "next best sources with score and why (name, type, structure, profile)",
        ["typeRisk"] = "data-loss hazard computed by dbm; never write it. Transform it away, or say in summary why it is acceptable",
        ["paths"] = "/tables/{target}/{field}, /tables/{target}/columns/{column}/{field}, /drops/{schema.table or schema.table.column}"
    };

    public static string Summary(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        Guard(m, src, tgt, options);
        var live = m.Tables.Values.Where(t => t.Kind != "skip").ToList();
        var tables = live.Count(t => t.Sources.Count > 0);
        var columns = live.Sum(t => t.Columns.Values.Count(c => !string.IsNullOrWhiteSpace(c.Expr)));
        var attention = MappingValidator.Attention(m, options).Count;
        var blockers = MappingValidator.Blockers(m, src, tgt).Count;
        return $"{tables} tables, {columns} columns mapped, {m.Drops.Count} drops, {attention} attention, {blockers} blockers";
    }

    public static JsonObject Draft(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        Guard(m, src, tgt, options);
        var blockers = MappingValidator.Blockers(m, src, tgt);
        var attention = MappingValidator.Attention(m, options);
        var confident = new JsonArray();
        var detail = new JsonArray();
        foreach (var t in tgt.Tables)
        {
            var map = MappingValidator.FindTableMap(m, t.Key);
            if (map is not null && IsConfident(t, map, blockers, options))
                confident.Add(new JsonObject
                {
                    ["target"] = t.Key,
                    ["source"] = map.Kind == "skip" ? "(skip)" : string.Join(", ", map.Sources),
                    ["confidence"] = R(map.Confidence),
                    ["columns"] = map.Columns.Values.Count(c => !string.IsNullOrWhiteSpace(c.Expr))
                });
            else
                detail.Add(TableDetail(t, map, src));
        }
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["options"] = new JsonObject { ["autoAccept"] = options.AutoAccept, ["candidate"] = options.Candidate },
            ["blockers"] = Strings(blockers),
            ["attention"] = Strings(attention),
            ["typeRisks"] = Strings(MappingValidator.RiskWarnings(m)),
            ["confident"] = confident,
            ["detail"] = detail,
            ["uncovered"] = Uncovered(m, src),
            ["drops"] = Drops(m),
            ["hint"] = Hint
        };
    }

    public static JsonObject Rework(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options,
        IReadOnlyList<FeedbackRow> feedback)
    {
        Guard(m, src, tgt, options);
        ArgumentNullException.ThrowIfNull(feedback);
        var contexts = new JsonArray();
        foreach (var f in feedback)
        {
            var item = new JsonObject { ["id"] = f.Id, ["anchor"] = f.Anchor };
            var context = Resolve(f.Anchor, m, src, tgt, options);
            if (context is not null) item["context"] = context;
            contexts.Add(item);
        }
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["options"] = new JsonObject { ["autoAccept"] = options.AutoAccept, ["candidate"] = options.Candidate },
            ["summary"] = Summary(m, src, tgt, options),
            ["blockers"] = Strings(MappingValidator.Blockers(m, src, tgt)),
            ["attention"] = Strings(MappingValidator.Attention(m, options)),
            ["typeRisks"] = Strings(MappingValidator.RiskWarnings(m)),
            ["contexts"] = contexts,
            ["hint"] = Hint
        };
    }

    /// <summary>Context for one feedback anchor: tablemap:/table:tgt: → table detail; colmap:/column:tgt: → column detail;
    /// column:src: → source column profile and usage; table:src: → source table and usage; anything else → overall uncovered list.</summary>
    public static JsonNode? Resolve(string? anchor, MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        Guard(m, src, tgt, options);
        if (anchor is null or "general" or "narrative")
        {
            var general = new JsonObject { ["uncovered"] = Uncovered(m, src), ["drops"] = Drops(m) };
            AddTypeRisks(general, m, (_, _, _) => true);
            return general;
        }
        if (TryStrip(anchor, "tablemap:", out var tkey) || TryStrip(anchor, "table:tgt:", out tkey))
        {
            var t = tgt.FindTable(tkey);
            return t is null ? Unknown(anchor) : TableDetail(t, MappingValidator.FindTableMap(m, t.Key), src);
        }
        if (TryStrip(anchor, "colmap:", out var ckey) || TryStrip(anchor, "column:tgt:", out ckey))
        {
            var hit = MappingValidator.ResolveSourceColumn(tgt, ckey);
            return hit is null ? Unknown(anchor) : ColumnDetail(hit.Value.Table, hit.Value.Column, m, src);
        }
        if (TryStrip(anchor, "column:src:", out var skey))
        {
            var hit = MappingValidator.ResolveSourceColumn(src, skey);
            if (hit is null) return Unknown(anchor);
            var (table, column) = hit.Value;
            var key = $"{table.Key}.{column.Name}";
            var obj = new JsonObject { ["table"] = table.Key, ["column"] = SourceColumn(table, column), ["usedBy"] = UsedBy(m, key) };
            AddTypeRisks(obj, m, (_, _, cm) => cm.SourceColumns.Contains(key, StringComparer.OrdinalIgnoreCase));
            if ((DropOf(m, key) ?? DropOf(m, table.Key)) is { } drop) obj["drop"] = drop.Reason;
            return obj;
        }
        if (TryStrip(anchor, "table:src:", out var stkey))
        {
            var table = src.FindTable(stkey);
            if (table is null) return Unknown(anchor);
            var obj = SourceTable(table);
            obj["usedBy"] = Strings(m.Tables.Where(kv => kv.Value.Sources.Contains(table.Key, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key));
            AddTypeRisks(obj, m, (_, map, cm) =>
                map.Sources.Contains(table.Key, StringComparer.OrdinalIgnoreCase)
                || cm.SourceColumns.Any(sc => MappingValidator.ResolveSourceColumn(src, sc)?.Table.Key == table.Key));
            if (DropOf(m, table.Key) is { } drop) obj["drop"] = drop.Reason;
            return obj;
        }
        return null;
    }

    public static JsonObject TableDetail(TableInfo t, TableMap? map, CatalogSnapshot src)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(src);
        var obj = new JsonObject { ["target"] = t.Key };
        if (map is not null)   // no map: the key is omitted (targetColumns and an empty columns object say enough)
        {
            obj["kind"] = map.Kind;
            obj["sources"] = Strings(map.Sources);
            if (map.From is not null) obj["from"] = map.From;
            if (map.Filter is not null) obj["filter"] = map.Filter;
            obj["confidence"] = R(map.Confidence);
            obj["method"] = EnumText.ToText(map.Method);
            if (map.Rationale is not null) obj["rationale"] = map.Rationale;
            if (map.Candidates is { Count: > 0 }) obj["tableCandidates"] = Candidates(map.Candidates);
        }
        obj["targetColumns"] = new JsonArray(t.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)TargetColumn(t, c)).ToArray());
        var columns = new JsonObject();
        if (map is not null)
            foreach (var c in t.Columns.OrderBy(c => c.Ordinal))
                if (MappingValidator.FindColumnMap(map, c.Name) is { } cm) columns[c.Name] = ColumnMapNode(cm);
        obj["columns"] = columns;
        var sourceKeys = new List<string>();
        if (map is not null)
        {
            sourceKeys.AddRange(map.Sources);
            sourceKeys.AddRange((map.Candidates ?? []).Select(c => c.Source));
        }
        obj["sourceTables"] = new JsonArray(sourceKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(src.FindTable).OfType<TableInfo>()
            .Take(MaxSourceTables)
            .Select(s => (JsonNode)SourceTable(s)).ToArray());
        return obj;
    }

    public static JsonObject ColumnDetail(TableInfo t, ColumnInfo c, MappingPayload m, CatalogSnapshot src)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(src);
        var map = MappingValidator.FindTableMap(m, t.Key);
        var cm = map is null ? null : MappingValidator.FindColumnMap(map, c.Name);
        var obj = new JsonObject { ["target"] = t.Key, ["column"] = TargetColumn(t, c) };
        if (map is not null)
        {
            obj["sources"] = Strings(map.Sources);
            if (map.From is not null) obj["from"] = map.From;
        }
        if (cm is not null) obj["map"] = ColumnMapNode(cm);
        var keys = (cm?.SourceColumns ?? []).Concat((cm?.Candidates ?? []).Select(x => x.Source)).Distinct(StringComparer.OrdinalIgnoreCase);
        var profiles = new JsonArray();
        foreach (var key in keys)
            if (MappingValidator.ResolveSourceColumn(src, key) is { } hit)
            {
                var node = SourceColumn(hit.Table, hit.Column);
                node["key"] = $"{hit.Table.Key}.{hit.Column.Name}";
                profiles.Add(node);
            }
        obj["sourceColumns"] = profiles;
        return obj;
    }

    public static JsonObject TargetColumn(TableInfo t, ColumnInfo c)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(c);
        var obj = new JsonObject { ["name"] = c.Name, ["type"] = c.TypeDisplay, ["nullable"] = c.IsNullable };
        if (c.IsIdentity) obj["identity"] = true;
        if (c.IsComputed) obj["computed"] = true;
        if (c.IsRowVersion) obj["rowversion"] = true;
        if (c.DefaultDefinition is not null) obj["default"] = c.DefaultDefinition;
        if (t.PrimaryKey?.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true) obj["pk"] = true;
        if (FkRef(t, c) is { } fk) obj["fk"] = fk;
        return obj;
    }

    public static JsonObject SourceTable(TableInfo s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new JsonObject
        {
            ["key"] = s.Key,
            ["rows"] = s.Rows,
            ["columns"] = new JsonArray(s.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)SourceColumn(s, c)).ToArray())
        };
    }

    public static JsonObject SourceColumn(TableInfo s, ColumnInfo c)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(c);
        var obj = new JsonObject { ["name"] = c.Name, ["type"] = c.TypeDisplay, ["nullable"] = c.IsNullable };
        if (s.PrimaryKey?.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true) obj["pk"] = true;
        if (FkRef(s, c) is { } fk) obj["fk"] = fk;
        if (c.Profile is { SampledRows: > 0 } p)
        {
            obj["nullRatio"] = R(p.NullRatio);
            if (p.DistinctRatio is double d) obj["distinctRatio"] = R(d);
            if (p.SemanticClass is not null) obj["class"] = p.SemanticClass;
            if (p.MaxLen is int maxLen) obj["maxLen"] = maxLen;
            if (p.Samples.Count > 0) obj["samples"] = Strings(p.Samples.Take(MaxSamples));
        }
        return obj;
    }

    private static JsonObject ColumnMapNode(ColumnMap cm)
    {
        var obj = new JsonObject { ["expr"] = cm.Expr };
        if (cm.SourceColumns.Count > 0) obj["sourceColumns"] = Strings(cm.SourceColumns);
        if (cm.Default is not null) obj["default"] = cm.Default;
        obj["confidence"] = R(cm.Confidence);
        obj["method"] = EnumText.ToText(cm.Method);
        if (MappingValidator.HasTypeRisk(cm)) obj["typeRisk"] = cm.TypeRisk;
        if (cm.Rationale is not null) obj["rationale"] = cm.Rationale;
        if (cm.Candidates is { Count: > 0 }) obj["candidates"] = Candidates(cm.Candidates);
        return obj;
    }

    /// <summary>A table carrying any type risk is never summarised as confident, whatever its method or confidence, so the agent
    /// always sees the risk in detail. Skip tables are exempt: they load no data, so there is no conversion to be risky about.</summary>
    private static bool IsConfident(TableInfo t, TableMap map, List<string> blockers, MatchOptions options)
    {
        if (blockers.Any(b => Mentions(b, t.Key))) return false;
        if (map.Kind == "skip") return true;
        if (map.Columns.Values.Any(MappingValidator.HasTypeRisk)) return false;
        if (map.Sources.Count == 0 || MappingValidator.NeedsReview(map.Method, map.Confidence, options)) return false;
        return !map.Columns.Values.Any(c => MappingValidator.NeedsReview(c.Method, c.Confidence, options));
    }

    /// <summary>Adds <c>typeRisks: {"schema.table.Column": risk}</c> for every column selected by <paramref name="include"/> that
    /// carries a type risk; omitted when there are none. Rework contexts that name target columns only by key use this so the
    /// risk is not lost on the rework path.</summary>
    private static void AddTypeRisks(JsonObject obj, MappingPayload m, Func<string, TableMap, ColumnMap, bool> include)
    {
        var risks = new JsonObject();
        foreach (var (tableKey, map) in m.Tables)
            foreach (var (name, cm) in map.Columns)
                if (MappingValidator.HasTypeRisk(cm) && include(tableKey, map, cm)) risks[$"{tableKey}.{name}"] = cm.TypeRisk;
        if (risks.Count > 0) obj["typeRisks"] = risks;
    }

    private static void Guard(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(tgt);
        ArgumentNullException.ThrowIfNull(options);
    }

    private static bool Mentions(string item, string tableKey) =>
        item.StartsWith(tableKey + ":", StringComparison.OrdinalIgnoreCase) || item.StartsWith(tableKey + ".", StringComparison.OrdinalIgnoreCase);

    private static JsonArray Uncovered(MappingPayload m, CatalogSnapshot src) =>
        Strings(MappingValidator.UncoveredSourceColumns(m, src).Select(key =>
            MappingValidator.ResolveSourceColumn(src, key) is { } hit ? $"{key} {hit.Column.TypeDisplay}" : key));

    private static JsonObject Drops(MappingPayload m)
    {
        var obj = new JsonObject();
        foreach (var (key, drop) in m.Drops) obj[key] = drop.Reason;
        return obj;
    }

    private static JsonArray UsedBy(MappingPayload m, string sourceColumnKey) =>
        Strings(m.Tables.SelectMany(t => t.Value.Columns
            .Where(c => c.Value.SourceColumns.Contains(sourceColumnKey, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"{t.Key}.{c.Key}")));

    private static DropDecision? DropOf(MappingPayload m, string key) =>
        m.Drops.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? FkRef(TableInfo t, ColumnInfo c)
    {
        foreach (var f in t.ForeignKeys)
        {
            var i = f.Columns.FindIndex(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return $"{f.RefKey}.{(i < f.RefColumns.Count ? f.RefColumns[i] : "?")}";
        }
        return null;
    }

    private static JsonArray Candidates(IEnumerable<Candidate> candidates) =>
        new(candidates.Select(c => (JsonNode)new JsonObject { ["source"] = c.Source, ["score"] = R(c.Score), ["why"] = c.Why }).ToArray());

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject Unknown(string anchor) => new() { ["error"] = $"anchor '{anchor}' does not resolve to a catalog object" };

    private static bool TryStrip(string anchor, string prefix, out string rest)
    {
        rest = anchor.StartsWith(prefix, StringComparison.Ordinal) ? anchor[prefix.Length..] : "";
        return rest.Length > 0;
    }

    private static double R(double value) => Math.Round(value, 2);
}
