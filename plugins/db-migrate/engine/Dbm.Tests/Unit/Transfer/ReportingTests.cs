using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class ReportingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static TransferTaskRow Row(string id, string target, long source, long done, long errors, TaskValidation? v, int seconds) =>
        new(1, id, target, int.Parse(id[1..]), TransferTaskStatus.Done, source, 0, done, errors, null, null, T0, T0.AddSeconds(seconds),
            null, v is null ? null : Json.Serialize(v));

    /// <summary>Row(), but every field a carry-forward can leave absent is settable: the source count, the status and the timestamps.</summary>
    private static TransferTaskRow RowOf(string id, string target, long? source, long done, long errors, TaskValidation? v,
        TransferTaskStatus status = TransferTaskStatus.Done, int seconds = 1) =>
        new(1, id, target, int.Parse(id[1..]), status, source, 0, done, errors, null, null, T0, T0.AddSeconds(seconds),
            null, v is null ? null : Json.Serialize(v));

    private static TransferRunRow Run(RunStatus status = RunStatus.Completed, DateTimeOffset? startedAt = null) =>
        new(9, 3, status, new TransferOptions(), startedAt ?? T0, null, null);

    [Fact]
    public void Build_aggregates_tasks_validation_samples_and_notes()
    {
        var run = new TransferRunRow(1, 3, RunStatus.Completed, new TransferOptions { ErrorMode = "skip" }, T0, null, null);
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.Customers", 1000, 1000, 0, new TaskValidation(true, 1000, 0, 0, 1000, [new ChecksumResult("Email", true, 5, 5)], null), 4),
            Row("T02", "app.Orders", 3005, 3000, 5, new TaskValidation(true, 3005, 5, 0, 3000, [], "rows were rejected"), 6),
        };
        var samples = Enumerable.Range(0, 7).Select(i => new ErrorRowEntry(i, 1, "T02", $"{{\"__k0\":{i}}}", "{}", $"bad {i}", T0)).ToList();
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, tasks, id => id == "T02" ? samples : [], T0.AddSeconds(10));

        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.Equal(10, report.DurationSec);
        Assert.Equal(4005, report.RowsSource);
        Assert.Equal(4000, report.RowsLoaded);
        Assert.Equal(5, report.RowsError);
        Assert.Equal(400, report.RowsPerSec);
        Assert.Equal(4, report.Tasks[0].DurationSec);
        Assert.Equal(FinalReportBuilder.MaxErrorSamples, report.Tasks[1].ErrorSamples.Count);
        Assert.Equal("{\"__k0\":0}", report.Tasks[1].ErrorSamples[0].Key);
        Assert.True(report.Tasks[0].CountMatch);
        Assert.True(report.Tasks[0].Checksums.Single().Match);
        Assert.Equal("rows were rejected", report.Tasks[1].ChecksumsSkipped);
        Assert.Contains(report.Notes, n => n.Contains("5 rows were rejected"));
        Assert.Contains(report.Notes, n => n.Contains("Row counts validated for all 2 tasks"));
        Assert.Contains(report.Notes, n => n.Contains("Checksums skipped for app.Orders: rows were rejected"));

        string json = Json.Serialize(report);
        Assert.Contains("\"status\":\"completed\"", json);
        Assert.Contains("\"errorSamples\":[", json);
        Assert.Equal("Transferred 4,000 of 4,005 rows into 2 tables in 10s (5 rejected); row counts validated, checksums 1/1 matched.",
            FinalReportBuilder.Summary(report));
    }

    [Fact]
    public void Mismatches_are_called_out_in_notes_and_summary()
    {
        var run = new TransferRunRow(2, 3, RunStatus.Completed, new TransferOptions(), T0, null, null);
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.A", 10, 9, 0, new TaskValidation(false, 10, 0, 0, 9, [new ChecksumResult("X", false, 1, 2)], null), 1),
        };
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1));
        Assert.Contains(report.Notes, n => n.Contains("Row counts do not match for: app.A"));
        Assert.Contains(report.Notes, n => n.Contains("app.A.X"));
        Assert.Contains("into 1 table in 1s; row counts MISMATCH, checksums 0/1 matched.", FinalReportBuilder.Summary(report));
    }

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(185, "3m 05s")]
    [InlineData(3720, "1h 02m")]
    public void Durations_are_compact(double seconds, string expected) => Assert.Equal(expected, FinalReportBuilder.Dur(seconds));

    [Fact]
    public void Preflight_passes_only_without_failed_error_checks()
    {
        var ok = new PreflightResult(3, T0, [new("a", true, "info", ""), new("b", false, "warning", "rows present")]);
        Assert.True(ok.Passed);
        var bad = ok with { Checks = [.. ok.Checks, new("c", false, "error", "no insert")] };
        Assert.False(bad.Passed);
    }

    [Fact]
    public void PlanCheck_flags_tasks_with_validation_errors()
    {
        var plan = new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = new() { ["T01"] = new TaskPlan { Target = "app.A", SourceQuery = "SELECT 1 AS [X]", Errors = ["bad column"] } },
        };
        var check = Preflight.PlanCheck(plan);
        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.Contains("T01", check.Detail);
        plan.Tasks["T01"].Errors.Clear();
        Assert.True(Preflight.PlanCheck(plan).Ok);
    }

    [Fact]
    public void Checksum_sql_casts_source_values_to_the_target_types_and_skips_unsupported_columns()
    {
        var task = new TaskPlan
        {
            Target = "app.Customers",
            SourceQuery = "SELECT s.[E] AS [Email] FROM [dbo].[CUST] AS s;",
            Columns = [new("Email", "Email"), new("Notes", "Notes"), new("DisplayName", "DisplayName")],
        };
        var shape = new TargetShape("app.Customers", [
            new TargetColumn("Email", "nvarchar", 240, 0, 0, false, false),
            new TargetColumn("Notes", "ntext", 16, 0, 0, false, false),
            new TargetColumn("DisplayName", "nvarchar", 202, 0, 0, false, true),
        ]);
        var cols = RunValidator.ChecksumColumns(task, shape);
        Assert.Equal("Email", cols.Single().Target);
        Assert.Equal("SELECT SUM(CAST(BINARY_CHECKSUM(CAST(q.[Email] AS nvarchar(120))) AS bigint)) AS [c0] FROM (\nSELECT s.[E] AS [Email] FROM [dbo].[CUST] AS s\n) AS q",
            RunValidator.SourceChecksumSql(task, cols));
        Assert.Equal("SELECT SUM(CAST(BINARY_CHECKSUM([Email]) AS bigint)) AS [c0] FROM [app].[Customers]",
            RunValidator.TargetChecksumSql("app.Customers", cols));
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // The absences. Every test below states a fact the report would otherwise be silent about, and the harm is always the same
    // shape: an operator reads a clean report for a migration nobody checked.
    // ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Harm: a task whose validation never ran has a null CountMatch, and Json.Options is WhenWritingNull - so the field simply is not
    /// in the JSON. A summary that reads "row counts validated" over a task nobody counted is the green light this whole task exists
    /// to withhold.
    /// </summary>
    [Fact]
    public void A_task_with_no_validation_is_named_instead_of_counted_as_validated()
    {
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.A", 10, 10, 0, new TaskValidation(true, 10, 0, 0, 10, [], null), 1),
            Row("T02", "app.B", 20, 20, 0, null, 1),
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1));

        Assert.Null(report.Tasks[1].CountMatch);
        Assert.False(report.Tasks[1].CountCompared);
        Assert.Equal(RunValidator.NoValidation, report.Tasks[1].ChecksumsSkipped);
        Assert.Contains(report.Notes, n => n.Contains("No validation was recorded for: app.B"));
        Assert.DoesNotContain(report.Notes, n => n.Contains("Row counts validated for all"));
        Assert.Contains("row counts NOT CONFIRMED", FinalReportBuilder.Summary(report));
        // and the task object itself says so, because a missing key says nothing
        Assert.Contains(RunValidator.NoValidation, Json.Serialize(report.Tasks[1]));
    }

    /// <summary>
    /// Harm (carry-forward 3): rows_source is null when a run was interrupted mid-PrepareAsync or a custom CountSql yielded no number.
    /// Printing 0 there tells the operator the source table was empty, so "0 of 0 rows transferred" reads as a complete migration.
    /// </summary>
    [Fact]
    public void An_unknown_source_count_is_never_printed_as_zero()
    {
        var tasks = new List<TransferTaskRow> { RowOf("T01", "app.A", null, 10, 0, null) };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1));

        Assert.Null(report.Tasks[0].RowsSource);
        Assert.Equal(1, report.TasksWithoutSource);
        Assert.Contains(report.Notes, n => n.Contains("source row count is unknown for: app.A"));
        Assert.Contains("of at least", FinalReportBuilder.Summary(report));
    }

    /// <summary>Harm (carry-forward 3): a rate divided by a duration nobody established reads as "0 rows/sec" for a run that moved
    /// millions, or as a real number for a run whose start time is missing.</summary>
    [Fact]
    public void A_run_without_a_start_time_reports_no_duration_and_no_rate()
    {
        var run = new TransferRunRow(9, 3, RunStatus.Completed, new TransferOptions(), null, null, null);
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, [Row("T01", "app.A", 10, 10, 0, null, 1)], _ => [], T0);

        Assert.Null(report.DurationSec);
        Assert.Null(report.RowsPerSec);
        Assert.Contains(report.Notes, n => n.Contains("no start time"));
        Assert.Contains("in an unknown time", FinalReportBuilder.Summary(report));
    }

    /// <summary>
    /// Harm (carry-forward 7, ruling 105): a crash between a chunk's commit and its checkpoint write loses the identity of a rejected
    /// row. rows_error still counts it, so the operator is told "5 rejected" and handed two rows, with nothing saying the other three
    /// are gone. 5.3 declares this at TASK end only, so a task that never finished never declares it - which is why the report must.
    /// </summary>
    [Fact]
    public void Rejected_rows_counted_but_never_recorded_are_named_beside_the_samples()
    {
        var samples = Enumerable.Range(0, 2).Select(i => new ErrorRowEntry(i, 1, "T01", null, "{}", $"bad {i}", T0)).ToList();
        var tasks = new List<TransferTaskRow> { RowOf("T01", "app.A", 10, 5, 5, null, TransferTaskStatus.Paused) };
        var report = FinalReportBuilder.Build(Run(RunStatus.Paused), RunStatus.Paused, tasks, _ => samples, T0.AddSeconds(1),
            null, _ => 2);

        Assert.Equal(2, report.Tasks[0].ErrorSamples.Count);
        Assert.Contains("3 of the 5 rejected rows were counted but never recorded", report.Tasks[0].ErrorSamplesNote);
        Assert.Contains(report.Notes, n => n.Contains("app.A") && n.Contains("never recorded"));
    }

    /// <summary>Harm: "0 error rows shown" for a task whose rows_error is not zero, with no reason given - even without a count to
    /// compare against, an empty sample list beside a non-zero counter has to say something.</summary>
    [Fact]
    public void No_rejected_row_shown_for_a_task_that_rejected_rows_is_explained_even_without_a_count()
    {
        var tasks = new List<TransferTaskRow> { RowOf("T01", "app.A", 10, 5, 5, null, TransferTaskStatus.Failed) };
        var report = FinalReportBuilder.Build(Run(RunStatus.Failed), RunStatus.Failed, tasks, _ => [], T0.AddSeconds(1));

        Assert.Empty(report.Tasks[0].ErrorSamples);
        Assert.Contains("none of the 5 rejected rows", report.Tasks[0].ErrorSamplesNote);
    }

    /// <summary>
    /// Harm (carry-forward 4, 5.3 review F9): a keyless task stopped by Cancel or Fail records status "paused" while the run records
    /// "cancelled". A reader of the task alone is told an operator paused it and that resuming will pick it up - neither is true.
    /// </summary>
    [Fact]
    public void A_paused_task_under_a_cancelled_run_says_the_run_ended_not_that_someone_paused_it()
    {
        var tasks = new List<TransferTaskRow>
        {
            RowOf("T01", "app.A", 10, 10, 0, null, TransferTaskStatus.Paused),
            RowOf("T02", "app.B", 10, 0, 0, null, TransferTaskStatus.Pending),
        };
        var report = FinalReportBuilder.Build(Run(RunStatus.Cancelled), RunStatus.Cancelled, tasks, _ => [], T0.AddSeconds(1));

        Assert.Contains("the run was cancelled", report.Tasks[0].StatusNote);
        Assert.Contains("did not finish", report.Tasks[0].StatusNote);
        Assert.Contains(report.Notes, n => n.Contains("app.A") && n.Contains("the run was cancelled"));
        Assert.Contains("never started", report.Tasks[1].StatusNote);
    }

    /// <summary>
    /// Harm (carry-forward 1): the run's own notes - a control table that was not ours, rejected rows counted but never recorded -
    /// live in RunContext.Notes and reach summary_json only through RunSummary. The moment this builder's output replaces it they
    /// drop out of the run's outcome record entirely.
    /// </summary>
    [Fact]
    public void Run_notes_are_carried_into_the_report_verbatim()
    {
        string note = "[dbo].[__dbm_checkpoint] in the target is not ours; it was left untouched (control_table_mismatch).";
        var tasks = new List<TransferTaskRow> { Row("T01", "app.A", 10, 10, 0, new TaskValidation(true, 10, 0, 0, 10, [], null), 1) };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1), [note, "  "]);

        Assert.Contains(note, report.Notes);                                   // verbatim, not paraphrased
        Assert.DoesNotContain("  ", report.Notes);                             // but a blank note is not a note
        Assert.Contains("control_table_mismatch", Json.Serialize(report));
    }

    /// <summary>
    /// Harm: a count that could not be compared - no rows_before, or no source count at all - must not read as a match. CountMatch is
    /// false there, so "false" would mean both "the numbers disagree" and "there were no numbers"; CountCompared and CountNote are
    /// what keep those apart, and the notes have to name them separately or a real mismatch is buried among unknowns.
    /// </summary>
    [Fact]
    public void Counts_that_could_not_be_compared_are_not_reported_as_matching_nor_as_a_mismatch()
    {
        var unknown = new TaskValidation(false, null, 0, null, 10, [], "disabled")
        {
            CountCompared = false,
            CountNote = "not compared: the target's row count before the run was never recorded",
        };
        var tasks = new List<TransferTaskRow>
        {
            RowOf("T01", "app.A", null, 10, 0, unknown),
            Row("T02", "app.B", 10, 9, 0, new TaskValidation(false, 10, 0, 0, 9, [], null) { CountNote = "expected 10, found 9" }, 1),
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1));

        Assert.False(report.Tasks[0].CountMatch);
        Assert.False(report.Tasks[0].CountCompared);
        Assert.True(report.Tasks[1].CountCompared);
        Assert.Contains(report.Notes, n => n.Contains("Row counts could not be compared for: app.A"));
        Assert.Contains(report.Notes, n => n.Contains("Row counts do not match for: app.B"));
        Assert.DoesNotContain(report.Notes, n => n.Contains("Row counts do not match for: app.A"));
        Assert.Contains("row counts MISMATCH", FinalReportBuilder.Summary(report));
    }

    /// <summary>
    /// Harm: BINARY_CHECKSUM cannot compare ntext, xml, a computed column or a column the target does not have, so those bindings are
    /// dropped from the comparison. "checksums 1/1 matched" over a table whose other three columns were never looked at is the same
    /// green light as no validation at all; ChecksumColumnsNote is what makes the drop visible.
    /// </summary>
    [Fact]
    public void Columns_that_cannot_be_checksummed_are_named_rather_than_silently_dropped()
    {
        var task = new TaskPlan
        {
            Target = "app.Customers",
            SourceQuery = "SELECT s.[E] AS [Email] FROM [dbo].[CUST] AS s;",
            Columns = [new("Email", "Email"), new("Notes", "Notes"), new("DisplayName", "DisplayName"), new("Gone", "Gone")],
        };
        var shape = new TargetShape("app.Customers", [
            new TargetColumn("Email", "nvarchar", 240, 0, 0, false, false),
            new TargetColumn("Notes", "ntext", 16, 0, 0, false, false),
            new TargetColumn("DisplayName", "nvarchar", 202, 0, 0, false, true),
        ]);

        var uncomparable = RunValidator.UncomparableColumns(task, shape);
        Assert.Equal(3, uncomparable.Count);
        Assert.Contains(uncomparable, c => c.Contains("Notes") && c.Contains("ntext"));
        Assert.Contains(uncomparable, c => c.Contains("DisplayName") && c.Contains("computed"));
        Assert.Contains(uncomparable, c => c.Contains("Gone") && c.Contains("not a column of the target"));

        var v = new TaskValidation(true, 10, 0, 0, 10, [new ChecksumResult("Email", true, 1, 1)], null)
        {
            ChecksumColumnsNote = "3 of 4 bound columns were not compared: " + string.Join(", ", uncomparable) + ".",
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, [Row("T01", "app.Customers", 10, 10, 0, v, 1)],
            _ => [], T0.AddSeconds(1));
        Assert.Contains(report.Notes, n => n.Contains("app.Customers") && n.Contains("were not compared") && n.Contains("ntext"));
    }

    /// <summary>A plan with no tasks has no task with a validation error, so the brief's wording passes it. It then reaches
    /// CreateRun, which refuses a run with no tasks (ruling L2) - an exception where a preflight error belongs (5.1 concern 2).</summary>
    [Fact]
    public void PlanCheck_refuses_a_plan_with_no_tasks()
    {
        var check = Preflight.PlanCheck(new SqlPlanPayload());
        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.Contains("no tasks", check.Detail);
    }

    /// <summary>
    /// Harm: when a connection cannot be opened, every check behind it is simply never added to the list. The screen then shows one
    /// red connection line above a set of checks that all passed - and nothing at all about the target's tables, permissions or
    /// contents, which a reader takes for "nothing wrong there". A check that did not run must not look like one that passed.
    /// <para>No SQL Server is touched: with no connection string saved, preflight never opens one.</para>
    /// </summary>
    [Fact]
    public async Task Preflight_lists_the_checks_it_could_not_run_instead_of_leaving_them_out()
    {
        using var svc = new XferServices();
        var s = svc.Services;
        var plan = new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = new() { ["T01"] = new TaskPlan { Target = "app.A", SourceQuery = "SELECT 1 AS [X]", Columns = [new("X", "X")] } },
        };
        s.Artifacts.Add(PhaseName.Sql, 0, Json.Serialize(plan), "script", null);
        s.Phases.SetCurrentVersion(PhaseName.Sql, 0);
        s.Phases.SetApproved(PhaseName.Sql, 0, null);

        var result = await Preflight.RunAsync(s, new TransferOptions(), default);

        Assert.False(result.Passed);
        Assert.Equal(0, result.SqlVersion);
        Assert.False(Check(result, "source_connection").Ok);
        Assert.False(Check(result, "target_connection").Ok);
        foreach (var name in new[] { "target_checks", "estimated_rows", "schema_drift" })
        {
            var notRun = Check(result, name);
            Assert.False(notRun.Ok);
            Assert.Contains("not", notRun.Detail, StringComparison.OrdinalIgnoreCase);
        }
        // and nothing in the list is a target check that "passed"
        Assert.DoesNotContain(result.Checks, c => c.Name is "target_tables" or "insert_permission" or "control_table" or "target_rows");
        Assert.All(result.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Detail)));
    }

    private static PreflightCheck Check(PreflightResult result, string name) => result.Checks.Single(c => c.Name == name);
}
