using System.Text.Json.Nodes;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingFlowTests
{
    [Fact]
    public async Task Map_auto_inline_drafts_and_hands_over_to_the_mapping_architect()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        services.Phases.SetStatus(PhaseName.Mapping, PhaseStatus.Running);

        var result = await CliRunner.RunAsync(project.Ws, null, "map", "auto", "--inline");

        Assert.Equal(0, result.Exit);
        Assert.Equal("drafting", (string?)result.Json["status"]);
        Assert.Equal("script", services.Artifacts.Latest(PhaseName.Mapping)!.Author);
        var next = services.Workflow.Next();
        Assert.Equal(("agent", "mapping-architect", "draft"), (next.Action, next.Agent, next.Mode));
        var packet = JsonNode.Parse(File.ReadAllText(next.Packet!))!;
        Assert.Contains("dbo.ORD_STATUS.STATUS_CD varchar(10)", packet["data"]!["uncovered"]!.AsArray().Select(n => (string?)n));
        Assert.Contains(packet["data"]!["detail"]!.AsArray(), d => (string?)d!["target"] == "app.Orders");
    }

    [Fact]
    public void Playbook_style_patch_resolves_the_blockers()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        services.AddMapping(draft, PhaseStatus.Drafting);
        static JsonNode J(string json) => JsonNode.Parse(json)!;
        var ops = new List<PatchOp>
        {
            new("replace", "/tables/app.Customers/columns/FirstName", J("""{"expr":"LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)","sourceColumns":["dbo.CUST.CUST_NM"],"confidence":1,"method":"agent","rationale":"First word of CUST_NM."}""")),
            new("replace", "/tables/app.Customers/columns/LastName", J("""{"expr":"LTRIM(SUBSTRING(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') + 1, 100))","sourceColumns":["dbo.CUST.CUST_NM"],"confidence":1,"method":"agent","rationale":"Remainder of CUST_NM after the first space."}""")),
            new("replace", "/tables/app.Orders/kind", J("\"merge\"")),
            new("replace", "/tables/app.Orders/sources", J("""["dbo.ORD_HDR","dbo.ORD_STATUS"]""")),
            new("add", "/tables/app.Orders/from", J("\"[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]\"")),
            new("replace", "/tables/app.Orders/method", J("\"agent\"")),
            new("replace", "/tables/app.Orders/columns/StatusCode", J("""{"expr":"st.[STATUS_CD]","sourceColumns":["dbo.ORD_STATUS.STATUS_CD","dbo.ORD_HDR.STATUS_ID"],"confidence":1,"method":"agent","rationale":"Status code from the ORD_STATUS lookup."}""")),
            new("add", "/drops/dbo.CUST.FAX_NO", J("""{"reason":"ShopV2 has no fax column.","method":"agent"}""")),
            new("add", "/drops/dbo.ORD_STATUS.STATUS_ID", J("""{"reason":"Lookup key; Orders stores the code.","method":"agent"}""")),
            new("add", "/drops/dbo.ORD_STATUS.STATUS_DESC", J("""{"reason":"Descriptions are not stored in ShopV2.","method":"agent"}""")),
        };
        var patch = new Patch("mapping", 0, ops, [], "Split CUST_NM, joined ORD_STATUS, dropped fax and status lookup columns.");

        var dry = services.Workflow.ApplyPatch(patch, dryRun: true);
        Assert.True(dry.Ok, string.Join("\n", dry.Errors));
        var applied = services.Workflow.ApplyPatch(patch);

        Assert.True(applied.Ok, string.Join("\n", applied.Errors));
        Assert.Equal(1, applied.Version);
        Assert.DoesNotContain(applied.Warnings, w => w.Contains("not mapped or dropped") || w.Contains("NOT NULL"));
        Assert.Equal(PhaseStatus.AwaitingReview, services.Phases.Get(PhaseName.Mapping).Status);
    }

    [Fact]
    public void Approve_is_blocked_by_blockers_and_allowed_for_the_approved_mapping()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        services.AddMapping(draft, PhaseStatus.AwaitingReview);

        var ex = Assert.Throws<WorkflowException>(() => services.Workflow.Approve(PhaseName.Mapping));
        Assert.Contains(ex.Details, d => d.Contains("dbo.ORD_STATUS"));

        services.AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");
        services.Workflow.Approve(PhaseName.Mapping);
        Assert.Equal(PhaseStatus.Approved, services.Phases.Get(PhaseName.Mapping).Status);
        Assert.Equal(PhaseStatus.Running, services.Phases.Get(PhaseName.Sql).Status);
    }
}
