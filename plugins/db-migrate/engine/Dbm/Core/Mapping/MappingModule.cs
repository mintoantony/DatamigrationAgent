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
    private readonly DbmServices services = services ?? throw new ArgumentNullException(nameof(services));

    public PhaseName Phase => PhaseName.Mapping;
    public string Agent => "mapping-architect";
    public string JobKind => "automap";

    /// <summary>True while attention or blockers remain. An unacknowledged type risk reaches this through attention; there is
    /// deliberately no risk clause of its own and no method-based exemption.</summary>
    public bool NeedsAgent(JsonNode draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (src, tgt) = Catalogs();
        var m = Parse(draft);
        return MappingValidator.Attention(m, Options()).Count > 0 || MappingValidator.Blockers(m, src, tgt).Count > 0;
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

    /// <summary>Errors reject the payload. When it is accepted, each column's engine-owned <c>typeRisk</c> and author-owned
    /// <c>riskAck</c> are reconciled — IN PLACE on <paramref name="payload"/>, because the engine stores that very node —
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
        var warnings = new List<string>();
        if (errors.Count == 0) NormaliseRisks(payload, m, Baseline(ctx), src, tgt, warnings);
        warnings.AddRange(MappingValidator.Blockers(m, src, tgt));
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

    // ---- the Risk model: engine-owned typeRisk/riskClass, author-owned riskAck -------------------------------------

    /// <summary>Risk normalisation, in place on the node the engine stores, for a payload with no structural errors.
    /// CHANGED (Risk model §5, G2): the table or column is absent from ctx.Current, the table's sources or from changed, or the
    /// column's trimmed expr, default or case-insensitive sourceColumns set changed.
    /// typeRisk and riskClass, first match wins (G1): (1) unchanged → Current's verbatim; (2) expr and default null → none;
    /// (3) bare single-source reference → TypeCompat with the source profile; (4) anything else → the sentinel.
    /// A supplied typeRisk is never stored; it earns a warning when present and different from Current's (§1).
    /// riskAck: see <see cref="NormaliseAck"/>.</summary>
    private static void NormaliseRisks(JsonNode payload, MappingPayload m, MappingPayload? baseline, CatalogSnapshot src, CatalogSnapshot tgt,
        List<string> warnings)
    {
        if (payload is not JsonObject root || Property(root, "tables") is not JsonObject tables) return;
        foreach (var (tableKey, tableNode) in tables.ToList())
        {
            if (tableNode is not JsonObject tableObj || Property(tableObj, "columns") is not JsonObject columns) continue;
            if (!m.Tables.TryGetValue(tableKey, out var map) || tgt.FindTable(tableKey) is not { } target) continue;
            var before = baseline is null ? null : MappingValidator.FindTableMap(baseline, tableKey);
            var tableChanged = before is null || !SameBinding(before, map);
            foreach (var (name, columnNode) in columns.ToList())
            {
                if (columnNode is not JsonObject columnObj || !map.Columns.TryGetValue(name, out var cm)) continue;
                if (target.FindColumn(name) is not { } targetColumn) continue;
                var old = before is null ? null : MappingValidator.FindColumnMap(before, name);
                var key = $"{tableKey}.{name}";
                if (Blank(cm.TypeRisk) is { } supplied && supplied != Blank(old?.TypeRisk))
                    warnings.Add($"{key}: typeRisk is computed by dbm; the supplied value was ignored");

                var changed = tableChanged || old is null || !SameColumn(old, cm);
                string? risk, riskClass;
                if (!changed)
                    (risk, riskClass) = (Blank(old!.TypeRisk), BackfillClass(map, old, targetColumn, src));
                else if (cm.Expr is null && cm.Default is null)
                    (risk, riskClass) = (null, null);
                else if (MappingValidator.BareSingleSource(map, cm, src) is { } hit)
                {
                    risk = Blank(TypeCompat.Check(ColumnType.From(hit.Column), ColumnType.From(targetColumn), hit.Column.Profile).Risk);
                    riskClass = TypeCompat.RiskClass(risk, hit.Column, targetColumn);
                }
                else
                    (risk, riskClass) = (MappingValidator.UnevaluatedRisk, TypeCompat.UnevaluatedClass);

                var ack = NormaliseAck(key, cm.RiskAck, old?.RiskAck, risk, changed, warnings);
                cm.TypeRisk = risk;
                cm.RiskClass = riskClass;
                cm.RiskAck = ack;
                SetProperty(columnObj, "typeRisk", risk is null ? null : JsonValue.Create(risk));
                SetProperty(columnObj, "riskClass", riskClass is null ? null : JsonValue.Create(riskClass));
                SetProperty(columnObj, "riskAck", ack is null ? null : new JsonObject { ["risk"] = ack.Risk, ["reason"] = ack.Reason });
            }
        }
    }

    /// <summary>An unchanged column keeps Current's riskClass. A risk stored before riskClass existed (the auto-mapper writes only
    /// typeRisk) gets its class filled in — only for a bare column whose recomputed text is identical to the stored one, so the
    /// class provably describes that stored risk; otherwise the stored value, possibly null, is kept.</summary>
    private static string? BackfillClass(TableMap map, ColumnMap old, ColumnInfo targetColumn, CatalogSnapshot src)
    {
        if (old.RiskClass is not null || Blank(old.TypeRisk) is not { } stored) return old.RiskClass;
        if (old.Expr is null || MappingValidator.BareSingleSource(map, old, src) is not { } hit) return null;
        var risk = TypeCompat.Check(ColumnType.From(hit.Column), ColumnType.From(targetColumn), hit.Column.Profile).Risk;
        return risk == stored ? TypeCompat.RiskClass(risk, hit.Column, targetColumn) : null;
    }

    /// <summary>riskAck is taken from the node as supplied; the engine only decides whether it may stand (§2, G3, G5).
    /// SUPPLIED by this change = present and different from Current's (the one comparison against Current that §2 allows).
    /// - an acknowledgement without a reason is no acknowledgement: stored as absent;
    /// - no computed risk → dropped, with a warning;
    /// - a changed column clears an acknowledgement this change did not supply (it cited a transform, sources or a binding that no
    ///   longer hold — this also covers every riskClass change, since a class can only change on a changed column);
    /// - finally, while the hazard class still matches, riskAck.risk is refreshed to the current wording (never the reason).
    /// Whether it then matches is decided by the predicate alone; a supplied acknowledgement is reported either way.</summary>
    private static RiskAck? NormaliseAck(string key, RiskAck? incoming, RiskAck? current, string? risk, bool changed, List<string> warnings)
    {
        if (incoming is not null && string.IsNullOrWhiteSpace(incoming.Reason)) incoming = null;
        var supplied = incoming is not null && !SameAck(incoming, current);
        if (incoming is null) return null;
        if (risk is null)
        {
            warnings.Add($"{key}: riskAck dropped: dbm computes no type risk for this column");
            return null;
        }
        if (changed && !supplied)
        {
            warnings.Add($"{key}: riskAck cleared: the column changed, so it acknowledged a different conversion");
            return null;
        }
        var ack = new RiskAck { Risk = incoming.Risk, Reason = incoming.Reason };
        if (ack.Risk != risk && TypeCompat.SameHazard(ack.Risk, risk)) ack.Risk = risk;
        if (supplied)
            warnings.Add(ack.Risk == risk
                ? $"{key}: risk acknowledged: {risk} (reason: {ack.Reason})"
                : $"{key}: riskAck does not name the current risk; its risk must be: {risk}");
        return ack;
    }

    private static bool SameAck(RiskAck a, RiskAck? b) =>
        b is not null && string.Equals(a.Risk, b.Risk, StringComparison.Ordinal) && string.Equals(a.Reason, b.Reason, StringComparison.Ordinal);

    private static bool SameBinding(TableMap a, TableMap b) =>
        SameText(a.From, b.From) && (a.Sources ?? []).SequenceEqual(b.Sources ?? [], StringComparer.OrdinalIgnoreCase);

    private static bool SameColumn(ColumnMap a, ColumnMap b) =>
        SameText(a.Expr, b.Expr) && SameText(a.Default, b.Default)
        && new HashSet<string>((a.SourceColumns ?? []).Where(s => s is not null), StringComparer.OrdinalIgnoreCase)
            .SetEquals((b.SourceColumns ?? []).Where(s => s is not null));

    private static bool SameText(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Deserialisation is case-insensitive, so the node may spell a property in any case: remove every spelling, then
    /// write the camelCase one — or none at all for null, so an unset value is omitted rather than stored as null or "".</summary>
    private static void SetProperty(JsonObject obj, string name, JsonNode? value)
    {
        foreach (var k in obj.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            obj.Remove(k);
        if (value is not null) obj[name] = value;
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
