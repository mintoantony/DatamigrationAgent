using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlValidatorUnitTests
{
    /// <summary>Open item 8 L2: the target connection must not be pooled, because the connection (and with it the #stg scaffold
    /// in tempdb) must end when this validation's connection closes - a pooled connection could hand the same physical
    /// connection, scaffold and all, to the next validation. Pinned via the connection-string builder the target `open` call
    /// receives, using the fake-opener seam (no live database needed).</summary>
    [Fact]
    public async Task Target_connection_disables_pooling_but_the_source_connection_is_untouched()
    {
        var captured = new List<string>();
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        const string sourceCs = "Server=nowhere;Database=x;Pooling=true";
        const string targetCs = "Server=nowhere;Database=y;Pooling=true";

        await SqlValidator.ValidateAsync(plan, sourceCs, targetCs, SampleCatalogs.Target(), null,
            (cs, _) => { captured.Add(cs); throw new InvalidOperationException("no server"); }, CancellationToken.None);

        Assert.Equal(2, captured.Count);
        Assert.Equal(sourceCs, captured[0]);   // the source connection keeps whatever pooling the caller configured
        var targetPooling = new SqlConnectionStringBuilder(captured[1]).Pooling;
        Assert.False(targetPooling, "the target connection must not be pooled (Pooling=true survived into it): " + captured[1]);
    }

    [Fact]
    public void CheckShape_reports_mode_staging_duplicates_and_go()
    {
        var task = new TaskPlan
        {
            Target = "app.Addresses",
            Mode = "staging_merge",
            SourceQuery = "SELECT 1 AS [City]\nGO",
            Columns = [new ColumnBinding("City", "City"), new ColumnBinding("City2", "city")],
        };
        var errors = SqlValidator.CheckShape(task);
        Assert.Contains("staging_merge requires stagingDdl", errors);
        Assert.Contains("staging_merge requires mergeSql", errors);
        Assert.Contains("City: bound more than once", errors);
        Assert.Contains("sourceQuery: GO batch separators are not allowed", errors);
    }

    [Fact]
    public void CheckShape_accepts_generated_tasks_and_go_inside_comments()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.All(plan.Tasks.Values, t => Assert.Empty(SqlValidator.CheckShape(t)));
        var task = plan.Tasks["T04"];
        task.PreSql.Add("-- GO live checklist\nUPDATE STATISTICS [app].[Addresses];");
        Assert.Empty(SqlValidator.CheckShape(task));
    }

    [Fact]
    public void Apply_keeps_generator_warnings_and_replaces_validator_warnings()
    {
        var plan = new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = { ["T01"] = new TaskPlan { Target = "app.X", Warnings = ["no key: single-transaction load", "validate: old"] } },
        };
        SqlValidator.Apply(plan, new ValidationReport(false, new() { ["T01"] = ["e1"] }, new() { ["T01"] = ["w1"] }, ["g1"], []));
        Assert.Equal(["e1"], plan.Tasks["T01"].Errors);
        Assert.Equal(["no key: single-transaction load", "validate: w1"], plan.Tasks["T01"].Warnings);
        Assert.Equal(["g1"], plan.Errors);

        SqlValidator.Apply(plan, new ValidationReport(true, new() { ["T01"] = [] }, new() { ["T01"] = [] }, [], [SqlValidator.SingleTaskGlobalWarning]), full: false);
        Assert.Empty(plan.Tasks["T01"].Errors);
        Assert.Equal(["no key: single-transaction load"], plan.Tasks["T01"].Warnings);
        Assert.Equal(["g1"], plan.Errors);
    }

    [Fact]
    public void Apply_replaces_only_prefixed_plan_warnings_and_only_on_a_full_report()
    {
        // M3: Ruling 18's discard warnings (no prefix) are the only notice that hand-written SQL was dropped.
        const string discard = "app.X: custom SQL discarded: the table is no longer generated";
        const string containsPrefix = "note: re-run validate: after editing";   // contains "validate: " but does not start with it
        List<string> Seeded() => [discard, "validate: postSql[0]: not checked: stale", containsPrefix];
        var plan = new SqlPlanPayload { Warnings = Seeded() };

        SqlValidator.Apply(plan, new ValidationReport(true, new(), new(), [], [SqlValidator.SingleTaskGlobalWarning]), full: false);
        Assert.Equal(Seeded(), plan.Warnings);   // (1) + (3): a partial run leaves plan.Warnings entirely alone, stale lines included

        SqlValidator.Apply(plan, new ValidationReport(true, new(), new(), [], ["preSql[1]: not checked: fresh"]));
        Assert.Equal([discard, containsPrefix, "validate: preSql[1]: not checked: fresh"], plan.Warnings);   // (1), (2), (4)

        SqlValidator.Apply(plan, new ValidationReport(true, new(), new(), [], ["preSql[1]: not checked: fresh"]));
        Assert.Equal([discard, containsPrefix, "validate: preSql[1]: not checked: fresh"], plan.Warnings);   // (2) replaced, not accumulated
    }

    /// <summary>Open item 8 L3, decision I-2: a connection that drops mid-run (InvalidOperationException, never SqlException) ends
    /// validation with a report, not an unhandled exception. A never-opened SqlConnection reproduces the same failure shape
    /// ("...requires an open and available Connection...") without a live database. Every task not yet reached must be marked
    /// not-checked, not left silently looking clean - the same rule the rest of this file already enforces for global statements.</summary>
    [Fact]
    public async Task A_connection_dropped_mid_validation_becomes_a_global_error_not_a_crash()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

        var report = await SqlValidator.ValidateAsync(plan, "Server=nowhere;Database=x", "Server=nowhere;Database=y",
            SampleCatalogs.Target(), null,
            (cs, _) => Task.FromResult(new SqlConnection(cs)),   // never opened: any command against it throws InvalidOperationException
            CancellationToken.None);

        Assert.False(report.Ok);
        Assert.Contains(report.GlobalErrors, e => e.StartsWith(SqlValidator.ValidationStoppedPrefix, StringComparison.Ordinal));
        Assert.All(plan.Tasks.Keys, id => Assert.Contains(report.TaskWarnings[id], w => w.Contains("not checked", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Connection_failure_messages_are_scrubbed_of_the_password()
    {
        // No real SqlClient message carries the password, so a synthetic failure does: this pins the Scrub, not the driver.
        const string password = "S3cret-Pa55word!";
        var cs = $"Server=nowhere;Database=x;User ID=u;Password={password}";
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var report = await SqlValidator.ValidateAsync(plan, cs, cs, SampleCatalogs.Target(), null,
            (connectionString, _) => throw new InvalidOperationException($"login failed for '{connectionString}'"), CancellationToken.None);
        Assert.Equal(2, report.GlobalErrors.Count(e => e.Contains("connection failed: login failed", StringComparison.Ordinal)));
        Assert.All(report.GlobalErrors, e => Assert.DoesNotContain(password, e, StringComparison.Ordinal));
        Assert.Contains(report.GlobalErrors, e => e.StartsWith("source connection failed: ", StringComparison.Ordinal) && e.Contains("***", StringComparison.Ordinal));
        Assert.Contains(report.GlobalErrors, e => e.StartsWith("target connection failed: ", StringComparison.Ordinal) && e.Contains("***", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_task_id_fails_before_connecting()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var report = await SqlValidator.ValidateAsync(plan, "Server=nowhere;Database=x", "Server=nowhere;Database=y",
            SampleCatalogs.Target(), "T99", CancellationToken.None);
        Assert.False(report.Ok);
        Assert.Equal(["unknown task T99"], report.GlobalErrors);
        Assert.Equal([SqlValidator.SingleTaskGlobalWarning], report.GlobalWarnings);
    }
}
