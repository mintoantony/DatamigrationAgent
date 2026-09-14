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

    /// <summary>Evaluated once when the automap job completes. True when any holds:
    /// (1) attention (the confidence band) or a blocker;
    /// (2) a type risk on a column the auto-mapper assigned in this run rather than kept: method is not carried;
    /// (3) a kept (carried) bare column whose recomputed risk text differs from the text in the latest version NOT authored by the
    ///     script — never another script draft (§7a).
    /// Risks never count as attention. Skip tables load no data and are exempt from (2) and (3).</summary>
    public bool NeedsAgent(JsonNode draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (src, tgt) = Catalogs();
        var m = Parse(draft);
        if (MappingValidator.Attention(m, Options()).Count > 0 || MappingValidator.Blockers(m, src, tgt).Count > 0) return true;
        var previous = PreviousVersion();
        foreach (var (tableKey, map) in m.Tables ?? new())
        {
            if (map is null || map.Kind == "skip") continue;
            foreach (var (name, cm) in map.Columns ?? new())
            {
                if (cm is null) continue;
                if (cm.Method != MapMethod.Carried)   // assigned in this run: a test on the draft alone
                {
                    if (MappingValidator.HasTypeRisk(cm)) return true;
                    continue;
                }
                if (previous is null || cm.Expr is null || MappingValidator.BareSingleSource(map, cm, src) is null) continue;
                var before = MappingValidator.FindTableMap(previous, tableKey) is { } pm ? MappingValidator.FindColumnMap(pm, name) : null;
                if (before is not null && Blank(before.TypeRisk) != Blank(cm.TypeRisk)) return true;
            }
        }
        return false;
    }

    /// <summary>The comparand for condition 3: the latest mapping version not authored by the script. A script draft is never
    /// the comparand, so a retried job (or a second rediscovery before the agent ran) cannot compare a draft against a draft and
    /// go quiet. Null when there is none or it cannot be read.</summary>
    private MappingPayload? PreviousVersion()
    {
        var meta = services.Artifacts.List(PhaseName.Mapping).LastOrDefault(a => a.Author != "script");
        if (meta is null || services.Artifacts.Get(PhaseName.Mapping, meta.Version) is not { } row) return null;
        try { return Json.Deserialize<MappingPayload>(row.PayloadJson); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return null; }
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

    /// <summary>Order: parse → structural errors (no normalisation when there are any) → type-risk normalisation, IN PLACE on
    /// <paramref name="payload"/> because the engine stores that very node → warnings. Warning classes, in this order:
    /// ownership ("typeRisk is computed by dbm"), blockers, attention (the confidence band), and type risks
    /// ("&lt;table&gt;.&lt;column&gt;: type risk: &lt;typeRisk&gt;", one per risk, never counted as attention; omitted for a rejected payload).</summary>
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
        // A rejected payload was not normalised, so its typeRisk values are whatever the author supplied: never quote them back
        // as engine risk lines.
        if (errors.Count == 0) warnings.AddRange(MappingValidator.RiskWarnings(m));
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

    // ---- type risks: engine-owned typeRisk --------------------------------------------------------------------------

    /// <summary>Normalisation, in place on the node the engine stores, for a payload with no structural errors.
    /// CHANGED: the column or its table is absent from the stored version, the table's sources (ordered, case-insensitive) or
    /// trimmed from differ, or the column's trimmed expr, trimmed default or case-insensitive sourceColumns set differ.
    /// First match wins: (1) not changed: the stored typeRisk verbatim; (2) expr blank and default blank (present-and-not-whitespace
    /// predicate, as the blocker rules use): no risk;
    /// (3) expr non-null and a bare single-source reference: TypeCompat with the source profile, as the auto-mapper computes it;
    /// (4) everything else: <see cref="TypeCompat.UnevaluatedRisk"/>.
    /// An incoming typeRisk is never stored; it earns a warning when present and different from the stored version's.
    /// An incoming riskAck or riskClass (fields of a withdrawn design) is stripped from the node with a warning.</summary>
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
                foreach (var removed in new[] { "riskAck", "riskClass" })
                    if (RemoveProperty(columnObj, removed))
                        warnings.Add($"{tableKey}.{name}: {removed} is not a mapping field; dbm removed it");
                if (Blank(cm.TypeRisk) is { } supplied && supplied != Blank(old?.TypeRisk))
                    warnings.Add($"{tableKey}.{name}: typeRisk is computed by dbm; the supplied value was ignored");

                string? risk;
                if (!(tableChanged || old is null || !SameColumn(old, cm)))
                    risk = Blank(old!.TypeRisk);
                else if (Blank(cm.Expr) is null && Blank(cm.Default) is null)
                    risk = null;
                else if (cm.Expr is not null && MappingValidator.BareSingleSource(map, cm, src) is { } hit)
                    risk = Blank(TypeCompat.Check(ColumnType.From(hit.Column), ColumnType.From(targetColumn), hit.Column.Profile).Risk);
                else
                    risk = TypeCompat.UnevaluatedRisk;

                cm.TypeRisk = risk;
                SetProperty(columnObj, "typeRisk", risk is null ? null : JsonValue.Create(risk));
            }
        }
    }

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

    /// <summary>Removes every case spelling of <paramref name="name"/>; true when one was present. The deserialised model has no
    /// such field, but the engine stores the node, so a field an older playbook taught would otherwise persist.</summary>
    private static bool RemoveProperty(JsonObject obj, string name)
    {
        var keys = obj.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var k in keys) obj.Remove(k);
        return keys.Count > 0;
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
