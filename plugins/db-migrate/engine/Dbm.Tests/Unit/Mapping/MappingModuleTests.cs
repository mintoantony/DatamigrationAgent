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
    public void Needs_agent_for_an_undecided_type_risk_even_when_nothing_else_is_open()
    {
        using var project = TempProject.Create();
        var module = new MappingModule(project.Services.WithSampleCatalogs());
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        // The auto-mapper's shape for varchar(300) -> nvarchar(200): fuzzy, at or above the auto-accept band.
        (comment.Method, comment.Confidence, comment.TypeRisk) = (MapMethod.Fuzzy, 0.88, "may truncate (source max 300)");
        Assert.True(module.NeedsAgent(Json.ToNode(m)));

        foreach (var decided in new[] { MapMethod.Agent, MapMethod.Human, MapMethod.Carried })
        {
            comment.Method = decided;
            Assert.False(module.NeedsAgent(Json.ToNode(m)), decided.ToString());
        }

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

    // ---- B7: a remap re-evaluates its own type risk ------------------------------------------------------------------

    private const string Truncates = "may truncate (source max 300)";

    private static MappingPayload WithComment(string expr, string sourceColumn, MapMethod method = MapMethod.Human, string? risk = null)
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Columns["Comment"] = new ColumnMap
        {
            Expr = expr, SourceColumns = [sourceColumn], Confidence = 1, Method = method, TypeRisk = risk
        };
        return m;
    }

    private static JsonObject CommentNode(JsonNode payload) => payload["tables"]!["app.Orders"]!["columns"]!["Comment"]!.AsObject();

    [Fact]
    public void A_remap_onto_a_lossy_source_gains_the_risk()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var before = WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID");            // int -> nvarchar(200): no risk
        var payload = Json.ToNode(WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT"));   // varchar(300) -> nvarchar(200)

        var check = module.Validate(Ctx(services, before), payload);

        Assert.True(check.Ok, string.Join("\n", check.Errors));
        Assert.Equal(Truncates, (string?)CommentNode(payload)["typeRisk"]);
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", check.Warnings);
    }

    [Fact]
    public void A_remap_away_from_a_lossy_source_loses_the_risk()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT", risk: Truncates);
        var payload = Json.ToNode(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID", risk: Truncates));   // stale risk left behind
        CommentNode(payload)["TypeRisk"] = "a differently-cased duplicate";
        CommentNode(payload).Remove("typeRisk");

        var check = module.Validate(Ctx(services, before), payload);

        Assert.True(check.Ok, string.Join("\n", check.Errors));
        Assert.DoesNotContain(CommentNode(payload), kv => kv.Key.Equals("typeRisk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(check.Warnings, w => w.StartsWith("app.Orders.Comment"));
    }

    [Fact]
    public void An_empty_expression_with_one_source_column_is_recomputed()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        after.Tables["app.Orders"].Columns["Comment"].Expr = null;
        var payload = Json.ToNode(after);

        Assert.True(module.Validate(Ctx(services, SampleMappings.Approved()), payload).Ok);
        Assert.Equal(Truncates, (string?)CommentNode(payload)["typeRisk"]);
    }

    [Fact]
    public void An_unchanged_column_keeps_its_risk_text_byte_for_byte()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        const string agentWording = "Agent: CMNT values over 200 chars exist in 3 rows — accepted, see rationale.";
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT", MapMethod.Agent, agentWording);
        var after = WithComment("s.[CMNT]", "DBO.ORD_HDR.cmnt", MapMethod.Agent, agentWording);   // same source, other case
        after.Tables["app.Orders"].Columns["Comment"].Rationale = "unrelated save";
        after.Tables["app.Customers"].Columns["Email"].Expr = "LOWER(s.[EMAIL_ADDR])";              // another column changes
        var payload = Json.ToNode(after);

        Assert.True(module.Validate(Ctx(services, before), payload).Ok);

        Assert.Equal(agentWording, (string?)CommentNode(payload)["typeRisk"]);
    }

    [Fact]
    public void A_human_custom_expression_replaces_the_stale_risk_with_the_custom_expression_attention()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT", risk: Truncates);
        var payload = Json.ToNode(WithComment("CAST(s.[CMNT] AS nvarchar(200))", "dbo.ORD_HDR.CMNT", risk: Truncates));

        var check = module.Validate(Ctx(services, before), payload);

        Assert.True(check.Ok, string.Join("\n", check.Errors));
        Assert.Equal(MappingModule.CustomExpressionRisk, (string?)CommentNode(payload)["typeRisk"]);
        Assert.Contains("app.Orders.Comment: type risk: custom expression: type risk not evaluated", check.Warnings);
        Assert.DoesNotContain(check.Warnings, w => w.Contains(Truncates));
    }

    [Fact]
    public void An_agent_custom_expression_keeps_exactly_what_the_patch_says()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT", risk: Truncates);

        var fixedByAgent = Json.ToNode(WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT", MapMethod.Agent));
        Assert.True(module.Validate(Ctx(services, before), fixedByAgent).Ok);
        Assert.False(CommentNode(fixedByAgent).ContainsKey("typeRisk"));     // no re-flag of the agent's own fix

        var keptByAgent = Json.ToNode(WithComment("RTRIM(s.[CMNT])", "dbo.ORD_HDR.CMNT", MapMethod.Agent, Truncates));
        Assert.True(module.Validate(Ctx(services, before), keptByAgent).Ok);
        Assert.Equal(Truncates, (string?)CommentNode(keptByAgent)["typeRisk"]);
    }

    [Fact]
    public void A_multi_source_bare_reference_counts_as_custom()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var after = SampleMappings.Approved();
        after.Tables["app.Orders"].Columns["StatusCode"].Expr = "st.[STATUS_CD] ";   // whitespace edit, still two source columns
        var payload = Json.ToNode(after);

        Assert.True(module.Validate(Ctx(services, SampleMappings.Approved()), payload).Ok);

        Assert.Equal(MappingModule.CustomExpressionRisk, (string?)payload["tables"]!["app.Orders"]!["columns"]!["StatusCode"]!["typeRisk"]);
    }

    [Fact]
    public void A_payload_with_errors_is_not_normalised()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        after.Tables["app.Nope"] = new TableMap();
        var payload = Json.ToNode(after);

        Assert.False(module.Validate(Ctx(services, SampleMappings.Approved()), payload).Ok);
        Assert.False(CommentNode(payload).ContainsKey("typeRisk"));
    }

    [Fact]
    public void The_recomputed_risk_is_stored_by_a_human_edit_and_read_back()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        services.AddMapping(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"), PhaseStatus.AwaitingReview, "human");
        static JsonNode J(string json) => JsonNode.Parse(json)!;
        var patch = new Patch("mapping", 0,
        [
            new("replace", "/tables/app.Orders/columns/Comment",
                J("""{"expr":"s.[CMNT]","sourceColumns":["dbo.ORD_HDR.CMNT"],"confidence":1,"method":"human"}""")),
        ], []);

        var applied = services.Workflow.HumanEdit(patch);

        Assert.True(applied.Ok, string.Join("\n", applied.Errors));
        var stored = services.Artifacts.Get(PhaseName.Mapping, applied.Version!.Value)!;
        Assert.Equal(Truncates, Json.Deserialize<MappingPayload>(stored.PayloadJson).Tables["app.Orders"].Columns["Comment"].TypeRisk);
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", applied.Warnings);

        // …and it reaches the agent from the stored version.
        var module = new MappingModule(services);
        var ctx = new ModuleContext { Services = services, Current = stored, OpenFeedback = [] };
        Assert.Equal(Truncates, (string?)module.BuildPacket(ctx, PacketMode.Draft)["detail"]![0]!["columns"]!["Comment"]!["typeRisk"]);
    }
}
