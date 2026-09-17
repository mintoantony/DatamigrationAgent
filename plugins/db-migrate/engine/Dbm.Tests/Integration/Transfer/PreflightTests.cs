using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class PreflightTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    private static PreflightCheck Check(IEnumerable<PreflightCheck> checks, string name) => checks.Single(c => c.Name == name);

    private static SqlPlanPayload PlanWithMissingTable()
    {
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T01"].IdentityInsert = true;
        plan.Tasks["T04"] = new TaskPlan { Target = "app.Missing", SourceQuery = "SELECT 1 AS [X]", Columns = [new("X", "X")] };
        plan.Order.Add("T04");
        return plan;
    }

    [Fact]
    public async Task Target_checks_report_missing_tables_existing_rows_and_permissions()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("INSERT app.Parent (Id, Name) VALUES (1, N'x');");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        var plan = PlanWithMissingTable();

        var checks = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions(), default);
        Assert.False(Check(checks, "target_tables").Ok);
        Assert.Equal("error", Check(checks, "target_tables").Severity);
        Assert.Contains("app.Missing", Check(checks, "target_tables").Detail);
        Assert.True(Check(checks, "insert_permission").Ok);
        Assert.True(Check(checks, "identity_insert_permission").Ok);
        Assert.True(Check(checks, "control_table").Ok);
        var rows = Check(checks, "target_rows");
        Assert.False(rows.Ok);
        Assert.Equal("warning", rows.Severity);
        Assert.Contains("app.Parent (1)", rows.Detail);
        Assert.DoesNotContain(checks, c => c.Name == "truncate_permission");

        var truncating = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions { TruncateTarget = true }, default);
        Assert.True(Check(truncating, "target_rows").Ok);
        Assert.Contains("emptied", Check(truncating, "target_rows").Detail);
        Assert.True(Check(truncating, "truncate_permission").Ok);
    }

    [Fact]
    public async Task Missing_permissions_are_errors()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("CREATE USER limited WITHOUT LOGIN; GRANT SELECT ON SCHEMA::app TO limited;");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new SqlCommand("EXECUTE AS USER = 'limited';", conn)) await cmd.ExecuteNonQueryAsync();

        var checks = await Preflight.TargetChecksAsync(conn, TransferEngineTests.Plan(), new TransferOptions(), default);
        Assert.False(Check(checks, "insert_permission").Ok);
        Assert.Contains("app.Parent", Check(checks, "insert_permission").Detail);
        Assert.False(Check(checks, "control_table").Ok);
        await using (var cmd = new SqlCommand("REVERT;", conn)) await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Source_estimate_counts_rows_per_task()
    {
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();
        var check = await Preflight.SourceEstimateAsync(conn, TransferEngineTests.Plan(), default);
        Assert.True(check.Ok);
        Assert.Contains("3,000 rows across 3 tasks", check.Detail);
        Assert.Contains("largest: app.Child 2,000", check.Detail);
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Harm: a table that does not exist is skipped by every later check in the loop, so "INSERT permission on every target table"
    /// and "All target tables are empty" are pronounced over a set that excludes it. The operator creates the missing table, re-runs,
    /// and discovers the permission problem the checklist said was not there.
    /// </summary>
    [Fact]
    public async Task Checks_that_could_not_cover_every_table_say_which_ones_they_skipped()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre_skip");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();

        var plan = PlanWithMissingTable();
        plan.Tasks["T04"].IdentityInsert = true;          // the missing table is one the identity check would have had to cover
        var checks = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions(), default);

        Assert.True(Check(checks, "insert_permission").Ok);
        Assert.Contains("app.Missing", Check(checks, "insert_permission").Detail);
        Assert.Contains("app.Missing", Check(checks, "target_rows").Detail);
        Assert.Contains("app.Missing", Check(checks, "identity_insert_permission").Detail);
    }

    /// <summary>
    /// Harm: a source count that comes back as NULL or as no row at all - a CountSql the plan supplies verbatim can do either - is not
    /// a count of zero. Added into the total it understates the volume the operator is about to move, and the estimate says nothing
    /// about having failed.
    /// </summary>
    [Fact]
    public async Task A_source_count_that_yields_no_number_is_not_counted_as_zero_rows()
    {
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T03"].CountSql = "SELECT CAST(NULL AS bigint);";

        var check = await Preflight.SourceEstimateAsync(conn, plan, default);

        Assert.False(check.Ok);
        Assert.Equal("warning", check.Severity);
        Assert.Contains("2,300 rows across 2 tasks", check.Detail);      // 300 + 2000; the 700 of T03 were never established
        Assert.Contains("T03", check.Detail);
        Assert.Contains("no number", check.Detail);
    }
}
