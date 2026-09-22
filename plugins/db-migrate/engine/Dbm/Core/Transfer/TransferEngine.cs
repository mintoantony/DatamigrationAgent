using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record TransferOutcome(RunStatus Status, string? Error);

/// <summary>Runs (or resumes) one transfer run inside the server process (spec section 9).</summary>
public sealed class TransferEngine
{
    private readonly DbmServices _services;
    private readonly SqlPlanPayload _plan;
    private readonly string _sourceCs;
    private readonly string _targetCs;
    /// <summary>True once this segment has run the plan's PreSql and nothing has undone it (ruling 184).</summary>
    private bool _preSqlApplied;

    public TransferEngine(DbmServices services, SqlPlanPayload plan, string sourceCs, string targetCs)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _sourceCs = sourceCs ?? throw new ArgumentNullException(nameof(sourceCs));
        _targetCs = targetCs ?? throw new ArgumentNullException(nameof(targetCs));
    }

    public static IReadOnlyList<string> PlanOrder(SqlPlanPayload plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var order = plan.Order.Where(plan.Tasks.ContainsKey).Distinct().ToList();
        foreach (var id in plan.Tasks.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!order.Contains(id)) order.Add(id);
        return order;
    }

    private static readonly AsyncLocal<Action<RunLock>?> AfterLockTakenHook = new();

    /// <summary>Test seam (open item 20): invoked with the run's lock right after it is taken, so a test can kill its session. Held in
    /// an <see cref="AsyncLocal{T}"/> so one test's seam cannot reach another's run; null in production.</summary>
    internal static Action<RunLock>? AfterLockTaken
    {
        get => AfterLockTakenHook.Value;
        set => AfterLockTakenHook.Value = value;
    }

    /// <summary>Ruling 212: this workspace as the owner of checkpoint rows and of the target lock - its identity and its folder (a
    /// path, never a connection detail).</summary>
    internal static CheckpointOwner OwnerOf(DbmServices services) => new(services.Transfers.WorkspaceId(), services.Ws.Root);

    /// <summary>
    /// Ruling 212 (b). Refuses <c>run_in_progress</c> when the target's checkpoint table holds unfinished rows of another project: that
    /// project's run is paused, failed or crashed part-way through this target and holds no lock while it waits, so the lock alone
    /// cannot see it. Called with the target lock held (by the engine, and by the start probe). A missing table is no refusal.
    /// </summary>
    /// <param name="fresh">Ruling 215 (N-1): the run has never loaded a row (every task's <c>RowsBefore</c> is null). Such a run owns no
    /// checkpoint yet, so a row this project already has for its run id was written by another copy of this workspace - a copied
    /// project folder carries the state database, and with it the workspace identity - and is refused rather than adopted.</param>
    internal static async Task EnsureNoForeignCheckpointsAsync(SqlConnection tgt, CheckpointOwner owner, long runId, bool fresh,
        string database, CancellationToken ct)
    {
        // N-1 and N-7 (Ruling 216): any row of this run, or any unfinished row of another run of this project, was written by a copy.
        if (fresh && await ControlTable.CopiedWorkspaceRowAsync(tgt, owner, runId, ct) is { } copied)
            throw new TransferException("run_in_progress",
                $"The target database {database} already holds checkpoints of run {copied.RunId} of this project"
                + (copied.Folder.Length > 0 ? $", written from the project folder {copied.Folder}" : "")
                + (copied.RunId == runId ? ", although this run has not loaded anything yet" : ", which is part-way through this target")
                + ". This project folder may be a copy of that one: both carry the same workspace, so their runs cannot be told apart in "
                + "the target, and this run would take over or delete the other's checkpoints. Resume or cancel that run from its own "
                + "folder, and work from one copy of the project only.");
        if (await ControlTable.ForeignUnfinishedAsync(tgt, owner, ct) is not { } other) return;
        // Ruling 216 (L-1): an earlier engine's rows under this very run id may be this run's own, just not matched to what it
        // recorded - dropping the table would destroy them, so that advice is only given for rows that cannot be this run's.
        bool maybeOurs = other.Legacy && !fresh && other.RunId == runId;
        throw new TransferException("run_in_progress",
            $"The target database {database} holds the unfinished checkpoints of {other.Description}"
            + (maybeOurs
                ? ". They may be this run's own checkpoints from before the upgrade, which could not be matched with what this run "
                  + "recorded, so they are left as they are and the run is not continued over them: continuing from a checkpoint that "
                  + "does not match could load rows twice or skip them. Cancel this run and start a new one; while those checkpoints "
                  + "remain, the new run's refusal says what they are and what to do."
                : ": that run is paused, failed or was interrupted part-way through loading this database. A run from here would load "
                  + "beside it, and into the tables it has half-loaded, so it is refused before it copies a row. "
                  + (other.Legacy
                      ? $"Resume or cancel that run from its own project first; if no db-migrate project still uses this target, "
                        + $"{ControlTable.Name} can be dropped."
                      : "Resume or cancel that run from its own project first.")));
    }

    /// <summary>Rulings 215 (N-3) and 216 (L-1): what this run recorded per task, and the largest chunk it can commit - what an earlier
    /// engine's row must match to be claimed as this run's.</summary>
    internal static LegacyClaim ClaimOf(long runId, TransferOptions options, IEnumerable<TransferTaskRow> tasks)
        => new(runId, tasks.Select(t => (t.TaskId, t.RowsDone, t.RowsError)).ToList(), MaxChunk(options));

    /// <summary>The largest chunk a run with these options can commit in one transaction: its chunk size, and never less than the
    /// 1,000-row chunks a task starts with.</summary>
    internal static long MaxChunk(TransferOptions options) => Math.Max(options.Normalized().ChunkSize, 1_000);

    /// <param name="target">Open item 22: recorded on the run - the server and database it loads into.</param>
    public long CreateRun(int sqlVersion, TransferOptions options, (string Server, string Database)? target = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _services.Transfers.CreateRun(sqlVersion, options.Normalized(),
            PlanOrder(_plan).Select(id => (id, _plan.Tasks[id].Target)).ToList(), target);
    }

    /// <summary>Fresh or resume; hard cancellation of <paramref name="ct"/> throws and leaves the run "running" (crash semantics).</summary>
    public async Task<TransferOutcome> RunAsync(long runId, TransferControl control, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        var repo = _services.Transfers;
        var run = repo.GetRun(runId) ?? throw new TransferException("no_run", $"Transfer run {runId} does not exist.");
        if (run.Status is RunStatus.Completed or RunStatus.Cancelled)
            throw new TransferException("run_finished", $"Transfer run {runId} is already {EnumText.ToText(run.Status)}.");
        var tasks = repo.Tasks(runId);
        var missing = tasks.Where(t => !_plan.Tasks.ContainsKey(t.TaskId)).Select(t => t.TaskId).ToList();
        if (missing.Count > 0) throw new TransferException("plan_mismatch", "The SQL plan has no task " + string.Join(", ", missing) + ".");

        // Ruling 103. A "running" run must stay resumable - that is what a crash leaves behind - so status alone cannot say whether a
        // runner is still alive. The lock can, and it is taken before anything is read or written, so a second runner is refused
        // before it copies a row rather than after it has doubled the table.
        // Rulings 209/212: the lock is the target's, so a run of another project loading into this database is refused here as well,
        // told which run and which project folder holds it.
        var owner = OwnerOf(_services);
        await using var runLock = await RunLock.AcquireAsync(_targetCs, runId, ct, owner);
        AfterLockTaken?.Invoke(runLock);
        // Ruling 212, review LOW-1: the database the lock session actually reached, against the one the run recorded. DB_NAME() only:
        // @@SERVERNAME is not stable (LocalDB renames its instance pipe on every start; an availability-group failover changes it).
        if (run.TargetDatabase is { } recorded && !string.Equals(runLock.Database, recorded, StringComparison.OrdinalIgnoreCase))
            throw new TransferException("target_changed",
                $"The target connection now reaches the database {runLock.Database}, but run {runId} loaded into {recorded}. Its "
                + $"checkpoints and the {ControlTable.Name} table live in that database, so it is not continued here. Point the target "
                + $"connection back at {recorded}, or cancel run {runId} and start a new one.");
        // Every checkpoint read and write of this segment is this project's (it flows into the task workers).
        ControlTable.CurrentOwner = owner;
        await using (var tgt = await SqlConnect.OpenAsync(_targetCs, ct))
        {
            bool shapeOk = true;
            try
            {
                await ControlTable.EnsureAsync(tgt, ct);   // creates, or upgrades an earlier engine's table in place
            }
            catch (TransferException ex) when (ex.Code == "control_table_mismatch")
            {
                shapeOk = false;                           // PrepareAsync meets it again and fails the run with it, as before
            }
            if (shapeOk)
            {
                // A resume segment claims the rows an earlier engine wrote for this very run (counters matching what it recorded); a
                // fresh run never adopts a row.
                bool fresh = tasks.All(t => t.RowsBefore is null);
                if (!fresh) await ControlTable.ClaimLegacyAsync(tgt, owner, ClaimOf(runId, run.Options, tasks), ct);
                await EnsureNoForeignCheckpointsAsync(tgt, owner, runId, fresh, runLock.Database, ct);
            }
        }

        var rc = new RunContext
        {
            Services = _services, RunId = runId, Plan = _plan, SourceCs = _sourceCs, TargetCs = _targetCs,
            Options = run.Options.Normalized(), Control = control,
            Progress = new TransferProgress(runId, _services.Sink, tasks),
            Secrets = Redactor.SecretsOf(_sourceCs).Concat(Redactor.SecretsOf(_targetCs)).ToList(),
        };
        // Open item 20: after every chunk commit the lock session is asked whether it still holds the locks. A lost lock pauses the run
        // after the current chunk (a runner elsewhere may already be loading) and says why; a resume takes the lock again.
        int lockLost = 0;
        void CheckLock(ChunkCommit _)
        {
            if (Volatile.Read(ref lockLost) != 0) return;
            // Synchronous on purpose: ChunkCommitted is raised synchronously on the task worker right after its commit, and holding
            // that worker for one short round trip is what makes the pause land before its next chunk. No deadlock: the worker runs on
            // the thread pool with no synchronisation context, and LostAsync touches only the lock's own connection.
            string? lost = runLock.LostAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (lost is null || Interlocked.Exchange(ref lockLost, 1) != 0) return;
            string note = rc.Scrub(lost + " Without it another transfer could load into this target beside this one, so the run was "
                                   + "paused after the current chunk. Resume takes the lock again and continues from the checkpoints.");
            rc.AddNote(note);
            rc.Log("error", note);
            control.RequestPause();
        }
        repo.SetRunStatus(runId, RunStatus.Running);
        _services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(RunStatus.Running) });
        rc.Log("info", $"Transfer run {runId}: {tasks.Count} tasks, parallelism {rc.Options.Parallelism}, errors: {rc.Options.ErrorMode}.");
        try
        {
            control.ChunkCommitted += CheckLock;   // inside the try whose finally removes it
            await PrepareAsync(rc, tasks, ct);
            tasks = repo.Tasks(runId);
            foreach (var t in tasks) rc.Progress.SetSource(t.TaskId, t.RowsSource);

            string? error = await ScheduleAsync(rc, tasks, ct);
            if (error is not null) return Finish(rc, RunStatus.Failed, error);
            bool allDone = repo.Tasks(runId).All(t => t.Status == TransferTaskStatus.Done);
            if (!allDone && control.Kind == StopKind.Cancel)
            {
                // Ruling 184: the plan's PreSql is not left in force behind a cancel. Best effort, one note per statement.
                foreach (var note in await GlobalSql.RestoreAsync(_targetCs, _plan.PostSql, rc.Scrub, ct)) rc.AddNote(note);
                _preSqlApplied = false;
                await DropControlTableAsync(rc, ct, cancelled: true);
                return Finish(rc, RunStatus.Cancelled, null);
            }
            if (!allDone) return Finish(rc, RunStatus.Paused, null);

            await using (var tgt = await SqlConnect.OpenAsync(_targetCs, ct))
            {
                await TargetOps.ExecAllAsync(tgt, _plan.PostSql, ct);
            }
            await DropControlTableAsync(rc, ct);
            string? summary = await FinishCompletedAsync(rc, ct);
            return Finish(rc, RunStatus.Completed, null, summary);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);   // crash semantics: run stays 'running'; RecoverInterrupted pauses it
        }
        catch (Exception ex)
        {
            return Finish(rc, RunStatus.Failed, rc.Describe(ex));
        }
        finally
        {
            control.ChunkCommitted -= CheckLock;
        }
    }

    private async Task PrepareAsync(RunContext rc, IReadOnlyList<TransferTaskRow> tasks, CancellationToken ct)
    {
        // EnsureAsync and DropAsync open their own transaction, so they need a connection with none of ours pending on it.
        await using var tgt = await SqlConnect.OpenAsync(_targetCs, ct);
        await ControlTable.EnsureAsync(tgt, ct);
        await TargetOps.ExecAllAsync(tgt, _plan.PreSql, ct);   // every segment (idempotent); before truncation (V10)
        _preSqlApplied = true;
        if (tasks.All(t => t.RowsBefore is not null)) return;   // setup finished in an earlier segment

        if (rc.Options.TruncateTarget)
            foreach (var line in await TargetOps.TruncateAsync(tgt, tasks.Select(t => t.Target).ToList(), ct))
                rc.Log("warn", line);
        await using var src = await SqlConnect.OpenAsync(_sourceCs, ct);
        foreach (var t in tasks)
        {
            long before = await TargetOps.CountTargetAsync(tgt, t.Target, ct);
            // CountSql is an arbitrary plan field returned verbatim. One that yields NULL or no row leaves the source count unknown,
            // and "unknown" must not be recorded as 0 - a 0 there silences the very warning that says an empty table is not expected.
            long? source = await TargetOps.ScalarLongOrNullAsync(src, TargetOps.CountSqlOf(_plan.Tasks[t.TaskId]), ct);
            rc.Repo.SetTaskCounts(rc.RunId, t.TaskId, source, before);
        }
    }

    private static async Task<string?> ScheduleAsync(RunContext rc, IReadOnlyList<TransferTaskRow> tasks, CancellationToken ct)
    {
        var runner = new TaskRunner(rc);
        var done = new HashSet<string>(tasks.Where(t => t.Status == TransferTaskStatus.Done).Select(t => t.TaskId));
        var remaining = tasks.Where(t => t.Status != TransferTaskStatus.Done).OrderBy(t => t.Ordinal).ToList();
        var running = new Dictionary<Task<TaskResult>, string>();
        int parallel = Math.Max(1, rc.Options.Parallelism);
        string? firstError = null;
        try
        {
            while (true)
            {
                if (!rc.Control.StopRequested)
                {
                    foreach (var t in remaining.ToList())
                    {
                        if (running.Count >= parallel) break;
                        bool ready = rc.Plan.Tasks[t.TaskId].DependsOn
                            .Where(d => d != t.TaskId && rc.Plan.Tasks.ContainsKey(d))
                            .All(done.Contains);
                        if (!ready) continue;
                        remaining.Remove(t);
                        running.Add(Task.Run(() => runner.RunAsync(t, ct), CancellationToken.None), t.TaskId);
                    }
                }
                if (running.Count == 0) break;
                var finished = await Task.WhenAny(running.Keys);
                running.Remove(finished);
                var result = await finished;
                if (result.Status == TransferTaskStatus.Done) done.Add(result.TaskId);
                else if (result.Status == TransferTaskStatus.Failed)
                {
                    firstError ??= $"{result.TaskId}: {TransferFailure.NonBlank(result.Error, "the task failed without an error message")}";
                    rc.Control.RequestFail();
                }
            }
        }
        catch
        {
            try { await Task.WhenAll(running.Keys); }
            catch (Exception) { /* the other workers observe the same token */ }
            throw;
        }
        if (firstError is null && !rc.Control.StopRequested && remaining.Count > 0)
            firstError = "Unsatisfiable task dependencies: " + string.Join(", ", remaining.Select(t => t.TaskId));
        return firstError;
    }

    /// <summary>
    /// Drops our checkpoint table, or - when a table of that name in the target is not ours - says so and leaves it alone. ControlTable
    /// refuses to drop a foreign table (ruling H1) by throwing; uncaught here that would report a migration which loaded every row as a
    /// failed run, and swallowed it would leave the table standing with nothing saying why. The note goes into the run's summary.
    /// </summary>
    private async Task DropControlTableAsync(RunContext rc, CancellationToken ct, bool cancelled = false)
    {
        var owner = OwnerOf(_services);
        if (rc.Options.KeepControlTable && !cancelled) return;   // a completed run's rows are all done: no claim on the target
        try
        {
            await using var tgt = await SqlConnect.OpenAsync(_targetCs, ct);
            if (cancelled)
            {
                // Ruling 215 (N-3): an earlier engine's rows of this run are this run's to end as well.
                try
                {
                    await ControlTable.ClaimLegacyAsync(tgt, owner, ClaimOf(rc.RunId, rc.Options, rc.Repo.Tasks(rc.RunId)), ct);
                }
                catch (TransferException ex) when (ex.Code == "run_in_progress")
                {
                    rc.AddNote(ex.Message + " The earlier version's checkpoints were left as they are.");
                }
                if (rc.Options.KeepControlTable)
                {
                    // N-2: the option keeps the table for auditing, never a claim on the target - the rows are marked done.
                    await ControlTable.RetireRunAsync(tgt, owner, rc.RunId, ct);
                    return;
                }
            }
            // Ruling 212 (c): this project's rows go; the table goes only if nothing of another project is left in it.
            if (!await ControlTable.ReleaseAsync(tgt, owner, rc.RunId, ct))
            {
                string note = $"{ControlTable.Name} was left in the target: this run's checkpoints were removed, but it still holds "
                              + "another project's checkpoints.";
                rc.AddNote(note);
                rc.Log("info", note);
            }
        }
        catch (TransferException ex) when (ex.Code == "control_table_mismatch")
        {
            string note = $"{ControlTable.Name} in the target is not ours; it was left untouched (control_table_mismatch).";
            rc.AddNote(note);
            rc.Log("warn", $"{note} {ex.Message}");
        }
    }

    /// <summary>
    /// What the run carries out with it when nothing else will: the error, and the notes the run collected (a control table that was
    /// not ours, rejected rows counted but never recorded). 5.4's own summary replaces this one, and must carry the notes forward.
    /// </summary>
    private static string? RunSummary(RunContext rc, string? error)
    {
        var notes = rc.Notes;
        if (error is null && notes.Count == 0) return null;
        List<string>? list = notes.Count == 0 ? null : notes.ToList();
        return Json.Serialize(new { error, notes = list });
    }

    /// <summary>
    /// Completion: validate every task (counts, and checksums where they can mean something), then build the final report, which is
    /// what <c>summary_json</c> stores from here on.
    /// <para><b>Nothing in here may fail the run.</b> Validation runs after the last row is loaded and after the run's own PostSql -
    /// operator SQL that may well have dropped a table the plan loaded into - and an exception escaping this method is caught by
    /// <see cref="RunAsync"/>'s general handler, which records the run as failed. A migration that moved every row would then be
    /// reported as a failure because the thing meant to confirm it could not run, and an operator would go and re-run a migration that
    /// was already correct. So every fault below becomes a note instead. "Never fails the run" is not permission to go quiet: the
    /// report names what it could not check, and a task with no validation is listed as unvalidated rather than left looking clean.</para>
    /// <para>This hook runs once, at run end, after the last task. It is deliberately nowhere near the per-chunk commit path: a write
    /// between a chunk's COMMIT and its checkpoint upsert double-loads that chunk on resume (rulings 114/115), and nothing here touches
    /// that seam.</para>
    /// </summary>
    private async Task<string?> FinishCompletedAsync(RunContext rc, CancellationToken ct)
    {
        await ValidateTasksAsync(rc, ct);
        try
        {
            var run = rc.Repo.GetRun(rc.RunId)!;
            // rc.Notes, verbatim: this report replaces RunSummary, which is the only thing that carried them into the run's outcome
            // record (the control table that was not ours, rejected rows counted but never recorded). Without this they would survive
            // only as log lines and the run's own outcome would stop telling the truth.
            var report = FinalReportBuilder.Build(run, RunStatus.Completed, rc.Repo.Tasks(rc.RunId),
                taskId => rc.Repo.ErrorRows(rc.RunId, taskId, FinalReportBuilder.MaxErrorSamples), Clock.Now(),
                rc.Notes, taskId => rc.Repo.ErrorRowCount(rc.RunId, taskId));
            return Json.Serialize(report);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Returning null falls back to RunSummary, which still carries the notes - a thinner outcome record, never a failed run.
            rc.Log("warn", $"Transfer run {rc.RunId}: the final report could not be built: {rc.Describe(ex)}");
            rc.AddNote($"The final report could not be built ({rc.Describe(ex)}); the run itself completed and its data is unaffected.");
            return null;
        }
    }

    private async Task ValidateTasksAsync(RunContext rc, CancellationToken ct)
    {
        try
        {
            await using var src = await SqlConnect.OpenAsync(_sourceCs, ct);
            await using var tgt = await SqlConnect.OpenAsync(_targetCs, ct);
            foreach (var row in rc.Repo.Tasks(rc.RunId))
            {
                try
                {
                    var v = await RunValidator.ValidateTaskAsync(src, tgt, _plan.Tasks[row.TaskId], row, rc.Options.ValidateChecksums, ct);
                    rc.Repo.SetTaskValidation(rc.RunId, row.TaskId, Json.Serialize(v with
                    {
                        // The skip reason can quote a SQL error, so it is scrubbed - and scrubbing can empty a message whose whole
                        // content was a secret. An empty string still serialises (only nulls vanish), but it would say nothing, and the
                        // one thing this field exists for is to say why the checksum list is empty.
                        ChecksumsSkipped = v.ChecksumsSkipped is null ? null
                            : TransferFailure.NonBlank(rc.Scrub(v.ChecksumsSkipped), "the reason was removed because it held a secret"),
                    }));
                    if (!v.CountMatch)
                        rc.Log("warn", $"{row.TaskId} {row.Target}: {v.CountNote ?? "the row counts were not confirmed"}.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    string note = $"{row.TaskId} {row.Target}: could not be validated ({rc.Describe(ex)}). Its row counts and values "
                                  + "are unconfirmed - the rows it loaded are unaffected.";
                    rc.AddNote(note);
                    rc.Log("warn", note);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            string note = $"No task of run {rc.RunId} could be validated ({rc.Describe(ex)}); nothing in this report's row counts or "
                          + "checksums was checked. The rows the run loaded are unaffected.";
            rc.AddNote(note);
            rc.Log("warn", note);
        }
    }

    private TransferOutcome Finish(RunContext rc, RunStatus status, string? error, string? summaryJson = null)
    {
        // Ruling 184: a failed or paused run keeps the plan's PreSql in force on purpose (Resume expects it), and says so.
        if (status is RunStatus.Failed or RunStatus.Paused && _preSqlApplied && GlobalSql.InForceNote(_plan.PreSql) is { } inForce)
            rc.AddNote(inForce);
        string text = EnumText.ToText(status);
        rc.Repo.SetRunStatus(rc.RunId, status, summaryJson ?? RunSummary(rc, error));
        _services.Sink.Publish("transfer_run_changed", new { runId = rc.RunId, status = text });
        rc.Progress.RunStatus = text;
        rc.Progress.Publish(force: true);
        rc.Log(status == RunStatus.Failed ? "error" : "info",
            error is null ? $"Transfer run {rc.RunId} {text}." : $"Transfer run {rc.RunId} failed: {error}");
        return new TransferOutcome(status, error);
    }
}
