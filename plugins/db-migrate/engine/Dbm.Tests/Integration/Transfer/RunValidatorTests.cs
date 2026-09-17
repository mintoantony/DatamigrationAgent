using System.Diagnostics;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class RunValidatorTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    [Fact]
    public async Task Completed_run_stores_validation_and_a_final_report()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var engine = new TransferEngine(svc.Services, TransferEngineTests.Plan(), fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ChunkSize = 500, ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        var v = svc.Services.Transfers.Tasks(runId).ToDictionary(t => t.TaskId, t => Json.Deserialize<TaskValidation>(t.ValidationJson!));
        Assert.All(v.Values, x => Assert.True(x.CountMatch));
        Assert.Equal(new[] { "Id", "Name" }, v["T01"].Checksums.Select(c => c.Column));
        Assert.All(v["T01"].Checksums, c => Assert.True(c.Match));
        Assert.Single(v["T03"].Checksums);
        Assert.True(v["T03"].Checksums[0].Match);
        Assert.Empty(v["T02"].Checksums);
        Assert.Equal("rows were rejected", v["T02"].ChecksumsSkipped);
        Assert.Equal(1998, v["T02"].RowsAfter);

        var report = Json.Deserialize<FinalReport>(svc.Services.Transfers.GetRun(runId)!.SummaryJson!);
        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.Equal(2, report.RowsError);
        Assert.Equal(2998, report.RowsLoaded);
        Assert.Equal(2, report.Tasks.Single(t => t.TaskId == "T02").ErrorSamples.Count);
    }

    [Fact]
    public async Task Checksums_catch_a_changed_value_and_counts_catch_an_extra_row()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.Tasks.Remove("T02");
        plan.Order.Remove("T02");
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        await tgt.ExecAsync("UPDATE app.Parent SET Name = N'changed' WHERE Id = 7; INSERT app.Log (Msg) VALUES (N'extra');");
        await using var src = new SqlConnection(fx.Src.ConnectionString);
        await using var dst = new SqlConnection(tgt.ConnectionString);
        await src.OpenAsync();
        await dst.OpenAsync();
        var rows = svc.Services.Transfers.Tasks(runId).ToDictionary(t => t.TaskId);

        var parent = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], rows["T01"], true, default);
        Assert.True(parent.CountMatch);
        Assert.True(parent.Checksums.Single(c => c.Column == "Id").Match);
        Assert.False(parent.Checksums.Single(c => c.Column == "Name").Match);

        var log = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T03"], rows["T03"], true, default);
        Assert.False(log.CountMatch);
        Assert.Equal(701, log.RowsAfter);

        var nonEmpty = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], rows["T01"] with { RowsBefore = 5 }, true, default);
        Assert.Equal("target table was not empty before the run", nonEmpty.ChecksumsSkipped);
        Assert.Empty(nonEmpty.Checksums);
    }

    /// <summary>
    /// Harm: rows_before and rows_source are both nullable, and defaulting either to 0 makes the arithmetic balance out of nothing.
    /// With rows_before taken as 0 a target that already held rows is declared to have started empty, so the checksums run and
    /// "matched" is reported over a table this run only partly wrote; with rows_source taken as 0, "0 - 0 == after - before" is
    /// arranged to hold and the counts are pronounced correct without a single number behind them.
    /// </summary>
    [Fact]
    public async Task Counts_this_run_never_recorded_are_not_defaulted_to_zero_and_matched()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_unknown");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.Tasks.Remove("T02");
        plan.Order.Remove("T02");
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        await using var src = new SqlConnection(fx.Src.ConnectionString);
        await using var dst = new SqlConnection(tgt.ConnectionString);
        await src.OpenAsync();
        await dst.OpenAsync();
        var row = svc.Services.Transfers.Tasks(runId).Single(t => t.TaskId == "T01");

        // No rows_before: the run never recorded what was already in the target, so the rows it added cannot be isolated.
        var noBefore = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], row with { RowsBefore = null }, true, default);
        Assert.False(noBefore.CountMatch);
        Assert.False(noBefore.CountCompared);
        Assert.Null(noBefore.RowsBefore);
        Assert.Contains("never recorded", noBefore.CountNote);
        Assert.Empty(noBefore.Checksums);
        Assert.Contains("never recorded", noBefore.ChecksumsSkipped);

        // No rows_source snapshot: validation counts the source now, and labels that number as the later figure it is.
        var noSource = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], row with { RowsSource = null }, true, default);
        Assert.True(noSource.CountMatch);
        Assert.Equal(300, noSource.RowsSource);
        Assert.Contains("counted after the run", noSource.RowsSourceNote);

        // Not even a countable source: the count is unknown, and unknown is not zero.
        var broken = plan.Tasks["T01"];
        broken.CountSql = "SELECT CAST(NULL AS bigint);";
        var noNumber = await RunValidator.ValidateTaskAsync(src, dst, broken, row with { RowsSource = null }, true, default);
        Assert.Null(noNumber.RowsSource);
        Assert.False(noNumber.CountMatch);
        Assert.False(noNumber.CountCompared);
        Assert.Contains("no source row count", noNumber.CountNote);

        // F9: the recount runs plan-supplied SQL inside the completion hook. A fault there - including the 120 s timeout that now
        // bounds it - is a note on the validation, not an exception out of a method the engine calls on the success path.
        broken.CountSql = "SELECT 1/0;";
        var faulted = await RunValidator.ValidateTaskAsync(src, dst, broken, row with { RowsSource = null }, true, default);
        Assert.Null(faulted.RowsSource);
        Assert.False(faulted.CountCompared);
        Assert.Contains("counting it now failed", faulted.RowsSourceNote);
        Assert.Contains("Divide by zero", faulted.RowsSourceNote);
    }

    /// <summary>
    /// Harm (N3, F9): the recount runs plan-supplied CountSql inside the completion hook, on a connection the run holds, with the run
    /// lock held. Unbounded (CommandTimeout = 0) a slow or blocked count hangs the hook for as long as the server takes, while the note
    /// it writes still tells the operator the count "failed within 120 s". A bound of 120 s cannot be witnessed without waiting two
    /// minutes, so the bound is injected here and the note's wording is derived from the same value: the sentence cannot claim a bound
    /// the query was not given.
    /// <para>The rig: PrepareAsync records rows_source as null (its CountSql yields NULL), the plan's CountSql is then swapped for a
    /// three-second one as the last chunk commits, and the completion hook's recount is the only thing left that runs it.</para>
    /// </summary>
    [Fact]
    public async Task The_in_hook_source_recount_is_bounded_and_a_timeout_becomes_a_note()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_recount");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        foreach (var id in new[] { "T02", "T03" })
        {
            plan.Tasks.Remove(id);
            plan.Order.Remove(id);
        }
        var task = plan.Tasks["T01"];
        task.CountSql = "SELECT CAST(NULL AS bigint);";                     // no source count is ever recorded for this run
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });

        var afterLastChunk = new Stopwatch();
        var control = new TransferControl();
        control.ChunkCommitted += _ =>
        {
            task.CountSql = "WAITFOR DELAY '00:00:03'; SELECT CAST(1 AS bigint);";
            afterLastChunk.Restart();
        };

        RunValidator.RecountTimeoutSecOverride = 1;
        try
        {
            var outcome = await engine.RunAsync(runId, control, default);
            afterLastChunk.Stop();

            // The bound first, because it is the thing with no other witness: if it is not applied the recount simply succeeds after
            // the full three seconds, and every assertion below would then be testing an unbounded query's happy path.
            Assert.True(afterLastChunk.Elapsed < TimeSpan.FromSeconds(2.5),
                $"the hook took {afterLastChunk.Elapsed.TotalSeconds:F1}s after the last chunk for a recount bounded at 1s, so the "
                + "bound was not applied and the query ran to its full three-second delay");
            Assert.Equal(RunStatus.Completed, outcome.Status);              // a bounded wait is a note, never a failed run
            var v = Json.Deserialize<TaskValidation>(svc.Services.Transfers.Tasks(runId).Single().ValidationJson!);
            Assert.Null(v.RowsSource);
            Assert.False(v.CountCompared);
            Assert.Contains("failed within 1 s", v.RowsSourceNote);
        }
        finally
        {
            RunValidator.RecountTimeoutSecOverride = null;
        }
    }

    /// <summary>
    /// Harm (F7): ValidateTaskAsync is public and 5.5 can hand it a task that has not finished. Its target holds part of a load, so
    /// checksums over it mismatch and rowsSource - rowsError never equals rowsAfter - rowsBefore. Both would be reported as findings
    /// against the data: a false alarm on a run that is simply not done, which costs the operator the same investigation a real
    /// mismatch does and teaches them to discount the ones that matter.
    /// </summary>
    [Fact]
    public async Task A_task_that_did_not_finish_is_not_checksummed_and_its_counts_are_not_compared()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_unfinished");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.Tasks.Remove("T02");
        plan.Order.Remove("T02");
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        await using var src = new SqlConnection(fx.Src.ConnectionString);
        await using var dst = new SqlConnection(tgt.ConnectionString);
        await src.OpenAsync();
        await dst.OpenAsync();
        var row = svc.Services.Transfers.Tasks(runId).Single(t => t.TaskId == "T01");

        var v = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], row with { Status = TransferTaskStatus.Paused }, true, default);

        Assert.Empty(v.Checksums);
        Assert.Contains("did not finish", v.ChecksumsSkipped);
        Assert.Contains("paused", v.ChecksumsSkipped);
        Assert.False(v.CountCompared);
        Assert.False(v.CountMatch);
        Assert.Contains("did not finish", v.CountNote);
        Assert.Equal(300, v.RowsAfter);          // the numbers are still reported; it is the verdict that is withheld
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Harm: validation runs after the last row is loaded and the run's own PostSql has executed - operator-authored SQL that may well
    /// have dropped a table the plan loaded into. An exception out of the validator is caught by RunAsync's general handler, which
    /// records the run as FAILED. A migration that moved every row would then be reported as a failure because the thing meant to
    /// confirm it could not run, sending an operator to re-run a migration that was already correct. Validation reports; it never
    /// fails the run - but "never fails the run" is not permission to go quiet, so the report has to name what it could not check.
    /// </summary>
    [Fact]
    public async Task Validation_that_cannot_run_is_named_and_never_fails_a_run_that_loaded_every_row()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_drop");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.PostSql = ["DROP TABLE app.Log;"];                      // the target T03 loaded into is gone before validation looks
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ChunkSize = 1000, Parallelism = 1, ErrorMode = "skip" });

        var outcome = await engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Completed, outcome.Status);                                  // every row loaded: this is not a failure
        Assert.Null(outcome.Error);
        Assert.Equal(300, await tgt.CountAsync("app.Parent"));
        Assert.Equal(1998, await tgt.CountAsync("app.Child"));

        var tasks = svc.Services.Transfers.Tasks(runId).ToDictionary(t => t.TaskId);
        Assert.NotNull(tasks["T01"].ValidationJson);                                        // the other tasks were still validated
        Assert.Null(tasks["T03"].ValidationJson);

        var report = Json.Deserialize<FinalReport>(svc.Services.Transfers.GetRun(runId)!.SummaryJson!);
        var t03 = report.Tasks.Single(t => t.TaskId == "T03");
        Assert.Null(t03.CountMatch);
        Assert.False(t03.CountCompared);
        Assert.Equal(RunValidator.NoValidation, t03.ChecksumsSkipped);
        Assert.Contains(report.Notes, n => n.Contains("app.Log") && n.Contains("could not be validated"));
        Assert.Contains(report.Notes, n => n.Contains("No validation was recorded for: app.Log"));
        Assert.Contains("row counts NOT CONFIRMED", FinalReportBuilder.Summary(report));
    }

    /// <summary>
    /// Ruling 103 gives a run an exclusive sp_getapplock for its lifetime and refuses a second runner. Validation reads; it does not
    /// run, so it must neither take that lock nor be refused by one - otherwise a report of a finished run could not be rendered while
    /// any other run against the same target was in flight, and a UI would show an error where a report belongs.
    /// </summary>
    [Fact]
    public async Task Validation_reads_a_target_whose_run_lock_another_session_holds()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_lock");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.Tasks.Remove("T02");
        plan.Order.Remove("T02");
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        await using var held = await RunLock.AcquireAsync(tgt.ConnectionString, runId, default);   // a live runner's lock
        await using var src = new SqlConnection(fx.Src.ConnectionString);
        await using var dst = new SqlConnection(tgt.ConnectionString);
        await src.OpenAsync();
        await dst.OpenAsync();
        var row = svc.Services.Transfers.Tasks(runId).Single(t => t.TaskId == "T01");

        var v = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], row, true, default);
        Assert.True(v.CountMatch);
        Assert.Equal(300, v.RowsAfter);
    }

    /// <summary>
    /// Harm (carry-forward 12, ruling 89): a keyless staging task whose source returns no rows never reaches the loader, so no merge
    /// ever runs. An accumulator seeded at zero reports ", merge affected 0" for it - a merge that never executed, described as one
    /// that ran and moved nothing. The status is the authority; the count is only ever read through it. This is the keyless half of
    /// that rule, which had no test of its own.
    /// </summary>
    [Fact]
    public async Task A_keyless_staging_task_with_no_source_rows_says_the_merge_did_not_run()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_merge");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("CREATE TABLE app.Empty (Msg nvarchar(100) NOT NULL);");
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.Tasks.Clear();
        plan.Order.Clear();
        const string query = "SELECT s.[Msg] AS [Msg] FROM [dbo].[Log] AS s WHERE 1 = 0";
        plan.Tasks["T01"] = new TaskPlan
        {
            Target = "app.Empty",
            Mode = "staging_merge",
            SourceQuery = query,
            CountSql = $"SELECT COUNT_BIG(*) FROM ({query}) AS q",
            Columns = [new("Msg", "Msg")],
            StagingDdl = "CREATE TABLE #stg (Msg nvarchar(100) NULL);",
            MergeSql = "INSERT app.Empty (Msg) SELECT Msg FROM #stg;",
        };
        plan.Order.Add("T01");
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ChunkSize = 500, Parallelism = 1, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        var lines = svc.Sink.Events.Where(e => e.Type == "log").Select(e => e.Payload?.ToString() ?? "")
            .Where(p => p.Contains("app.Empty") && p.Contains("committed")).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains("the merge did not run", l));
        Assert.All(lines, l => Assert.DoesNotContain("merge affected 0", l));

        // and the validator reads a real zero: 0 at source, 0 in the target, counts compared and matching.
        var v = Json.Deserialize<TaskValidation>(svc.Services.Transfers.Tasks(runId).Single().ValidationJson!);
        Assert.True(v.CountMatch);
        Assert.True(v.CountCompared);
        Assert.Equal(0, v.RowsSource);
        Assert.Equal(0, v.RowsAfter);
    }
}
