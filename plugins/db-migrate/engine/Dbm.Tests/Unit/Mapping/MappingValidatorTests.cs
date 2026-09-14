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
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ResolveSourceColumn_splits_on_the_last_dot(string? key, bool found)
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
    public void Null_sources_and_columns_collections_from_json_are_errors_and_do_not_throw()
    {
        // Task 3.4 fix round 1 (B2): tolerating a null collection is not accepting it — the packet builder dereferences both.
        var json = "{\"tables\":{\"app.Products\":{\"kind\":\"direct\",\"sources\":null,\"columns\":null}},\"drops\":{}}";
        var m = Json.Deserialize<MappingPayload>(json);

        Assert.Equal(["app.Products: sources must be an array", "app.Products: columns must be an object"],
            MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target()));
        Assert.Contains(Blockers(m), b => b.StartsWith("app.Products: no source table"));
    }

    [Theory]
    [InlineData(MapMethod.Exact, 1.0)]
    [InlineData(MapMethod.Human, 1.0)]
    [InlineData(MapMethod.Agent, 1.0)]
    [InlineData(MapMethod.Fuzzy, 0.95)]
    public void An_unacknowledged_type_risk_is_attention_whatever_the_method_and_confidence(MapMethod method, double confidence)
    {
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.Method, comment.Confidence, comment.TypeRisk) = (method, confidence, "may truncate (source max 300)");

        Assert.Equal(["app.Orders.Comment: type risk: may truncate (source max 300)"], MappingValidator.Attention(m, Options));
        Assert.True(MappingValidator.ColumnNeedsAttention(comment, Options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_type_risk_is_no_risk(string? risk)
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Columns["Comment"].TypeRisk = risk;

        Assert.False(MappingValidator.HasTypeRisk(m.Tables["app.Orders"].Columns["Comment"]));
        Assert.Empty(MappingValidator.Attention(m, Options));
    }

    [Fact]
    public void A_column_below_the_band_with_a_type_risk_is_listed_once_with_the_risk_text_last()
    {
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.Method, comment.Confidence, comment.TypeRisk) = (MapMethod.Fuzzy, 0.5, "may truncate (source max 300)");

        Assert.Equal(["app.Orders.Comment: s.[CMNT] needs review (confidence 0.50, fuzzy); type risk: may truncate (source max 300)"],
            MappingValidator.Attention(m, Options));
    }

    [Theory]
    [InlineData(MapMethod.Human, 1.0)]
    [InlineData(MapMethod.Fuzzy, 0.5)]
    public void The_attention_message_ends_with_the_stored_risk_verbatim_so_it_can_be_copied(MapMethod method, double confidence)
    {
        // Risk model §7: the acknowledgement workflow depends on copying this text into riskAck.risk.
        const string risk = "may truncate (source max 100) (sampled 1,000 rows fit; not proof for the full table)";
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.Method, comment.Confidence, comment.TypeRisk) = (method, confidence, risk);

        var message = Assert.Single(MappingValidator.Attention(m, Options));

        Assert.StartsWith("app.Orders.Comment: ", message);
        const string marker = "type risk: ";
        Assert.Equal(comment.TypeRisk, message[(message.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
    }

    public static TheoryData<RiskAck?, bool> Acks() => new()
    {
        { new RiskAck { Risk = "may truncate (source max 300)", Reason = "notes over 200 chars are expendable" }, false },
        { null, true },
        { new RiskAck(), true },                                                                       // null members
        { new RiskAck { Risk = "may truncate (source max 300)", Reason = "  " }, true },                // no reason
        { new RiskAck { Risk = null, Reason = "reflexive" }, true },                                    // names no hazard
        { new RiskAck { Risk = "", Reason = "reflexive" }, true },
        { new RiskAck { Risk = "may truncate (source max 297)", Reason = "old wording" }, true },       // exact equality only
        { new RiskAck { Risk = "time part dropped", Reason = "a different hazard" }, true },
    };

    [Theory]
    [MemberData(nameof(Acks))]
    public void The_risk_predicate_is_exact_equality_of_the_named_risk_plus_a_reason(RiskAck? ack, bool open)
    {
        var m = SampleMappings.Approved();
        var comment = m.Tables["app.Orders"].Columns["Comment"];
        (comment.TypeRisk, comment.RiskAck) = ("may truncate (source max 300)", ack);

        Assert.Equal(open, MappingValidator.HasOpenTypeRisk(comment));
        Assert.Equal(open ? 1 : 0, MappingValidator.Attention(m, Options).Count);
        Assert.Equal(!open, MappingValidator.HasAcknowledgedTypeRisk(comment));
    }

    [Fact]
    public void An_acknowledged_sentinel_is_closed_like_any_other_risk()
    {
        var comment = new ColumnMap
        {
            TypeRisk = MappingValidator.UnevaluatedRisk,
            RiskAck = new RiskAck { Risk = MappingValidator.UnevaluatedRisk, Reason = "LEFT caps it at 200" }
        };
        Assert.False(MappingValidator.HasOpenTypeRisk(comment));
    }

    [Fact]
    public void Unset_engine_and_author_risk_fields_are_omitted_from_the_stored_json()
    {
        var json = Json.Serialize(SampleMappings.Approved());
        Assert.DoesNotContain("riskAck", json);
        Assert.DoesNotContain("riskClass", json);
        var m = SampleMappings.Approved();
        m.Tables["app.Orders"].Columns["Comment"].RiskAck = new RiskAck { Risk = "r", Reason = "why" };
        Assert.Contains("\"riskAck\":{\"risk\":\"r\",\"reason\":\"why\"}", Json.Serialize(m));
    }

    [Theory]
    [InlineData("s.[CMNT]", "dbo.ORD_HDR.CMNT", null, true)]
    [InlineData("  s.[CMNT]  ", "dbo.ORD_HDR.CMNT", null, true)]
    [InlineData("S.[cmnt]", "DBO.ord_hdr.CMNT", null, true)]
    [InlineData("st.[STATUS_CD]", "dbo.ORD_STATUS.STATUS_CD", null, true)]      // alias declared in from
    [InlineData("s.[NOTES]", "dbo.ORD_HDR.CMNT", null, false)]                  // reads another column than it names
    [InlineData("st.[CMNT]", "dbo.ORD_HDR.CMNT", null, false)]                  // alias bound to another table
    [InlineData("x.[CMNT]", "dbo.ORD_HDR.CMNT", null, false)]                   // alias the FROM parse cannot resolve
    [InlineData("s.CMNT", "dbo.ORD_HDR.CMNT", null, false)]                     // not bracketed
    [InlineData("[CMNT]", "dbo.ORD_HDR.CMNT", null, false)]                     // no alias
    [InlineData("s . [CMNT]", "dbo.ORD_HDR.CMNT", null, false)]
    [InlineData("CAST(s.[CMNT] AS nvarchar(200))", "dbo.ORD_HDR.CMNT", null, false)]
    [InlineData("s.[CMNT]", "dbo.ORD_HDR.CMNT", "''", true)]                    // a default applies only when expr is null
    [InlineData("'N/A'", null, null, false)]                                    // literal, no source column
    public void Bare_single_source_reference_is_defined_tightly(string expr, string? sourceColumn, string? dflt, bool bare)
    {
        var orders = SampleMappings.Approved().Tables["app.Orders"];   // merge: sources[0] = dbo.ORD_HDR (s), st = dbo.ORD_STATUS
        var cm = new ColumnMap { Expr = expr, SourceColumns = sourceColumn is null ? [] : [sourceColumn], Default = dflt };

        Assert.Equal(bare, MappingValidator.BareSingleSource(orders, cm, SampleCatalogs.Source()) is not null);
    }

    [Fact]
    public void Bare_reference_with_two_source_columns_is_not_bare()
    {
        var orders = SampleMappings.Approved().Tables["app.Orders"];
        Assert.Null(MappingValidator.BareSingleSource(orders, orders.Columns["StatusCode"], SampleCatalogs.Source()));
    }

    [Fact]
    public void Public_validator_methods_reject_null_arguments()
    {
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();
        Assert.Throws<ArgumentNullException>(() => MappingValidator.Errors(null!, src, tgt));
        Assert.Throws<ArgumentNullException>(() => MappingValidator.Blockers(SampleMappings.Approved(), null!, tgt));
        Assert.Throws<ArgumentNullException>(() => MappingValidator.Attention(SampleMappings.Approved(), null!));
        Assert.Throws<ArgumentNullException>(() => MappingValidator.UncoveredSourceColumns(null!, src));
        Assert.Throws<ArgumentNullException>(() => MappingValidator.FindTableMap(null!, "app.Orders"));
    }

    [Fact]
    public void Null_element_inside_source_columns_list_produces_the_existing_unknown_source_column_message()
    {
        // Exact repro: a JSON null INSIDE an otherwise non-null "sourceColumns" array (not the array itself, nor
        // the containing objects, being null) previously threw inside ResolveSourceColumn.
        var json = "{\"tables\":{\"app.Products\":{\"kind\":\"direct\",\"sources\":[\"dbo.PROD\"],\"columns\":" +
                   "{\"Name\":{\"expr\":\"s.[PROD_NM]\",\"sourceColumns\":[null]}}}},\"drops\":{}}";
        var m = Json.Deserialize<MappingPayload>(json);

        var errors = MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target());

        Assert.Equal(["app.Products.Name: unknown source column ''"], errors);
    }

    [Fact]
    public void Validator_methods_do_not_throw_on_malformed_entries_collections_or_elements_inside_them()
    {
        var m = new MappingPayload();
        m.Tables["app.Customers"] = null!;
        m.Tables["app.Products"] = new TableMap { Kind = "direct", Sources = null!, Columns = null! };
        m.Tables["app.Orders"] = new TableMap
        {
            Kind = "direct",
            Sources = ["dbo.ORD_HDR"],
            Columns = new()
            {
                ["OrderId"] = null!,
                ["CustomerId"] = new ColumnMap { Expr = "s.[CUST_ID]", SourceColumns = ["dbo.ORD_HDR.CUST_ID", null!] },
            }
        };
        // A null element inside an otherwise non-null Sources list.
        m.Tables["app.Addresses"] = new TableMap { Kind = "direct", Sources = ["dbo.ADDR", null!], Columns = new() };
        m.Drops["dbo.CUST"] = null!;
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();

        var errors = MappingValidator.Errors(m, src, tgt);
        var blockers = MappingValidator.Blockers(m, src, tgt);
        var attention = MappingValidator.Attention(m, Options);
        var uncovered = MappingValidator.UncoveredSourceColumns(m, src);

        Assert.Contains("target table 'app.Customers' has no mapping (expected an object)", errors);
        Assert.Contains("app.Orders.OrderId: no column mapping (expected an object)", errors);
        Assert.Contains("app.Orders.CustomerId: unknown source column ''", errors);
        Assert.Contains("app.Addresses: unknown source table ''", errors);
        Assert.Contains("drop 'dbo.CUST' has no decision (expected an object)", errors);
        Assert.Contains(blockers, b => b.StartsWith("app.Products: no source table"));
        Assert.NotNull(attention);
        Assert.NotNull(uncovered);
    }
}
