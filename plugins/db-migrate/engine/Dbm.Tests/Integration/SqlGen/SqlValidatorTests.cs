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
    public async Task Target_statements_are_parse_checked_but_never_executed()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();   // PreSql disables FK_Customers_PrimaryAddress
        plan.PostSql.Add("ALTER TABLE [app].[Customers] NOCHEK CONSTRAINT [FK_Customers_PrimaryAddress];");
        var report = await Validate(pair, plan);
        Assert.Contains("postSql[1]: Incorrect syntax near 'NOCHEK'.", report.GlobalErrors);
        var disabled = await Scalar(pair.TargetCs, "SELECT CAST(is_disabled AS int) FROM sys.foreign_keys WHERE name = 'FK_Customers_PrimaryAddress'");
        Assert.Equal(0, disabled);
    }

    [Fact]
    public async Task Statement_that_turns_parseonly_off_is_refused_and_not_executed()
    {
        // SET PARSEONLY acts at parse time: sent inside the checked batch, OFF would make the rest of that batch execute.
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        await SampleDatabases.RunAsync(pair.TargetCs, "INSERT INTO [app].[AuditEvents] ([EventTime], [UserName], [Action]) VALUES (SYSUTCDATETIME(), N'probe', N'parseonly probe');");
        var plan = Plan();
        plan.PostSql.Add("SET PARSEONLY OFF; DELETE FROM [app].[AuditEvents];");
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].PreSql.Add("set  parseonly off\nDELETE FROM [app].[AuditEvents];");
        var report = await Validate(pair, plan);
        Assert.Equal(1, await Scalar(pair.TargetCs, "SELECT COUNT(*) FROM [app].[AuditEvents]"));
        Assert.Equal(["postSql[1]: SET PARSEONLY is not allowed"], report.GlobalErrors.Where(e => e.Contains("PARSEONLY", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal([$"preSql[{plan.Tasks[id].PreSql.Count - 1}]: SET PARSEONLY is not allowed"],
            report.TaskErrors[id].Where(e => e.Contains("PARSEONLY", StringComparison.OrdinalIgnoreCase)));

        // The refusal in ParseCheckAsync is the boundary and does not rely on CheckShape having run.
        await using var conn = new SqlConnection(pair.TargetCs);
        await conn.OpenAsync();
        Assert.Equal("SET PARSEONLY is not allowed", await SqlValidator.ParseCheckAsync(conn, "SET PARSEONLY OFF; DELETE FROM [app].[AuditEvents];", CancellationToken.None));
        Assert.Equal(1, await Scalar(pair.TargetCs, "SELECT COUNT(*) FROM [app].[AuditEvents]"));
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
}
