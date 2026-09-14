using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.Mapping;

/// <summary>Phase module for MAPPING: automap job → mapping-architect agent → human review.</summary>
public sealed class MappingModule(DbmServices services) : IPhaseModule
{
    public PhaseName Phase => PhaseName.Mapping;
    public string Agent => "mapping-architect";
    public string JobKind => "automap";

    public bool NeedsAgent(JsonNode draft)
    {
        var (src, tgt) = Catalogs();
        var m = Parse(draft);
        return MappingValidator.Attention(m, Options()).Count > 0 || MappingValidator.Blockers(m, src, tgt).Count > 0;
    }

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        var (src, tgt) = Catalogs();
        var m = Json.Deserialize<MappingPayload>(ctx.Current.PayloadJson);
        return mode == PacketMode.Draft
            ? MappingPacket.Draft(m, src, tgt, Options())
            : MappingPacket.Rework(m, src, tgt, Options(), ctx.OpenFeedback);
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        MappingPayload m;
        try { m = Parse(payload); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new PayloadCheck([$"payload is not a valid mapping: {e.Message}"], []);
        }
        var (src, tgt) = Catalogs();
        var warnings = MappingValidator.Blockers(m, src, tgt);
        warnings.AddRange(MappingValidator.Attention(m, Options()));
        return new PayloadCheck(MappingValidator.Errors(m, src, tgt), warnings);
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload)
    {
        var (src, tgt) = Catalogs();
        return MappingValidator.Blockers(Parse(payload), src, tgt);
    }

    public string Summarize(JsonNode payload)
    {
        var (src, tgt) = Catalogs();
        return MappingPacket.Summary(Parse(payload), src, tgt, Options());
    }

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
