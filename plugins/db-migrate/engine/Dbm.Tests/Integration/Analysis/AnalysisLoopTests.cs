using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Analysis;

[Trait("Category", "Integration")]
public sealed class AnalysisLoopTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Discover_analyze_packet_agent_patch_and_validation()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        var s = project.Services;
        s.Workflow.OnConnectionsSaved();                     // Setup approved, Discovery running, "discover" queued

        var r = await CliRunner.RunAsync(project.Ws, null, "discover", "--inline");
        Assert.True(r.Exit == 0, r.Out);
        Assert.Equal((8, 6), ((int)r.Json["src"]!, (int)r.Json["tgt"]!));
        Assert.True((int)r.Json["ran"]! >= 2);               // discover + analyze

        Assert.Equal(PhaseStatus.Approved, s.Phases.Get(PhaseName.Discovery).Status);
        Assert.Equal(PhaseStatus.Drafting, s.Phases.Get(PhaseName.Analysis).Status);
        var v0 = s.Artifacts.Latest(PhaseName.Analysis)!;
        Assert.Equal((0, "script"), (v0.Version, v0.Author));
        var r09 = Json.Deserialize<AnalysisPayload>(v0.PayloadJson).Findings.Single(f => f.Value.Rule == "R09").Key;

        var next = s.Workflow.Next();
        Assert.Equal(("agent", "schema-analyst", "analysis", "draft"), (next.Action, next.Agent, next.Phase, next.Mode));
        var packetText = File.ReadAllText(next.Packet!);
        Assert.DoesNotContain(fixture.Pair.SourceCs, packetText);
        var packet = JsonNode.Parse(packetText)!;
        Assert.Equal(0, (int)packet["baseVersion"]!);
        Assert.Contains(packet["data"]!["detail"]!.AsArray(), d => (string?)d!["id"] == r09);

        var patch = new Patch("analysis", 0, new List<PatchOp>
        {
            new("add", "/narrative", new JsonObject
            {
                ["summary"] = "LegacyShop moves about 19,700 rows into ShopV2 in under a minute; two orphan orders and one heap need decisions first.",
                ["risks"] = new JsonArray($"2 orphan orders in dbo.ORD_HDR will be rejected by the target foreign key ({r09})."),
                ["recommendations"] = new JsonArray("Decide whether to re-parent or drop the 2 orphan orders before mapping."),
            }),
            new("add", $"/findings/{r09}/commentary", JsonValue.Create("The orphans reference customers that no longer exist; filter them in the Orders source query.")),
        }, new List<FeedbackResponse>(), "Narrative and commentary drafted.");

        var dry = s.Workflow.ApplyPatch(patch, dryRun: true);
        Assert.True(dry.Ok, string.Join("; ", dry.Errors));
        var applied = s.Workflow.ApplyPatch(patch);
        Assert.True(applied.Ok, string.Join("; ", applied.Errors));
        Assert.Equal(1, applied.Version);
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Equal("agent", s.Artifacts.Latest(PhaseName.Analysis)!.Author);
        Assert.Equal("review", s.Workflow.Next().Reason);

        r = await CliRunner.RunAsync(project.Ws, null, "show", r09);
        Assert.Equal(0, r.Exit);
        Assert.Contains("commentary: The orphans reference customers", r.Out);

        var tampered = new Patch("analysis", 1, new List<PatchOp> { new("replace", "/estimates/estimatedMinutes", JsonValue.Create(99)) },
            new List<FeedbackResponse>());
        var rejected = s.Workflow.HumanEdit(tampered);
        Assert.False(rejected.Ok);
        Assert.Contains(rejected.Errors, e => e.Contains("/estimates must not be changed"));
    }
}
