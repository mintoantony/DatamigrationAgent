using System.Text.Json.Nodes;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingPacketTests
{
    private static readonly MatchOptions Options = new();

    private static JsonObject Draft(MappingPayload m) => MappingPacket.Draft(m, SampleCatalogs.Source(), SampleCatalogs.Target(), Options);

    private static IEnumerable<string?> Texts(JsonNode? array) => array!.AsArray().Select(n => (string?)n);

    private static FeedbackRow Feedback(long id, string? anchor) =>
        new(id, PhaseName.Mapping, 1, anchor, "please fix", FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Approved_mapping_is_summarised_as_confident_tables_only()
    {
        var data = Draft(SampleMappings.Approved());

        Assert.Equal(6, data["confident"]!.AsArray().Count);
        Assert.Empty(data["detail"]!.AsArray());
        Assert.Empty(data["blockers"]!.AsArray());
        Assert.Empty(data["uncovered"]!.AsArray());
        Assert.Equal(4, data["drops"]!.AsObject().Count);
        var orders = data["confident"]!.AsArray().Single(n => (string?)n!["target"] == "app.Orders")!;
        Assert.Equal("dbo.ORD_HDR, dbo.ORD_STATUS", (string?)orders["source"]);
        Assert.Equal(7, (int?)orders["columns"]);
        Assert.NotNull(data["legend"]);
        Assert.Contains("dbm show", (string?)data["hint"]);
    }

    [Fact]
    public void Uncertain_table_is_detailed_with_target_columns_map_and_source_profiles()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Customers"].Columns["Email"].Method = MapMethod.Fuzzy;
        m.Tables["app.Customers"].Columns["Email"].Confidence = 0.6;

        var data = Draft(m);

        Assert.Equal(5, data["confident"]!.AsArray().Count);
        var detail = data["detail"]!.AsArray().Single()!;
        Assert.Equal("app.Customers", (string?)detail["target"]);
        var targetColumns = detail["targetColumns"]!.AsArray();
        Assert.Equal(10, targetColumns.Count);
        var id = targetColumns.Single(c => (string?)c!["name"] == "CustomerId")!;
        Assert.True((bool?)id["identity"]);
        Assert.True((bool?)id["pk"]);
        var primaryAddress = targetColumns.Single(c => (string?)c!["name"] == "PrimaryAddressId")!;
        Assert.Equal("app.Addresses.AddressId", (string?)primaryAddress["fk"]);
        Assert.Equal("(sysutcdatetime())", (string?)targetColumns.Single(c => (string?)c!["name"] == "CreatedAt")!["default"]);
        Assert.True((bool?)targetColumns.Single(c => (string?)c!["name"] == "DisplayName")!["computed"]);
        Assert.Equal("fuzzy", (string?)detail["columns"]!["Email"]!["method"]);
        var source = detail["sourceTables"]!.AsArray().Single()!;
        Assert.Equal("dbo.CUST", (string?)source["key"]);
        var name = source["columns"]!.AsArray().Single(c => (string?)c!["name"] == "CUST_NM")!;
        Assert.Equal("text", (string?)name["class"]);
        Assert.Equal(3, name["samples"]!.AsArray().Count);
    }

    [Fact]
    public void Auto_draft_packet_lists_blockers_uncovered_columns_and_caps_source_tables()
    {
        var m = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), Options);

        var data = Draft(m);

        Assert.Contains("source table dbo.ORD_STATUS is not mapped or dropped (3 columns)", Texts(data["blockers"]));
        Assert.Contains("dbo.ORD_STATUS.STATUS_CD varchar(10)", Texts(data["uncovered"]));
        Assert.Contains(data["detail"]!.AsArray(), d => (string?)d!["target"] == "app.Orders");
        Assert.All(data["detail"]!.AsArray(), d => Assert.InRange(d!["sourceTables"]!.AsArray().Count, 1, MappingPacket.MaxSourceTables));
        Assert.Equal("empty table with no matching target", (string?)data["drops"]!["dbo.TMP_IMPORT"]);
        Assert.DoesNotContain("Server=", data.ToJsonString());
    }

    [Fact]
    public void Samples_are_capped_at_three()
    {
        var src = SampleCatalogs.Source();
        var cust = src.FindTable("dbo.CUST")!;
        var many = cust.Columns[1] with { Profile = cust.Columns[1].Profile! with { Samples = ["a", "b", "c", "d", "e"] } };
        var node = MappingPacket.SourceColumn(cust, many);
        Assert.Equal(["a", "b", "c"], node["samples"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Rework_resolves_each_anchor_to_its_slice()
    {
        var m = SampleMappings.Approved();
        var feedback = new[]
        {
            Feedback(1, "colmap:app.Customers.Email"),
            Feedback(2, "tablemap:app.Orders"),
            Feedback(3, "column:src:dbo.CUST.FAX_NO"),
            Feedback(4, null),
            Feedback(5, "table:src:dbo.ORD_STATUS"),
            Feedback(6, "finding:12"),
            Feedback(7, "colmap:app.Nope.X"),
        };

        var data = MappingPacket.Rework(m, SampleCatalogs.Source(), SampleCatalogs.Target(), Options, feedback);
        var contexts = data["contexts"]!.AsArray();

        Assert.Equal(7, contexts.Count);
        var email = contexts[0]!["context"]!;
        Assert.Equal("Email", (string?)email["column"]!["name"]);
        Assert.Equal("s.[EMAIL_ADDR]", (string?)email["map"]!["expr"]);
        Assert.Equal("dbo.CUST.EMAIL_ADDR", (string?)email["sourceColumns"]![0]!["key"]);
        var orders = contexts[1]!["context"]!;
        Assert.Equal("app.Orders", (string?)orders["target"]);
        Assert.StartsWith("[dbo].[ORD_HDR] AS s JOIN", (string?)orders["from"]);
        Assert.Equal(2, orders["sourceTables"]!.AsArray().Count);
        Assert.Equal("ShopV2 no longer stores fax numbers.", (string?)contexts[2]!["context"]!["drop"]);
        Assert.NotNull(contexts[3]!["context"]!["uncovered"]);
        Assert.Contains("app.Orders", Texts(contexts[4]!["context"]!["usedBy"]));
        Assert.Null(contexts[5]!["context"]);
        Assert.Contains("does not resolve", (string?)contexts[6]!["context"]!["error"]);
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers", (string?)data["summary"]);
    }

    [Fact]
    public void Source_column_context_lists_the_target_columns_that_use_it()
    {
        var node = MappingPacket.Resolve("column:src:dbo.CUST.CUST_NM", SampleMappings.Approved(),
            SampleCatalogs.Source(), SampleCatalogs.Target(), Options)!;
        Assert.Equal(["app.Customers.FirstName", "app.Customers.LastName"], node["usedBy"]!.AsArray().Select(n => (string)n!).ToArray());
    }

    private const string Truncates = "may truncate (source max 300)";

    /// <summary>An Approved() copy (not a fixture change) where Orders.Comment has the auto-mapper's B1 shape: a fuzzy match
    /// at or above the auto-accept band that carries a truncation risk.</summary>
    private static MappingPayload WithRiskyConfidentComment()
    {
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.Method, comment.Confidence, comment.TypeRisk) = (MapMethod.Fuzzy, 0.9, Truncates);
        return m;
    }

    [Fact]
    public void A_high_confidence_risky_column_details_its_table_and_carries_the_risk()
    {
        var data = Draft(WithRiskyConfidentComment());

        Assert.Equal(5, data["confident"]!.AsArray().Count);
        Assert.DoesNotContain(data["confident"]!.AsArray(), n => (string?)n!["target"] == "app.Orders");
        var detail = Assert.Single(data["detail"]!.AsArray())!;
        Assert.Equal("app.Orders", (string?)detail["target"]);
        Assert.Equal(Truncates, (string?)detail["columns"]!["Comment"]!["typeRisk"]);
        Assert.Contains($"app.Orders.Comment: type risk: {Truncates}", Texts(data["attention"]));
    }

    [Fact]
    public void A_decided_column_carrying_a_risk_still_details_its_table()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Columns["Comment"].TypeRisk = Truncates;   // method human, confidence 1

        Assert.Equal(5, Draft(m)["confident"]!.AsArray().Count);
    }

    [Fact]
    public void An_acknowledged_risk_keeps_its_table_confident_and_is_listed_as_acknowledged()
    {
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.TypeRisk, comment.RiskAck) = (Truncates, new RiskAck { Risk = Truncates, Reason = "notes over 200 chars are expendable" });

        var data = Draft(m);

        Assert.Equal(6, data["confident"]!.AsArray().Count);
        Assert.Empty(data["attention"]!.AsArray());
        var ack = Assert.Single(data["acknowledged"]!.AsArray())!;
        Assert.Equal(("app.Orders.Comment", Truncates, "notes over 200 chars are expendable"),
            ((string?)ack["column"], (string?)ack["typeRisk"], (string?)ack["reason"]));
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers",
            MappingPacket.Summary(m, SampleCatalogs.Source(), SampleCatalogs.Target(), Options));   // E6: no acknowledgement count

        comment.Method = MapMethod.Fuzzy;   // detailed for another reason: the detail column shows the acknowledgement beside the risk
        comment.Confidence = 0.5;
        var column = Draft(m)["detail"]!.AsArray().Single()!["columns"]!["Comment"]!;
        Assert.Equal(Truncates, (string?)column["typeRisk"]);
        Assert.Equal(Truncates, (string?)column["riskAck"]!["risk"]);
        Assert.Equal("notes over 200 chars are expendable", (string?)column["riskAck"]!["reason"]);
    }

    [Fact]
    public void A_skipped_table_carrying_a_risk_stays_confident()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Kind = "skip";
        m.Tables["app.Orders"].Columns["Comment"].TypeRisk = Truncates;
        m.Drops["dbo.ORD_HDR"] = new DropDecision("test", MapMethod.Human);
        m.Drops["dbo.ORD_STATUS"] = new DropDecision("test", MapMethod.Human);

        var data = Draft(m);

        Assert.Contains(data["confident"]!.AsArray(), n => (string?)n!["target"] == "app.Orders");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_risk_neither_details_the_table_nor_renders(string blank)
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Columns["Comment"].TypeRisk = blank;
        Assert.Equal(6, Draft(m)["confident"]!.AsArray().Count);

        m.Tables["app.Orders"].Columns["Comment"].Method = MapMethod.Fuzzy;
        m.Tables["app.Orders"].Columns["Comment"].Confidence = 0.5;
        var detail = Assert.Single(Draft(m)["detail"]!.AsArray())!;
        Assert.Null(detail["columns"]!["Comment"]!["typeRisk"]);
        Assert.False(detail["columns"]!.AsObject()["Comment"]!.AsObject().ContainsKey("typeRisk"));
    }

    [Fact]
    public void Rework_contexts_carry_the_risk_on_every_anchor_kind()
    {
        var m = WithRiskyConfidentComment();
        var feedback = new[]
        {
            Feedback(1, "colmap:app.Orders.Comment"),
            Feedback(2, "column:src:dbo.ORD_HDR.CMNT"),
            Feedback(3, "table:src:dbo.ORD_HDR"),
            Feedback(4, null),
            Feedback(5, "tablemap:app.Orders"),
            Feedback(6, "column:src:dbo.CUST.CUST_NM"),
        };

        var contexts = MappingPacket.Rework(m, SampleCatalogs.Source(), SampleCatalogs.Target(), Options, feedback)["contexts"]!.AsArray();

        Assert.Equal(Truncates, (string?)contexts[0]!["context"]!["map"]!["typeRisk"]);
        Assert.Equal(Truncates, (string?)contexts[1]!["context"]!["typeRisks"]!["app.Orders.Comment"]);
        Assert.Equal(Truncates, (string?)contexts[2]!["context"]!["typeRisks"]!["app.Orders.Comment"]);
        Assert.Equal(Truncates, (string?)contexts[3]!["context"]!["typeRisks"]!["app.Orders.Comment"]);
        Assert.Equal(Truncates, (string?)contexts[4]!["context"]!["columns"]!["Comment"]!["typeRisk"]);
        Assert.Null(contexts[5]!["context"]!["typeRisks"]);   // unrelated source column: key omitted
    }

    [Fact]
    public void Auto_draft_risky_detail_columns_carry_their_risk()
    {
        var m = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), Options);

        var data = Draft(m);

        var risky = m.Tables.SelectMany(t => t.Value.Columns.Where(c => MappingValidator.HasTypeRisk(c.Value))
            .Select(c => (Table: t.Key, Column: c.Key, c.Value.TypeRisk))).ToList();
        Assert.NotEmpty(risky);
        foreach (var (table, column, risk) in risky)
        {
            var detail = data["detail"]!.AsArray().Single(d => (string?)d!["target"] == table)!;
            Assert.Equal(risk, (string?)detail["columns"]![column]!["typeRisk"]);
        }
    }

    [Fact]
    public void Table_detail_without_a_map_omits_the_map_key()
    {
        var tgt = SampleCatalogs.Target();
        var node = MappingPacket.TableDetail(tgt.FindTable("app.Orders")!, null, SampleCatalogs.Source());
        Assert.False(node.ContainsKey("map"));
        Assert.Empty(node["columns"]!.AsObject());
    }

    [Fact]
    public void Public_packet_statics_reject_null_arguments()
    {
        var (m, src, tgt) = (SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var table = tgt.FindTable("app.Orders")!;
        Assert.Throws<ArgumentNullException>(() => MappingPacket.Summary(null!, src, tgt, Options));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.Draft(m, null!, tgt, Options));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.Rework(m, src, tgt, Options, null!));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.Resolve("general", m, src, tgt, null!));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.TableDetail(null!, null, src));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.ColumnDetail(table, null!, m, src));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.TargetColumn(table, null!));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.SourceTable(null!));
        Assert.Throws<ArgumentNullException>(() => MappingPacket.SourceColumn(null!, table.Columns[0]));
    }

    [Fact]
    public void Summary_counts_tables_columns_drops_attention_and_blockers()
    {
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers",
            MappingPacket.Summary(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target(), Options));
    }
}
