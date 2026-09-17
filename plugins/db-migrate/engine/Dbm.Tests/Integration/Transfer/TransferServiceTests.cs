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

    // ------------------------------------------------------------------ fix round 1

    /// <summary>
    /// F1 / ruling 129. Two <see cref="TransferService.Resume"/> calls - a double-click, or the UI button plus
    /// <c>dbm transfer resume</c> - must not both launch. The reviewer measured what happens when they do: the loser is refused by the
    /// run lock and its bookkeeping clobbers the winner's, so for the rest of the transfer the run row reads <c>paused</c>,
    /// <c>Active</c> reads false and <c>Pause()</c> answers <c>not_running</c> <b>while rows are being written</b>. The operator's next
    /// click on a paused run is Cancel, whose paused branch drops <c>dbo.__dbm_checkpoint</c> under the live runner.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_resumes_hand_the_run_to_one_runner_and_refuse_the_other()
    {
        await using var rig = await RigAsync();
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T02" && c.ChunkNo == 2) rig.Service.Pause(); };
        long runId = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Paused, rig.Service.View().Run!.Status);

        // Ruling 133. Observed through the seam, not through a subscriber: the resumed run is live, has committed a chunk and is held
        // inside this delegate while it is read, so every assertion below is about a running transfer rather than about whichever task
        // the service happened to be holding when the test looked.
        bool? activeDuringResume = null;
        RunStatus? statusDuringResume = null;
        string? pauseDuringResume = "the resumed run committed no chunk";
        long childRowsDuringResume = -1;
        var bothDoorsAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int observations = 0;
        TransferService.AfterFirstChunk = async () =>
        {
            if (Interlocked.Increment(ref observations) > 1) return;
            await bothDoorsAnswered.Task;
            activeDuringResume = rig.Service.IsActive;
            statusDuringResume = rig.Service.View().Run!.Status;
            childRowsDuringResume = await rig.Tgt.CountAsync("app.Child");
            try
            {
                rig.Service.Pause();
                pauseDuringResume = null;
            }
            catch (TransferException ex)
            {
                pauseDuringResume = ex.Code;
            }
            observed.TrySetResult();
        };

        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<(long? Id, string? Code)> ResumeOnceAsync()
            {
                await gate.Task;
                try { return (rig.Service.Resume(), null); }
                catch (TransferException ex) { return (null, ex.Code); }
            }
            var first = Task.Run(ResumeOnceAsync);
            var second = Task.Run(ResumeOnceAsync);
            gate.TrySetResult();
            var outcomes = await Task.WhenAll(first, second);
            bothDoorsAnswered.TrySetResult();
            // Wait for the seam, not for Current: with two runners Current is whichever one launched last, and that is exactly the
            // confusion this test is about. The seam fires on the winner, so it is the only handle on "the run is live".
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await rig.Service.Current;

            // The harm first: rows were being written, so the service must not have described an idle, paused run that ignores Pause.
            Assert.True(activeDuringResume);
            Assert.Equal(RunStatus.Running, statusDuringResume);
            Assert.Null(pauseDuringResume);                                           // Pause() was accepted, not refused
            Assert.True(childRowsDuringResume > 200, $"the resumed run had written no new rows: {childRowsDuringResume}");
            Assert.Single(outcomes, o => o.Id is not null);
            Assert.Equal(runId, outcomes.Single(o => o.Id is not null).Id);
            Assert.Equal("busy", outcomes.Single(o => o.Id is null).Code);
        }
        finally
        {
            TransferService.AfterFirstChunk = null;
        }

        // And the pause that was accepted took effect, so the run is still the operator's to finish.
        Assert.Equal(RunStatus.Paused, rig.Service.View().Run!.Status);
        rig.Service.Resume();
        await rig.Service.Current;
        Assert.Equal(RunStatus.Completed, rig.Service.View().Run!.Status);
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal(700, await rig.Tgt.CountAsync("app.Log"));
    }

    /// <summary>
    /// F5 / R1's headline property. Two <c>StartAsync</c> calls racing must produce one run, not two: the second run id gets its own
    /// <see cref="RunLock"/> resource, so the engine's lock does not exclude it and two runners load the same tables at once.
    /// <para>Also F8: <c>ArtifactRepo.NextVersion</c> answers <b>0</b> for a phase with no artifact, so without the brief's
    /// <c>Math.Max(1, …)</c> floor the first Complete artifact - the migration's certificate - is stored as v0 and the Complete phase
    /// is approved at v0.</para>
    /// </summary>
    [Fact]
    public async Task Two_concurrent_starts_produce_one_run_and_its_report_is_version_one()
    {
        await using var rig = await RigAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(long? Id, string? Code)> StartOnceAsync()
        {
            await gate.Task;
            try { return (await rig.Service.StartAsync(Skip with { ChunkSize = 1000 }, rig.Tgt.Name, default), null); }
            catch (TransferException ex) { return (null, ex.Code); }
        }
        var first = Task.Run(StartOnceAsync);
        var second = Task.Run(StartOnceAsync);
        gate.TrySetResult();
        var outcomes = await Task.WhenAll(first, second);
        await rig.Service.Current;

        // The harm first: one run row, one runner, one set of rows.
        var ids = rig.S.Db.Query("SELECT id FROM transfer_run ORDER BY id", r => r.GetInt64(0));
        Assert.Single(ids);
        Assert.Equal(RunStatus.Completed, rig.S.Transfers.GetRun(ids[0])!.Status);
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Single(outcomes, o => o.Id is not null);
        Assert.Equal("busy", outcomes.Single(o => o.Id is null).Code);

        // F8: the first Complete artifact of a fresh workspace is v1, not v0.
        Assert.Equal(1, rig.S.Artifacts.Latest(PhaseName.Complete)!.Version);
        Assert.Equal(1, rig.S.Phases.Get(PhaseName.Complete).ApprovedVersion);
    }

    /// <summary>
    /// F3 / ruling 128. The typed confirmation compares the name the <b>saved connection</b> reports, and <c>schema_drift</c> compares
    /// a fingerprint that is purely structural, so a target repointed at a different database with an identical schema passes both. The
    /// plan was generated against the discovered catalog, and that is what the saved connection has to still match.
    /// </summary>
    [Fact]
    public async Task A_target_repointed_at_an_identical_database_is_refused_before_a_run_exists()
    {
        await using var rig = await RigAsync();
        await using var other = await TempDatabase.CreateAsync("dbm_svc_swap");
        // The same schema down to the auto-generated primary-key constraint names, which is what makes the fingerprints identical -
        // otherwise schema_drift would catch the swap by accident and this guard would look unnecessary.
        string pkParent = await rig.Tgt.ScalarAsync<string>("SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('app.Parent') AND is_primary_key = 1");
        string pkChild = await rig.Tgt.ScalarAsync<string>("SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('app.Child') AND is_primary_key = 1");
        await other.ExecAsync($"""
            CREATE SCHEMA app;
            GO
            CREATE TABLE app.Parent (Id int NOT NULL CONSTRAINT [{pkParent}] PRIMARY KEY, Name nvarchar(50) NOT NULL);
            CREATE TABLE app.Child (Id int NOT NULL CONSTRAINT [{pkChild}] PRIMARY KEY,
              ParentId int NOT NULL CONSTRAINT FK_Child_Parent REFERENCES app.Parent (Id),
              Qty int NOT NULL CONSTRAINT CK_Child_Qty CHECK (Qty > 0), At datetime2(0) NOT NULL);
            CREATE TABLE app.Log (Msg nvarchar(100) NOT NULL);
            """);
        var swapped = await SqlConnect.ProbeAsync(other.ConnectionString, default);
        await using (var conn = await SqlConnect.OpenAsync(other.ConnectionString, default))
        {
            // The premise, asserted: nothing about the schema tells these two databases apart, so nothing but this guard can.
            Assert.Equal(rig.S.Catalog.Fingerprint(Side.Tgt),
                Fingerprint.Compute(await CatalogExtractor.ExtractAsync(conn, swapped, default)));
        }
        rig.S.Connections.Save(Side.Tgt, other.ConnectionString, swapped);

        var thrown = await Record.ExceptionAsync(() => rig.Service.StartAsync(Skip, other.Name, default));
        await rig.Service.Current;

        // The harm first: a plan written for one database must never be loaded into another.
        Assert.Null(rig.S.Transfers.Latest());
        Assert.Equal(0, await other.CountAsync("app.Parent"));
        Assert.Equal(PhaseStatus.AwaitingReview, rig.S.Phases.Get(PhaseName.Ready).Status);
        var ex = Assert.IsType<TransferException>(thrown);
        Assert.Equal("not_ready", ex.Code);
        Assert.Contains("discovered catalog", ex.Message);
        Assert.Contains(other.Name, ex.Message);
    }

    /// <summary>
    /// F4 / ruling 130. A resume the run lock refuses did nothing at all, so it must not be the thing that erases why the run failed.
    /// <c>TransferRepo.SetRunStatus</c> is <c>COALESCE($Summary, summary_json)</c>: a non-null summary <b>replaces</b>.
    /// <para>This also ships the test F11 says <c>RecordNotRun</c> never had.</para>
    /// </summary>
    [Fact]
    public async Task A_resume_the_run_lock_refuses_keeps_the_reason_the_run_failed()
    {
        await using var rig = await RigAsync();
        long runId = await rig.Service.StartAsync(Skip with { ErrorMode = "stop", ChunkSize = 500 }, rig.Tgt.Name, default);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Failed, rig.S.Transfers.GetRun(runId)!.Status);
        Assert.Contains("CK_Child_Qty", rig.S.Transfers.GetRun(runId)!.SummaryJson);

        var before = rig.Service.View().Run!;

        await using (await RunLock.AcquireAsync(rig.Tgt.ConnectionString, runId, default))
        {
            Assert.Equal(runId, rig.Service.Resume());
            await rig.Service.Current;
        }

        // Ruling 131. The refusal did nothing, so everything an operator reads is as it was - except the note.
        var view = rig.Service.View();
        Assert.Equal(RunStatus.Failed, view.Run!.Status);
        Assert.Equal(before.EndedAt, view.Run.EndedAt);
        Assert.Equal(before.Error, view.Run.Error);
        Assert.Contains("CK_Child_Qty", view.Run.Error);
        Assert.True(view.CanStart);                                                   // the Execute button is not disabled by a no-op
        Assert.Null(view.CannotStart);
        Assert.True(view.CanResume);                                                  // ruling 125: resumable again once the lock is free
        // Ruling 130: and the note is beside what was already there, in the record as well as the view.
        Assert.Contains(view.Run.Notes, n => n.Contains("already being run", StringComparison.Ordinal));
        Assert.Contains("CK_Child_Qty", rig.S.Transfers.GetRun(runId)!.SummaryJson);
    }

    /// <summary>
    /// Ruling 134, the fresh-start arm. A run this attempt <b>created</b> never started and loaded nothing, so a refusal closes it
    /// <c>cancelled</c> - the same close <c>EnsureNoOtherRunnerAsync</c> gives a start the probe refused (ruling 126). Left
    /// <c>running</c> instead it is a run reading "running" with no runner: <c>CancelAsync</c> answers <c>not_cancellable</c>,
    /// <c>Resume()</c> answers <c>not_resumable</c> and a new start is refused <c>not_ready</c>, so the operator cannot clear it at all
    /// short of restarting the server.
    /// <para><b>The trigger is unreachable from a test</b> (rulings 104, 112, 121): it needs a foreign runner to take this run's lock
    /// in the window between <c>EnsureNoOtherRunnerAsync</c> releasing the probe and the engine acquiring it, which is microseconds
    /// wide and not observable from here. So the arm is driven directly, with the origin its caller passes and the row in the state
    /// the caller leaves - and the end state below is the real one, asserted by using it.</para>
    /// </summary>
    [Fact]
    public async Task A_fresh_run_the_engine_lock_refuses_is_closed_so_the_operator_can_start_again()
    {
        await using var rig = await RigAsync();
        // Exactly what StartAsync leaves behind at the moment it launches: the run created and 'running', the workflow moved on.
        long refused = rig.S.Transfers.CreateRun(1, Skip, [("T01", "app.Parent"), ("T02", "app.Child"), ("T03", "app.Log")]);
        rig.S.Workflow.OnTransferStarted();
        Assert.Equal(RunStatus.Running, rig.S.Transfers.GetRun(refused)!.Status);

        rig.Service.RecordNotRun(refused, TransferService.RunOrigin.Created,
            $"Transfer run {refused} is already being run: dbm:transfer_run:{refused} is held by another session in the target.");

        // The harm first, and asserted by doing the thing the operator would do next: a run that cannot be cancelled, cannot be
        // resumed and blocks every new start is one they cannot clear at all short of restarting the server.
        var view = rig.Service.View();
        Assert.True(view.CanStart, view.CannotStart ?? "");
        long second = await rig.Service.StartAsync(Skip with { ChunkSize = 1000 }, rig.Tgt.Name, default);
        await rig.Service.Current;
        Assert.NotEqual(refused, second);
        Assert.Equal(RunStatus.Completed, rig.S.Transfers.GetRun(second)!.Status);
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));

        // And the refused run is closed, carrying the lock's sentence.
        Assert.Equal(RunStatus.Cancelled, view.Run!.Status);
        Assert.Contains(view.Run.Notes, n => n.Contains("already being run", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Cancelled, rig.S.Transfers.GetRun(refused)!.Status);
    }

    /// <summary>
    /// Ruling 134, the paused arm - fully reachable, because <c>Resume()</c> takes no probe: the lock can simply be held before it is
    /// called. A refusal leaves a paused run paused, with its note, and it resumes for real once the lock is free.
    /// </summary>
    [Fact]
    public async Task A_resume_the_run_lock_refuses_leaves_a_paused_run_paused_and_resumable()
    {
        await using var rig = await RigAsync();
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T01" && c.ChunkNo == 1) rig.Service.Pause(); };
        long runId = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Paused, rig.S.Transfers.GetRun(runId)!.Status);
        var before = rig.Service.View().Run!;

        await using (await RunLock.AcquireAsync(rig.Tgt.ConnectionString, runId, default))
        {
            Assert.Equal(runId, rig.Service.Resume());
            await rig.Service.Current;
        }

        var view = rig.Service.View();
        Assert.Equal(RunStatus.Paused, view.Run!.Status);
        Assert.Equal(before.EndedAt, view.Run.EndedAt);
        Assert.True(view.CanResume);
        Assert.Contains(view.Run.Notes, n => n.Contains("already being run", StringComparison.Ordinal));

        rig.Service.Resume();
        await rig.Service.Current;
        Assert.Equal(RunStatus.Completed, rig.Service.View().Run!.Status);
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
    }

    /// <summary>
    /// F6 / R6. The typed confirmation is the last gate before a database is written to, so it is exact. The brief's own test types
    /// <c>Name.ToUpperInvariant() + "X"</c>, which differs by the trailing X whatever the comparison is; this one differs by case
    /// alone.
    /// </summary>
    [Fact]
    public async Task The_typed_confirmation_differing_only_by_case_starts_nothing()
    {
        await using var rig = await RigAsync();
        string wrongCase = rig.Tgt.Name.ToUpperInvariant();
        Assert.NotEqual(rig.Tgt.Name, wrongCase);                                     // the rig's name really does have letters in it

        var thrown = await Record.ExceptionAsync(() => rig.Service.StartAsync(Skip, wrongCase, default));
        await rig.Service.Current;

        Assert.Null(rig.S.Transfers.Latest());
        Assert.Equal(PhaseStatus.AwaitingReview, rig.S.Phases.Get(PhaseName.Ready).Status);
        Assert.Equal("confirm_mismatch", Assert.IsType<TransferException>(thrown).Code);
    }
}
