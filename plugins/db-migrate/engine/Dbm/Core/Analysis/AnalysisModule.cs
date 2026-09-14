using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.Analysis;

/// <summary>Phase module for ANALYSIS: packets for the schema-analyst, patch validation, approval blockers.</summary>
public sealed class AnalysisModule : IPhaseModule
{
    public const int BriefCap = 60;
    public const int LargestTables = 15;
    public const int TargetTableCap = 100;
    public const int MinSummaryChars = 40;
    public const string Hint = "use `dbm show <table>` / `dbm search <text>` for detail";

    public PhaseName Phase => PhaseName.Analysis;
    public string Agent => "schema-analyst";
    public string JobKind => "analyze";

    public bool NeedsAgent(JsonNode draft) => draft["narrative"] is null;

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        var payload = Json.Deserialize<AnalysisPayload>(ctx.Current.PayloadJson);
        return mode == PacketMode.Draft ? DraftPacket(ctx, payload) : ReworkPacket(ctx, payload);
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (payload is not JsonObject doc) return new PayloadCheck(["payload must be a JSON object"], []);

        ValidateNarrative(doc["narrative"], errors, warnings);

        if (JsonNode.Parse(ctx.Current.PayloadJson) is JsonObject baseDoc)
        {
            var keys = doc.Select(p => p.Key).Union(baseDoc.Select(p => p.Key), StringComparer.Ordinal);
            foreach (var key in keys.Where(k => k is not ("narrative" or "findings")))
                if (!JsonNode.DeepEquals(doc[key], baseDoc[key])) errors.Add($"/{key} must not be changed (only /narrative and /findings/<id>/commentary are editable)");
            ValidateFindings(doc["findings"] as JsonObject, baseDoc["findings"] as JsonObject, errors);
        }
        return new PayloadCheck(errors, warnings);
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload) =>
        payload["narrative"] is null
            ? ["Narrative missing: the schema-analyst must write the summary, risks and recommendations before approval."]
            : [];

    public string Summarize(JsonNode payload)
    {
        var p = Json.FromNode<AnalysisPayload>(payload);
        return Analyzer.Summary(p) + (p.Narrative is null ? " · narrative pending" : " · narrative ready");
    }

    // ---- packets -------------------------------------------------------------------------------------------------

    private static JsonObject DraftPacket(ModuleContext ctx, AnalysisPayload p)
    {
        var src = ctx.Services.Catalog.Get(Side.Src);
        var tgt = ctx.Services.Catalog.Get(Side.Tgt);
        var findings = p.Findings.ToList();
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["source"] = Json.ToNode(p.Source),
            ["target"] = Json.ToNode(p.Target),
            ["estimates"] = Json.ToNode(p.Estimates),
            ["totals"] = Totals(p),
            ["detail"] = Detailed(findings),
            ["brief"] = new JsonObject
            {
                ["medium"] = Brief(findings, Severity.Medium),
                ["low"] = Brief(findings, Severity.Low),
            },
            ["info"] = InfoByRule(findings),
            ["largestSourceTables"] = new JsonArray((src?.Tables ?? new List<TableInfo>())
                .OrderByDescending(t => t.Rows).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Take(LargestTables)
                .Select(t => (JsonNode?)new JsonObject
                {
                    ["key"] = t.Key, ["rows"] = t.Rows, ["mb"] = Math.Round(t.SizeMb, 2),
                    ["pk"] = t.PrimaryKey is not null, ["heap"] = t.IsHeap,
                }).ToArray()),
            ["targetTables"] = new JsonArray((tgt?.Tables ?? new List<TableInfo>())
                .Select(t => t.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Take(TargetTableCap).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["targetTablesTotal"] = tgt?.Tables.Count ?? 0,
            ["hint"] = Hint,
        };
    }

    private static JsonObject ReworkPacket(ModuleContext ctx, AnalysisPayload p)
    {
        var context = new JsonArray();
        foreach (var feedback in ctx.OpenFeedback)
            context.Add(new JsonObject
            {
                ["id"] = feedback.Id,
                ["anchor"] = feedback.Anchor,
                ["context"] = ResolveAnchor(ctx, p, feedback.Anchor),
            });
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["currentNarrative"] = p.Narrative is null ? null : Json.ToNode(p.Narrative),
            ["totals"] = Totals(p),
            ["detail"] = Detailed(p.Findings.ToList()),
            ["feedbackContext"] = context,
            ["hint"] = Hint,
        };
    }

    /// <summary>finding:&lt;id&gt; → the finding; table:&lt;side&gt;:&lt;key&gt; → compact table text; column:… → column line; narrative → narrative.</summary>
    public static JsonNode? ResolveAnchor(ModuleContext ctx, AnalysisPayload p, string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return null;
        if (anchor == "narrative") return p.Narrative is null ? null : Json.ToNode(p.Narrative);
        var parts = anchor.Split(':', 3);
        if (parts[0] == "finding" && parts.Length == 2)
            return p.Findings.TryGetValue(parts[1], out var finding) ? Detail(parts[1], finding) : JsonValue.Create($"unknown finding {parts[1]}");
        if (parts.Length == 3 && parts[0] is "table" or "column")
        {
            var side = parts[1] == "tgt" ? Side.Tgt : Side.Src;
            var snapshot = ctx.Services.Catalog.Get(side);
            if (snapshot is null) return null;
            if (parts[0] == "table")
                return snapshot.FindTable(parts[2]) is { } t ? JsonValue.Create(CatalogText.Table(side, t, 40)) : JsonValue.Create($"unknown table {parts[2]}");
            var dot = parts[2].LastIndexOf('.');
            if (dot > 0 && snapshot.FindTable(parts[2][..dot]) is { } table && table.FindColumn(parts[2][(dot + 1)..]) is { } column)
                return JsonValue.Create(CatalogText.TableHeader(side, table) + "\n  " + CatalogText.ColumnLine(table, column));
            return JsonValue.Create($"unknown column {parts[2]}");
        }
        return null;
    }

    private static JsonObject Legend() => new()
    {
        ["id"] = "finding id; feedback anchor finding:<id>; patch path /findings/<id>/commentary",
        ["sev"] = "severity: critical|high|medium|low|info",
        ["obj"] = "object: schema.table, schema.table.column, 'A ↔ B' cycle, or database",
        ["msg"] = "script-generated message",
        ["n"] = "count (orphan rows, trigger count, rows)",
        ["rows"] = "row count",
        ["mb"] = "size in MB",
        ["pk"] = "has a primary key",
        ["heap"] = "no clustered index",
    };

    private static JsonObject Totals(AnalysisPayload p)
    {
        var totals = new JsonObject();
        foreach (var severity in Enum.GetValues<Severity>())
            totals[EnumText.ToText(severity)] = p.Findings.Values.Count(f => f.Severity == severity);
        return totals;
    }

    private static JsonArray Detailed(List<KeyValuePair<string, Finding>> findings) =>
        new(findings.Where(f => f.Value.Severity is Severity.Critical or Severity.High)
            .Select(f => (JsonNode?)Detail(f.Key, f.Value)).ToArray());

    private static JsonObject Detail(string id, Finding f)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["rule"] = f.Rule, ["title"] = f.Title, ["sev"] = EnumText.ToText(f.Severity),
            ["side"] = f.Side, ["obj"] = f.Object, ["msg"] = f.Message,
        };
        if (f.Count is { } n) o["n"] = n;
        if (f.Commentary is { } c) o["commentary"] = c;
        return o;
    }

    private static JsonObject Brief(List<KeyValuePair<string, Finding>> findings, Severity severity)
    {
        var matching = findings.Where(f => f.Value.Severity == severity).ToList();
        return new JsonObject
        {
            ["total"] = matching.Count,
            ["items"] = new JsonArray(matching.Take(BriefCap).Select(f => (JsonNode?)new JsonObject
            {
                ["id"] = f.Key, ["rule"] = f.Value.Rule, ["obj"] = f.Value.Object, ["msg"] = f.Value.Message,
            }).ToArray()),
        };
    }

    private static JsonArray InfoByRule(List<KeyValuePair<string, Finding>> findings) =>
        new(findings.Where(f => f.Value.Severity == Severity.Info)
            .GroupBy(f => f.Value.Rule)
            .Select(g => (JsonNode?)new JsonObject
            {
                ["rule"] = g.Key,
                ["title"] = g.First().Value.Title,
                ["count"] = g.Count(),
                ["examples"] = new JsonArray(g.Take(3).Select(f => (JsonNode?)JsonValue.Create(f.Value.Object)).ToArray()),
            }).ToArray());

    // ---- validation ----------------------------------------------------------------------------------------------

    private static void ValidateNarrative(JsonNode? node, List<string> errors, List<string> warnings)
    {
        if (node is null)
        {
            errors.Add("/narrative is required: {summary, risks[], recommendations[]}");
            return;
        }
        Narrative? narrative;
        try
        {
            narrative = node.Deserialize<Narrative>(Json.Options);
        }
        catch (JsonException ex)
        {
            errors.Add($"/narrative is malformed: {ex.Message}");
            return;
        }
        if (narrative is null)
        {
            errors.Add("/narrative is required: {summary, risks[], recommendations[]}");
            return;
        }
        var summary = narrative.Summary?.Trim() ?? "";
        if (summary.Length < MinSummaryChars) errors.Add($"/narrative/summary must be at least {MinSummaryChars} characters");
        var recommendations = (narrative.Recommendations ?? new List<string>()).Count(r => !string.IsNullOrWhiteSpace(r));
        if (recommendations == 0) errors.Add("/narrative/recommendations needs at least one item");
        var words = summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 150) warnings.Add($"/narrative/summary has {words} words (target ≤ 150)");
        if ((narrative.Risks?.Count ?? 0) > 12) warnings.Add("/narrative/risks has more than 12 items");
        if (recommendations > 8) warnings.Add("/narrative/recommendations has more than 8 items");
    }

    private static void ValidateFindings(JsonObject? findings, JsonObject? baseFindings, List<string> errors)
    {
        if (findings is null)
        {
            errors.Add("/findings must not be removed");
            return;
        }
        baseFindings ??= new JsonObject();
        foreach (var (id, _) in findings)
            if (!baseFindings.ContainsKey(id)) errors.Add($"commentary targets unknown finding '{id}' (only existing ids may be annotated)");
        foreach (var (id, baseFinding) in baseFindings)
        {
            if (!findings.TryGetPropertyValue(id, out var finding) || finding is null)
            {
                errors.Add($"/findings/{id} must not be removed");
                continue;
            }
            if (!JsonNode.DeepEquals(WithoutCommentary(finding), WithoutCommentary(baseFinding)))
                errors.Add($"/findings/{id}: only commentary may change");
            if (finding["commentary"] is { } commentary && !(commentary is JsonValue v && v.TryGetValue<string>(out _)))
                errors.Add($"/findings/{id}/commentary must be a string");
        }
    }

    private static JsonNode? WithoutCommentary(JsonNode? node)
    {
        if (node is not JsonObject o) return node;
        var copy = (JsonObject)o.DeepClone();
        copy.Remove("commentary");
        return copy;
    }
}
