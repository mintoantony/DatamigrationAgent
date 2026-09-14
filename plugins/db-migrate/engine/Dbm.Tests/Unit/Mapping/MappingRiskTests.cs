using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

/// <summary>Task 3.4 fix round 1: typeRisk is engine-owned (B7, C5, C6, D1, E3, F2–F5), riskAck is author-owned (C2, D4, D5, E2, F1).
/// Payloads are Approved() copies built inline; SampleMappings.cs is never edited.</summary>
public class MappingRiskTests
{
    private const string Truncates = "may truncate (source max 300)";
    private const string Sentinel = MappingValidator.UnevaluatedRisk;

    private static MappingPayload WithComment(string? expr, params string[] sourceColumns)
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Columns["Comment"] = new ColumnMap
        {
            Expr = expr, SourceColumns = sourceColumns.ToList(), Confidence = 1, Method = MapMethod.Human
        };
        return m;
    }

    private static ColumnMap Comment(MappingPayload m) => m.Tables["app.Orders"].Columns["Comment"];

    private static JsonObject CommentNode(JsonNode payload) => payload["tables"]!["app.Orders"]!["columns"]!["Comment"]!.AsObject();

    /// <summary>Runs Validate with <paramref name="before"/> as ctx.Current on the serialised <paramref name="after"/> and returns
    /// the check plus the node exactly as the engine would store it.</summary>
    private static (PayloadCheck Check, JsonNode Node) Validate(MappingPayload before, JsonNode after)
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var ctx = new ModuleContext
        {
            Services = services,
            Current = new ArtifactRow(1, PhaseName.Mapping, 3, Json.Serialize(before), "human", null, DateTimeOffset.UtcNow),
            OpenFeedback = []
        };
        var check = new MappingModule(services).Validate(ctx, after);
        Assert.True(check.Ok, string.Join("\n", check.Errors));
        return (check, after);
    }

    private static (PayloadCheck Check, JsonNode Node) Validate(MappingPayload before, MappingPayload after) => Validate(before, Json.ToNode(after));

    // ---- typeRisk ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_approved_fixture_saved_over_itself_is_untouched()
    {
        var (check, node) = Validate(SampleMappings.Approved(), SampleMappings.Approved());

        Assert.Empty(check.Warnings);
        Assert.Equal(Json.Serialize(SampleMappings.Approved()), node.ToJsonString(Json.Options));
    }

    [Fact]
    public void A_remap_onto_a_lossy_source_gains_the_risk()
    {
        var (check, node) = Validate(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"), WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT"));

        Assert.Equal(Truncates, (string?)CommentNode(node)["typeRisk"]);
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", check.Warnings);
    }

    [Fact]
    public void A_remap_away_from_a_lossy_source_loses_the_risk_even_when_the_node_still_carries_it()
    {
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(before).TypeRisk = Truncates;
        var after = WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID");
        Comment(after).TypeRisk = Truncates;   // a field-level edit leaves the old risk in the node

        var (check, node) = Validate(before, after);

        Assert.False(CommentNode(node).ContainsKey("typeRisk"));
        Assert.DoesNotContain(check.Warnings, w => w.StartsWith("app.Orders.Comment"));
    }

    [Fact]
    public void An_unchanged_column_restores_Current_risk_that_the_patch_dropped_or_altered()
    {
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(before).TypeRisk = "text an earlier version stored, kept byte-for-byte";

        var dropped = WithComment(" s.[CMNT] ", "DBO.ORD_HDR.cmnt");   // whole-object replace omitting the field; reformatted only
        Assert.Equal("text an earlier version stored, kept byte-for-byte", (string?)CommentNode(Validate(before, dropped).Node)["typeRisk"]);

        var altered = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(altered).TypeRisk = "an agent's own wording";
        var (check, node) = Validate(before, altered);
        Assert.Equal("text an earlier version stored, kept byte-for-byte", (string?)CommentNode(node)["typeRisk"]);
        Assert.Contains("app.Orders.Comment: typeRisk is computed by dbm; the supplied value was ignored", check.Warnings);
    }

    [Fact]
    public void A_carried_risk_in_the_node_is_not_reported_as_written()
    {
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(before).TypeRisk = Truncates;
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        (Comment(after).TypeRisk, Comment(after).Rationale) = (Truncates, "edited the rationale only");

        Assert.DoesNotContain(Validate(before, after).Check.Warnings, w => w.Contains("ignored"));
    }

    [Fact]
    public void A_non_bare_expression_over_changed_source_columns_takes_the_sentinel_and_no_supplied_text()
    {
        var before = WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID");
        var after = WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT");
        Comment(after).TypeRisk = "nothing to see";

        var (check, node) = Validate(before, after);

        Assert.Equal(Sentinel, (string?)CommentNode(node)["typeRisk"]);
        Assert.Contains($"app.Orders.Comment: type risk: {Sentinel}", check.Warnings);
    }

    [Fact]
    public void An_expression_edit_over_the_same_source_columns_takes_the_sentinel_not_Current_text()
    {
        // Risk model §5: a changed column that is not a bare reference takes the sentinel; nothing keeps Current's text.
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(before).TypeRisk = Truncates;

        var (_, node) = Validate(before, WithComment("RTRIM(s.[CMNT])", "dbo.ORD_HDR.CMNT"));

        Assert.Equal(Sentinel, (string?)CommentNode(node)["typeRisk"]);
    }

    [Fact]
    public void An_expression_edit_over_a_column_with_no_risk_text_takes_the_sentinel()
    {
        // The fixture's custom expressions carry no text (stored before this rule existed): the first edit sentinels them.
        var (_, node) = Validate(SampleMappings.Approved(), Json.ToNode(Edit("LEFT(s.[CUST_NM], 20)")));

        Assert.Equal(Sentinel, (string?)node["tables"]!["app.Customers"]!["columns"]!["FirstName"]!["typeRisk"]);

        static MappingPayload Edit(string expr)
        {
            var m = SampleMappings.Approved();
            m.Tables["app.Customers"].Columns["FirstName"].Expr = expr;
            return m;
        }
    }

    [Fact]
    public void An_expression_that_names_one_column_but_reads_another_takes_the_sentinel()
    {
        var (_, node) = Validate(SampleMappings.Approved(), WithComment("s.[TOTAL_AMT]", "dbo.ORD_HDR.CMNT"));

        Assert.Equal(Sentinel, (string?)CommentNode(node)["typeRisk"]);
    }

    [Fact]
    public void A_change_to_the_table_sources_or_from_recomputes_every_bare_column()
    {
        var before = SampleMappings.Approved();
        Comment(before).TypeRisk = null;   // the fixture's state: no text on a lossy bare column
        var after = SampleMappings.Approved();
        after.Tables["app.Orders"].From = "[dbo].[ORD_HDR] AS s INNER JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]";

        var (_, node) = Validate(before, after);

        Assert.Equal(Truncates, (string?)CommentNode(node)["typeRisk"]);
        Assert.Equal("fractional seconds rounded to 0 digits", (string?)node["tables"]!["app.Orders"]!["columns"]!["OrderDate"]!["typeRisk"]);
    }

    [Fact]
    public void A_default_is_a_conversion_and_takes_the_sentinel()
    {
        var after = WithComment(null);
        Comment(after).Default = "'n/a'";

        var (_, node) = Validate(SampleMappings.Approved(), after);

        Assert.Equal(Sentinel, (string?)CommentNode(node)["typeRisk"]);
    }

    [Fact]
    public void No_expression_and_no_default_has_no_risk()
    {
        var before = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(before).TypeRisk = Truncates;

        var (_, node) = Validate(before, WithComment(null, "dbo.ORD_HDR.CMNT"));

        Assert.False(CommentNode(node).ContainsKey("typeRisk"));
    }

    [Fact]
    public void A_payload_with_errors_is_not_normalised()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        after.Tables["app.Nope"] = new TableMap();
        var node = Json.ToNode(after);
        var ctx = new ModuleContext
        {
            Services = services,
            Current = new ArtifactRow(1, PhaseName.Mapping, 3, Json.Serialize(SampleMappings.Approved()), "human", null, DateTimeOffset.UtcNow),
            OpenFeedback = []
        };

        Assert.False(new MappingModule(services).Validate(ctx, node).Ok);
        Assert.False(CommentNode(node).ContainsKey("typeRisk"));
    }

    [Fact]
    public void The_recomputed_risk_is_stored_by_a_human_edit_and_reaches_the_packet()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        services.AddMapping(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"), PhaseStatus.AwaitingReview, "human");
        var patch = new Patch("mapping", 0,
            [new("replace", "/tables/app.Orders/columns/Comment", JsonNode.Parse("""{"expr":"s.[CMNT]","sourceColumns":["dbo.ORD_HDR.CMNT"],"confidence":1,"method":"human"}"""))], []);

        var applied = services.Workflow.HumanEdit(patch);

        Assert.True(applied.Ok, string.Join("\n", applied.Errors));
        var stored = services.Artifacts.Get(PhaseName.Mapping, applied.Version!.Value)!;
        Assert.Equal(Truncates, Comment(Json.Deserialize<MappingPayload>(stored.PayloadJson)).TypeRisk);
        var ctx = new ModuleContext { Services = services, Current = stored, OpenFeedback = [] };
        Assert.Equal(Truncates, (string?)new MappingModule(services).BuildPacket(ctx, PacketMode.Draft)["detail"]![0]!["columns"]!["Comment"]!["typeRisk"]);
    }

    // ---- riskClass -----------------------------------------------------------------------------------------------------

    [Fact]
    public void RiskClass_is_written_beside_the_risk_and_omitted_without_one()
    {
        var (_, lossy) = Validate(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"), WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT"));
        Assert.Equal("may truncate (source max)|varchar(500)->nvarchar(200)", (string?)CommentNode(lossy)["riskClass"]);

        var (_, custom) = Validate(SampleMappings.Approved(), WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT"));
        Assert.Equal(TypeCompat.UnevaluatedClass, (string?)CommentNode(custom)["riskClass"]);

        var supplied = WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID");
        Comment(supplied).RiskClass = "forged";
        var (_, safe) = Validate(WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT"), supplied);
        Assert.False(CommentNode(safe).ContainsKey("riskClass"));
    }

    [Fact]
    public void A_bare_reference_carrying_a_default_is_recomputed_not_sentinelled()
    {
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(after).Default = "N''";   // applies only when expr is null, so the conversion is still s.[CMNT]

        Assert.Equal(Truncates, (string?)CommentNode(Validate(SampleMappings.Approved(), after).Node)["typeRisk"]);
    }

    [Fact]
    public void A_column_or_table_absent_from_Current_counts_as_changed()
    {
        var withoutComment = SampleMappings.Approved();
        withoutComment.Tables["app.Orders"].Columns.Remove("Comment");
        Assert.Equal(Truncates, (string?)CommentNode(Validate(withoutComment, SampleMappings.Approved()).Node)["typeRisk"]);

        var withoutOrders = SampleMappings.Approved();
        withoutOrders.Tables.Remove("app.Orders");
        var (_, node) = Validate(withoutOrders, SampleMappings.Approved());
        Assert.Equal(Truncates, (string?)CommentNode(node)["typeRisk"]);
        Assert.Equal(Sentinel, (string?)node["tables"]!["app.Orders"]!["columns"]!["StatusCode"]!["typeRisk"]);
    }

    // ---- riskAck -------------------------------------------------------------------------------------------------------

    private static RiskAck Ack(string? risk, string? reason = "values over 200 characters are notes we can lose") => new() { Risk = risk, Reason = reason };

    /// <summary>A lossy bare Comment as the engine stores it (risk and class), optionally acknowledged.</summary>
    private static MappingPayload RiskyComment(RiskAck? ack = null)
    {
        var m = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        (Comment(m).TypeRisk, Comment(m).RiskClass, Comment(m).RiskAck) = (Truncates, "may truncate (source max)|varchar(500)->nvarchar(200)", ack);
        return m;
    }

    private static JsonObject? AckNode(JsonNode node) => CommentNode(node)["riskAck"] as JsonObject;

    [Fact]
    public void A_new_acknowledgement_naming_the_risk_closes_it_and_is_listed()
    {
        var (check, node) = Validate(RiskyComment(), RiskyComment(Ack(Truncates)));

        Assert.Equal(Truncates, (string?)AckNode(node)!["risk"]);
        Assert.Equal("values over 200 characters are notes we can lose", (string?)AckNode(node)!["reason"]);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("type risk:"));
        Assert.Contains($"app.Orders.Comment: risk acknowledged: {Truncates} (reason: values over 200 characters are notes we can lose)", check.Warnings);
    }

    [Fact]
    public void A_field_level_acknowledgement_that_names_nothing_stays_open_even_though_the_node_still_carries_the_risk()
    {
        var after = RiskyComment(Ack(null, "ok"));   // add /…/riskAck {"reason":"ok"}: typeRisk is still in the node, but the ack names nothing

        var (check, node) = Validate(RiskyComment(), after);

        Assert.NotNull(AckNode(node));
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", check.Warnings);
        Assert.Contains($"app.Orders.Comment: riskAck does not name the current risk; its risk must be: {Truncates}", check.Warnings);
    }

    [Fact]
    public void An_acknowledgement_with_a_drifted_wording_of_the_same_hazard_is_refreshed_but_another_hazard_is_not()
    {
        var (_, drifted) = Validate(RiskyComment(), RiskyComment(Ack("may truncate (source max 297)")));
        Assert.Equal(Truncates, (string?)AckNode(drifted)!["risk"]);
        Assert.Equal("values over 200 characters are notes we can lose", (string?)AckNode(drifted)!["reason"]);

        var (check, other) = Validate(RiskyComment(), RiskyComment(Ack("time part dropped")));
        Assert.Equal("time part dropped", (string?)AckNode(other)!["risk"]);
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", check.Warnings);
    }

    [Fact]
    public void An_acknowledgement_on_a_column_with_no_risk_is_dropped_with_a_warning_not_rejected()
    {
        var after = WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID");
        Comment(after).RiskAck = Ack("anything", "being careful");

        var (check, node) = Validate(SampleMappings.Approved(), after);

        Assert.True(check.Ok);
        Assert.Null(AckNode(node));
        Assert.Contains("app.Orders.Comment: riskAck dropped: dbm computes no type risk for this column", check.Warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_acknowledgement_without_a_reason_is_stored_as_absent(string? reason)
    {
        var (check, node) = Validate(RiskyComment(), RiskyComment(Ack(Truncates, reason)));

        Assert.False(CommentNode(node).ContainsKey("riskAck"));
        Assert.DoesNotContain("riskAck", node.ToJsonString(Json.Options));
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", check.Warnings);
    }

    [Fact]
    public void A_carried_acknowledgement_survives_an_unrelated_save_including_on_a_sentinel()
    {
        static MappingPayload State()
        {
            var m = RiskyComment(Ack(Truncates));
            var first = m.Tables["app.Customers"].Columns["FirstName"];
            (first.TypeRisk, first.RiskClass, first.RiskAck) = (Sentinel, TypeCompat.UnevaluatedClass, Ack(Sentinel, "checked by hand"));
            return m;
        }
        var after = State();
        after.Tables["app.Products"].Columns["Name"].Rationale = "unrelated";

        var (check, node) = Validate(State(), after);

        Assert.Equal(Truncates, (string?)AckNode(node)!["risk"]);
        Assert.Equal(Sentinel, (string?)node["tables"]!["app.Customers"]!["columns"]!["FirstName"]!["riskAck"]!["risk"]);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void A_carried_acknowledgement_is_cleared_by_any_change_to_its_column()
    {
        // expr
        var exprChanged = RiskyComment(Ack(Truncates));
        Comment(exprChanged).Expr = "LEFT(s.[CMNT], 200)";
        AssertCleared(RiskyComment(Ack(Truncates)), exprChanged);

        // sourceColumns
        var sourcesChanged = RiskyComment(Ack(Truncates));
        Comment(sourcesChanged).SourceColumns.Add("dbo.ORD_HDR.ORD_ID");
        AssertCleared(RiskyComment(Ack(Truncates)), sourcesChanged);

        // the table's from, under a sentinel: the old acknowledgement would otherwise match the fresh sentinel
        static MappingPayload Sentinelled()
        {
            var m = SampleMappings.Approved();
            var status = m.Tables["app.Orders"].Columns["StatusCode"];
            (status.TypeRisk, status.RiskClass, status.RiskAck) = (Sentinel, TypeCompat.UnevaluatedClass, Ack(Sentinel, "the join yields the code verbatim"));
            return m;
        }
        var fromChanged = Sentinelled();
        fromChanged.Tables["app.Orders"].From = "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[CUST_ID]";
        var (check, node) = Validate(Sentinelled(), fromChanged);
        Assert.Equal(Sentinel, (string?)node["tables"]!["app.Orders"]!["columns"]!["StatusCode"]!["typeRisk"]);
        Assert.Null(node["tables"]!["app.Orders"]!["columns"]!["StatusCode"]!["riskAck"]);
        Assert.Contains($"app.Orders.StatusCode: type risk: {Sentinel}", check.Warnings);

        // default, under a default-only sentinel
        static MappingPayload DefaultOnly(string dflt)
        {
            var m = WithComment(null);
            (Comment(m).Default, Comment(m).TypeRisk, Comment(m).RiskClass, Comment(m).RiskAck) =
                (dflt, Sentinel, TypeCompat.UnevaluatedClass, Ack(Sentinel, "a short literal"));
            return m;
        }
        AssertCleared(DefaultOnly("N'n/a'"), DefaultOnly("REPLICATE(N'x', 400)"));

        static void AssertCleared(MappingPayload before, MappingPayload after)
        {
            var (check, node) = Validate(before, after);
            Assert.Null(AckNode(node));
            Assert.Contains(check.Warnings, w => w == "app.Orders.Comment: riskAck cleared: the column changed, so it acknowledged a different conversion");
        }
    }

    [Fact]
    public void A_change_that_supplies_its_own_acknowledgement_keeps_it()
    {
        var after = WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT");
        Comment(after).RiskAck = Ack(Sentinel, "LEFT caps the text at the target length");

        var (check, node) = Validate(RiskyComment(Ack(Truncates)), after);

        Assert.Equal(Sentinel, (string?)AckNode(node)!["risk"]);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("type risk:"));
    }

    [Fact]
    public void Removing_an_acknowledgement_reopens_the_risk()
    {
        var (check, node) = Validate(RiskyComment(Ack(Truncates)), RiskyComment());

        Assert.Null(AckNode(node));
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", check.Warnings);
    }

    // ---- carry-over ------------------------------------------------------------------------------------------------------

    private static CatalogSnapshot WithColumn(CatalogSnapshot catalog, string table, string column, Func<ColumnInfo, ColumnInfo> change) =>
        catalog with
        {
            Tables = catalog.Tables.Select(t => t.Key != table ? t
                : t with { Columns = t.Columns.Select(c => c.Name == column ? change(c) : c).ToList() }).ToList()
        };

    private static ColumnMap CarriedComment(CatalogSnapshot src, CatalogSnapshot tgt, MappingPayload previous) =>
        Comment(AutoMapper.Map(src, tgt, Synonyms.Default(), new MatchOptions(), previous));

    [Fact]
    public void An_acknowledgement_made_through_a_human_edit_survives_an_automap_rerun_and_still_matches()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        var draftLike = RiskyComment();
        Comment(draftLike).RiskClass = null;   // as the auto-mapper stores it: typeRisk only; Validate backfills the class
        services.AddMapping(draftLike, PhaseStatus.AwaitingReview, "human");
        var ack = JsonNode.Parse($$"""{"risk":"{{Truncates}}","reason":"values over 200 characters are notes we can lose"}""");
        var applied = services.Workflow.HumanEdit(new Patch("mapping", 0, [new("add", "/tables/app.Orders/columns/Comment/riskAck", ack)], []));
        Assert.True(applied.Ok, string.Join("\n", applied.Errors));
        var stored = Json.Deserialize<MappingPayload>(services.Artifacts.Get(PhaseName.Mapping, applied.Version!.Value)!.PayloadJson);
        Assert.Equal("may truncate (source max)|varchar(500)->nvarchar(200)", Comment(stored).RiskClass);
        Assert.False(MappingValidator.HasOpenTypeRisk(Comment(stored)));

        var carried = CarriedComment(SampleCatalogs.Source(), SampleCatalogs.Target(), stored);

        Assert.Equal(MapMethod.Carried, carried.Method);
        Assert.Equal(Truncates, carried.TypeRisk);
        Assert.Equal(Truncates, carried.RiskAck?.Risk);
        Assert.False(MappingValidator.HasOpenTypeRisk(carried));
    }

    [Fact]
    public void Carry_over_refreshes_the_token_when_only_the_observed_length_moved()
    {
        // Same declared types, a different sampled maximum: "(source max 300)" becomes "(source max 297)".
        var src = WithColumn(SampleCatalogs.Source(), "dbo.ORD_HDR", "CMNT", c => c with { Profile = c.Profile! with { MaxLen = 297 } });

        var carried = CarriedComment(src, SampleCatalogs.Target(), RiskyComment(Ack(Truncates)));

        Assert.Equal("may truncate (source max 297)", carried.TypeRisk);
        Assert.Equal("may truncate (source max 297)", carried.RiskAck?.Risk);
        Assert.Equal("values over 200 characters are notes we can lose", carried.RiskAck?.Reason);
        Assert.False(MappingValidator.HasOpenTypeRisk(carried));
    }

    [Fact]
    public void Carry_over_clears_the_acknowledgement_when_a_declared_type_changed_even_though_the_text_did_not()
    {
        // nvarchar(200) -> nvarchar(250) against an observed 300: the text stays "may truncate (source max 300)".
        var tgt = WithColumn(SampleCatalogs.Target(), "app.Orders", "Comment", c => c with { MaxLength = 250 });

        var carried = CarriedComment(SampleCatalogs.Source(), tgt, RiskyComment(Ack(Truncates)));

        Assert.Equal(Truncates, carried.TypeRisk);
        Assert.Equal("may truncate (source max)|varchar(500)->nvarchar(250)", carried.RiskClass);
        Assert.Null(carried.RiskAck);
        Assert.True(MappingValidator.HasOpenTypeRisk(carried));
    }

    [Fact]
    public void Carry_over_keeps_a_custom_expression_column_verbatim()
    {
        var previous = WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT");
        (Comment(previous).TypeRisk, Comment(previous).RiskClass, Comment(previous).RiskAck) = (Sentinel, TypeCompat.UnevaluatedClass, Ack(Sentinel, "capped"));
        var src = WithColumn(SampleCatalogs.Source(), "dbo.ORD_HDR", "CMNT", c => c with { MaxLength = 4000 });

        var carried = CarriedComment(src, SampleCatalogs.Target(), previous);

        Assert.Equal((Sentinel, TypeCompat.UnevaluatedClass, Sentinel, "capped"), (carried.TypeRisk, carried.RiskClass, carried.RiskAck?.Risk, carried.RiskAck?.Reason));
    }
}
