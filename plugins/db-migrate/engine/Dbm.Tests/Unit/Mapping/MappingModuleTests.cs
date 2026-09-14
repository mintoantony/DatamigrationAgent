using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Patching;
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
        using var project = TempProject.Create();
        var module = new MappingModule(project.Services);
        Assert.Equal((PhaseName.Mapping, "mapping-architect", "automap"), (module.Phase, module.Agent, module.JobKind));
        Assert.IsType<MappingModule>(project.Services.Modules[PhaseName.Mapping]);
    }

    [Fact]
    public void Null_services_and_null_arguments_are_rejected_at_the_boundary()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => new MappingModule(null!)).ParamName);
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        Assert.Throws<ArgumentNullException>(() => module.NeedsAgent(null!));
        Assert.Throws<ArgumentNullException>(() => module.Summarize(null!));
        Assert.Throws<ArgumentNullException>(() => module.BuildPacket(null!, PacketMode.Draft));
        Assert.Throws<ArgumentNullException>(() => module.Validate(Ctx(services, SampleMappings.Approved()), null!));
        Assert.Throws<ArgumentNullException>(() => module.ApprovalBlockers(null!, Json.ToNode(SampleMappings.Approved())));
    }

    [Fact]
    public void Needs_agent_for_an_unacknowledged_type_risk_even_when_nothing_else_is_open()
    {
        using var project = TempProject.Create();
        var module = new MappingModule(project.Services.WithSampleCatalogs());
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        // The auto-mapper's shape for varchar(300) -> nvarchar(200): fuzzy, at or above the auto-accept band.
        (comment.Method, comment.Confidence, comment.TypeRisk) = (MapMethod.Fuzzy, 0.88, "may truncate (source max 300)");
        Assert.True(module.NeedsAgent(Json.ToNode(m)));

        foreach (var method in new[] { MapMethod.Exact, MapMethod.Agent, MapMethod.Human, MapMethod.Carried })
        {
            comment.Method = method;   // method never exempts a risk (C3): only an acknowledgement does
            comment.RiskAck = null;
            Assert.True(module.NeedsAgent(Json.ToNode(m)), method.ToString());
            comment.RiskAck = new RiskAck { Risk = comment.TypeRisk, Reason = "accepted" };
            Assert.False(module.NeedsAgent(Json.ToNode(m)), method.ToString());
        }
        comment.RiskAck = null;

        comment.Method = MapMethod.Exact;
        m.Tables["app.Orders"].Kind = "skip";   // a skipped table loads nothing, so its risk is no hazard
        m.Drops["dbo.ORD_HDR"] = new DropDecision("test", MapMethod.Human);
        m.Drops["dbo.ORD_STATUS"] = new DropDecision("test", MapMethod.Human);
        Assert.False(module.NeedsAgent(Json.ToNode(m)));
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

    [Fact]
    public void Build_packet_round_trips_a_type_risk_through_the_stored_json()
    {
        // ctx.Current holds serialised JSON: this is the only test that would catch a renamed or [JsonIgnore]d TypeRisk.
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.Method, comment.Confidence, comment.TypeRisk) = (MapMethod.Fuzzy, 0.9, Truncates);
        Assert.Contains("\"typeRisk\":\"may truncate (source max 300)\"", Json.Serialize(m));

        var draft = module.BuildPacket(Ctx(services, m), PacketMode.Draft);
        Assert.Equal(5, draft["confident"]!.AsArray().Count);
        Assert.Equal(Truncates, (string?)draft["detail"]![0]!["columns"]!["Comment"]!["typeRisk"]);

        var feedback = new FeedbackRow(7, PhaseName.Mapping, 3, "colmap:app.Orders.Comment", "check length",
            FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);
        var rework = module.BuildPacket(Ctx(services, m, feedback), PacketMode.Rework);
        Assert.Equal(Truncates, (string?)rework["contexts"]![0]!["context"]!["map"]!["typeRisk"]);
    }

    private const string Truncates = "may truncate (source max 300)";
}
