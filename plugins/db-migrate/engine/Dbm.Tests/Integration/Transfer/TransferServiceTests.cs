using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class TransferServiceTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    private sealed class Rig(XferServices svc, TempDatabase tgt) : IAsyncDisposable
    {
        public XferServices Svc { get; } = svc;
        public TempDatabase Tgt { get; } = tgt;
        public DbmServices S => Svc.Services;
        public TransferService Service { get; } = new(svc.Services);
        public async ValueTask DisposeAsync()
        {
            try { await Service.Current; } catch (Exception) { }
            Svc.Dispose();
            await Tgt.DisposeAsync();
        }
    }

    private static async Task SaveSideAsync(DbmServices s, Side side, string cs)
    {
        var meta = await SqlConnect.ProbeAsync(cs, default);
        s.Connections.Save(side, cs, meta);
        await using var conn = await SqlConnect.OpenAsync(cs, default);
        var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, default);
        s.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
    }

    private async Task<Rig> RigAsync()
    {
        var tgt = await TempDatabase.CreateAsync("dbm_svc_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        var rig = new Rig(new XferServices(), tgt);
        await SaveSideAsync(rig.S, Side.Src, fx.Src.ConnectionString);
        await SaveSideAsync(rig.S, Side.Tgt, tgt.ConnectionString);
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping }) rig.S.Phases.SetApproved(p, 1, null);
        rig.S.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(TransferEngineTests.Plan()), "script", "test plan");
        rig.S.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        rig.S.Phases.SetApproved(PhaseName.Sql, 1, null);
        rig.S.Phases.SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview);
        return rig;
    }

    private static readonly TransferOptions Skip = new() { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };

    [Fact]
    public async Task Start_requires_the_exact_target_database_name()
    {
        await using var rig = await RigAsync();
        var ex = await Assert.ThrowsAsync<TransferException>(() => rig.Service.StartAsync(Skip, rig.Tgt.Name.ToUpperInvariant() + "X", default));
        Assert.Equal("confirm_mismatch", ex.Code);
        Assert.Null(rig.S.Transfers.Latest());
    }

    [Fact]
    public async Task Preflight_then_start_runs_to_completion_and_completes_the_workflow()
    {
        await using var rig = await RigAsync();
        var pre = await rig.Service.PreflightAsync(Skip, default);
        Assert.True(pre.Passed, string.Join("; ", pre.Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.Equal(1, pre.SqlVersion);
        Assert.Contains(pre.Checks, c => c.Name == "schema_drift" && c.Ok);
        Assert.Equal(rig.Tgt.Name, rig.Service.View().TargetDatabase);
        Assert.True(rig.Service.View().CanStart);
        Assert.Equal(3, rig.Service.View().Tasks.Count);                              // plan tasks shown before the first run

        long runId = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        Assert.Equal(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Ready).Status);
        await rig.Service.Current;

        var view = rig.Service.View();
        Assert.Equal(runId, view.Run!.Id);
        Assert.Equal(RunStatus.Completed, view.Run.Status);
        Assert.True(view.Run.HasReport);
        Assert.Equal(2998, view.Totals.RowsDone);
        Assert.Equal(2, view.Totals.RowsError);
        Assert.All(view.Tasks, t => Assert.True(t.Validation!.CountMatch));
        var report = rig.S.Artifacts.Latest(PhaseName.Complete)!;
        Assert.Equal("script", report.Author);
        Assert.StartsWith("Transferred 2,998 of 3,000 rows into 3 tables", report.Summary);
        Assert.Equal(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Transfer).Status);
        Assert.Equal(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Complete).Status);
        var next = rig.S.Workflow.Next();
        Assert.Equal("stop", next.Action);
        Assert.Equal("complete", next.Reason);
        Assert.Equal(2, rig.S.Transfers.ErrorRows(runId, "T02").Count);
    }

    [Fact]
    public async Task Pause_and_resume_through_the_service()
    {
        await using var rig = await RigAsync();
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T02" && c.ChunkNo == 2) rig.Service.Pause(); };
        await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Paused, rig.Service.View().Run!.Status);
        Assert.False(rig.Service.View().CanStart);
        var next = rig.S.Workflow.Next();
        Assert.Equal("await", next.Action);
        Assert.Equal("transfer_paused", next.Reason);
        Assert.Equal("paused_run", (await Assert.ThrowsAsync<TransferException>(() => rig.Service.StartAsync(Skip, rig.Tgt.Name, default))).Code);

        rig.Service.Resume();
        await rig.Service.Current;
        Assert.Equal(RunStatus.Completed, rig.Service.View().Run!.Status);
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal("not_resumable", Assert.Throws<TransferException>(() => rig.Service.Resume()).Code);
    }

    [Fact]
    public async Task Cancelled_run_can_be_restarted_with_truncation()
    {
        await using var rig = await RigAsync();
        int pauses = 0;   // pause only the first run; the handler stays subscribed for the second one
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T01" && c.ChunkNo == 1 && Interlocked.Increment(ref pauses) == 1) rig.Service.Pause(); };
        long first = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;
        await rig.Service.CancelAsync(default);
        Assert.Equal(RunStatus.Cancelled, rig.S.Transfers.GetRun(first)!.Status);
        Assert.Equal("transfer_cancelled", rig.S.Workflow.Next().Reason);
        Assert.True(rig.Service.View().CanStart);

        long second = await rig.Service.StartAsync(Skip with { TruncateTarget = true, ChunkSize = 1000 }, rig.Tgt.Name, default);
        Assert.NotEqual(first, second);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Completed, rig.S.Transfers.GetRun(second)!.Status);
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        await using var conn = new SqlConnection(rig.Tgt.ConnectionString);
        await conn.OpenAsync();
        Assert.False(await ControlTable.ExistsAsync(conn, default));
    }

    // ------------------------------------------------------------------ beyond the brief

    /// <summary>
    /// Carry-forward 4. Before the first run there is no run to read counts from, so every total is 0 - and a 0 that can mean both
    /// "nothing to move" and "nobody has counted yet" is the recurring defect of this project. <c>TasksWithoutSource</c> is what says
    /// which, and it is what stops 5.6 rendering "0 of 0 rows" over a plan that will move three thousand.
    /// </summary>
    [Fact]
    public async Task Before_the_first_run_the_view_shows_the_plan_and_says_that_no_row_count_is_known()
    {
        await using var rig = await RigAsync();
        var view = rig.Service.View();

        Assert.Null(view.Run);
        Assert.True(view.CanStart);
        Assert.Null(view.CannotStart);
        Assert.Equal(1, view.SqlVersion);
        Assert.Null(view.PlanNote);
        Assert.Equal(3, view.Tasks.Count);
        Assert.All(view.Tasks, t => Assert.Equal(TransferTaskStatus.Pending, t.Status));
        Assert.All(view.Tasks, t => Assert.Null(t.RowsSource));
        Assert.Equal(0, view.Totals.RowsSource);
        Assert.Equal(3, view.Totals.TasksWithoutSource);
        Assert.Equal(3, view.Totals.TasksTotal);
        Assert.Equal(0, view.Totals.TasksDone);
        Assert.False(view.CanResume);
        Assert.NotNull(view.CannotResume);
        Assert.Equal(new[] { "T01" }, view.Tasks.Single(t => t.TaskId == "T02").DependsOn);
        Assert.True(view.Tasks.Single(t => t.TaskId == "T03").Keyless);
        Assert.False(view.Tasks.Single(t => t.TaskId == "T01").Keyless);

        long runId = await rig.Service.StartAsync(Skip with { ChunkSize = 1000 }, rig.Tgt.Name, default);
        await rig.Service.Current;
        var after = rig.Service.View();
        Assert.Equal(RunStatus.Completed, rig.S.Transfers.GetRun(runId)!.Status);
        Assert.Equal(0, after.Totals.TasksWithoutSource);                             // now every task really has been counted
        Assert.Equal(3000, after.Totals.RowsSource);
    }

    /// <summary>
    /// Carry-forward 6. The Complete artifact is the migration's certificate: the UI links to it, `dbm export report` renders it and
    /// the workflow approves the Complete phase on the strength of it. Storing one for a run that did not complete hands the operator
    /// a report for a half-loaded target and closes the migration over it.
    /// </summary>
    [Fact]
    public async Task A_failed_run_stores_no_complete_report_and_carries_its_error_into_the_view()
    {
        await using var rig = await RigAsync();
        await rig.Service.StartAsync(Skip with { ErrorMode = "stop", ChunkSize = 500 }, rig.Tgt.Name, default);
        await rig.Service.Current;

        var view = rig.Service.View();
        Assert.Equal(RunStatus.Failed, view.Run!.Status);
        Assert.False(view.Run.HasReport);
        Assert.Contains("CK_Child_Qty", view.Run.Error);
        Assert.Null(rig.S.Artifacts.Latest(PhaseName.Complete));
        Assert.NotEqual(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Complete).Status);
        Assert.Equal(PhaseStatus.Running, rig.S.Phases.Get(PhaseName.Transfer).Status);
        Assert.Equal("transfer_failed", rig.S.Workflow.Next().Reason);
        Assert.True(view.CanResume);                                                  // a failed run resumes from its checkpoints
        Assert.True(view.CanStart);                                                   // or is abandoned for a new run id
    }

    /// <summary>
    /// Carry-forward 1 / ruling 103. The run lock is the guard behind the service's own: it refuses a second runner when two processes
    /// share a target. That refusal reaches the operator through <see cref="TransferService.StartAsync"/>, so it has to arrive as
    /// "busy, try again" with the lock's own sentence - not as a run recorded <c>failed</c>, which sends the operator to investigate a
    /// migration that never started, and not as a <c>paused</c> run, which blocks every later start until it is cancelled.
    /// </summary>
    [Fact]
    public async Task A_start_the_run_lock_refuses_is_busy_and_leaves_the_operator_able_to_retry()
    {
        await using var rig = await RigAsync();
        long willBe = (rig.S.Transfers.Latest()?.Id ?? 0) + 1;
        await using var held = await RunLock.AcquireAsync(rig.Tgt.ConnectionString, willBe, default);

        var thrown = await Record.ExceptionAsync(() => rig.Service.StartAsync(Skip, rig.Tgt.Name, default));
        await rig.Service.Current;

        // The harm first: nothing of the migration may move for a transfer that never started - not the target, not the workflow, and
        // not the operator's ability to try again once the other runner is done.
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(PhaseStatus.AwaitingReview, rig.S.Phases.Get(PhaseName.Ready).Status);
        Assert.True(rig.Service.View().CanStart);
        var run = rig.S.Transfers.Latest();
        Assert.NotNull(run);
        Assert.Equal(willBe, run.Id);
        Assert.NotEqual(RunStatus.Failed, run.Status);
        Assert.Contains(rig.Service.View().Run!.Notes, n => n.Contains("already being run", StringComparison.Ordinal));
        var ex = Assert.IsType<TransferException>(thrown);
        Assert.Equal("busy", ex.Code);
        Assert.Contains("already being run", ex.Message);
    }

    /// <summary>
    /// Carry-forward 5, the half that fixes the comparison's shape. <c>Skip</c> is normalised into a fresh instance on every call, so a
    /// reference comparison would re-run the checklist every time; the same run of checks has to be recognised as the same.
    /// </summary>
    [Fact]
    public async Task A_pre_flight_that_still_describes_this_start_is_reused_rather_than_run_again()
    {
        await using var rig = await RigAsync();
        var pre = await rig.Service.PreflightAsync(Skip, default);
        Assert.True(pre.Passed);
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T01" && c.ChunkNo == 1) rig.Service.Pause(); };

        await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);

        Assert.Same(pre, rig.Service.LastPreflight);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Paused, rig.Service.View().Run!.Status);
    }

    /// <summary>
    /// Carry-forward 5, the half with the harm behind it: a checklist taken against one set of options is not a checklist for another,
    /// and a target that lost a table since it was taken is exactly what the checklist exists to catch. Reusing it starts a run that
    /// fails partway into a target it has already written to.
    /// </summary>
    [Fact]
    public async Task A_single_changed_option_re_runs_pre_flight_and_a_failing_one_starts_nothing()
    {
        await using var rig = await RigAsync();
        var pre = await rig.Service.PreflightAsync(Skip, default);
        Assert.True(pre.Passed);
        await rig.Tgt.ExecAsync("DROP TABLE app.Log;");

        var thrown = await Record.ExceptionAsync(() => rig.Service.StartAsync(Skip with { ChunkSize = 500 }, rig.Tgt.Name, default));
        await rig.Service.Current;

        // The harm first: no run may be created, and no row written, on the strength of a checklist that no longer describes this
        // target - the operator would get a run that dies partway into a database it has already loaded rows into.
        Assert.Null(rig.S.Transfers.Latest());
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Parent"));
        Assert.NotSame(pre, rig.Service.LastPreflight);
        Assert.False(rig.Service.LastPreflight!.Passed);
        var ex = Assert.IsType<TransferException>(thrown);
        Assert.Equal("preflight_failed", ex.Code);
        Assert.Contains(ex.Details, d => d.Contains("app.Log", StringComparison.Ordinal));
    }

    /// <summary>
    /// Carry-forward 3. A pause is a request, not an event: keyed tasks stop after their current chunk and a keyless one keeps loading
    /// until its table is done (rulings 104, 5.3 F9). Between the click and the stop the run row still reads <c>running</c>, so without
    /// a state of its own the screen shows a running transfer that ignores the button - and the operator clicks it again.
    /// <para>The second half is 5.4's <c>StatusNote</c> join: a task left <c>paused</c> under a run that was <b>cancelled</b> is not
    /// waiting for a resume, and a reader of the task row alone cannot tell.</para>
    /// </summary>
    [Fact]
    public async Task A_pause_reads_as_pausing_until_it_takes_effect_and_a_cancelled_run_says_its_tasks_did_not_finish()
    {
        await using var rig = await RigAsync();
        string? stoppingDuring = null;
        RunStatus? statusDuring = null;
        rig.Service.ChunkCommitted += c =>
        {
            if (c.TaskId != "T01" || c.ChunkNo != 1) return;
            rig.Service.Pause();
            var during = rig.Service.View();
            stoppingDuring = during.Stopping;
            statusDuring = during.Run!.Status;
        };

        await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;

        Assert.Equal("pausing", stoppingDuring);
        Assert.Equal(RunStatus.Running, statusDuring);                                 // the run row cannot say it yet; the view can
        Assert.Null(rig.Service.View().Stopping);
        Assert.Equal(RunStatus.Paused, rig.Service.View().Run!.Status);

        await rig.Service.CancelAsync(default);
        var view = rig.Service.View();
        Assert.Equal(RunStatus.Cancelled, view.Run!.Status);
        var t01 = view.Tasks.Single(t => t.TaskId == "T01");
        Assert.Equal(TransferTaskStatus.Paused, t01.Status);
        Assert.Contains("the run was cancelled", t01.StatusNote);
        Assert.Contains("T03", view.Tasks.Where(t => t.Keyless).Select(t => t.TaskId));
    }

    /// <summary>
    /// Carry-forward 7. Shutting the server down is a hard stop, not a drain: the run stays <c>running</c> exactly as a crash leaves it,
    /// the next process recovers it as <c>paused</c>, and the resume continues from the committed checkpoints. The harm this pins is the
    /// resume that silently starts from row zero - which on <c>app.Log</c>, a table with no key at all, nothing else would notice.
    /// </summary>
    [Fact]
    public async Task A_server_stop_mid_run_is_recovered_as_paused_and_resumes_from_its_checkpoints()
    {
        await using var rig = await RigAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T02" && c.ChunkNo == 2) reached.TrySetResult(); };
        long runId = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await reached.Task;

        await rig.Service.StopAsync();
        Assert.Equal(RunStatus.Running, rig.S.Transfers.GetRun(runId)!.Status);        // what a killed process leaves behind

        var restarted = new TransferService(rig.S);                                    // the next server process
        Assert.Equal(1, restarted.RecoverInterrupted());
        Assert.Equal(RunStatus.Paused, restarted.View().Run!.Status);
        Assert.True(restarted.View().CanResume);
        Assert.Equal(runId, restarted.Resume());
        await restarted.Current;

        Assert.Equal(RunStatus.Completed, restarted.View().Run!.Status);
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal(700, await rig.Tgt.CountAsync("app.Log"));
        Assert.Equal(2, rig.S.Transfers.ErrorRows(runId, "T02").Count);                // recorded once, across the stop
    }

    /// <summary>
    /// Carry-forward 8 (5.3 ruling 105, 5.4 carry-forward 1). A run carries notes out with it - a checkpoint table that was not ours,
    /// rejected rows counted but never recorded - and they live in <c>summary_json</c>. If the view drops them the operator is shown a
    /// clean completed run and the note survives only in a log file nobody opens.
    /// </summary>
    [Fact]
    public async Task A_run_note_reaches_the_view_so_a_completed_run_is_not_read_as_a_clean_one()
    {
        await using var rig = await RigAsync();
        var tgt = rig.Tgt;
        rig.Service.ChunkCommitted += c =>
        {
            if (c.TaskId != "T03") return;   // the last task: swap our checkpoint table for a customer's, as the run goes to drop it
            tgt.ExecAsync("""
                DROP TABLE dbo.__dbm_checkpoint;
                GO
                CREATE TABLE dbo.__dbm_checkpoint (note nvarchar(50) NOT NULL);
                GO
                INSERT dbo.__dbm_checkpoint (note) VALUES (N'customer data');
                """).GetAwaiter().GetResult();
        };

        await rig.Service.StartAsync(Skip with { ChunkSize = 1000 }, rig.Tgt.Name, default);
        await rig.Service.Current;

        var run = rig.Service.View().Run!;
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Null(run.Error);                                                       // every row loaded: this is not a failed run
        Assert.Contains(run.Notes, n => n.Contains("control_table_mismatch", StringComparison.Ordinal));
    }
}
