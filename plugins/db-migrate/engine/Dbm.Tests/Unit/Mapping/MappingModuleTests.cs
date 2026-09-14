using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingModuleTests
{
    private static ModuleContext Ctx(DbmServices s, MappingPayload m, params FeedbackRow[] feedback) => new()
    {
        Services = s,
        Current = new ArtifactRow(1, PhaseName.Mapping, 3, Json.Serialize(m), "agent", null, DateTimeOffset.UtcNow),
        OpenFeedback = feedback
    };

    [Fact]
    public void Identity_and_registration()
    {
        var module = new MappingModule(null!);
        Assert.Equal((PhaseName.Mapping, "mapping-architect", "automap"), (module.Phase, module.Agent, module.JobKind));
        using var project = TempProject.Create();
        Assert.IsType<MappingModule>(project.Services.Modules[PhaseName.Mapping]);
    }

    [Fact]
    public void Needs_agent_while_attention_or_blockers_remain()
    {
        using var project = TempProject.Create();
        var module = new MappingModule(project.Services.WithSampleCatalogs());
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        Assert.True(module.NeedsAgent(Json.ToNode(draft)));
        Assert.False(module.NeedsAgent(Json.ToNode(SampleMappings.Approved())));
    }

    [Fact]
    public void Validate_rejects_errors_and_reports_blockers_and_attention_as_warnings()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approved = SampleMappings.Approved();
        var ok = module.Validate(Ctx(services, approved), Json.ToNode(approved));
        Assert.True(ok.Ok);
        Assert.Empty(ok.Warnings);

        var bad = SampleMappings.Approved();
        bad.Tables["app.Nope"] = new TableMap();
        bad.Drops.Remove("dbo.CUST.FAX_NO");
        bad.Tables["app.Customers"].Columns["Email"].Method = MapMethod.Vector;
        bad.Tables["app.Customers"].Columns["Email"].Confidence = 0.6;
        var check = module.Validate(Ctx(services, bad), Json.ToNode(bad));
        Assert.False(check.Ok);
        Assert.Contains("unknown target table 'app.Nope'", check.Errors);
        Assert.Contains("source column dbo.CUST.FAX_NO is not mapped or dropped", check.Warnings);
        Assert.Contains(check.Warnings, w => w.StartsWith("app.Customers.Email:"));

        var garbage = JsonNode.Parse("""{"tables":{"app.Customers":{"method":"robot"}}}""")!;
        var rejected = module.Validate(Ctx(services, approved), garbage);
        Assert.StartsWith("payload is not a valid mapping", Assert.Single(rejected.Errors));
    }

    [Fact]
    public void Approval_blockers_and_summary_come_from_the_validator()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approved = SampleMappings.Approved();
        Assert.Empty(module.ApprovalBlockers(Ctx(services, approved), Json.ToNode(approved)));
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers", module.Summarize(Json.ToNode(approved)));

        approved.Drops.Remove("dbo.TMP_IMPORT");
        Assert.Equal(["source table dbo.TMP_IMPORT is not mapped or dropped (1 columns)"],
            module.ApprovalBlockers(Ctx(services, approved), Json.ToNode(approved)));
    }

    [Fact]
    public void Build_packet_uses_the_draft_or_rework_shape()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approved = SampleMappings.Approved();

        var draft = module.BuildPacket(Ctx(services, approved), PacketMode.Draft);
        Assert.Equal(6, draft["confident"]!.AsArray().Count);

        var feedback = new FeedbackRow(12, PhaseName.Mapping, 3, "colmap:app.Customers.Email", "Lower-case the email",
            FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);
        var rework = module.BuildPacket(Ctx(services, approved, feedback), PacketMode.Rework);
        Assert.Equal(12, (long?)rework["contexts"]![0]!["id"]);
        Assert.Equal("Email", (string?)rework["contexts"]![0]!["context"]!["column"]!["name"]);
    }
}
