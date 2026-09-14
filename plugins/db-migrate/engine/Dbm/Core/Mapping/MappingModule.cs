using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.Mapping;

/// <summary>Phase module for MAPPING: automap job → mapping-architect agent → human review.</summary>
/// <remarks>The module uses the <see cref="DbmServices"/> it was constructed with and ignores <c>ctx.Services</c>. That is a
/// deliberate choice: the registry constructs it with the same instance it later passes in every context.</remarks>
public sealed class MappingModule(DbmServices services) : IPhaseModule
{
    /// <summary>The type risk written when a changed column's expression is custom (not a bare single-source reference) and was
    /// not written by the agent: its risk cannot be inferred, and the previous risk described a different conversion.</summary>
    public const string CustomExpressionRisk = "custom expression: type risk not evaluated";

    private readonly DbmServices services = services ?? throw new ArgumentNullException(nameof(services));

    public PhaseName Phase => PhaseName.Mapping;
    public string Agent => "mapping-architect";
    public string JobKind => "automap";

    /// <summary>True while blockers remain, a script proposal is below the auto-accept band, or a column carries a type risk that
    /// neither the agent nor a human has decided (agent, human and carried methods count as decided). Skipped tables load no
    /// data and are exempt, as in the packet's confidence test.</summary>
    public bool NeedsAgent(JsonNode draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (src, tgt) = Catalogs();
        var m = Parse(draft);
        var options = Options();
        if (MappingValidator.Blockers(m, src, tgt).Count > 0) return true;
        foreach (var map in (m.Tables ?? new()).Values)
        {
            if (map is null || map.Kind == "skip") continue;
            if (MappingValidator.NeedsReview(map.Method, map.Confidence, options)) return true;
            foreach (var cm in (map.Columns ?? new()).Values)
            {
                if (cm is null) continue;
                if (MappingValidator.NeedsReview(cm.Method, cm.Confidence, options)) return true;
                if (MappingValidator.HasTypeRisk(cm) && !MappingCarryOver.IsKept(cm.Method)) return true;
            }
        }
        return false;
    }

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var (src, tgt) = Catalogs();
        var m = Json.Deserialize<MappingPayload>(ctx.Current.PayloadJson);
        return mode == PacketMode.Draft
            ? MappingPacket.Draft(m, src, tgt, Options())
            : MappingPacket.Rework(m, src, tgt, Options(), ctx.OpenFeedback);
    }

    /// <summary>Errors reject the payload. When it is accepted, each column's <c>typeRisk</c> is brought in line with the
    /// conversion the payload now specifies — IN PLACE on <paramref name="payload"/>, because the engine stores that very node —
    /// before blockers and attention are computed, so the stored warnings match the stored payload.</summary>
    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(payload);
        MappingPayload m;
        try { m = Parse(payload); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new PayloadCheck([$"payload is not a valid mapping: {e.Message}"], []);
        }
        var (src, tgt) = Catalogs();
        var errors = MappingValidator.Errors(m, src, tgt);
        if (errors.Count == 0) RecomputeTypeRisks(payload, m, Baseline(ctx), src, tgt);
        var warnings = MappingValidator.Blockers(m, src, tgt);
        warnings.AddRange(MappingValidator.Attention(m, Options()));
        return new PayloadCheck(errors, warnings);
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(payload);
        var (src, tgt) = Catalogs();
        return MappingValidator.Blockers(Parse(payload), src, tgt);
    }

    public string Summarize(JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var (src, tgt) = Catalogs();
        return MappingPacket.Summary(Parse(payload), src, tgt, Options());
    }

    // ---- type-risk recompute ---------------------------------------------------------------------------------------

    /// <summary>INVARIANT: a stored typeRisk describes the conversion the payload currently specifies. Rules:
    /// (1) a column whose expr and sourceColumns are unchanged from the base version keeps its typeRisk verbatim;
    /// (2) a changed column whose expr is empty or a bare reference to its single source column gets the TypeCompat risk of
    ///     that pair (the property is removed when there is none);
    /// (3) a changed column with a custom expression (CAST, CASE, literal, several sources…) cannot have its risk inferred:
    ///     if the agent wrote it, its typeRisk is left exactly as the patch has it (the playbook makes the agent carry the risk
    ///     or justify dropping it); otherwise it becomes <see cref="CustomExpressionRisk"/>, which is attention like any risk;
    /// (4) a changed column that reads nothing (no expr, no source columns) has no conversion and no risk.
    /// Requires a payload with no validation errors.</summary>
    private static void RecomputeTypeRisks(JsonNode payload, MappingPayload m, MappingPayload? baseline, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        if (payload is not JsonObject root || Property(root, "tables") is not JsonObject tables) return;
        foreach (var (tableKey, tableNode) in tables.ToList())
        {
            if (tableNode is not JsonObject tableObj || Property(tableObj, "columns") is not JsonObject columns) continue;
            if (!m.Tables.TryGetValue(tableKey, out var map) || tgt.FindTable(tableKey) is not { } target) continue;
            var before = baseline is null ? null : MappingValidator.FindTableMap(baseline, tableKey);
            foreach (var (name, columnNode) in columns.ToList())
            {
                if (columnNode is not JsonObject columnObj || !map.Columns.TryGetValue(name, out var cm)) continue;
                var old = before is null ? null : MappingValidator.FindColumnMap(before, name);
                if (old is not null && SameConversion(old, cm)) continue;
                if (target.FindColumn(name) is not { } targetColumn) continue;
                var risk = EvaluateTypeRisk(cm, targetColumn, src);
                if (risk.Keep) continue;
                cm.TypeRisk = risk.Text;
                SetTypeRisk(columnObj, risk.Text);
            }
        }
    }

    private static (bool Keep, string? Text) EvaluateTypeRisk(ColumnMap cm, ColumnInfo targetColumn, CatalogSnapshot src)
    {
        var sources = cm.SourceColumns.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var emptyExpr = string.IsNullOrWhiteSpace(cm.Expr);
        if (emptyExpr && sources.Count == 0) return (false, null);
        if (sources.Count == 1 && MappingValidator.ResolveSourceColumn(src, sources[0]) is { } hit
            && (emptyExpr || string.Equals(MappingValidator.BareColumnName(cm.Expr), hit.Column.Name, StringComparison.OrdinalIgnoreCase)))
            return (false, TypeCompat.Check(ColumnType.From(hit.Column), ColumnType.From(targetColumn), hit.Column.Profile).Risk);
        return cm.Method == MapMethod.Agent ? (true, null) : (false, CustomExpressionRisk);
    }

    private static bool SameConversion(ColumnMap a, ColumnMap b) =>
        string.Equals(a.Expr, b.Expr, StringComparison.Ordinal)
        && new HashSet<string>((a.SourceColumns ?? []).Where(s => s is not null), StringComparer.OrdinalIgnoreCase)
            .SetEquals((b.SourceColumns ?? []).Where(s => s is not null));

    /// <summary>Deserialisation is case-insensitive, so the node may spell the property in any case: remove every spelling,
    /// then write the camelCase one.</summary>
    private static void SetTypeRisk(JsonObject column, string? risk)
    {
        foreach (var key in column.Select(kv => kv.Key).Where(k => string.Equals(k, "typeRisk", StringComparison.OrdinalIgnoreCase)).ToList())
            column.Remove(key);
        if (risk is not null) column["typeRisk"] = risk;
    }

    private static JsonNode? Property(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var exact) ? exact
            : obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The base version the payload was derived from, or null when it cannot be read (every column then counts as changed).</summary>
    private static MappingPayload? Baseline(ModuleContext ctx)
    {
        try { return Json.Deserialize<MappingPayload>(ctx.Current.PayloadJson); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }

    // ---- services --------------------------------------------------------------------------------------------------

    private MatchOptions Options()
    {
        var settings = services.Project.GetSettings();
        return new MatchOptions(settings.AutoAcceptScore, settings.CandidateScore);
    }

    private (CatalogSnapshot Src, CatalogSnapshot Tgt) Catalogs() =>
        (services.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("source catalog missing; run discovery first"),
         services.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("target catalog missing; run discovery first"));

    private static MappingPayload Parse(JsonNode node) => Json.FromNode<MappingPayload>(node);
}
