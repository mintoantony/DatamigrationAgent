using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlGeneratorTests
{
    static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    static TaskPlan Task(SqlPlanPayload plan, string target) => plan.Tasks.Values.Single(t => t.Target == target);

    static string IdOf(SqlPlanPayload plan, string target) => plan.Tasks.Single(kv => kv.Value.Target == target).Key;

    [Fact]
    public void Tasks_are_numbered_in_dependency_order()
    {
        var plan = Plan();
        Assert.Equal(["T01", "T02", "T03", "T04", "T05", "T06"], plan.Order);
        Assert.Equal(["app.AuditEvents", "app.Customers", "app.Products", "app.Addresses", "app.Orders", "app.OrderLines"],
            plan.Order.Select(id => plan.Tasks[id].Target));
        int Pos(string t) => plan.Order.IndexOf(IdOf(plan, t));
        Assert.True(Pos("app.Products") < Pos("app.Addresses") && Pos("app.Customers") < Pos("app.Addresses"));
        Assert.True(Pos("app.Orders") > Pos("app.Customers") && Pos("app.Orders") > Pos("app.Addresses"));
        Assert.True(Pos("app.OrderLines") > Pos("app.Orders") && Pos("app.OrderLines") > Pos("app.Products"));
    }

    [Fact]
    public void DependsOn_excludes_the_cycle_edge()
    {
        var plan = Plan();
        Assert.Empty(Task(plan, "app.Customers").DependsOn);
        Assert.Equal([IdOf(plan, "app.Customers")], Task(plan, "app.Addresses").DependsOn);
        Assert.Equal(["T02", "T04"], Task(plan, "app.Orders").DependsOn);
        Assert.Equal(["T03", "T05"], Task(plan, "app.OrderLines").DependsOn);
        Assert.Empty(Task(plan, "app.AuditEvents").DependsOn);
    }

    [Fact]
    public void Addresses_source_query_is_exact()
    {
        const string expected =
            "SELECT\n" +
            "    s.[ADDR_ID] AS [AddressId],\n" +
            "    s.[CUST_ID] AS [CustomerId],\n" +
            "    s.[LINE1] AS [Line1],\n" +
            "    s.[CITY] AS [City],\n" +
            "    s.[ZIP] AS [PostalCode],\n" +
            "    s.[CTRY_CD] AS [CountryCode],\n" +
            "    s.[ADDR_ID] AS [__k0]\n" +
            "FROM [dbo].[ADDR] AS s";
        var task = Task(Plan(), "app.Addresses");
        Assert.Equal(expected, task.SourceQuery);
        Assert.Equal("SELECT COUNT_BIG(*) FROM (\n" + expected + "\n) AS q", task.CountSql);
        Assert.Equal(["__k0"], task.KeyColumns);
        Assert.Equal(["AddressId", "CustomerId", "Line1", "City", "PostalCode", "CountryCode"], task.Columns.Select(c => c.Target));
        Assert.All(task.Columns, c => Assert.Equal(c.Target, c.Source));
        Assert.Equal("direct", task.Mode);
    }

    [Fact]
    public void Orders_source_query_uses_the_merge_from_clause()
    {
        const string expected =
            "SELECT\n" +
            "    s.[ORD_ID] AS [OrderId],\n" +
            "    s.[CUST_ID] AS [CustomerId],\n" +
            "    s.[ORD_DT] AS [OrderDate],\n" +
            "    st.[STATUS_CD] AS [StatusCode],\n" +
            "    s.[SHIP_ADDR_ID] AS [ShippingAddressId],\n" +
            "    s.[TOTAL_AMT] AS [TotalAmount],\n" +
            "    s.[CMNT] AS [Comment],\n" +
            "    s.[ORD_ID] AS [__k0]\n" +
            "FROM [dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]";
        var task = Task(Plan(), "app.Orders");
        Assert.Equal(expected, task.SourceQuery);
        Assert.Contains("target has 1 trigger(s); not fired unless FireTriggers", task.Warnings);
    }

    [Fact]
    public void OrderLines_has_a_composite_key()
    {
        var task = Task(Plan(), "app.OrderLines");
        Assert.Equal(["__k0", "__k1"], task.KeyColumns);
        Assert.EndsWith("    s.[ORD_ID] AS [__k0],\n    s.[LINE_NO] AS [__k1]\nFROM [dbo].[ORD_LINE] AS s", task.SourceQuery);
    }

    [Fact]
    public void Cycle_constraint_is_disabled_before_and_rechecked_after()
    {
        var plan = Plan();
        Assert.Equal(["ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];"], plan.PreSql);
        Assert.Equal(["ALTER TABLE [app].[Customers] WITH CHECK CHECK CONSTRAINT [FK_Customers_PrimaryAddress];"], plan.PostSql);
        Assert.Contains(plan.Warnings, w => w.StartsWith("FK cycle broken at app.Customers -> app.Addresses", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityInsert_follows_bound_identity_columns()
    {
        var plan = Plan();
        Assert.True(Task(plan, "app.Customers").IdentityInsert);
        Assert.True(Task(plan, "app.Addresses").IdentityInsert);
        Assert.True(Task(plan, "app.Products").IdentityInsert);
        Assert.True(Task(plan, "app.Orders").IdentityInsert);
        Assert.False(Task(plan, "app.OrderLines").IdentityInsert);
        Assert.False(Task(plan, "app.AuditEvents").IdentityInsert);
    }

    [Fact]
    public void Heap_source_has_no_keys_and_a_warning()
    {
        var task = Task(Plan(), "app.AuditEvents");
        Assert.Empty(task.KeyColumns);
        Assert.Contains(SqlGenerator.NoKeyWarning, task.Warnings);
        Assert.DoesNotContain("__k", task.SourceQuery);
    }

    [Fact]
    public void Computed_and_rowversion_columns_are_never_bound()
    {
        var plan = Plan();
        Assert.DoesNotContain(Task(plan, "app.Customers").Columns, c => c.Target == "DisplayName");
        Assert.DoesNotContain(Task(plan, "app.Products").Columns, c => c.Target == "RowVer");
    }

    [Fact]
    public void Lob_columns_get_a_small_chunk_size()
    {
        var plan = Plan();
        Assert.Equal(SqlGenerator.LobChunkSize, Task(plan, "app.Customers").ChunkSize);
        Assert.Null(Task(plan, "app.Addresses").ChunkSize);
    }

    [Fact]
    public void Type_risk_from_the_mapping_becomes_a_warning()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Orders"].Columns["Comment"].TypeRisk = "varchar(500) -> nvarchar(200) may truncate";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.Contains("Comment: varchar(500) -> nvarchar(200) may truncate", Task(plan, "app.Orders").Warnings);
    }

    [Fact]
    public void Lookup_maps_become_staging_merge_tasks()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var task = Task(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), "app.Addresses");
        Assert.Equal("staging_merge", task.Mode);
        Assert.StartsWith("CREATE TABLE #stg (\n    [AddressId] int NULL,\n    [CustomerId] int NULL,\n    [Line1] nvarchar(200)", task.StagingDdl);
        Assert.Contains("\n    [__k0] int NULL\n);", task.StagingDdl);
        Assert.Equal(
            "INSERT INTO [app].[Addresses] ([AddressId], [CustomerId], [Line1], [City], [PostalCode], [CountryCode])\n" +
            "SELECT [AddressId], [CustomerId], [Line1], [City], [PostalCode], [CountryCode]\n" +
            "FROM #stg;", task.MergeSql);
        Assert.Contains(SqlGenerator.ReviewMergeWarning, task.Warnings);
    }

    [Fact]
    public void Filter_becomes_a_parenthesised_where_clause()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Filter = "s.[CTRY_CD] <> 'XX'";
        var task = Task(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), "app.Addresses");
        Assert.EndsWith("\nFROM [dbo].[ADDR] AS s\nWHERE (s.[CTRY_CD] <> 'XX')", task.SourceQuery);
    }

    [Fact]
    public void Skip_maps_produce_no_task()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.AuditEvents"].Kind = "skip";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.Equal(5, plan.Tasks.Count);
        Assert.DoesNotContain(plan.Tasks.Values, t => t.Target == "app.AuditEvents");
        Assert.Equal("app.Customers", plan.Tasks["T01"].Target);
    }

    [Fact]
    public void Custom_task_with_unchanged_mapping_is_carried_over()
    {
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.SourceQuery = addresses.SourceQuery + "\nWHERE s.[CITY] <> ''";
        addresses.PostSql.Add("UPDATE STATISTICS [app].[Addresses];");
        addresses.Custom = true;

        var second = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target(), first);
        var carried = Task(second, "app.Addresses");
        Assert.True(carried.Custom);
        Assert.Equal(addresses.SourceQuery, carried.SourceQuery);
        Assert.Equal(["UPDATE STATISTICS [app].[Addresses];"], carried.PostSql);
        Assert.Equal(SqlGenerator.CountSql(addresses.SourceQuery), carried.CountSql);
        Assert.Contains(SqlGenerator.CarriedWarning, carried.Warnings);
        Assert.False(Task(second, "app.Orders").Custom);
    }

    [Fact]
    public void Custom_task_is_regenerated_when_its_mapping_changed()
    {
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.SourceQuery = "SELECT 1 AS [AddressId]";
        addresses.Custom = true;

        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Columns["City"].Expr = "UPPER(s.[CITY])";
        var second = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), first);
        var task = Task(second, "app.Addresses");
        Assert.False(task.Custom);
        Assert.Contains("    UPPER(s.[CITY]) AS [City],", task.SourceQuery);
        Assert.NotEqual(addresses.MappingHash, task.MappingHash);
    }

    [Fact]
    public void Plan_round_trips_through_json_with_camel_case_names()
    {
        var plan = Plan();
        var json = Json.Serialize(plan);
        Assert.Contains("\"sourceQuery\":", json);
        Assert.Contains("\"mappingHash\":", json);
        var back = Json.Deserialize<SqlPlanPayload>(json);
        Assert.Equal(plan.Order, back.Order);
        Assert.Equal(plan.Tasks["T05"].SourceQuery, back.Tasks["T05"].SourceQuery);
        Assert.Equal(plan.Tasks["T05"].Columns, back.Tasks["T05"].Columns);
        Assert.Matches("^[0-9a-f]{64}$", back.Tasks["T05"].MappingHash!);
    }

    // ---- Amendment: a sentinel risk is not a data-loss warning ------------------------------------------------------

    [Fact]
    public void Sentinel_risk_becomes_the_unevaluated_warning_and_not_a_loss_warning()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Orders"].Columns["Comment"].TypeRisk = TypeCompat.UnevaluatedRisk;
        mapping.Tables["app.Orders"].Columns["TotalAmount"].TypeRisk = "decimal(12,2) -> decimal(10,2) may overflow";
        var warnings = Task(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), "app.Orders").Warnings;

        Assert.Equal(["Comment: " + SqlGenerator.UnevaluatedWarning], warnings.Where(w => w.StartsWith("Comment:", StringComparison.Ordinal)));
        Assert.DoesNotContain("Comment: " + TypeCompat.UnevaluatedRisk, warnings);
        // An ordinary risk on a neighbouring column still renders as the loss line.
        Assert.Contains("TotalAmount: decimal(12,2) -> decimal(10,2) may overflow", warnings);
    }

    [Fact]
    public void UnevaluatedWarning_text_is_pinned()
    {
        Assert.Equal("conversion not evaluated: custom expression", SqlGenerator.UnevaluatedWarning);
    }

    // ---- Amendment: the mapping hash covers only SQL-relevant fields ----------------------------------------------

    static TableMap Addresses() => SampleMappings.Approved().Tables["app.Addresses"];

    public static TheoryData<string> NonSqlEdits() => new() { "typeRisk", "rationale", "confidence", "method", "candidates" };

    static void ApplyNonSqlEdit(TableMap map, string field)
    {
        var col = map.Columns["City"];
        switch (field)
        {
            case "typeRisk": col.TypeRisk = "varchar(60) -> nvarchar(50) may truncate (sampled)"; break;
            case "rationale": map.Rationale = "re-explained"; col.Rationale = "re-explained"; break;
            case "confidence": map.Confidence = 0.42; col.Confidence = 0.42; break;
            case "method": map.Method = MapMethod.Agent; col.Method = MapMethod.Agent; break;
            case "candidates":
                map.Candidates = [new Candidate("dbo.ADDR2", 0.5, "name")];
                col.Candidates = [new Candidate("dbo.ADDR.CITY_NM", 0.5, "name")];
                break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    [Theory]
    [MemberData(nameof(NonSqlEdits))]
    public void MappingHash_ignores_fields_that_do_not_change_the_sql(string field)
    {
        var map = Addresses();
        var before = SqlGenerator.MappingHash(map);
        ApplyNonSqlEdit(map, field);
        Assert.Equal(before, SqlGenerator.MappingHash(map));
    }

    public static TheoryData<string> SqlEdits() => new() { "kind", "sources", "from", "filter", "expr", "sourceColumns", "default" };

    [Theory]
    [MemberData(nameof(SqlEdits))]
    public void MappingHash_changes_with_every_sql_relevant_field(string field)
    {
        var map = Addresses();
        var before = SqlGenerator.MappingHash(map);
        var col = map.Columns["City"];
        switch (field)
        {
            case "kind": map.Kind = "lookup"; break;
            case "sources": map.Sources.Add("dbo.CUST"); break;
            case "from": map.From = "[dbo].[ADDR] AS s"; break;
            case "filter": map.Filter = "s.[CITY] <> ''"; break;
            case "expr": col.Expr = "UPPER(s.[CITY])"; break;
            case "sourceColumns": col.SourceColumns.Add("dbo.ADDR.ZIP"); break;
            case "default": col.Default = "N'?'"; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        Assert.NotEqual(before, SqlGenerator.MappingHash(map));
    }

    [Fact]
    public void MappingHash_changes_when_a_column_is_added_or_renamed()
    {
        var map = Addresses();
        var before = SqlGenerator.MappingHash(map);
        var city = map.Columns["City"];
        map.Columns.Remove("City");
        map.Columns["Town"] = city;
        Assert.NotEqual(before, SqlGenerator.MappingHash(map));
    }

    [Fact]
    public void MappingHash_does_not_depend_on_column_insertion_order()
    {
        var map = Addresses();
        var reordered = Addresses();
        reordered.Columns = map.Columns.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(SqlGenerator.MappingHash(map), SqlGenerator.MappingHash(reordered));
    }

    [Theory]
    [MemberData(nameof(NonSqlEdits))]
    public void Custom_task_survives_a_regeneration_that_changed_only_non_sql_fields(string field)
    {
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.SourceQuery = "SELECT 1 AS [AddressId]";
        addresses.Custom = true;

        var mapping = SampleMappings.Approved();
        ApplyNonSqlEdit(mapping.Tables["app.Addresses"], field);
        var carried = Task(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), first), "app.Addresses");
        Assert.True(carried.Custom);
        Assert.Equal("SELECT 1 AS [AddressId]", carried.SourceQuery);
    }

    [Fact]
    public void Generate_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>("mapping", () => SqlGenerator.Generate(null!, SampleCatalogs.Source(), SampleCatalogs.Target()));
        Assert.Throws<ArgumentNullException>("src", () => SqlGenerator.Generate(SampleMappings.Approved(), null!, SampleCatalogs.Target()));
        Assert.Throws<ArgumentNullException>("tgt", () => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), null!));
        Assert.Throws<ArgumentNullException>("map", () => SqlGenerator.MappingHash(null!));
        Assert.Throws<ArgumentNullException>("sourceQuery", () => SqlGenerator.CountSql(null!));
    }
}
