using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

/// <summary>Task 3.4 fix round 1 (B2). INVARIANT: any payload Validate accepts can be processed by Summarize and by BuildPacket
/// in both modes. Every case runs the FULL chain: when Validate accepts, Summarize, Draft and Rework (with an anchor of every
/// kind) must not throw; when it rejects, the engine must not store it.</summary>
public class MappingMalformedPayloadTests
{
    /// <summary>(name, mutation of the approved payload's JSON, whether Validate must reject it).</summary>
    public static TheoryData<string, bool> Cases() => new()
    {
        { "tables null", true },
        { "drops null", true },
        { "notes null", true },
        { "table entry null", true },
        { "table sources null", true },
        { "table sources contain null", true },
        { "table columns null", true },
        { "table candidates contain null", true },
        { "table candidate without source", true },
        { "column entry null", true },
        { "column sourceColumns null", true },
        { "column sourceColumns contain null", true },
        { "column candidates contain null", true },
        { "column candidate without source", true },
        { "drop entry null", true },
        { "skip table columns null", true },
        { "riskAck is a bare string (the old shape)", true },
        { "riskAck is an array", true },
        { "riskAck risk is a number", true },
        // Benign shapes: must be accepted AND survive the chain.
        { "tables empty", false },
        { "table candidates null", false },
        { "table candidates empty", false },
        { "column candidates null", false },
        { "column candidate why null", false },
        { "column typeRisk null", false },
        { "notes absent", false },
        { "sources absent", false },
        { "riskAck null", false },
        { "riskAck with null members", false },
        { "riskAck empty object", false },
        { "riskAck on a risky column with null members", false },
    };

    private static JsonObject Mutate(string name)
    {
        var root = Json.ToNode(SampleMappings.Approved()).AsObject();
        var orders = root["tables"]!["app.Orders"]!.AsObject();
        var comment = orders["columns"]!["Comment"]!.AsObject();
        switch (name)
        {
            case "tables null": root["tables"] = null; break;
            case "drops null": root["drops"] = null; break;
            case "notes null": root["notes"] = null; break;
            case "table entry null": root["tables"]!["app.Orders"] = null; break;
            case "table sources null": orders["sources"] = null; break;
            case "table sources contain null": orders["sources"]!.AsArray().Add(null); break;
            case "table columns null": orders["columns"] = null; break;
            case "table candidates contain null": orders["candidates"] = new JsonArray((JsonNode?)null); break;
            case "table candidate without source": orders["candidates"] = JsonNode.Parse("""[{"score":0.5,"why":"x"}]"""); break;
            case "column entry null": orders["columns"]!["Comment"] = null; break;
            case "column sourceColumns null": comment["sourceColumns"] = null; break;
            case "column sourceColumns contain null": comment["sourceColumns"]!.AsArray().Add(null); break;
            case "column candidates contain null": comment["candidates"] = new JsonArray((JsonNode?)null); break;
            case "column candidate without source": comment["candidates"] = JsonNode.Parse("""[{"score":0.5,"why":"x"}]"""); break;
            case "drop entry null": root["drops"]!["dbo.TMP_IMPORT"] = null; break;
            case "skip table columns null": orders["kind"] = "skip"; orders["columns"] = null; break;
            case "tables empty": root["tables"] = new JsonObject(); break;
            case "table candidates null": orders["candidates"] = null; break;
            case "table candidates empty": orders["candidates"] = new JsonArray(); break;
            case "column candidates null": comment["candidates"] = null; break;
            case "column candidate why null": comment["candidates"] = JsonNode.Parse("""[{"source":"dbo.ORD_HDR.ORD_ID","score":0.5,"why":null}]"""); break;
            case "column typeRisk null": comment["typeRisk"] = null; break;
            case "notes absent": root.Remove("notes"); break;
            case "sources absent": orders.Remove("sources"); break;
            case "riskAck is a bare string (the old shape)": comment["riskAck"] = "accepted"; break;
            case "riskAck is an array": comment["riskAck"] = new JsonArray("accepted"); break;
            case "riskAck risk is a number": comment["riskAck"] = JsonNode.Parse("""{"risk":5,"reason":"x"}"""); break;
            case "riskAck null": comment["riskAck"] = null; break;
            case "riskAck with null members": comment["riskAck"] = JsonNode.Parse("""{"risk":null,"reason":null}"""); break;
            case "riskAck empty object": comment["riskAck"] = new JsonObject(); break;
            case "riskAck on a risky column with null members":
                comment["typeRisk"] = "may truncate (source max 300)";
                comment["riskAck"] = JsonNode.Parse("""{"risk":null,"reason":"x"}""");
                break;
            default: throw new ArgumentException(name);
        }
        return root;
    }

