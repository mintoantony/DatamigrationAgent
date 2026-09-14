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

/// <summary>Task 3.4 fix round 1, THE RISK MODEL (the only normative risk text): typeRisk is engine-owned; Validate's ordered cases;
/// carry-over's own sequence; risks are their own warning class, never attention. Payloads are Approved() copies built inline;
/// SampleMappings.cs is never edited.</summary>
public class MappingRiskTests
{
    private const string Truncates = "may truncate (source max 300)";
    private const string Sentinel = TypeCompat.UnevaluatedRisk;

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

    [Fact]
    public void A_bare_reference_carrying_a_default_is_recomputed_not_sentinelled()
    {
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(after).Default = "N''";   // applies only when expr is null, so the conversion is still s.[CMNT]

        Assert.Equal(Truncates, (string?)CommentNode(Validate(SampleMappings.Approved(), after).Node)["typeRisk"]);
    }

    [Fact]
    public void A_column_or_table_absent_from_the_stored_version_counts_as_changed()
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

    [Fact]
    public void Risks_are_their_own_warning_class_after_attention_and_never_attention()
    {
        var after = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        (Comment(after).Method, Comment(after).Confidence) = (MapMethod.Fuzzy, 0.5);   // also below the band

        var (check, _) = Validate(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"), after);

        var attention = check.Warnings.IndexOf("app.Orders.Comment: s.[CMNT] needs review (confidence 0.50, fuzzy)");
        var risk = check.Warnings.IndexOf($"app.Orders.Comment: type risk: {Truncates}");
        Assert.True(attention >= 0 && risk > attention, string.Join(Environment.NewLine, check.Warnings));
    }

    [Fact]
    public void Serialised_mappings_carry_no_acknowledgement_or_class_fields()
    {
        var (_, node) = Validate(WithComment("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"), WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT"));
        var json = node.ToJsonString(Json.Options);
        Assert.DoesNotContain("riskAck", json);
        Assert.DoesNotContain("riskClass", json);
    }

    // ---- carry-over: its own sequence ------------------------------------------------------------------------------------

    private static CatalogSnapshot WithColumn(CatalogSnapshot catalog, string table, string column, Func<ColumnInfo, ColumnInfo> change) =>
        catalog with
        {
            Tables = catalog.Tables.Select(t => t.Key != table ? t
                : t with { Columns = t.Columns.Select(c => c.Name == column ? change(c) : c).ToList() }).ToList()
        };

    private static ColumnMap Carried(CatalogSnapshot src, MappingPayload previous) =>
        Comment(AutoMapper.Map(src, SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions(), previous));

    [Fact]
    public void Carry_over_recomputes_a_bare_column_when_its_source_type_changed()
    {
        var previous = WithComment("s.[CMNT]", "dbo.ORD_HDR.CMNT");
        Comment(previous).TypeRisk = Truncates;
        var widened = WithColumn(SampleCatalogs.Source(), "dbo.ORD_HDR", "CMNT", c => c with { MaxLength = 150, Profile = null });

        var carried = Carried(widened, previous);

        Assert.Equal(MapMethod.Carried, carried.Method);
        Assert.Null(carried.TypeRisk);
    }

    [Fact]
    public void Carry_over_keeps_a_custom_expression_column_verbatim_and_writes_no_sentinel()
    {
        var legacy = WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT");   // stored before the model: no text
        Assert.Null(Carried(SampleCatalogs.Source(), legacy).TypeRisk);

        var sentinelled = WithComment("LEFT(s.[CMNT], 200)", "dbo.ORD_HDR.CMNT");
        Comment(sentinelled).TypeRisk = Sentinel;
        var changedSource = WithColumn(SampleCatalogs.Source(), "dbo.ORD_HDR", "CMNT", c => c with { MaxLength = 4000 });
        Assert.Equal(Sentinel, Carried(changedSource, sentinelled).TypeRisk);
    }

    [Fact]
    public void Carry_over_of_a_column_with_no_expression_and_no_default_has_no_risk()
    {
        var previous = WithComment(null, "dbo.ORD_HDR.CMNT");
        Comment(previous).TypeRisk = "stale text";

        Assert.Null(Carried(SampleCatalogs.Source(), previous).TypeRisk);
    }
}
