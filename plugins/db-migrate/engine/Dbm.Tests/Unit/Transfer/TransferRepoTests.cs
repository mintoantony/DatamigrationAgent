using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class TransferRepoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbm-xfer-repo-" + Guid.NewGuid().ToString("N"));
    private readonly StateDb _db;
    private readonly TransferRepo _repo;

    public TransferRepoTests()
    {
        Directory.CreateDirectory(_dir);
        _db = StateDb.Open(Path.Combine(_dir, "state.db"));
        _repo = new TransferRepo(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private long NewRun() => _repo.CreateRun(3, new TransferOptions { ChunkSize = 500, ErrorMode = "skip" },
        [("T01", "app.Customers"), ("T02", "app.Orders")]);

    [Fact]
    public void CreateRun_stores_running_run_and_pending_tasks_in_order()
    {
        long id = NewRun();
        var run = _repo.GetRun(id)!;
        Assert.Equal(3, run.SqlVersion);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(500, run.Options.ChunkSize);
        Assert.Equal("skip", run.Options.ErrorMode);
        Assert.NotNull(run.StartedAt);
        Assert.Equal("running", _db.Scalar<string>("SELECT status FROM transfer_run WHERE id = $Id", new { Id = id }));

        var tasks = _repo.Tasks(id);
        Assert.Equal(new[] { "T01", "T02" }, tasks.Select(t => t.TaskId));
        Assert.Equal(new[] { 0, 1 }, tasks.Select(t => t.Ordinal));
        Assert.All(tasks, t => Assert.Equal(TransferTaskStatus.Pending, t.Status));
        Assert.Equal("pending", _db.Scalar<string>("SELECT status FROM transfer_task WHERE run_id = $Id AND task_id = 'T01'", new { Id = id }));
    }

    /// <summary>Open item 22: the target is recorded at creation and read back exactly; a run created without one reads null, never "".</summary>
    [Fact]
    public void CreateRun_records_the_target_it_will_load_into()
    {
        long with = _repo.CreateRun(1, new TransferOptions(), [("T01", "app.A")], (@"HOST\SQL", "Shop"));
        long without = _repo.CreateRun(1, new TransferOptions(), [("T01", "app.A")]);

        var run = _repo.GetRun(with)!;
        Assert.True(run.TargetServer == @"HOST\SQL" && run.TargetDatabase == "Shop",
            $"the run did not record its target: {run.TargetServer}/{run.TargetDatabase}");
        Assert.Null(_repo.GetRun(without)!.TargetDatabase);
        Assert.Null(_repo.Latest()!.TargetServer);
    }

    [Fact]
    public void Task_status_transitions_set_timestamps_and_error()
    {
        long id = NewRun();
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
        var running = _repo.Task(id, "T01")!;
        Assert.NotNull(running.StartedAt);
        Assert.Null(running.EndedAt);

        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Failed, "boom");
        var failed = _repo.Task(id, "T01")!;
        Assert.Equal(TransferTaskStatus.Failed, failed.Status);
        Assert.Equal("boom", failed.Error);
        Assert.NotNull(failed.EndedAt);

        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
        var again = _repo.Task(id, "T01")!;
        Assert.Null(again.Error);
        Assert.Null(again.EndedAt);
        Assert.Equal(running.StartedAt, again.StartedAt);
    }

    [Fact]
    public void Progress_counts_validation_and_run_status_round_trip()
    {
        long id = NewRun();
        _repo.SetTaskCounts(id, "T02", 3005, 0);
        _repo.UpdateTaskProgress(id, "T02", 1000, 2, "[{\"t\":\"int\",\"s\":4,\"p\":10,\"c\":0,\"v\":\"1000\"}]");
        _repo.SetTaskValidation(id, "T02", "{\"countMatch\":true}");
        var t = _repo.Task(id, "T02")!;
        Assert.Equal(3005, t.RowsSource);
        Assert.Equal(0, t.RowsBefore);
        Assert.Equal(1000, t.RowsDone);
        Assert.Equal(2, t.RowsError);
        Assert.NotNull(t.HeartbeatAt);
        Assert.Contains("\"v\":\"1000\"", t.LastKeyJson);
        Assert.Equal("{\"countMatch\":true}", t.ValidationJson);

        _repo.SetRunStatus(id, RunStatus.Paused);
        Assert.Null(_repo.GetRun(id)!.EndedAt);
        _repo.SetRunStatus(id, RunStatus.Completed, "{\"runId\":1}");
        var run = _repo.GetRun(id)!;
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.NotNull(run.EndedAt);
        Assert.Equal("{\"runId\":1}", run.SummaryJson);
        Assert.Equal(id, _repo.Latest()!.Id);
    }

    [Fact]
    public void Error_rows_filter_by_task_and_limit()
    {
        long id = NewRun();
        for (int i = 0; i < 5; i++) _repo.AddErrorRow(id, "T02", $"{{\"__k0\":{i}}}", "{\"Comment\":\"x\"}", $"err {i}");
        _repo.AddErrorRow(id, "T01", null, "{}", "other");
        Assert.Equal(3, _repo.ErrorRows(id, "T02", 3).Count);
        Assert.Equal("err 0", _repo.ErrorRows(id, "T02", 3)[0].Error);
        Assert.Equal(6, _repo.ErrorRows(id).Count);
        Assert.Equal(5, _repo.ErrorRowCount(id, "T02"));
    }

    [Fact]
    public void RecoverInterrupted_pauses_running_runs_and_tasks()
    {
        long id = NewRun();
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Done);
        _repo.UpdateTaskStatus(id, "T02", TransferTaskStatus.Running);
        Assert.Equal(1, _repo.RecoverInterrupted());
        Assert.Equal(RunStatus.Paused, _repo.GetRun(id)!.Status);
        Assert.Equal(TransferTaskStatus.Done, _repo.Task(id, "T01")!.Status);
        Assert.Equal(TransferTaskStatus.Paused, _repo.Task(id, "T02")!.Status);
        Assert.Equal(0, _repo.RecoverInterrupted());
    }

    [Fact]
    public void Options_normalize_clamps_and_rejects_unknown_error_mode()
    {
        var o = new TransferOptions { ChunkSize = 0, Parallelism = 99, ErrorMode = "SKIP" }.Normalized();
        Assert.Equal(1, o.ChunkSize);
        Assert.Equal(32, o.Parallelism);
        Assert.Equal("skip", o.ErrorMode);
        Assert.True(o.SkipErrors);
        Assert.DoesNotContain("skipErrors", Json.Serialize(o));
        var ex = Assert.Throws<TransferException>(() => new TransferOptions { ErrorMode = "ignore" }.Normalized());
        Assert.Equal("bad_options", ex.Code);
    }

    // ---- additions beyond the brief: crash-recovery reach, and absences that must say why ----

    [Fact]
    public void RecoverInterrupted_leaves_other_runs_and_non_running_tasks_alone_and_reaches_paused_runs()
    {
        long done = NewRun();
        _repo.UpdateTaskStatus(done, "T01", TransferTaskStatus.Done);
        _repo.UpdateTaskStatus(done, "T02", TransferTaskStatus.Failed, "x");
        _repo.SetRunStatus(done, RunStatus.Failed, "{}");

        long paused = NewRun();
        _repo.UpdateTaskStatus(paused, "T01", TransferTaskStatus.Running);   // still draining when the process died
        _repo.SetRunStatus(paused, RunStatus.Paused);

        long live = NewRun();
        _repo.UpdateTaskStatus(live, "T01", TransferTaskStatus.Running);
        _repo.UpdateTaskStatus(live, "T02", TransferTaskStatus.Failed, "bad row");

        Assert.Equal(1, _repo.RecoverInterrupted());

        Assert.Equal(RunStatus.Failed, _repo.GetRun(done)!.Status);
        Assert.NotNull(_repo.GetRun(done)!.EndedAt);
        Assert.Equal(TransferTaskStatus.Done, _repo.Task(done, "T01")!.Status);
        Assert.Equal(TransferTaskStatus.Failed, _repo.Task(done, "T02")!.Status);

        Assert.Equal(RunStatus.Paused, _repo.GetRun(paused)!.Status);
        Assert.Equal(TransferTaskStatus.Paused, _repo.Task(paused, "T01")!.Status);
        Assert.Equal(TransferTaskStatus.Pending, _repo.Task(paused, "T02")!.Status);

        Assert.Equal(RunStatus.Paused, _repo.GetRun(live)!.Status);
        Assert.Null(_repo.GetRun(live)!.EndedAt);
        Assert.Equal(TransferTaskStatus.Paused, _repo.Task(live, "T01")!.Status);
        Assert.Equal(TransferTaskStatus.Failed, _repo.Task(live, "T02")!.Status);
        Assert.Equal("bad row", _repo.Task(live, "T02")!.Error);

        Assert.Equal(0, _repo.RecoverInterrupted());
    }

    [Fact]
    public void RecoverInterrupted_repairs_orphaned_tasks_under_a_paused_run_without_counting_them()
    {
        long id = NewRun();
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
        _repo.UpdateTaskStatus(id, "T02", TransferTaskStatus.Running);
        _repo.SetRunStatus(id, RunStatus.Paused);

        // The contracted count is runs changed running -> paused. Two task rows are repaired here and the count is still 0:
        // do not "simplify" the return value to include them (ruling Q1a) without a signature ruling.
        Assert.Equal(0, _repo.RecoverInterrupted());
        Assert.Equal(RunStatus.Paused, _repo.GetRun(id)!.Status);
        Assert.Equal(TransferTaskStatus.Paused, _repo.Task(id, "T01")!.Status);
        Assert.Equal(TransferTaskStatus.Paused, _repo.Task(id, "T02")!.Status);
    }

    [Fact]
    public void RecoverInterrupted_deliberately_leaves_a_running_task_under_a_terminal_run_untouched()
    {
        // Unreachable through the engine (a run turns terminal only after its tasks finish), so it signals a bug or a hand edit.
        // Repairing it would hide that, and marking it failed would rewrite a completed run's report (ruling Q1b).
        foreach (var terminal in new[] { RunStatus.Completed, RunStatus.Failed, RunStatus.Cancelled })
        {
            long id = NewRun();
            _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
            _repo.UpdateTaskStatus(id, "T02", TransferTaskStatus.Done);
            _repo.SetRunStatus(id, terminal, "{\"ok\":true}");
            var runBefore = _repo.GetRun(id)!;
            var taskBefore = _repo.Task(id, "T01")!;

            Assert.Equal(0, _repo.RecoverInterrupted());

            Assert.Equal(runBefore, _repo.GetRun(id));
            Assert.Equal(taskBefore, _repo.Task(id, "T01"));
            Assert.Equal(TransferTaskStatus.Running, _repo.Task(id, "T01")!.Status);
        }
    }

    [Fact]
    public void Mutators_on_unknown_run_or_task_throw_instead_of_silently_doing_nothing()
    {
        long id = NewRun();
        Assert.Equal("unknown_run", Assert.Throws<TransferException>(() => _repo.SetRunStatus(id + 1, RunStatus.Paused)).Code);
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.UpdateTaskStatus(id, "T99", TransferTaskStatus.Running)).Code);
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.UpdateTaskProgress(id, "T99", 1, 0, null)).Code);
        // The harm a silent return would hide: the progress write went nowhere, so a resume would restart from nothing.
        Assert.All(_repo.Tasks(id), t => { Assert.Equal(0, t.RowsDone); Assert.Null(t.LastKeyJson); });
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.SetTaskCounts(id + 1, "T01", 1, 0)).Code);
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.SetTaskValidation(id, "T99", "{}")).Code);
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.AddErrorRow(id + 1, "T01", null, "{}", "e")).Code);
        Assert.Empty(_repo.ErrorRows(id + 1));
        Assert.Null(_repo.GetRun(id + 1));
        Assert.Null(_repo.Task(id, "T99"));
        Assert.Empty(_repo.Tasks(id + 1));
    }

    [Fact]
    public void Failed_status_requires_a_reason_and_other_statuses_reject_one()
    {
        long id = NewRun();
        Assert.Throws<ArgumentException>(() => _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Failed));
        Assert.Throws<ArgumentException>(() => _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Failed, "  "));
        Assert.Throws<ArgumentException>(() => _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Done, "why"));
        Assert.Equal(TransferTaskStatus.Pending, _repo.Task(id, "T01")!.Status);

        // Only running clears a stored error; the other statuses keep it (brief contract, ruling Q4).
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Failed, "boom");
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Paused);
        Assert.Equal("boom", _repo.Task(id, "T01")!.Error);
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Pending);
        Assert.Equal("boom", _repo.Task(id, "T01")!.Error);
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Done);
        Assert.Equal("boom", _repo.Task(id, "T01")!.Error);
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
        Assert.Null(_repo.Task(id, "T01")!.Error);
    }

    [Fact]
    public void SetRunStatus_clears_ended_at_and_summary_when_resumed()
    {
        long id = NewRun();
        _repo.SetRunStatus(id, RunStatus.Failed, "{\"x\":1}");
        Assert.NotNull(_repo.GetRun(id)!.EndedAt);
        _repo.SetRunStatus(id, RunStatus.Running);
        var run = _repo.GetRun(id)!;
        Assert.Null(run.EndedAt);
        Assert.Null(run.SummaryJson);
    }

    [Fact]
    public void CreateRun_persists_normalized_options_and_rejects_bad_input()
    {
        // The harm: SkipErrors is derived by exact comparison, so a stored "SKIP" would read back as a stop-on-error run
        // (the first bad row fails the task although the operator chose skip), and a stored ChunkSize 0 reads back as TOP (0).
        var raw = new TransferOptions { ChunkSize = 0, ErrorMode = "SKIP" };
        Assert.False(raw.SkipErrors);
        long skip = _repo.CreateRun(1, raw, [("T01", "a.b")]);
        var stored = _repo.GetRun(skip)!.Options;
        Assert.True(stored.SkipErrors);
        Assert.Equal("skip", stored.ErrorMode);
        Assert.Equal(1, stored.ChunkSize);
        Assert.Contains("\"errorMode\":\"skip\"", _db.Scalar<string>("SELECT options_json FROM transfer_run WHERE id = $Id", new { Id = skip }));

        long id = _repo.CreateRun(1, new TransferOptions { ChunkSize = -5, ErrorMode = " Stop " }, [("T01", "a.b")]);
        var o = _repo.GetRun(id)!.Options;
        Assert.Equal(1, o.ChunkSize);
        Assert.Equal("stop", o.ErrorMode);
        Assert.Throws<TransferException>(() => _repo.CreateRun(1, new TransferOptions { ErrorMode = "nope" }, [("T01", "a.b")]));
        Assert.Throws<ArgumentNullException>(() => _repo.CreateRun(1, null!, [("T01", "a.b")]));
        Assert.Throws<ArgumentException>(() => _repo.CreateRun(1, new TransferOptions(), [(null!, "a.b")]));
        Assert.Equal(id, _repo.Latest()!.Id);   // rejected runs left nothing behind
    }

    // ---- fix round 1 (M3, L2) ----

    [Fact]
    public void Tasks_come_back_in_creation_ordinal_not_task_id_order()
    {
        // Creation order deliberately differs from ordinal string order and from the (run_id, task_id) key order.
        long id = _repo.CreateRun(1, new TransferOptions(), [("T02", "app.B"), ("T01", "app.A"), ("T10", "app.C"), ("T03", "app.D")]);
        var tasks = _repo.Tasks(id);
        Assert.Equal(new[] { "T02", "T01", "T10", "T03" }, tasks.Select(t => t.TaskId));
        Assert.Equal(new[] { 0, 1, 2, 3 }, tasks.Select(t => t.Ordinal));
    }

    [Fact]
    public void ErrorRows_come_back_in_insertion_order_across_and_within_tasks()
    {
        long id = _repo.CreateRun(1, new TransferOptions(), [("T02", "app.B"), ("T01", "app.A")]);
        _repo.AddErrorRow(id, "T02", null, "{}", "e0 T02");
        _repo.AddErrorRow(id, "T01", null, "{}", "e1 T01");
        _repo.AddErrorRow(id, "T02", null, "{}", "e2 T02");
        _repo.AddErrorRow(id, "T01", null, "{}", "e3 T01");
        Assert.Equal(new[] { "e0 T02", "e1 T01", "e2 T02", "e3 T01" }, _repo.ErrorRows(id).Select(e => e.Error));
        Assert.Equal(new[] { "e1 T01", "e3 T01" }, _repo.ErrorRows(id, "T01").Select(e => e.Error));
        var ids = _repo.ErrorRows(id).Select(e => e.Id).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);
    }

    [Fact]
    public void ErrorRowCount_for_an_unknown_run_or_task_throws_instead_of_reading_as_clean()
    {
        long id = NewRun();
        Assert.Equal(0, _repo.ErrorRowCount(id, "T01"));   // a real task with no errors
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.ErrorRowCount(id, "T99")).Code);
        Assert.Equal("unknown_task", Assert.Throws<TransferException>(() => _repo.ErrorRowCount(id + 1, "T01")).Code);
    }

    [Fact]
    public void CreateRun_refuses_an_empty_task_list_that_would_read_as_all_done()
    {
        long before = NewRun();
        long? id = null;
        bool guarded = false;
        try { id = _repo.CreateRun(1, new TransferOptions(), []); }
        catch (ArgumentException) { guarded = true; }

        if (!guarded)
        {
            // The harm: a run with no tasks satisfies "every task is done", so 5.3 completes it having copied nothing.
            Assert.False(_repo.Tasks(id!.Value).All(t => t.Status == TransferTaskStatus.Done),
                "an empty run reads as all tasks done: it would complete as a successful migration of nothing");
        }
        Assert.True(guarded);
        Assert.Equal(before, _repo.Latest()!.Id);
    }

    [Fact]
    public void CreateRun_applies_the_checkpoint_task_id_length_limit()
    {
        string id64 = new('a', 64);
        long ok = _repo.CreateRun(1, new TransferOptions(), [(id64, "a.b")]);
        Assert.Equal(id64, _repo.Tasks(ok).Single().TaskId);
        Assert.Throws<ArgumentException>(() => _repo.CreateRun(1, new TransferOptions(), [("T01", "a.b"), (id64 + "b", "c.d")]));
        Assert.Equal(ok, _repo.Latest()!.Id);
    }

    [Fact]
    public void CreateRun_task_identity_is_ordinal_with_no_surrounding_whitespace()
    {
        long before = NewRun();

        // Exact duplicates: SQLite's (run_id, task_id) key would already refuse them; all the guard adds is a named
        // ArgumentException in place of a raw SqliteException from inside the transaction.
        var dup = Assert.Throws<ArgumentException>(() => _repo.CreateRun(1, new TransferOptions(), [("T01", "a.b"), ("T01", "c.d")]));
        Assert.Contains("T01", dup.Message);

        // Surrounding whitespace: SQL Server ignores trailing spaces in comparisons under every collation, so "T01 " would
        // share T01's checkpoint row in the target even though SQLite stores them as two tasks (ruling Q5).
        foreach (var bad in new[] { "T01 ", " T01", "T01\t" })
            Assert.Throws<ArgumentException>(() => _repo.CreateRun(1, new TransferOptions(), [("T01", "a.b"), (bad, "c.d")]));
        Assert.Equal(before, _repo.Latest()!.Id);   // nothing written by any refused run

        // Ordinal: ids differing only by case are two tasks here, and two rows in the BIN2 checkpoint table.
        long id = _repo.CreateRun(1, new TransferOptions(), [("T01", "a.b"), ("t01", "c.d")]);
        Assert.Equal(new[] { "T01", "t01" }, _repo.Tasks(id).Select(t => t.TaskId));
    }
}