    private static IReadOnlyList<FeedbackRow> EveryAnchorKind(CatalogSnapshot src, CatalogSnapshot tgt)
    {
        var anchors = new List<string?> { null, "general", "narrative", "finding:1", "colmap:app.Nope.X" };
        foreach (var t in tgt.Tables)
        {
            anchors.Add($"tablemap:{t.Key}");
            anchors.Add($"table:tgt:{t.Key}");
            anchors.AddRange(t.Columns.SelectMany(c => new[] { $"colmap:{t.Key}.{c.Name}", $"column:tgt:{t.Key}.{c.Name}" }));
        }
        foreach (var s in src.Tables)
        {
            anchors.Add($"table:src:{s.Key}");
            anchors.AddRange(s.Columns.Select(c => $"column:src:{s.Key}.{c.Name}"));
        }
        return anchors.Select((a, i) => new FeedbackRow(i + 1, PhaseName.Mapping, 0, a, "check", FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow)).ToList();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Validate_accepts_only_what_summarize_and_both_packet_modes_can_process(string name, bool rejected)
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approvedRow = new ArtifactRow(1, PhaseName.Mapping, 0, Json.Serialize(SampleMappings.Approved()), "human", null, DateTimeOffset.UtcNow);
        var payload = Mutate(name);

        var check = module.Validate(new ModuleContext { Services = services, Current = approvedRow, OpenFeedback = [] }, payload);

        Assert.Equal(rejected, !check.Ok);
        if (!check.Ok) return;
        // Accepted: the rest of the chain must hold, on the node exactly as the engine would store it.
        var stored = new ArtifactRow(2, PhaseName.Mapping, 1, payload.ToJsonString(Json.Options), "human", null, DateTimeOffset.UtcNow);
        var feedback = EveryAnchorKind(SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.NotEmpty(module.Summarize(JsonNode.Parse(stored.PayloadJson)!));
        Assert.NotNull(module.BuildPacket(new ModuleContext { Services = services, Current = stored, OpenFeedback = [] }, PacketMode.Draft));
        Assert.NotNull(module.BuildPacket(new ModuleContext { Services = services, Current = stored, OpenFeedback = feedback }, PacketMode.Rework));
        module.NeedsAgent(JsonNode.Parse(stored.PayloadJson)!);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rejected_shapes_throw_somewhere_in_the_chain_or_the_carry_over_without_the_check(string name, bool rejected)
    {
        // Evidence that each rejection is needed: bypassing Validate, the chain (or the automap carry-over) throws.
        if (!rejected || name is "table sources contain null" or "column sourceColumns contain null" or "table candidate without source"
                or "column candidate without source") return;   // these four are rejected as meaningless, not because they throw
        if (name.StartsWith("riskAck", StringComparison.Ordinal)) return;   // rejected at deserialisation: Validate reports the parse error
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();
        var m = Json.FromNode<MappingPayload>(Mutate(name));
        var options = new Dbm.Core.Matching.MatchOptions();

        Assert.ThrowsAny<Exception>(() =>
        {
            MappingPacket.Summary(m, src, tgt, options);
            MappingPacket.Draft(m, src, tgt, options);
            MappingPacket.Rework(m, src, tgt, options, EveryAnchorKind(src, tgt));
            Dbm.Core.Matching.AutoMapper.Map(src, tgt, Dbm.Core.Matching.Synonyms.Default(), options, m);
        });
    }

    [Fact]
    public void Odd_but_well_formed_acknowledgements_in_an_artifact_that_skipped_Validate_do_not_throw()
    {
        // Carried job drafts are stored without Validate, so the predicate and every consumer must tolerate these shapes.
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var root = Json.ToNode(SampleMappings.Approved()).AsObject();
        var columns = root["tables"]!["app.Orders"]!["columns"]!;
        columns["Comment"]!["typeRisk"] = "may truncate (source max 300)";
        columns["Comment"]!["riskAck"] = JsonNode.Parse("""{"risk":null,"reason":null}""");
        columns["OrderDate"]!["typeRisk"] = "fractional seconds rounded to 0 digits";
        columns["OrderDate"]!["riskAck"] = new JsonObject();
        columns["CustomerId"]!["riskAck"] = JsonNode.Parse("""{"risk":"nothing","reason":"no risk here"}""");
        var row = new ArtifactRow(2, PhaseName.Mapping, 1, root.ToJsonString(Json.Options), "script", null, DateTimeOffset.UtcNow);
        var feedback = EveryAnchorKind(SampleCatalogs.Source(), SampleCatalogs.Target());

        Assert.Contains("2 attention", module.Summarize(JsonNode.Parse(row.PayloadJson)!));
        Assert.True(module.NeedsAgent(JsonNode.Parse(row.PayloadJson)!));
        Assert.NotNull(module.BuildPacket(new ModuleContext { Services = services, Current = row, OpenFeedback = [] }, PacketMode.Draft));
        Assert.NotNull(module.BuildPacket(new ModuleContext { Services = services, Current = row, OpenFeedback = feedback }, PacketMode.Rework));
        Dbm.Core.Matching.AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Dbm.Core.Matching.Synonyms.Default(),
            new Dbm.Core.Matching.MatchOptions(), Json.Deserialize<MappingPayload>(row.PayloadJson));
    }

    [Fact]
    public void A_rejected_human_edit_is_not_stored_and_the_phase_stays_workable()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        services.AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");

        var bad = services.Workflow.HumanEdit(new Patch("mapping", 0, [new("replace", "/tables/app.Orders/columns", null)], []));

        Assert.False(bad.Ok);
        Assert.Contains("app.Orders: columns must be an object", bad.Errors);
        Assert.Equal(0, services.Phases.Get(PhaseName.Mapping).CurrentVersion);
        var ok = services.Workflow.HumanEdit(new Patch("mapping", 0,
            [new("replace", "/tables/app.Orders/rationale", JsonValue.Create("still editable"))], []));
        Assert.True(ok.Ok, string.Join("\n", ok.Errors));
    }
}
