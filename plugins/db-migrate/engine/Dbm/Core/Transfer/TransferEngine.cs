using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;

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

    public long CreateRun(int sqlVersion, TransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _services.Transfers.CreateRun(sqlVersion, options.Normalized(),
            PlanOrder(_plan).Select(id => (id, _plan.Tasks[id].Target)).ToList());
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
        await using var runLock = await RunLock.AcquireAsync(_targetCs, runId, ct);

        var rc = new RunContext
        {
            Services = _services, RunId = runId, Plan = _plan, SourceCs = _sourceCs, TargetCs = _targetCs,
            Options = run.Options.Normalized(), Control = control,
            Progress = new TransferProgress(runId, _services.Sink, tasks),
            Secrets = Redactor.SecretsOf(_sourceCs).Concat(Redactor.SecretsOf(_targetCs)).ToList(),
        };
        repo.SetRunStatus(runId, RunStatus.Running);
        _services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(RunStatus.Running) });
        rc.Log("info", $"Transfer run {runId}: {tasks.Count} tasks, parallelism {rc.Options.Parallelism}, errors: {rc.Options.ErrorMode}.");
        try
        {
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
                await DropControlTableAsync(rc, ct);
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
    private async Task DropControlTableAsync(RunContext rc, CancellationToken ct)
    {
        if (rc.Options.KeepControlTable) return;
        try
        {
            await using var tgt = await SqlConnect.OpenAsync(_targetCs, ct);
            await ControlTable.DropAsync(tgt, ct);
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
