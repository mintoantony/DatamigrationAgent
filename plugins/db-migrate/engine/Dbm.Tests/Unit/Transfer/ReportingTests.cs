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

    /// <summary>Ruling 208: a report's error sample carries the server's number beside the text, and a row recorded without one (before
    /// migration step 2, or a client-side failure) carries none - not a 0. <b>Harm:</b> the number the engine now stores is lost on the way
    /// to the one screen that lists the rejected rows.</summary>
    [Fact]
    public void An_error_sample_carries_the_error_number_and_a_missing_one_is_absent()
    {
        var tasks = new List<TransferTaskRow> { Row("T02", "app.Orders", 3005, 3003, 2, null, 6) };
        var rows = new List<ErrorRowEntry>
        {
            new(1, 1, "T02", "{\"__k0\":1}", "{}", "FK conflict", T0) { ErrorNumber = 547 },
            new(2, 1, "T02", "{\"__k0\":2}", "{}", "client-side truncation", T0),
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => rows, T0.AddSeconds(10), [], _ => 2);

        var samples = report.Tasks[0].ErrorSamples;
        Assert.True(samples[0].ErrorNumber == 547, "the report's error sample lost the row's error number");
        Assert.Null(samples[1].ErrorNumber);
        string json = Json.Serialize(samples);
        Assert.Equal("[{\"key\":\"{\\\"__k0\\\":1}\",\"error\":\"FK conflict\",\"errorNumber\":547},"
                     + "{\"key\":\"{\\\"__k0\\\":2}\",\"error\":\"client-side truncation\"}]", json);
    }

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

    /// <summary>Ruling 186 (open item 27): over a target that already held rows the counts balance rows added - a doubled keyless table
    /// balances too - so the headline says what was compared instead of an unqualified "validated".</summary>
    [Fact]
    public void A_target_that_was_not_empty_before_the_run_is_named_in_the_headline_instead_of_validated()
    {
        var run = new TransferRunRow(1, 3, RunStatus.Completed, new TransferOptions { ErrorMode = "skip" }, T0, null, null);
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.Customers", 1000, 1000, 0, new TaskValidation(true, 1000, 0, 0, 1000, [], "disabled"), 4),
            Row("T02", "app.AuditEvents", 5000, 5000, 0, new TaskValidation(true, 5000, 0, 5000, 10000, [], "target table was not empty before the run"), 6)
                with { RowsBefore = 5000 },
        };

        var report = FinalReportBuilder.Build(run, RunStatus.Completed, tasks, _ => [], T0.AddSeconds(10));
        string headline = FinalReportBuilder.Summary(report);

        Assert.True(headline == "Transferred 6,000 of 6,000 rows into 2 tables in 10s; target tables were not empty before this run; counts compare rows added.",
            "headline over a target that already held rows: " + headline);
        Assert.Contains(report.Notes, n => n.StartsWith("Target tables were not empty before this run: app.AuditEvents (5,000).", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Notes, n => n.Contains("Row counts validated", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ruling 192 (open item 30). A task that loaded none of a non-empty source balances its counts - 3 005 source, 3 005 rejected,
    /// 0 added - so "row counts validated" was true to the letter over a table that received nothing. <b>Harm:</b> the 5.7 reviewer's
    /// wrong FK mapping completed under "row counts validated, checksums 23/23 matched" with two tables empty.
    /// </summary>
    [Fact]
    public void A_task_that_loaded_nothing_of_a_non_empty_source_is_named_in_the_headline_instead_of_validated()
    {
        var run = new TransferRunRow(1, 3, RunStatus.Completed, new TransferOptions { ErrorMode = "skip" }, T0, null, null);
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.Customers", 1000, 1000, 0, new TaskValidation(true, 1000, 0, 0, 1000, [new ChecksumResult("Email", true, 5, 5)], null), 4),
            Row("T02", "app.Orders", 3005, 0, 3005, new TaskValidation(true, 3005, 3005, 0, 0, [], "rows were rejected"), 6),
            Row("T03", "app.Empty", 0, 0, 0, new TaskValidation(true, 0, 0, 0, 0, [], null), 1),   // an empty source is not "loaded nothing"
        };
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, tasks, _ => [], T0.AddSeconds(10));
        string headline = FinalReportBuilder.Summary(report);

        Assert.True(headline == "Transferred 1,000 of 4,005 rows into 3 tables in 10s (3,005 rejected); app.Orders loaded 0 of 3,005 rows, "
                               + "checksums 1/1 matched.",
            "headline over a task that loaded nothing: " + headline);
        Assert.DoesNotContain(report.Notes, n => n.Contains("validated for all", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.StartsWith("app.Orders loaded 0 of 3,005 source rows (3,005 rejected).", StringComparison.Ordinal));

        // Beside a mismatch the word "validated" is already gone; the task is still named.
        var mixed = tasks.Select(t => t.TaskId == "T01" ? t with { ValidationJson = Json.Serialize(new TaskValidation(false, 1000, 0, 0, 999, [], "disabled")) } : t).ToList();
        string mixedHeadline = FinalReportBuilder.Summary(FinalReportBuilder.Build(run, RunStatus.Completed, mixed, _ => [], T0.AddSeconds(10)));
        Assert.True(mixedHeadline.Contains("row counts MISMATCH; app.Orders loaded 0 of 3,005 rows", StringComparison.Ordinal),
            "a task that loaded nothing is not named beside a mismatch: " + mixedHeadline);
    }

    /// <summary>
    /// Open item 44: the report carries, on each task that loaded no row of a non-empty source, the sentence its notes carry, so the
    /// Report screen can name the task in a warning without wording one of its own - and the re-run into a table that already held
    /// rows (Ruling 186, exempted by Ruling 201 when its rejects are duplicate keys) is worded here, once. <b>Harm:</b> the screen
    /// shows green "every task matches" over a table that received nothing, or tells a confirmed re-run its mapping is wrong.
    /// </summary>
    [Fact]
    public void A_task_that_loaded_nothing_carries_its_note_and_a_rerun_is_worded_as_one()
    {
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.Customers", 1000, 1000, 0, new TaskValidation(true, 1000, 0, 0, 1000, [], null), 4),
            Row("T02", "app.Orders", 3005, 0, 3005, new TaskValidation(true, 3005, 3005, 0, 0, [], "rows were rejected"), 6),
            Row("T03", "app.Products", 200, 0, 200, new TaskValidation(true, 200, 200, 0, 0, [], "rows were rejected"), 2) with { RowsBefore = 200 },
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => [], T0.AddSeconds(10));

        Assert.Null(report.Tasks[0].LoadedNothingNote);
        string? orders = report.Tasks[1].LoadedNothingNote;
        Assert.True(orders is not null && orders.StartsWith("app.Orders loaded 0 of 3,005 source rows (3,005 rejected).", StringComparison.Ordinal)
                    && orders.Contains("the mapping or SQL is the likelier cause", StringComparison.Ordinal),
            "the task that loaded nothing does not carry its note: " + orders);
        string? products = report.Tasks[2].LoadedNothingNote;
        Assert.True(products == "app.Products loaded 0 of 200 source rows (200 rejected). The table already held 200 rows before this "
                                + "run: if the rejected rows are duplicate keys, those rows were already there; any other error points at "
                                + "the mapping or SQL.",
            "the re-run into a table that already held rows is not worded as one: " + products);
        Assert.Contains(orders, report.Notes);
        Assert.Contains(products, report.Notes);
        Assert.Contains("\"loadedNothingNote\":", Json.Serialize(report.Tasks[1]), StringComparison.Ordinal);
    }

    /// <summary>Review F7: a task that loaded nothing and rejected nothing - the source shrank between count and read - is not told
    /// that "every row is rejected". <b>Harm:</b> the note sends the operator to rejected rows that do not exist.</summary>
    [Fact]
    public void A_task_that_loaded_nothing_and_rejected_nothing_is_not_described_as_rejected()
    {
        var tasks = new List<TransferTaskRow> { Row("T01", "app.Gone", 40, 0, 0, new TaskValidation(false, 40, 0, 0, 0, [], null), 1) };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1));
        string note = report.Notes.Single(n => n.StartsWith("app.Gone loaded 0 of 40 source rows", StringComparison.Ordinal));
        Assert.True(!note.Contains("rejected)", StringComparison.Ordinal) && note.Contains("source query returned no rows", StringComparison.Ordinal),
            "a task that rejected nothing was described as having its rows rejected: " + note);
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
    /// Harm (carry-forward 7, ruling 105): a crash between a chunk's commit and its rejected rows being written loses the identity of
    /// a rejected row. rows_error still counts it, so the operator is told "5 rejected" and handed two rows, with nothing saying the
    /// other three are gone.
    /// <para>This is a <b>harm test</b>, and its rig is the shape production actually produces: a <c>Done</c> task under a
    /// <c>Completed</c> run, with an <c>errorRowCount</c> supplied - which is exactly what <c>FinishCompletedAsync</c> hands
    /// <see cref="FinalReportBuilder.Build"/>. Ruling 105's crash window leaves precisely this state behind.</para>
    /// </summary>
    [Fact]
    public void Rejected_rows_counted_but_never_recorded_are_named_beside_the_samples()
    {
        var samples = Enumerable.Range(0, 2).Select(i => new ErrorRowEntry(i, 1, "T01", null, "{}", $"bad {i}", T0)).ToList();
        var tasks = new List<TransferTaskRow> { RowOf("T01", "app.A", 10, 5, 5, null) };      // Done, under a Completed run
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, tasks, _ => samples, T0.AddSeconds(1), null, _ => 2);

        Assert.Equal(TransferTaskStatus.Done, report.Tasks[0].Status);
        Assert.Equal(2, report.Tasks[0].ErrorSamples.Count);
        Assert.Contains("3 of the 5 rejected rows were counted but never recorded", report.Tasks[0].ErrorSamplesNote);
        Assert.Contains(report.Notes, n => n.Contains("app.A") && n.Contains("never recorded"));
    }

    /// <summary>
    /// <b>Contract assertion, not a harm test — its trigger is unreachable from this task</b> (rulings 104, 112, 121). The only
    /// production caller of <see cref="FinalReportBuilder.Build"/> is <c>TransferEngine.FinishCompletedAsync</c>, which always passes
    /// an <c>errorRowCount</c>, so the null-count arm below cannot be entered today. It exists because <c>Build</c> is public and its
    /// count argument is optional: a 5.5 caller that omits it must still not be shown "0 rejected rows" beside a non-zero rows_error
    /// with no reason. Deleting the arm fails only this test, and that is the recorded, intended outcome.
    /// </summary>
    [Fact]
    public void No_rejected_row_shown_is_explained_even_without_a_count_contract_assertion_trigger_unreachable_from_this_task()
    {
        var tasks = new List<TransferTaskRow> { RowOf("T01", "app.A", 10, 5, 5, null, TransferTaskStatus.Failed) };
        var report = FinalReportBuilder.Build(Run(RunStatus.Failed), RunStatus.Failed, tasks, _ => [], T0.AddSeconds(1));

        Assert.Empty(report.Tasks[0].ErrorSamples);
        Assert.Contains("none of the 5 rejected rows", report.Tasks[0].ErrorSamplesNote);
    }

    /// <summary>
    /// <b>Contract assertion, not a harm test — its trigger is unreachable from this task</b> (rulings 104, 112, 121). Carry-forward 4
    /// is real (a keyless task stopped by Cancel records "paused" while the run records "cancelled", 5.3 review F9), but
    /// <see cref="FinalReportBuilder.Build"/>'s only production caller runs after <c>allDone</c> and always passes
    /// <c>RunStatus.Completed</c>, so a completed run cannot contain a paused task and no <c>(Paused, Cancelled)</c> pair can occur
    /// today. The join exists for the moment 5.5 builds a report for a cancelled or failed run; until then, deleting this arm fails
    /// only this test, and that is the recorded, intended outcome.
    /// </summary>
    [Fact]
    public void A_paused_task_under_a_cancelled_run_says_the_run_ended_contract_assertion_trigger_unreachable_from_this_task()
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

    /// <summary>
    /// Harm (F3, carry-forward 8): rows_source is a snapshot taken when the task's segment started. When the run never took one,
    /// validation counts the source again at the end - a second, later figure. The label for that lives on TaskValidation, but what an
    /// operator reads is the REPORT: without the label there, "rowsSource: 300" is taken for the snapshot, and a source that gained
    /// rows during the load looks like a source that was always that size.
    /// </summary>
    [Fact]
    public void A_source_count_measured_after_the_run_is_labelled_in_the_report()
    {
        var v = new TaskValidation(true, 300, 0, 0, 300, [], null)
        {
            RowsSourceNote = "counted after the run, not the snapshot this run takes when a task starts - the source may have changed in between",
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, [RowOf("T01", "app.A", null, 300, 0, v)], _ => [], T0.AddSeconds(1));

        Assert.Equal(300, report.Tasks[0].RowsSource);
        Assert.Equal(0, report.TasksWithoutSource);
        Assert.Contains("counted after the run", report.Tasks[0].RowsSourceNote);
        Assert.Contains("counted after the run", Json.Serialize(report.Tasks[0]));      // and it survives into summary_json
        Assert.Contains(report.Notes, n => n.Contains("app.A") && n.Contains("counted after the run"));
    }

    /// <summary>
    /// Harm (F8): the headline that becomes the Complete artifact summary said "checksums 5/5 matched" for a task binding six columns,
    /// one of which nobody could compare. The sentence naming it is in the notes, but the one line an operator reads first has to carry
    /// the shortfall or it reads as a clean sweep.
    /// </summary>
    [Fact]
    public void Summary_names_the_columns_nobody_compared()
    {
        var v = new TaskValidation(true, 7, 0, 0, 7, [new ChecksumResult("A", true, 1, 1)], null)
        {
            ChecksumColumnsNotCompared = 1,
            ChecksumColumnsNote = "1 of 2 bound columns were not compared: DT (datetime cannot be checksummed exactly).",
        };
        var report = FinalReportBuilder.Build(Run(), RunStatus.Completed, [Row("T01", "app.A", 7, 7, 0, v, 1)], _ => [], T0.AddSeconds(1));

        Assert.Equal(1, report.Tasks[0].ChecksumColumnsNotCompared);
        Assert.Contains("checksums 1/1 matched, 1 column not compared.", FinalReportBuilder.Summary(report));
    }

    /// <summary>F10: a sum of per-row checksums is not a set comparison - two changed rows can cancel. The report says so, once, and
    /// only when a checksum actually ran (saying it when none did would be noise about a comparison that never happened).</summary>
    [Fact]
    public void Notes_say_a_checksum_is_a_sum_only_when_a_checksum_ran()
    {
        var withSums = new TaskValidation(true, 7, 0, 0, 7, [new ChecksumResult("A", true, 1, 1)], null);
        var one = FinalReportBuilder.Build(Run(), RunStatus.Completed, [Row("T01", "app.A", 7, 7, 0, withSums, 1)], _ => [], T0.AddSeconds(1));
        Assert.Contains(one.Notes, n => n.Contains("equal sums do not prove identical rows"));

        var noSums = new TaskValidation(true, 7, 0, 0, 7, [], "disabled");
        var other = FinalReportBuilder.Build(Run(), RunStatus.Completed, [Row("T01", "app.A", 7, 7, 0, noSums, 1)], _ => [], T0.AddSeconds(1));
        Assert.DoesNotContain(other.Notes, n => n.Contains("equal sums do not prove identical rows"));
    }

    /// <summary>
    /// Harm (F6): "approved" and "its artifact is gone" are two different states, and collapsing them sends the operator to re-approve
    /// a plan they already approved - which cannot help, because the payload is what is missing.
    /// </summary>
    [Fact]
    public async Task An_approved_plan_whose_artifact_is_missing_is_not_reported_as_never_approved()
    {
        using var svc = new XferServices();
        var s = svc.Services;
        s.Artifacts.Add(PhaseName.Sql, 0, Json.Serialize(new SqlPlanPayload()), "script", null);
        s.Phases.SetCurrentVersion(PhaseName.Sql, 0);
        s.Phases.SetApproved(PhaseName.Sql, 0, null);
        s.Db.Execute("DELETE FROM artifact");                       // the payload is gone; the approval is not

        var result = await Preflight.RunAsync(s, new TransferOptions(), default);

        var check = Check(result, "sql_plan");
        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.Contains("artifact", check.Detail);
        Assert.DoesNotContain("not approved yet", check.Detail);
        Assert.False(result.Passed);
    }

    /// <summary>
    /// F5, the half that can be reached without a server: every check detail goes through the redaction pass, and a detail that is
    /// blank after it says so rather than becoming an invisible reason. The pass itself is pinned end to end by
    /// <c>PreflightTests.Server_messages_in_the_checklist_are_scrubbed_of_the_connection_secret</c>.
    /// </summary>
    [Fact]
    public void A_preflight_detail_is_scrubbed_and_a_blank_one_says_so()
    {
        Assert.Equal("cannot connect as ***", Preflight.Scrubbed("cannot connect as hunter2xyz", ["hunter2xyz"]));
        Assert.Contains("removed", Preflight.Scrubbed("", []));
        Assert.False(string.IsNullOrWhiteSpace(Preflight.Scrubbed("   ", ["hunter2xyz"])));
    }

    /// <summary>
    /// Ruling 139 (T5.6 fix round 1). A check the run could not carry out and a check that ran and found a fault are built apart here
    /// - <c>NotRun(...)</c> against <c>Err(...)</c> - but nothing carried the difference to the screen, which guessed it from the
    /// wording and got the engine's own <c>target_probe</c> error wrong. The flag is what the screen reads now, so it has to be set in
    /// exactly one builder: on every check that did not run, and on no check that did.
    /// </summary>
    [Fact]
    public async Task A_check_that_could_not_be_carried_out_is_flagged_and_a_check_that_failed_is_not()
    {
        using var svc = new XferServices();
        var s = svc.Services;
        s.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = { ["T01"] = new TaskPlan { Target = "app.A", SourceQuery = "SELECT 1 AS [X]", CountSql = "SELECT COUNT_BIG(*)" } },
        }), "script", null);
        s.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        s.Phases.SetApproved(PhaseName.Sql, 1, null);
        // No connections saved: the two connection checks fail outright, and everything behind them cannot be carried out.

        var result = await Preflight.RunAsync(s, new TransferOptions(), default);

        Assert.False(Check(result, "source_connection").NotRun);          // it ran; it found no connection
        Assert.False(Check(result, "target_connection").NotRun);
        Assert.True(Check(result, "schema_drift").NotRun);                // these could not run at all
        Assert.True(Check(result, "target_checks").NotRun);
        Assert.True(Check(result, "estimated_rows").NotRun);
        Assert.All(result.Checks.Where(c => c.Ok), c => Assert.False(c.NotRun));
        // Always serialised, so the screen reads a false rather than inferring it from a key that is not there.
        Assert.Contains("\"notRun\":true", Json.Serialize(Check(result, "target_checks")));
        Assert.Contains("\"notRun\":false", Json.Serialize(Check(result, "source_connection")));
    }

    private static PreflightCheck Check(PreflightResult result, string name) => result.Checks.Single(c => c.Name == name);
}
