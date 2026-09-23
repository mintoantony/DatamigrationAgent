using Dbm.Core;
using Dbm.Core.Catalog;
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
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        var task = Task(plan, "app.Addresses");
        Assert.Equal("staging_merge", task.Mode);
        // BulkLoader refuses a direct task that carries a MergeSql (ruling 111), so no generated task may carry one: the merge belongs
        // to the staging mode that runs it. Every other task in this same plan is direct.
        Assert.All(plan.Tasks.Values.Where(t => t.Mode != "staging_merge"), t => Assert.Null(t.MergeSql));
        Assert.Contains(plan.Tasks.Values, t => t.Mode != "staging_merge");
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
        Assert.Equal("conversion not verified: custom expression", SqlGenerator.UnevaluatedWarning);
    }

    [Fact]
    public void No_plan_warning_ever_contains_the_sentinel_risk_text()
    {
        var mapping = SampleMappings.Approved();
        foreach (var map in mapping.Tables.Values)
            foreach (var column in map.Columns.Values)
                column.TypeRisk = TypeCompat.UnevaluatedRisk;
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());

        var all = plan.Warnings.Concat(plan.Tasks.Values.SelectMany(t => t.Warnings)).ToList();
        Assert.Contains(all, w => w.EndsWith(": " + SqlGenerator.UnevaluatedWarning, StringComparison.Ordinal));
        Assert.DoesNotContain(all, w => w.Contains(TypeCompat.UnevaluatedRisk, StringComparison.Ordinal));
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

    // ---- Fix round 1: custom SQL dropped on regeneration is announced ------------------------------------------------

    [Fact]
    public void DiscardedWarning_text_is_pinned()
    {
        Assert.Equal("custom SQL discarded: the mapping for this table changed", SqlGenerator.DiscardedWarning);
    }

    [Fact]
    public void Discarding_custom_sql_because_the_mapping_changed_warns_on_that_task_only()
    {
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.SourceQuery = "SELECT 1 AS [AddressId]";
        addresses.Custom = true;

        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Columns["City"].Expr = "UPPER(s.[CITY])";
        mapping.Tables["app.Orders"].Filter = "s.[TOTAL_AMT] > 0";   // Orders was not custom: its mapping changes too, but no human SQL is lost
        var second = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), first);

        var regenerated = Task(second, "app.Addresses");
        Assert.False(regenerated.Custom);
        Assert.Contains(SqlGenerator.DiscardedWarning, regenerated.Warnings);
        Assert.DoesNotContain(SqlGenerator.CarriedWarning, regenerated.Warnings);
        // A non-custom task whose mapping changed lost nothing a human wrote.
        Assert.DoesNotContain(second.Tasks.Values.Where(t => t.Target != "app.Addresses"), t => t.Warnings.Contains(SqlGenerator.DiscardedWarning));
    }

    [Fact]
    public void Carried_custom_sql_and_a_first_generation_never_warn_of_a_discard()
    {
        Assert.DoesNotContain(Plan().Tasks.Values, t => t.Warnings.Contains(SqlGenerator.DiscardedWarning));

        var first = Plan();
        Task(first, "app.Addresses").Custom = true;
        var second = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target(), first);
        Assert.True(Task(second, "app.Addresses").Custom);
        Assert.DoesNotContain(second.Tasks.Values, t => t.Warnings.Contains(SqlGenerator.DiscardedWarning));
    }

    // ---- Fix round 2: custom SQL for a table that gets no task at all is announced at plan level ----------------------

    public static TheoryData<string> NoTaskRoutes() => new() { "skipped", "removed from the mapping", "absent from the target catalog" };

    /// <summary>Regenerates against <paramref name="previous"/> with app.Addresses no longer producing a task by <paramref name="route"/>.</summary>
    static SqlPlanPayload GenerateWithoutAddresses(string route, SqlPlanPayload previous)
    {
        var mapping = SampleMappings.Approved();
        var tgt = SampleCatalogs.Target();
        switch (route)
        {
            case "skipped": mapping.Tables["app.Addresses"].Kind = "skip"; break;
            case "removed from the mapping": mapping.Tables.Remove("app.Addresses"); break;
            case "absent from the target catalog": tgt = tgt with { Tables = tgt.Tables.Where(t => t.Key != "app.Addresses").ToList() }; break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), tgt, previous);
        Assert.DoesNotContain(plan.Tasks.Values, t => t.Target == "app.Addresses");
        return plan;
    }

    static List<string> AllWarnings(SqlPlanPayload plan) => plan.Warnings.Concat(plan.Tasks.Values.SelectMany(t => t.Warnings)).ToList();

    [Fact]
    public void DiscardedNoTaskWarning_text_is_pinned()
    {
        Assert.Equal("custom SQL discarded: the table is no longer generated", SqlGenerator.DiscardedNoTaskWarning);
    }

    [Theory]
    [MemberData(nameof(NoTaskRoutes))]
    public void Custom_sql_for_a_table_that_gets_no_task_is_reported_at_plan_level(string route)
    {
        var first = Plan();
        Task(first, "app.Addresses").SourceQuery = "SELECT 1 AS [AddressId]";
        Task(first, "app.Addresses").Custom = true;

        var plan = GenerateWithoutAddresses(route, first);
        Assert.Equal(["app.Addresses: " + SqlGenerator.DiscardedNoTaskWarning],
            plan.Warnings.Where(w => w.Contains(SqlGenerator.DiscardedNoTaskWarning, StringComparison.Ordinal)));
        Assert.DoesNotContain(AllWarnings(plan), w => w.Contains(SqlGenerator.DiscardedWarning, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NoTaskRoutes))]
    public void A_non_custom_task_that_stops_being_generated_reports_nothing(string route)
    {
        var plan = GenerateWithoutAddresses(route, Plan());
        Assert.DoesNotContain(AllWarnings(plan), w => w.Contains("custom SQL discarded", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_level_discard_lines_are_one_per_lost_custom_task_ordered_by_target()
    {
        // Task ids deliberately sort the opposite way to the targets.
        var previous = new SqlPlanPayload
        {
            Tasks =
            {
                ["T01"] = new TaskPlan { Target = "app.Orders", Custom = true, MappingHash = "x" },
                ["T02"] = new TaskPlan { Target = "app.Addresses", Custom = true, MappingHash = "y" },
                ["T03"] = new TaskPlan { Target = "app.AuditEvents", Custom = false },
            },
        };
        var mapping = SampleMappings.Approved();
        foreach (var key in new[] { "app.Orders", "app.Addresses", "app.AuditEvents" }) mapping.Tables[key].Kind = "skip";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), previous);
        Assert.Equal(["app.Addresses: " + SqlGenerator.DiscardedNoTaskWarning, "app.Orders: " + SqlGenerator.DiscardedNoTaskWarning],
            plan.Warnings.Where(w => w.Contains(SqlGenerator.DiscardedNoTaskWarning, StringComparison.Ordinal)));
    }

    [Fact]
    public void Plan_level_discard_lines_are_ordered_ordinally_not_by_culture()
    {
        // "app.Àbbey" (leading A-with-grave, U+00C0 = 192) sorts AFTER "app.Zoo" ('Z' = U+005A = 90) under ordinal
        // comparison, but BEFORE it under culture-aware comparison, which weighs the base letter 'A' ahead of the diacritic.
        // A fixture using only plain ASCII names (e.g. "app.Addresses"/"app.Orders") sorts the same either way and would not
        // catch a regression to StringComparer.CurrentCulture.
        var previous = new SqlPlanPayload
        {
            Tasks =
            {
                ["T01"] = new TaskPlan { Target = "app.Zoo", Custom = true, MappingHash = "z" },
                ["T02"] = new TaskPlan { Target = "app.Àbbey", Custom = true, MappingHash = "a" },
            },
        };
        var mapping = new MappingPayload();
        var src = TestCatalogs.Snapshot(TestCatalogs.Meta("S"), []);
        var tgt = TestCatalogs.Snapshot(TestCatalogs.Meta("T"), []);

        var plan = SqlGenerator.Generate(mapping, src, tgt, previous);

        Assert.Equal(
            ["app.Zoo: " + SqlGenerator.DiscardedNoTaskWarning, "app.Àbbey: " + SqlGenerator.DiscardedNoTaskWarning],
            plan.Warnings.Where(w => w.Contains(SqlGenerator.DiscardedNoTaskWarning, StringComparison.Ordinal)));
    }

    [Fact]
    public void NullTargetWarning_text_is_pinned()
    {
        Assert.Equal("custom SQL discarded: task has no target table", SqlGenerator.NullTargetWarning);
    }

    [Fact]
    public void A_custom_task_with_a_null_target_gets_a_plan_warning_naming_the_task_instead_of_being_dropped()
    {
        // Only a hand-corrupted plan can produce this: TaskPlan.Target is a non-nullable string at compile time, but nothing
        // stops a stored JSON payload from carrying an explicit "target": null.
        var previous = new SqlPlanPayload
        {
            Tasks = { ["T07"] = new TaskPlan { Target = null!, Custom = true, MappingHash = "n" } },
        };
        var mapping = new MappingPayload();
        var src = TestCatalogs.Snapshot(TestCatalogs.Meta("S"), []);
        var tgt = TestCatalogs.Snapshot(TestCatalogs.Meta("T"), []);

        var plan = SqlGenerator.Generate(mapping, src, tgt, previous);

        Assert.Contains("T07: " + SqlGenerator.NullTargetWarning, plan.Warnings);
    }

    [Fact]
    public void A_still_generated_or_carried_custom_task_never_produces_the_plan_level_line()
    {
        var first = Plan();
        Task(first, "app.Addresses").Custom = true;
        Task(first, "app.Orders").Custom = true;
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Orders"].Filter = "s.[TOTAL_AMT] > 0";   // Orders regenerates (task warning), Addresses carries over
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), first);
        Assert.True(Task(plan, "app.Addresses").Custom);
        Assert.Contains(SqlGenerator.DiscardedWarning, Task(plan, "app.Orders").Warnings);
        Assert.DoesNotContain(AllWarnings(plan), w => w.Contains(SqlGenerator.DiscardedNoTaskWarning, StringComparison.Ordinal));
    }

    [Fact]
    public void Discard_checks_match_the_previous_target_case_insensitively()
    {
        // The previous plan names the table in different case; it is the same SQL Server object under a default collation.
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.Target = "APP.ADDRESSES";
        addresses.SourceQuery = "SELECT 1 AS [AddressId]";
        addresses.Custom = true;

        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Columns["City"].Expr = "UPPER(s.[CITY])";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), first);

        Assert.Contains(SqlGenerator.DiscardedWarning, Task(plan, "app.Addresses").Warnings);
        Assert.DoesNotContain(AllWarnings(plan), w => w.Contains(SqlGenerator.DiscardedNoTaskWarning, StringComparison.Ordinal));
    }

    // ---- Fix round 1: a cycle is cut only on an edge whose EVERY child-side FK column is nullable ----------------------

    static TableMap EmptyMap() => new() { Kind = "direct" };

    /// <summary>A two-table cycle app.A &lt;-&gt; app.B. The edge (app.A, app.B) sorts first ordinally and is always NOT NULL, so it is
    /// cut unless (app.B, app.A) is preferred. <paramref name="bFks"/> are app.B's FKs to app.A, over columns B1..B3.</summary>
    static SqlPlanPayload CyclePlan(bool b1Nullable, bool b2Nullable, params ForeignKeyInfo[] bFks)
    {
        var a = TestCatalogs.Table("app.A", 0,
            [TestCatalogs.Col("Id", "int", nullable: false), TestCatalogs.Col("Id2", "int", nullable: false), TestCatalogs.Col("BId", "int", nullable: false)],
            pk: ["Id", "Id2"], fks: [TestCatalogs.Fk("FK_A_B", "BId", "app.B", "Id")]);
        var b = TestCatalogs.Table("app.B", 0,
            [TestCatalogs.Col("Id", "int", nullable: false), TestCatalogs.Col("B1", "int", nullable: b1Nullable),
             TestCatalogs.Col("B2", "int", nullable: b2Nullable), TestCatalogs.Col("B3", "int", nullable: true)],
            pk: ["Id"], fks: bFks);
        var tgt = TestCatalogs.Snapshot(TestCatalogs.Meta("T"), [a, b]);
        var src = TestCatalogs.Snapshot(TestCatalogs.Meta("S"), []);
        var mapping = new MappingPayload { Tables = { ["app.A"] = EmptyMap(), ["app.B"] = EmptyMap() } };
        return SqlGenerator.Generate(mapping, src, tgt);
    }

    static ForeignKeyInfo CompositeFk(string name, params string[] columns) => new(name, columns.ToList(), "app", "A", ["Id", "Id2"], false, false);

    [Fact]
    public void Composite_fk_with_one_not_null_column_is_not_a_preferred_cut()
    {
        // FK_B_A (B1 NULL, B2 NOT NULL): child rows cannot be inserted with a NULL reference, so deferring it cannot work.
        var plan = CyclePlan(b1Nullable: true, b2Nullable: false, CompositeFk("FK_B_A", "B1", "B2"));
        Assert.Equal(["ALTER TABLE [app].[A] NOCHECK CONSTRAINT [FK_A_B];"], plan.PreSql);
        Assert.Equal(["ALTER TABLE [app].[A] WITH CHECK CHECK CONSTRAINT [FK_A_B];"], plan.PostSql);
    }

    [Fact]
    public void Composite_fk_with_every_column_nullable_is_the_preferred_cut()
    {
        var plan = CyclePlan(b1Nullable: true, b2Nullable: true, CompositeFk("FK_B_A", "B1", "B2"));
        Assert.Equal(["ALTER TABLE [app].[B] NOCHECK CONSTRAINT [FK_B_A];"], plan.PreSql);
        Assert.Equal(["ALTER TABLE [app].[B] WITH CHECK CHECK CONSTRAINT [FK_B_A];"], plan.PostSql);
        Assert.Contains(plan.Warnings, w => w.StartsWith("FK cycle broken at app.B -> app.A", StringComparison.Ordinal));
    }

    [Fact]
    public void Edge_with_several_fks_is_preferred_only_when_every_fk_is_nullable()
    {
        // Two FKs on the same edge (app.B -> app.A): FK_B_A_1 over nullable B3, FK_B_A_2 over NOT NULL B2.
        var mixed = CyclePlan(b1Nullable: true, b2Nullable: false, CompositeFk("FK_B_A_1", "B3", "B1"), CompositeFk("FK_B_A_2", "B2", "B1"));
        Assert.Equal(["ALTER TABLE [app].[A] NOCHECK CONSTRAINT [FK_A_B];"], mixed.PreSql);

        var safe = CyclePlan(b1Nullable: true, b2Nullable: true, CompositeFk("FK_B_A_1", "B3", "B1"), CompositeFk("FK_B_A_2", "B2", "B1"));
        Assert.Equal(["ALTER TABLE [app].[B] NOCHECK CONSTRAINT [FK_B_A_1];", "ALTER TABLE [app].[B] NOCHECK CONSTRAINT [FK_B_A_2];"], safe.PreSql);
    }

    // ---- Fix round 1: task ids widen past 99 tasks, and every reference uses the widened ids -------------------------

    [Theory]
    [InlineData(100)]   // the exact boundary: width comes from the count itself, not count - 1
    [InlineData(101)]
    public void Past_99_tasks_ids_widen_to_three_digits_and_every_reference_agrees(int count)
    {
        // count tables in a chain: app.t000 <- app.t001 <- ..., and the last table also references app.t000.
        string Key(int i) => "app.t" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        var tables = Enumerable.Range(0, count).Select(i =>
        {
            var fks = new List<ForeignKeyInfo>();
            if (i > 0) fks.Add(TestCatalogs.Fk($"FK_{i}_prev", "Prev", Key(i - 1), "Id"));
            if (i == count - 1) fks.Add(TestCatalogs.Fk($"FK_{i}_root", "Root", Key(0), "Id"));
            return TestCatalogs.Table(Key(i), 0,
                [TestCatalogs.Col("Id", "int", nullable: false), TestCatalogs.Col("Prev", "int"), TestCatalogs.Col("Root", "int")],
                pk: ["Id"], fks: fks);
        }).ToList();
        var mapping = new MappingPayload();
        for (var i = 0; i < count; i++) mapping.Tables[Key(i)] = EmptyMap();

        var plan = SqlGenerator.Generate(mapping, TestCatalogs.Snapshot(TestCatalogs.Meta("S"), []), TestCatalogs.Snapshot(TestCatalogs.Meta("T"), tables));

        Assert.Equal(count, plan.Order.Count);
        Assert.Equal("T001", plan.Order[0]);
        Assert.Equal("T099", plan.Order[98]);
        Assert.Equal("T100", plan.Order[99]);
        if (count > 100) Assert.Equal("T101", plan.Order[100]);
        Assert.All(plan.Order, id => Assert.Matches("^T[0-9]{3}$", id));
        // Equal width is what keeps ordinal id order equal to execution order.
        Assert.Equal(plan.Order, plan.Order.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(plan.Order.OrderBy(x => x, StringComparer.Ordinal), plan.Tasks.Keys.OrderBy(x => x, StringComparer.Ordinal));

        for (var i = 0; i < count; i++)
        {
            var id = plan.Order[i];
            Assert.Equal(Key(i), plan.Tasks[id].Target);
            Assert.All(plan.Tasks[id].DependsOn, d => Assert.True(plan.Tasks.ContainsKey(d), $"{id} depends on unissued id {d}"));
        }
        Assert.Empty(plan.Tasks["T001"].DependsOn);
        Assert.Equal(count == 100 ? new[] { "T001", "T099" } : new[] { "T099" }, plan.Tasks["T100"].DependsOn);
        if (count > 100) Assert.Equal(["T001", "T100"], plan.Tasks["T101"].DependsOn);
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

    /// <summary>Byte-for-byte the stagingDdl the generator emitted BEFORE StagingDdl was extracted (captured from commit 56f821c's
    /// generator). The shared builder has two callers — the generator and SqlValidator's scaffold — and must not drift.</summary>
    const string AddressesStagingDdl =
        "CREATE TABLE #stg (\n    [AddressId] int NULL,\n    [CustomerId] int NULL,\n" +
        "    [Line1] nvarchar(200) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n    [City] nvarchar(80) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n" +
        "    [PostalCode] nvarchar(12) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n    [CountryCode] char(2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n" +
        "    [__k0] int NULL\n);";

    const string ProductsStagingDdl =
        "CREATE TABLE #stg (\n    [ProductId] int NULL,\n    [Name] nvarchar(150) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n" +
        "    [Description] nvarchar(500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n    [UnitPrice] decimal(19,4) NULL,\n    [IsActive] bit NULL,\n" +
        "    [__k0] int NULL\n);";

    [Fact]
    public void Staging_ddl_is_byte_identical_through_the_shared_builder()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        mapping.Tables["app.Products"].Kind = "lookup";
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();
        var plan = SqlGenerator.Generate(mapping, src, tgt);

        foreach (var (target, expected) in new[] { ("app.Addresses", AddressesStagingDdl), ("app.Products", ProductsStagingDdl) })
        {
            var task = Task(plan, target);
            Assert.Equal(expected, task.StagingDdl);

            // The builder called directly, with the same typed inputs the generator uses, yields the same bytes.
            var table = tgt.FindTable(target)!;
            var primary = src.FindTable(mapping.Tables[target].Sources[0])!;
            var keys = primary.BestKey()!.Select((k, i) => ("__k" + i, primary.FindColumn(k)));
            Assert.Equal(expected, SqlGenerator.StagingDdl(task.Columns.Select(b => table.FindColumn(b.Target)!), keys));
        }
    }
}
