using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingValidatorTests
{
    private static readonly MatchOptions Options = new();

    private static List<string> Errors(MappingPayload m) => MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target());
    private static List<string> Blockers(MappingPayload m) => MappingValidator.Blockers(m, SampleCatalogs.Source(), SampleCatalogs.Target());

    [Fact]
    public void Approved_sample_mapping_is_clean()
    {
        var m = SampleMappings.Approved();
        Assert.Empty(Errors(m));
        Assert.Empty(Blockers(m));
        Assert.Empty(MappingValidator.Attention(m, Options));
        Assert.Empty(MappingValidator.UncoveredSourceColumns(m, SampleCatalogs.Source()));
    }

    [Fact]
    public void Payload_round_trips_through_json_with_snake_case_methods()
    {
        var json = Json.Serialize(SampleMappings.Approved());
        Assert.Contains("\"method\":\"human\"", json);
        Assert.Contains("\"app.Customers\"", json);
        Assert.Contains("\"FirstName\"", json);
        Assert.DoesNotContain("\"default\":null", json);
        var back = Json.Deserialize<MappingPayload>(json);
        Assert.Equal("merge", back.Tables["app.Orders"].Kind);
        Assert.Equal(MapMethod.Human, back.Drops["dbo.CUST.FAX_NO"].Method);
        Assert.Empty(Blockers(back));
    }

    [Fact]
    public void Missing_table_map_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Tables.Remove("app.AuditEvents");
        var blockers = Blockers(m);
        Assert.Contains(blockers, b => b.StartsWith("app.AuditEvents: no table mapping"));
        Assert.Contains("source table dbo.AUDIT_LOG is not mapped or dropped (3 columns)", blockers);
    }

    [Fact]
    public void Table_map_without_sources_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.AuditEvents"].Sources.Clear();
        Assert.Contains(Blockers(m), b => b.StartsWith("app.AuditEvents: no source table"));
    }

    [Fact]
    public void Uncovered_source_column_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Drops.Remove("dbo.CUST.FAX_NO");
        Assert.Equal(["source column dbo.CUST.FAX_NO is not mapped or dropped"], Blockers(m));
        Assert.Equal(["dbo.CUST.FAX_NO"], MappingValidator.UncoveredSourceColumns(m, SampleCatalogs.Source()));
    }

    [Fact]
    public void Undropped_empty_table_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Drops.Remove("dbo.TMP_IMPORT");
        Assert.Equal(["source table dbo.TMP_IMPORT is not mapped or dropped (1 columns)"], Blockers(m));
    }

    [Fact]
    public void Not_null_column_without_expression_or_default_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Customers"].Columns["FirstName"].Expr = null;
        Assert.Contains("app.Customers.FirstName: NOT NULL without default needs an expression or default", Blockers(m));

        m.Tables["app.Customers"].Columns["FirstName"].Default = "N''";
        Assert.Empty(Blockers(m));
    }

    [Fact]
    public void Missing_column_map_for_not_null_column_is_a_blocker_but_nullable_identity_and_defaulted_columns_are_not()
    {
        var m = SampleMappings.Approved();
        var customers = m.Tables["app.Customers"].Columns;
        customers.Remove("CustomerId");      // identity
        customers.Remove("CreatedAt");       // has default
        customers.Remove("Email");           // nullable
        m.Drops["dbo.CUST.EMAIL_ADDR"] = new DropDecision("not needed", MapMethod.Human);
        m.Drops["dbo.CUST.CRT_DT"] = new DropDecision("not needed", MapMethod.Human);
        Assert.Empty(Blockers(m));

        m.Tables["app.Products"].Columns.Remove("IsActive");
        m.Drops["dbo.PROD.ACTIVE_FLG"] = new DropDecision("not needed", MapMethod.Human);
        Assert.Equal(["app.Products.IsActive: NOT NULL without default needs an expression or default"], Blockers(m));
    }

    [Fact]
    public void Skip_map_needs_no_columns_but_does_not_cover_sources()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.AuditEvents"] = new TableMap { Kind = "skip", Method = MapMethod.Human, Rationale = "not migrated" };
        Assert.Equal(["source table dbo.AUDIT_LOG is not mapped or dropped (3 columns)"], Blockers(m));

        m.Drops["dbo.AUDIT_LOG"] = new DropDecision("audit history stays in the legacy system", MapMethod.Human);
        Assert.Empty(Blockers(m));
        Assert.Empty(Errors(m));
    }

    [Fact]
    public void Unknown_names_and_invalid_kind_are_errors()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Nope"] = new TableMap { Sources = ["dbo.CUST"] };
        m.Tables["app.Products"].Kind = "weird";
        m.Tables["app.Products"].Sources.Add("dbo.NOPE");
        m.Tables["app.Products"].Columns["Nope"] = new ColumnMap { Expr = "1" };
        m.Tables["app.Products"].Columns["Name"].SourceColumns = ["dbo.PROD.NOPE"];
        m.Drops["dbo.GONE"] = new DropDecision("x", MapMethod.Human);
        m.Drops["dbo.CUST.PHONE_NO"] = new DropDecision(" ", MapMethod.Human);

        var errors = Errors(m);

        Assert.Contains("unknown target table 'app.Nope'", errors);
        Assert.Contains("app.Products: invalid kind 'weird' (use direct, merge, lookup or skip)", errors);
        Assert.Contains("app.Products: unknown source table 'dbo.NOPE'", errors);
        Assert.Contains("unknown target column 'app.Products.Nope'", errors);
        Assert.Contains("app.Products.Name: unknown source column 'dbo.PROD.NOPE'", errors);
        Assert.Contains("unknown drop 'dbo.GONE' (use schema.table or schema.table.column)", errors);
        Assert.Contains("drop 'dbo.CUST.PHONE_NO' needs a reason", errors);
    }

    [Fact]
    public void Keys_must_use_catalog_case()
    {
        var m = SampleMappings.Approved();
        var map = m.Tables["app.Addresses"];
        m.Tables.Remove("app.Addresses");
        m.Tables["app.addresses"] = map;
        map.Columns["city"] = map.Columns["City"];
        map.Columns.Remove("City");

        var errors = Errors(m);

        Assert.Contains("target table 'app.addresses' must be written as 'app.Addresses' (catalog case)", errors);
        Assert.Contains("target column 'app.Addresses.city' must be written as 'City' (catalog case)", errors);
        Assert.Empty(Blockers(m));
    }

    [Fact]
    public void Computed_and_rowversion_columns_cannot_be_written()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Customers"].Columns["DisplayName"] = new ColumnMap { Expr = "s.[CUST_NM]", SourceColumns = ["dbo.CUST.CUST_NM"] };
        m.Tables["app.Products"].Columns["RowVer"] = new ColumnMap { Default = "0x00" };
        m.Tables["app.Products"].Columns["Name"].Expr = "  ";

        var errors = Errors(m);

        Assert.Contains("app.Customers.DisplayName is computed and cannot be written", errors);
        Assert.Contains("app.Products.RowVer is a rowversion and cannot be written", errors);
        Assert.Contains("app.Products.Name: expr is empty (use null for unmapped)", errors);
    }

    [Fact]
    public void Attention_lists_script_proposals_below_auto_accept()
    {
        var m = SampleMappings.Approved();
        var email = m.Tables["app.Customers"].Columns["Email"];
        email.Method = MapMethod.Fuzzy;
        email.Confidence = 0.62;
        var phone = m.Tables["app.Customers"].Columns["Phone"];
        phone.Method = MapMethod.Vector;
        phone.Confidence = 0.9;                                  // above the band: no attention
        var notes = m.Tables["app.Customers"].Columns["Notes"];
        notes.Method = MapMethod.Agent;
        notes.Confidence = 0.4;                                  // agent-decided: no attention
        m.Tables["app.Orders"].Method = MapMethod.Vector;
        m.Tables["app.Orders"].Confidence = 0.7;
        m.Tables["app.AuditEvents"].Sources.Clear();
        m.Tables["app.AuditEvents"].Method = MapMethod.Vector;
        m.Tables["app.AuditEvents"].Confidence = 0.42;
        m.Tables["app.OrderLines"].Columns["Quantity"] = new ColumnMap { Method = MapMethod.Vector, Confidence = 0.31 };

        var attention = MappingValidator.Attention(m, Options);

        Assert.Equal(
        [
            "app.AuditEvents: no confident source table (best 0.42)",
            "app.Customers.Email: s.[EMAIL_ADDR] needs review (confidence 0.62, fuzzy)",
            "app.OrderLines.Quantity: unmapped (best candidate 0.31)",
            "app.Orders: source dbo.ORD_HDR needs review (confidence 0.70, vector)",
        ], attention);
    }

    [Theory]
    [InlineData("dbo.CUST.CUST_NM", true)]
    [InlineData("DBO.cust.cust_nm", true)]
    [InlineData("dbo.CUST", false)]
    [InlineData("dbo.CUST.", false)]
    [InlineData("CUST_NM", false)]
    public void ResolveSourceColumn_splits_on_the_last_dot(string key, bool found)
    {
        Assert.Equal(found, MappingValidator.ResolveSourceColumn(SampleCatalogs.Source(), key) is not null);
    }

    [Fact]
    public void Attention_orders_flagged_columns_within_a_table_by_name()
    {
        var m = SampleMappings.Approved();
        var customers = m.Tables["app.Customers"].Columns;
        // Declared in SampleMappings as Phone (index 4) before Notes (index 8); alphabetically Notes < Phone,
        // so this only passes if Attention sorts columns instead of relying on dictionary insertion order.
        customers["Phone"].Method = MapMethod.Fuzzy;
        customers["Phone"].Confidence = 0.5;
        customers["Notes"].Method = MapMethod.Fuzzy;
        customers["Notes"].Confidence = 0.5;

        var attention = MappingValidator.Attention(m, Options);

        Assert.Equal(
        [
            "app.Customers.Notes: CAST(s.[NOTES] AS nvarchar(max)) needs review (confidence 0.50, fuzzy)",
            "app.Customers.Phone: s.[PHONE_NO] needs review (confidence 0.50, fuzzy)",
        ], attention);
    }

    [Fact]
    public void Errors_does_not_throw_on_null_table_and_drop_entries_from_json()
    {
        // System.Text.Json happily deserializes a JSON null into a non-nullable dictionary value.
        var json = "{\"tables\":{\"app.Customers\":null},\"drops\":{\"dbo.CUST\":null}}";
        var m = Json.Deserialize<MappingPayload>(json);

        var errors = MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target());

        Assert.Equal(
        [
            "target table 'app.Customers' has no mapping (expected an object)",
            "drop 'dbo.CUST' has no decision (expected an object)",
        ], errors);
    }

    [Fact]
    public void Errors_does_not_throw_on_null_column_map_entry_from_json()
    {
        var json = "{\"tables\":{\"app.Products\":{\"kind\":\"direct\",\"sources\":[\"dbo.PROD\"],\"columns\":{\"Name\":null}}},\"drops\":{}}";
        var m = Json.Deserialize<MappingPayload>(json);

        var errors = MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target());

        Assert.Contains("app.Products.Name: no column mapping (expected an object)", errors);
    }

    [Fact]
    public void Null_sources_and_columns_collections_from_json_are_treated_as_empty()
    {
        var json = "{\"tables\":{\"app.Products\":{\"kind\":\"direct\",\"sources\":null,\"columns\":null}},\"drops\":{}}";
        var m = Json.Deserialize<MappingPayload>(json);

        Assert.Empty(MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target()));
        Assert.Contains(Blockers(m), b => b.StartsWith("app.Products: no source table"));
    }

    [Fact]
    public void Validator_methods_do_not_throw_on_malformed_entries_and_collections()
    {
        var m = new MappingPayload();
        m.Tables["app.Customers"] = null!;
        m.Tables["app.Products"] = new TableMap { Kind = "direct", Sources = null!, Columns = null! };
        m.Tables["app.Orders"] = new TableMap { Kind = "direct", Sources = ["dbo.ORD_HDR"], Columns = new() { ["OrderId"] = null! } };
        m.Drops["dbo.CUST"] = null!;
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();

        var errors = MappingValidator.Errors(m, src, tgt);
        var blockers = MappingValidator.Blockers(m, src, tgt);
        var attention = MappingValidator.Attention(m, Options);
        var uncovered = MappingValidator.UncoveredSourceColumns(m, src);

        Assert.Contains("target table 'app.Customers' has no mapping (expected an object)", errors);
        Assert.Contains("app.Orders.OrderId: no column mapping (expected an object)", errors);
        Assert.Contains("drop 'dbo.CUST' has no decision (expected an object)", errors);
        Assert.Contains(blockers, b => b.StartsWith("app.Products: no source table"));
        Assert.NotNull(attention);
        Assert.NotNull(uncovered);
    }
}
