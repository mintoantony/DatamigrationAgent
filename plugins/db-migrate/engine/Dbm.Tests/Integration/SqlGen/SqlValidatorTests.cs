using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public class SqlValidatorTests
{
    static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    static string IdOf(SqlPlanPayload plan, string target) => plan.Tasks.Single(kv => kv.Value.Target == target).Key;

    static Task<ValidationReport> Validate(SamplePair pair, SqlPlanPayload plan, string? only = null) =>
        SqlValidator.ValidateAsync(plan, pair.SourceCs, pair.TargetCs, SampleCatalogs.Target(), only, CancellationToken.None);

    static string Describe(ValidationReport r) =>
        string.Join("\n", r.GlobalErrors.Concat(r.TaskErrors.SelectMany(kv => kv.Value.Select(e => $"{kv.Key}: {e}"))));

    static async Task<object?> Scalar(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Generated_sample_plan_validates_ok()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var report = await Validate(pair, plan);
        Assert.True(report.Ok, Describe(report));
        Assert.Equal(plan.Order, report.TaskErrors.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(report.GlobalErrors);
    }

    [Fact]
    public async Task Unknown_source_column_is_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].SourceQuery = plan.Tasks[id].SourceQuery.Replace("s.[CITY]", "s.[CITY_NAME]");
        var report = await Validate(pair, plan);
        Assert.False(report.Ok);
        Assert.Contains(report.TaskErrors[id], e => e == "sourceQuery: Invalid column name 'CITY_NAME'.");
    }

    [Fact]
    public async Task Narrowing_comment_is_a_risk_warning()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var report = await Validate(pair, plan);
        Assert.Contains(report.TaskWarnings[IdOf(plan, "app.Orders")], w => w.StartsWith("Comment: varchar(500) -> nvarchar(200)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_alias_and_non_insertable_target_are_errors()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Customers");
        plan.Tasks[id].Columns.Add(new ColumnBinding("FirstName", "DisplayName"));
        plan.Tasks[id].KeyColumns.Add("__k9");
        var report = await Validate(pair, plan, id);
        Assert.Contains("DisplayName: computed column cannot be loaded", report.TaskErrors[id]);
        Assert.Contains("key column '__k9' is not in the source query result", report.TaskErrors[id]);
        Assert.Equal([id], report.TaskErrors.Keys);
    }

    [Fact]
    public async Task Global_statement_syntax_errors_are_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        // Non-execution is proven by the canary tests below; this pins that a global statement's syntax error is reported.
        var plan = Plan();
        plan.PostSql.Add("ALTER TABLE [app].[Customers] NOCHEK CONSTRAINT [FK_Customers_PrimaryAddress];");
        var report = await Validate(pair, plan);
        Assert.Contains("postSql[1]: Incorrect syntax near 'NOCHEK'.", report.GlobalErrors);
    }

    [Fact]
    public async Task Describing_a_source_query_never_executes_it()
    {
        // Same escape shapes that defeated the PARSEONLY check, aimed at sp_describe_first_result_set on the source.
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        await SampleDatabases.RunAsync(pair.SourceCs, "CREATE TABLE dbo.ProbeRows (x int NOT NULL); INSERT INTO dbo.ProbeRows (x) VALUES (1), (2);");
        string[] probes =
        [
            "SET PARSEONLY OFF; DELETE FROM dbo.ProbeRows; SELECT 1 AS [x];",
            "SET PARSEONLY OFF;\nDELETE FROM dbo.ProbeRows;",
            "SET FMTONLY OFF; DELETE FROM dbo.ProbeRows; SELECT 1 AS [x];",
            "SELECT 1 AS [x]; DELETE FROM dbo.ProbeRows;",
            "DELETE FROM dbo.ProbeRows; SELECT 1 AS [x];",
            "SELECT 1 AS [x]\nGO\nDELETE FROM dbo.ProbeRows;",
            "'; DELETE FROM dbo.ProbeRows; --",
            "SELECT 1 AS [x]'; DELETE FROM dbo.ProbeRows; SELECT N'",
            "EXEC('DELETE FROM dbo.ProbeRows'); SELECT 1 AS [x];",
            "SELECT 1 AS [x]; EXEC sp_executesql N'DELETE FROM dbo.ProbeRows';",
            "DELETE FROM dbo.ProbeRows OUTPUT deleted.x AS [x];",
            "INSERT INTO dbo.ProbeRows (x) OUTPUT inserted.x AS [x] VALUES (99);",
        ];
        await using (var conn = new SqlConnection(pair.SourceCs))
        {
            await conn.OpenAsync();
            foreach (var probe in probes)
            {
                await SqlValidator.DescribeAsync(conn, probe, CancellationToken.None);
                Assert.True("2:3" == (string?)await Scalar(pair.SourceCs, "SELECT CONCAT(COUNT(*), ':', SUM(x)) FROM dbo.ProbeRows"), $"side effect from: {probe}");
            }
        }

        var plan = Plan();
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].SourceQuery = probes[0];
        await Validate(pair, plan, id);
        Assert.Equal(2, await Scalar(pair.SourceCs, "SELECT COUNT(*) FROM dbo.ProbeRows"));
        Assert.Equal(3, await Scalar(pair.SourceCs, "SELECT SUM(x) FROM dbo.ProbeRows"));

        // Control: the same probe run as a plain batch does delete, so the row check above can see a side effect.
        await Scalar(pair.SourceCs, probes[0]);
        Assert.Equal(0, await Scalar(pair.SourceCs, "SELECT COUNT(*) FROM dbo.ProbeRows"));
    }

    [Fact]
    public async Task Connection_failure_is_a_global_error_without_the_password()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        const string password = "S3cret-Pa55word!";
        var bad = new SqlConnectionStringBuilder(pair.TargetCs)
            { IntegratedSecurity = false, UserID = "dbm_no_such_login", Password = password, ConnectTimeout = 5 }.ConnectionString;
        var plan = Plan();
        var report = await SqlValidator.ValidateAsync(plan, pair.SourceCs, bad, SampleCatalogs.Target(), null, CancellationToken.None);
        Assert.False(report.Ok);
        Assert.Contains(report.GlobalErrors, e => e.StartsWith("target connection failed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(password, Json.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_without_terminator_is_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].MergeSql = "MERGE [app].[Addresses] AS t USING #stg AS s ON t.[AddressId] = s.[AddressId]\n" +
                                  "WHEN NOT MATCHED THEN INSERT ([CustomerId], [Line1], [City], [CountryCode]) VALUES (s.[CustomerId], s.[Line1], s.[City], s.[CountryCode])";
        var report = await Validate(pair, plan, id);
        Assert.Contains("mergeSql: A MERGE statement must be terminated by a semi-colon (;).", report.TaskErrors[id]);
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith("stagingDdl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_replaces_previous_validator_warnings()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var report = await Validate(pair, plan);
        SqlValidator.Apply(plan, report);
        SqlValidator.Apply(plan, report);
        var orders = plan.Tasks[IdOf(plan, "app.Orders")];
        Assert.Single(orders.Warnings, w => w.StartsWith(SqlValidator.WarningPrefix + "Comment:", StringComparison.Ordinal));
        Assert.Contains("target has 1 trigger(s); not fired unless FireTriggers", orders.Warnings);
        Assert.Empty(plan.Errors);
    }

    // ---------------- target statements: sp_describe with an engine-built #stg scaffold ----------------

    static SqlPlanPayload StagingPlan(params string[] lookups)
    {
        var mapping = SampleMappings.Approved();
        foreach (var table in lookups.DefaultIfEmpty("app.Addresses")) mapping.Tables[table].Kind = "lookup";
        return SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
    }

    static string NotChecked(string field) => field + ": not checked: ";

    [Fact]
    public async Task Generated_staging_plans_validate_ok_without_unchecked_statements()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan("app.Addresses", "app.Products");   // two #stg scaffolds on one connection
        var report = await Validate(pair, plan);
        Assert.True(report.Ok, Describe(report));
        Assert.DoesNotContain(report.TaskWarnings.Values.SelectMany(w => w), w => w.Contains(": not checked: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generated_merge_is_bound_against_the_scaffold()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan();
        var id = IdOf(plan, "app.Addresses");
        var merge = plan.Tasks[id].MergeSql!;

        plan.Tasks[id].MergeSql = merge.Replace("INSERT INTO [app].[Addresses]", "INSERT INTO [app].[NoSuchPerm]", StringComparison.Ordinal);
        Assert.Contains("mergeSql: Invalid object name 'app.NoSuchPerm'.", (await Validate(pair, plan, id)).TaskErrors[id]);

        plan.Tasks[id].MergeSql = merge.Replace("\nSELECT [AddressId]", "\nSELECT [Nope]", StringComparison.Ordinal);
        Assert.Contains("mergeSql: Invalid column name 'Nope'.", (await Validate(pair, plan, id)).TaskErrors[id]);
    }

    [Fact]
    public async Task Staging_ddl_errors_are_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan();
        var id = IdOf(plan, "app.Addresses");
        var ddl = plan.Tasks[id].StagingDdl!;
        plan.Tasks[id].StagingDdl = ddl.Replace("CREATE TABLE", "CREATE TALBE", StringComparison.Ordinal);
        Assert.Contains("stagingDdl: Unknown object type 'TALBE' used in a CREATE, DROP, or ALTER statement.", (await Validate(pair, plan, id)).TaskErrors[id]);
        plan.Tasks[id].StagingDdl = ddl.Replace("[AddressId] int", "[AddressId] intt", StringComparison.Ordinal);
        Assert.Contains((await Validate(pair, plan, id)).TaskErrors[id], e => e.StartsWith("stagingDdl: ", StringComparison.Ordinal) && e.Contains("intt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Temp_tables_validation_cannot_create_are_not_checked_but_visible()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan();
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].MergeSql = plan.Tasks[id].MergeSql!.Replace("FROM #stg;", "FROM #mystg;", StringComparison.Ordinal);
        var report = await Validate(pair, plan, id);
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith("mergeSql", StringComparison.Ordinal));
        Assert.Contains(report.TaskWarnings[id], w => w.StartsWith(NotChecked("mergeSql"), StringComparison.Ordinal) && w.Contains("#mystg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Custom_merge_column_errors_are_not_checked_but_visible()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan();
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].Custom = true;
        plan.Tasks[id].MergeSql = plan.Tasks[id].MergeSql!.Replace("\nSELECT [AddressId]", "\nSELECT [Nope]", StringComparison.Ordinal);
        var report = await Validate(pair, plan, id);
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith("mergeSql", StringComparison.Ordinal));
        Assert.Contains(report.TaskWarnings[id], w => w.StartsWith(NotChecked("mergeSql"), StringComparison.Ordinal));

        // A custom merge aimed at a missing PERMANENT table is still an error.
        plan.Tasks[id].MergeSql = plan.Tasks[id].MergeSql!.Replace("INSERT INTO [app].[Addresses]", "INSERT INTO [app].[NoSuchPerm]", StringComparison.Ordinal)
            .Replace("\nSELECT [Nope]", "\nSELECT [AddressId]", StringComparison.Ordinal);
        Assert.Contains("mergeSql: Invalid object name 'app.NoSuchPerm'.", (await Validate(pair, plan, id)).TaskErrors[id]);
    }

    [Fact]
    public async Task Objects_created_earlier_in_the_task_are_not_checked_but_typos_are_errors()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Customers");
        var task = plan.Tasks[id];
        task.Custom = true;
        task.PreSql.Add("CREATE TABLE [dbo].[stage_x] ([id] int);");
        task.PostSql.Add("INSERT INTO [app].[AuditEvents] ([EventTime], [UserName], [Action]) SELECT SYSUTCDATETIME(), N'x', CAST([id] AS nvarchar(200)) FROM dbo.stage_x;");
        task.PostSql.Add("DELETE FROM [dbo].[stage_typo];");
        var report = await Validate(pair, plan, id);
        var created = $"postSql[{task.PostSql.Count - 2}]";
        var typo = $"postSql[{task.PostSql.Count - 1}]";
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith(created, StringComparison.Ordinal));
        Assert.Contains(report.TaskWarnings[id], w => w.StartsWith(NotChecked(created), StringComparison.Ordinal));
        Assert.Contains($"{typo}: Invalid object name 'dbo.stage_typo'.", report.TaskErrors[id]);
        Assert.Equal(0, await Scalar(pair.TargetCs, "SELECT COUNT(*) FROM sys.tables WHERE name = N'stage_x'"));
    }

    [Fact]
    public async Task Global_statements_that_cannot_be_checked_are_visible()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        plan.PreSql.Add("CREATE TABLE [dbo].[agent_stage] ([id] int);");
        plan.PostSql.Add("INSERT INTO [app].[AuditEvents] ([EventTime], [UserName], [Action]) SELECT SYSUTCDATETIME(), N'x', N'y' FROM [dbo].[agent_stage];");
        var report = await Validate(pair, plan);
        var field = $"postSql[{plan.PostSql.Count - 1}]";
        Assert.DoesNotContain(report.GlobalErrors, e => e.StartsWith(field, StringComparison.Ordinal));
        Assert.Contains(report.GlobalWarnings!, w => w.StartsWith(NotChecked(field), StringComparison.Ordinal) && w.Contains($"preSql[{plan.PreSql.Count - 1}]", StringComparison.Ordinal));
        Assert.True(report.Ok, Describe(report));
        // A single-task run never checks the global statements, and says so rather than returning an empty list.
        Assert.Equal([SqlValidator.SingleTaskGlobalWarning], (await Validate(pair, plan, plan.Order[0])).GlobalWarnings);
        Assert.Empty((await Validate(pair, Plan())).GlobalWarnings);
    }

    [Fact]
    public async Task Each_merge_is_checked_against_its_own_scaffold()
    {
        // Rule 10: one connection serves every task and #stg is a fixed name. Task B binds a strict subset of task A's columns;
        // if A's wider scaffold survived, B's merge naming a column only A has would bind cleanly and false-pass.
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan();
        var a = IdOf(plan, "app.Addresses");
        var wide = plan.Tasks[a];
        var narrow = Json.Deserialize<TaskPlan>(Json.Serialize(wide));
        narrow.Columns.RemoveAll(c => c.Target == "City");
        narrow.SourceQuery = narrow.SourceQuery.Replace(",\n    s.[CITY] AS [City]", "", StringComparison.Ordinal);
        Assert.Contains("[City]", narrow.MergeSql);   // generated merge still names City: a real error against B's own scaffold
        plan.Tasks["T99"] = narrow;
        plan.Order.Add("T99");

        var report = await Validate(pair, plan);
        Assert.DoesNotContain(report.TaskErrors[a], e => e.StartsWith("mergeSql", StringComparison.Ordinal));
        Assert.Contains("mergeSql: Invalid column name 'City'.", report.TaskErrors["T99"]);
    }

    [Fact]
    public async Task A_later_direct_task_never_sees_an_earlier_staging_scaffold()
    {
        // A custom direct task naming #stg, validated after a staging task on the same connection, must not bind against the
        // staging task's leftover scaffold: it is "not checked" (rule 6), never clean.
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = StagingPlan();
        var direct = IdOf(plan, "app.Orders");   // FK child of app.Addresses, so validated after it
        Assert.True(plan.Order.IndexOf(IdOf(plan, "app.Addresses")) < plan.Order.IndexOf(direct));
        plan.Tasks[direct].Custom = true;
        plan.Tasks[direct].PreSql.Add("INSERT INTO [app].[AuditEvents] ([EventTime], [UserName], [Action]) SELECT SYSUTCDATETIME(), [City], N'x' FROM #stg;");
        var field = $"preSql[{plan.Tasks[direct].PreSql.Count - 1}]";

        var report = await Validate(pair, plan);
        Assert.DoesNotContain(report.TaskErrors[direct], e => e.StartsWith(field, StringComparison.Ordinal));
        Assert.Contains(report.TaskWarnings[direct], w => w.StartsWith(NotChecked(field), StringComparison.Ordinal) && w.Contains("#stg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fields_with_several_statements_report_errors_and_disclose_unverified_binding()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Customers");
        var task = plan.Tasks[id];
        task.Custom = true;
        task.PreSql.Add("UPDATE STATISTICS [app].[Customers]; SELECT * FROM [app].[NoSuchTable];");   // server silent on the 2nd statement
        task.PreSql.Add("SELECT 1 AS [x]; SELCT oops;");                                                // parse error still fails the batch
        task.PreSql.Add("UPDATE STATISTICS [app].[Customers];  \n");                                    // one trailing ';' is a single statement
        var report = await Validate(pair, plan, id);
        string Field(int back) => $"preSql[{task.PreSql.Count - back}]";

        Assert.Contains(report.TaskWarnings[id], w => w.StartsWith(NotChecked(Field(3)), StringComparison.Ordinal) && w.Contains("multiple statements", StringComparison.Ordinal));
        Assert.Contains(report.TaskErrors[id], e => e.StartsWith(Field(2) + ": Incorrect syntax", StringComparison.Ordinal));
        Assert.Contains(report.TaskWarnings[id], w => w.StartsWith(NotChecked(Field(2)), StringComparison.Ordinal) && w.Contains("multiple statements", StringComparison.Ordinal));
        Assert.DoesNotContain(report.TaskWarnings[id], w => w.StartsWith(Field(1) + ":", StringComparison.Ordinal));
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith(Field(1) + ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_statement_the_server_cannot_analyze_is_not_checked_but_visible()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Customers");
        plan.Tasks[id].PreSql.Add("CREATE TABLE #work ([x] int); SELECT [x] FROM #work;");   // sp_describe 11525 (temp table in a multi-statement batch): a wrapper with no underlying error
        var report = await Validate(pair, plan, id);
        var field = $"preSql[{plan.Tasks[id].PreSql.Count - 1}]";
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith(field, StringComparison.Ordinal));
        Assert.Contains(report.TaskWarnings[id], w => w.StartsWith(NotChecked(field), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Valid_statements_without_a_result_set_are_not_errors()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Customers");
        string[] valid =
        [
            "ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];",
            "UPDATE STATISTICS [app].[Customers];",
            "SET IDENTITY_INSERT [app].[Customers] ON;",
            "DELETE FROM [app].[AuditEvents] WHERE 1 = 0;",
            "UPDATE [app].[Customers] SET [Notes] = NULL WHERE 1 = 0;",
            "DECLARE @x int = 1;",
            "IF 1 = 0 PRINT 'x';",
            "DISABLE TRIGGER [trg_Orders_Audit] ON [app].[Orders];",
            "EXEC sp_executesql N'SELECT 1 AS [x]';",
        ];
        plan.Tasks[id].PreSql.AddRange(valid);
        var report = await Validate(pair, plan, id);
        Assert.True(report.TaskErrors[id].Count == 0, string.Join("\n", report.TaskErrors[id]));
        // Caveat 2: none of these may be filtered down to an empty message and land as "not checked" either.
        Assert.DoesNotContain(report.TaskWarnings[id], w => w.Contains(SqlValidator.NotCheckedMarker, StringComparison.Ordinal));
    }

    // ---------------- validation rules (one assertion each) ----------------

    /// <summary>Replaces a task's query and bindings with a constant SELECT whose result types sp_describe reports exactly.</summary>
    static void Rewire(TaskPlan task, string select, bool identityInsert, params (string Source, string Target)[] bindings)
    {
        task.SourceQuery = select;
        task.KeyColumns.Clear();
        task.Columns = bindings.Select(b => new ColumnBinding(b.Source, b.Target)).ToList();
        task.IdentityInsert = identityInsert;
    }

    [Fact]
    public async Task Binding_rules_are_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var customers = IdOf(plan, "app.Customers");
        Rewire(plan.Tasks[customers],
            "SELECT ISNULL(CAST(1 AS int), 0) AS [CustomerId], ISNULL(CAST(N'a' AS nvarchar(50)), N'') AS [FirstName], CAST(NULL AS nvarchar(50)) AS [LastName], 1, 2 AS [Dup], 3 AS [Dup]",
            identityInsert: false, ("CustomerId", "CustomerId"), ("FirstName", "FirstName"), ("LastName", "LastName"), ("Missing", "Email"));
        var products = IdOf(plan, "app.Products");
        Rewire(plan.Tasks[products], "SELECT CAST(N'x' AS nvarchar(150)) AS [Name], CAST(NULL AS binary(8)) AS [RowVer]",
            identityInsert: true, ("Name", "Name"), ("RowVer", "RowVer"));
        var lines = IdOf(plan, "app.OrderLines");
        plan.Tasks[lines].Target = "app.NoSuchTable";

        var report = await Validate(pair, plan);

        Assert.Contains("CustomerId: identity column is bound but identityInsert is false; the target will generate new values", report.TaskWarnings[customers]);   // H
        Assert.Contains("identityInsert is true but no identity column is bound", report.TaskWarnings[products]);                                                 // I
        Assert.Contains("LastName: source may be NULL but the target column is NOT NULL", report.TaskWarnings[customers]);                                       // K
        Assert.DoesNotContain(report.TaskWarnings[customers], w => w.StartsWith("FirstName: source may be NULL", StringComparison.Ordinal));
        Assert.Contains("RowVer: rowversion column cannot be loaded", report.TaskErrors[products]);                                                               // L
        Assert.Contains("target table app.NoSuchTable not found in the target catalog", report.TaskErrors[lines]);                                              // M
        Assert.Contains("sourceQuery: every result column needs an alias", report.TaskErrors[customers]);                                                        // N
        Assert.Contains("sourceQuery: duplicate result column 'Dup'", report.TaskErrors[customers]);                                                             // O
        Assert.Contains("Email: column 'Missing' is not in the source query result", report.TaskErrors[customers]);                                              // P
    }

    [Fact]
    public async Task Incompatible_type_is_an_error_and_fails_the_report()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var orders = IdOf(plan, "app.Orders");
        Rewire(plan.Tasks[orders], "SELECT CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS [CustomerId]",
            identityInsert: false, ("CustomerId", "CustomerId"));
        var report = await Validate(pair, plan, orders);
        Assert.Equal(["CustomerId: uniqueidentifier -> int: uniqueidentifier cannot be converted to int"], report.TaskErrors[orders]);   // J
        Assert.False(report.Ok);
    }

    // ---------------- execution canary (Ruling 26) ----------------
    // Every plan field carries a MONOTONIC side effect: a sentinel row, or a sentinel table. Nothing in the fixture removes
    // either, so if any statement executes during validation the evidence stays behind. Mechanism-independent on purpose.
    // The check has two halves and neither may be relaxed:
    //   (a) no plan SQL executed: every sentinel absent;
    //   (b) the ONLY thing validation creates is the engine's session-local #stg: nothing lands in either user database
    //       (sys.objects identical before and after), and the untouched generated merge of app.Products still binds, which it
    //       can only do against a #stg that validation itself created on its own session.

    const string CanaryTable = "[app].[__dbm_canary]";
    const string CanaryCreated = "__dbm_canary_created";

    static string CanaryInsert(string label) => $"INSERT INTO {CanaryTable} ([Label]) VALUES (N'{label}');";

    static Task<object?> ObjectsAsync(string cs) =>
        Scalar(cs, "SELECT STRING_AGG(CAST(CONCAT(object_id, ':', name) AS nvarchar(max)), N',') WITHIN GROUP (ORDER BY object_id) FROM sys.objects");

    sealed record Canary(SqlPlanPayload Plan, object? TargetObjects, object? SourceObjects);

    static async Task<Canary> CanaryPlanAsync(SamplePair pair)
    {
        await SampleDatabases.RunAsync(pair.TargetCs, $"CREATE TABLE {CanaryTable} ([Label] nvarchar(100) NOT NULL);");
        await SampleDatabases.RunAsync(pair.SourceCs, "CREATE TABLE [dbo].[__dbm_canary] ([Label] nvarchar(100) NOT NULL);");
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";   // staging_merge: the task has stagingDdl and mergeSql
        mapping.Tables["app.Products"].Kind = "lookup";    // left generated: its merge needs the engine's #stg to bind (half b)
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        plan.PreSql.Add(CanaryInsert("global preSql"));
        plan.PostSql.Add($"CREATE TABLE [app].[{CanaryCreated}] ([x] int);");
        var stg = plan.Tasks[IdOf(plan, "app.Addresses")];
        stg.PreSql.Add(CanaryInsert("task preSql"));
        stg.StagingDdl = CanaryInsert("stagingDdl");
        stg.MergeSql = CanaryInsert("mergeSql");
        stg.PostSql.Add(CanaryInsert("task postSql"));
        plan.Tasks[IdOf(plan, "app.Customers")].SourceQuery =
            "INSERT INTO [dbo].[__dbm_canary] ([Label]) OUTPUT inserted.[Label] AS [FirstName] VALUES (N'sourceQuery');";
        return new Canary(plan, await ObjectsAsync(pair.TargetCs), await ObjectsAsync(pair.SourceCs));
    }

    static async Task AssertNothingExecutedAsync(SamplePair pair, Canary canary, string entryPoint, ValidationReport? report = null)
    {
        // (a) no plan SQL executed
        Assert.True(0 == (int)(await Scalar(pair.TargetCs, $"SELECT COUNT(*) FROM {CanaryTable}"))!,
            $"{entryPoint}: target canary rows: {await Scalar(pair.TargetCs, $"SELECT STRING_AGG([Label], N', ') FROM {CanaryTable}")}");
        Assert.True(0 == (int)(await Scalar(pair.TargetCs, $"SELECT COUNT(*) FROM sys.tables WHERE name = N'{CanaryCreated}'"))!,
            $"{entryPoint}: target canary table was created");
        Assert.True(0 == (int)(await Scalar(pair.SourceCs, "SELECT COUNT(*) FROM [dbo].[__dbm_canary]"))!,
            $"{entryPoint}: source canary rows");
        // (b) nothing created in either user database (a #temp table never appears in the user database's sys.objects) ...
        Assert.True(Equals(canary.TargetObjects, await ObjectsAsync(pair.TargetCs)), $"{entryPoint}: target sys.objects changed");
        Assert.True(Equals(canary.SourceObjects, await ObjectsAsync(pair.SourceCs)), $"{entryPoint}: source sys.objects changed");
        // ... and the generated Products merge bound against the engine's own #stg.
        if (report is not null && report.TaskErrors.ContainsKey(IdOf(canary.Plan, "app.Products")))
        {
            var products = IdOf(canary.Plan, "app.Products");
            Assert.DoesNotContain(report.TaskErrors[products], e => e.StartsWith("mergeSql", StringComparison.Ordinal));
            Assert.DoesNotContain(report.TaskWarnings[products], w => w.StartsWith("mergeSql", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task No_plan_sql_is_executed_through_any_entry_point()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var canary = await CanaryPlanAsync(pair);
        var plan = canary.Plan;

        await AssertNothingExecutedAsync(pair, canary, "ValidateAsync (full)", await Validate(pair, plan));

        foreach (var id in plan.Order)
            await AssertNothingExecutedAsync(pair, canary, $"ValidateAsync ({id})", await Validate(pair, plan, id));

        await using (var target = new SqlConnection(pair.TargetCs))
        {
            await target.OpenAsync();
            var stg = plan.Tasks[IdOf(plan, "app.Addresses")];
            foreach (var sql in plan.PreSql.Concat(plan.PostSql).Concat(stg.PreSql).Append(stg.StagingDdl!).Append(stg.MergeSql!).Concat(stg.PostSql))
            {
                await SqlValidator.CheckTargetStatementAsync(target, sql, CancellationToken.None);
                await SqlValidator.DescribeAsync(target, sql, CancellationToken.None);
            }
        }
        await AssertNothingExecutedAsync(pair, canary, "statement-level checks");

        await using (var source = new SqlConnection(pair.SourceCs))
        {
            await source.OpenAsync();
            await SqlValidator.DescribeAsync(source, plan.Tasks[IdOf(plan, "app.Customers")].SourceQuery, CancellationToken.None);
        }
        await AssertNothingExecutedAsync(pair, canary, "DescribeAsync (source)");

        var sabotaged = Json.Deserialize<SqlPlanPayload>(Json.Serialize(plan));
        sabotaged.Tasks[IdOf(plan, "app.Products")].MergeSql = sabotaged.Tasks[IdOf(plan, "app.Products")].MergeSql!.Replace("FROM #stg;", "FROM #nostg;", StringComparison.Ordinal);
        Assert.Contains((await Validate(pair, sabotaged)).TaskWarnings[IdOf(plan, "app.Products")], w => w.StartsWith("mergeSql: not checked:", StringComparison.Ordinal));   // half (b) can fail

        // Control: the oracle can see execution. Run each canary as a plain batch and watch it land.
        await Scalar(pair.TargetCs, CanaryInsert("control"));
        await Scalar(pair.TargetCs, plan.PostSql[^1]);
        await Scalar(pair.SourceCs, plan.Tasks[IdOf(plan, "app.Customers")].SourceQuery);
        Assert.Equal(1, await Scalar(pair.TargetCs, $"SELECT COUNT(*) FROM {CanaryTable}"));
        Assert.Equal(1, await Scalar(pair.TargetCs, $"SELECT COUNT(*) FROM sys.tables WHERE name = N'{CanaryCreated}'"));
        Assert.Equal(1, await Scalar(pair.SourceCs, "SELECT COUNT(*) FROM [dbo].[__dbm_canary]"));
    }

    [Theory]
    [InlineData("SET PARSEONLY OFF; ")]
    [InlineData("SET PARSE\u0640ONLY OFF; ")]
    [InlineData("SET \u0640PARSEONLY OFF; ")]
    [InlineData("SET \u01F6PARSEONLY OFF; ")]
    [InlineData("SET \u0400PARSEONLY OFF; ")]
    [InlineData("SET \u02EEPARSEONLY OFF; ")]
    [InlineData("SET PARSEONLY\u0640 OFF; ")]
    public async Task Sandbox_escape_prefixes_do_not_execute(string prefix)
    {
        // H1: SQL Server matches SET option names under collation rules, so these all read as PARSEONLY OFF to the server.
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var canary = await CanaryPlanAsync(pair);
        var plan = canary.Plan;
        plan.PreSql[^1] = prefix + plan.PreSql[^1];
        plan.PostSql[^1] = prefix + plan.PostSql[^1];
        var stg = plan.Tasks[IdOf(plan, "app.Addresses")];
        stg.PreSql[^1] = prefix + stg.PreSql[^1];
        stg.StagingDdl = prefix + stg.StagingDdl;
        stg.MergeSql = prefix + stg.MergeSql;
        stg.PostSql[^1] = prefix + stg.PostSql[^1];

        await AssertNothingExecutedAsync(pair, canary, "ValidateAsync (full)", await Validate(pair, plan));
        await AssertNothingExecutedAsync(pair, canary, "ValidateAsync (staging task)", await Validate(pair, plan, IdOf(plan, "app.Addresses")));

        // The boundary itself, independent of ValidateAsync: the statement-level check never executes an escape attempt.
        await using (var target = new SqlConnection(pair.TargetCs))
        {
            await target.OpenAsync();
            foreach (var sql in new[] { plan.PreSql[^1], plan.PostSql[^1], stg.PreSql[^1], stg.StagingDdl!, stg.MergeSql!, stg.PostSql[^1] })
                await SqlValidator.CheckTargetStatementAsync(target, sql, CancellationToken.None);
        }
        await AssertNothingExecutedAsync(pair, canary, "CheckTargetStatementAsync (direct)");
    }
}
