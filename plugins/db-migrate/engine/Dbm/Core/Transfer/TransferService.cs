using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>
/// The latest run, as the execute screen reads it.
/// </summary>
/// <param name="Error">The run's own error, from <c>summary_json</c>, and only for a failed run - a null here is "this run did not
/// fail", not "nobody looked".</param>
/// <param name="HasReport">True only when a Complete artifact was <b>stored</b> for this run (ruling 117). The service never builds a
/// report on demand, so this is never true for a run that did not complete.</param>
public sealed record TransferRunView(long Id, int SqlVersion, RunStatus Status, TransferOptions Options, DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt, string? Error, bool HasReport)
{
    /// <summary>
    /// The notes the run carried out with it (<c>summary_json.notes</c>): a checkpoint table that was not ours, rejected rows counted
    /// but never recorded (5.3 ruling 105), a validator that could not run (5.4). Passed through word for word and never summarised
    /// into a flag - a completed run with notes is not the same thing as a clean one, and this is the only place the difference shows.
    /// </summary>
    public List<string> Notes { get; init; } = [];
}

/// <summary>
/// One task of the latest run, or - before the first run - one task of the approved plan shown as <c>pending</c>.
/// </summary>
/// <param name="RowsSource">The snapshot taken when the task's first segment started (5.3 F10), or null when this run never
/// established one. Never 0 for "unknown".</param>
/// <param name="Keyless">No usable key: the task loads in one transaction, ignores a pause until its table is done and restarts from
/// scratch after a failure (rulings 104, 5.3 F9). False also when the task's plan is not available, so it is a claim only when a plan
/// was read.</param>
public sealed record TransferTaskView(string TaskId, string Target, int Ordinal, TransferTaskStatus Status, long? RowsSource, long? RowsBefore,
    long RowsDone, long RowsError, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? HeartbeatAt, string? Error,
    TaskValidation? Validation, List<string> DependsOn, bool Keyless)
{
    /// <summary>What <see cref="Status"/> means once the run's own status is taken into account (5.4's join). A keyless task stopped by
    /// Cancel or Fail rolls back and records <c>paused</c>: read alone it says an operator paused it and that a resume will pick it up,
    /// and neither is true.</summary>
    public string? StatusNote { get; init; }

    /// <summary>Why <see cref="Validation"/> is null although something was recorded for this task (ruling 132): the stored validation
    /// will not parse. A null <see cref="Validation"/> otherwise means no validation was recorded at all, and the two must not look
    /// alike - one says nothing was checked, the other says the record of what was checked is unreadable.</summary>
    public string? ValidationNote { get; init; }
}

/// <param name="RowsSource">The sum over the tasks that <b>have</b> a source count. While <see cref="TasksWithoutSource"/> is non-zero
/// this is a floor, not a total, and a 0 beside a non-zero <see cref="TasksWithoutSource"/> means nobody has counted yet.</param>
public sealed record TransferTotals(long RowsSource, long RowsDone, long RowsError, int TasksDone, int TasksTotal)
{
    /// <summary>How many tasks have no source count. Always serialised (not nullable), so a reader sees the 0 when there are none.</summary>
    public int TasksWithoutSource { get; init; }
}

/// <param name="SqlVersion">The approved SQL version, or <b>null</b> when there is no approved plan - never 0, which is a real version
/// number here (5.4 carry-forward 2). <see cref="PlanNote"/> says which.</param>
public sealed record TransferView(TransferRunView? Run, List<TransferTaskView> Tasks, TransferTotals Totals, bool Active, bool CanStart,
    string? TargetDatabase, int? SqlVersion, PreflightResult? Preflight, TransferOptions Defaults)
{
    /// <summary>Why <see cref="CanStart"/> is false; null exactly when it is true. A disabled button with no sentence beside it is the
    /// defect this field exists to stop.</summary>
    public string? CannotStart { get; init; }

    /// <summary>Whether a paused or failed run can be continued. The target-identity check that <see cref="TransferService.Resume"/>
    /// also makes is not repeated here: it reads the discovered catalog, which is too big to deserialise on every poll.</summary>
    public bool CanResume { get; init; }

    /// <summary>Why <see cref="CanResume"/> is false; null exactly when it is true.</summary>
    public string? CannotResume { get; init; }

    /// <summary>"pausing" / "cancelling" / "failing" while a stop has been asked for and the run has not reached it yet; null
    /// otherwise. The run row still reads <c>running</c> for that whole window - a keyless task holds it until its table is done - so
    /// without this the screen shows a transfer that is ignoring the button.</summary>
    public string? Stopping { get; init; }

    /// <summary>Why <see cref="Tasks"/> is empty or short, and why <see cref="SqlVersion"/> is null: no approved plan, an artifact that
    /// is gone, or one that cannot be read. An empty task list with nothing beside it reads as a plan with nothing in it.</summary>
    public string? PlanNote { get; init; }
}

/// <summary>
/// Server singleton (<c>WebState.Transfer</c>): owns the one background run of this process and connects it to the workflow.
/// <para><b>This is the only caller of <see cref="TransferEngine.RunAsync"/>.</b> Ruling 103's ordering follows from that:
/// <see cref="RecoverInterrupted"/> runs once at server start, before any run can be launched, because a run left <c>running</c> by a
/// dead process is the same row as one being executed right now.</para>
/// <para>Two guards stand in front of a second runner. This class's own <c>busy</c> refusal covers this process; the engine's
/// <see cref="RunLock"/> covers two processes sharing one target. The lock's refusal is asked for here, before a run is launched, so
/// that it reaches the operator as "busy, try again" rather than as a run recorded <c>failed</c>.</para>
/// </summary>
public sealed class TransferService
{
    /// <summary>How long a passing pre-flight still describes the start it was taken for.</summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(15);

    private readonly DbmServices _services;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _active, _starting;
    /// <summary>The run <see cref="_control"/> belongs to, so a stop request can name it without re-reading the row it is stopping
    /// (which is also the row that may not parse - ruling 142).</summary>
    private long _activeRunId;
    private Task? _current;
    private TransferControl? _control;
    private TransferOptions? _preflightOptions;
    private PreflightResult? _lastPreflight;

    public TransferService(DbmServices services) => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Raised on the worker thread right after a chunk and its checkpoint commit. 5.6 subscribes for progress; tests use it to
    /// stop a run at a known instant.</summary>
    public event Action<ChunkCommit>? ChunkCommitted;

    private static readonly AsyncLocal<Func<Task>?> AfterFirstChunkHook = new();

    /// <summary>
    /// Test seam (ruling 133; 5.3's <c>TaskRunner.AfterChunkTransaction</c> is the precedent). Invoked once per launched segment, on the
    /// worker, immediately after that segment's <b>first</b> chunk and checkpoint commit - the one instant where the run is provably
    /// live, has written rows, and is still executing. <see cref="RunBackgroundAsync"/> <b>awaits</b> it, so a test can read
    /// <see cref="IsActive"/>, <see cref="View"/> and <see cref="Pause"/> there instead of racing them.
    /// <para>Without it the only way to observe a live run is a subscriber on <see cref="ChunkCommitted"/>, which is why the
    /// two-concurrent-resumes test's kill landed on an observation that never happened (<c>Actual: null</c>) rather than on the harm it
    /// names. Held in an <see cref="AsyncLocal{T}"/> so one test's seam cannot reach another test's run, and <b>null in production</b>,
    /// where it costs one null check per launched segment and nothing per chunk.</para>
    /// </summary>
    internal static Func<Task>? AfterFirstChunk
    {
        get => AfterFirstChunkHook.Value;
        set => AfterFirstChunkHook.Value = value;
    }

    public PreflightResult? LastPreflight { get { lock (_lock) return _lastPreflight; } }

    public bool IsActive { get { lock (_lock) return _active; } }

    /// <summary>The background run, or <see cref="Task.CompletedTask"/> when idle.</summary>
    public Task Current { get { lock (_lock) return _current ?? Task.CompletedTask; } }

    /// <summary>
    /// Server start, once, before anything can reach <see cref="TransferEngine.RunAsync"/>: runs and tasks a dead process left
    /// <c>running</c> become <c>paused</c>, which is resumable from their checkpoints.
    /// <para>The return is the number of <b>runs</b> changed. A 0 means "nothing was left running" and nothing else: a state database
    /// that cannot be read throws out of here rather than answering with a number, because a 0 that can also mean "could not look" is
    /// the one answer a caller cannot act on. Orphaned tasks under an already-paused run are repaired but not counted (5.1's
    /// contract), which is why the log line says runs.</para>
    /// </summary>
    public int RecoverInterrupted()
    {
        int n = _services.Transfers.RecoverInterrupted();
        if (n > 0)
            _services.Sink.Publish("log", new { level = "warn",
                message = $"{n} interrupted transfer run(s) marked paused; resume from the Execute screen." });
        return n;
    }

    /// <summary>
    /// The execute screen's checklist, and <b>the boundary for everything that can go wrong producing one</b> (ruling 123).
    /// <para><c>Preflight.RunAsync</c> never throws for a SQL fault (ruling 119), but it can still throw before it reaches a probe at
    /// all: a connection row that cannot be decrypted (protected under another Windows user), a fault in <c>Redactor.SecretsOf</c>, or
    /// a plan artifact whose task map has a null value - <c>"tasks": {"T01": null}</c> passes <c>PlanOrder</c>'s <c>ContainsKey</c>
    /// filter and raises a <see cref="NullReferenceException"/> from <c>PlanCheck</c>, outside every catch. None of those may reach the
    /// browser as a 500: an exception loses every line of the checklist that already passed, including the one naming the problem. So
    /// the fault becomes a checklist line of its own - named <c>preflight</c>, never ok, severity error, so
    /// <see cref="PreflightResult.Passed"/> cannot come back true over a checklist nobody could produce.</para>
    /// </summary>
    public async Task<PreflightResult> PreflightAsync(TransferOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Outside the boundary on purpose: bad options are the caller's mistake and must stay a 409 "bad_options", not a checklist
        // line about a transfer nobody asked to check.
        var normalized = options.Normalized();
        PreflightResult result;
        try
        {
            result = MalformedPlanResult() ?? await Preflight.RunAsync(_services, normalized, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            result = CouldNotRun(ex);
        }
        lock (_lock)
        {
            _lastPreflight = result;
            _preflightOptions = normalized;
        }
        return result;
    }

    /// <summary>
    /// Starts a new run (contract C7's start rules) and returns its id; the run itself proceeds in the background on
    /// <see cref="Current"/>.
    /// </summary>
    public async Task<long> StartAsync(TransferOptions options, string confirmTarget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.Normalized();
        lock (_lock)
        {
            if (_active || _starting) throw new TransferException("busy", "A transfer is already running.");
            _starting = true;
        }
        try
        {
            var latest = _services.Transfers.Latest();
            if (latest?.Status == RunStatus.Paused)
                throw new TransferException("paused_run", $"Run {latest.Id} is paused; resume or cancel it first.");
            // A restart abandons a failed or cancelled run for a NEW run id: its checkpoint rows are ignored, and the __dbm_* tables
            // are ours, so they never count as schema drift.
            bool restart = _services.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Running
                           && latest?.Status is RunStatus.Failed or RunStatus.Cancelled;
            if (!restart && _services.Phases.Get(PhaseName.Ready).Status != PhaseStatus.AwaitingReview)
                throw new TransferException("not_ready", "The transfer starts from the Ready step: approve the SQL phase first.");
            var savedTarget = SavedTargetMeta();
            // Ordinal, and only after Trim: this is the last gate before a database is written to, so it is exact (R6). A near miss
            // accepted here makes the typed confirmation a formality.
            if (savedTarget?.Database is not { } database || !string.Equals((confirmTarget ?? "").Trim(), database, StringComparison.Ordinal))
                throw new TransferException("confirm_mismatch", "Type the target database name exactly to confirm the transfer.");
            EnsureTargetMatchesTheDiscoveredCatalog(savedTarget);
            ApprovedPlan approved;
            try
            {
                approved = Preflight.LoadApprovedPlan(_services) ?? throw new TransferException("not_ready", "The SQL phase is not approved.");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
                // Ruling 123 at the start door: the plan artifact is read here before pre-flight can report anything about it.
                throw new TransferException("not_ready", "The approved SQL plan could not be read: " + Describe(ex)
                                                         + " Re-generate the SQL phase and approve it again.");
            }
            if (MalformedTasks(approved.Plan) is { Count: > 0 } malformed)
                throw new TransferException("not_ready", $"The approved SQL plan artifact (sql v{approved.Version}) is malformed: "
                                                         + string.Join(", ", malformed) + " has no task body. Re-generate the SQL phase.");
            var (srcCs, tgtCs) = ConnectionStrings();

            PreflightResult? pre;
            TransferOptions? preOptions;
            lock (_lock)
            {
                pre = _lastPreflight;
                preOptions = _preflightOptions;
            }
            // Structural, not reference: both sides are normalised copies, so a reference test would re-run the checklist every time
            // and a missing test would start a run on a checklist taken for different options against a target that has since changed.
            if (restart || pre is null || !pre.Passed || pre.SqlVersion != approved.Version || Clock.Now() - pre.At > PreflightMaxAge
                || preOptions != options)
                pre = await PreflightAsync(options, ct);
            if (!pre.Passed)
                throw new TransferException("preflight_failed", "Pre-flight checks failed.",
                    pre.Checks.Where(c => !c.Ok && c.Severity == "error").Select(c => $"{c.Name}: {c.Detail}").ToList());

            var engine = new TransferEngine(_services, approved.Plan, srcCs, tgtCs);
            long runId = engine.CreateRun(approved.Version, options);
            await EnsureNoOtherRunnerAsync(runId, tgtCs, ct);
            if (!restart) _services.Workflow.OnTransferStarted();
            Launch(engine, runId, srcCs, tgtCs, RunOrigin.Created);
            return runId;
        }
        finally
        {
            lock (_lock) _starting = false;
        }
    }

    /// <summary>Asks running tasks to stop after their current chunk. Keyless tasks are one transaction and keep loading until their
    /// table is done (rulings 104, 5.3 F9), so the run stays <c>running</c> - and <c>TransferView.Stopping</c> is what says so.</summary>
    public void Pause()
    {
        TransferControl? control;
        long runId;
        lock (_lock)
        {
            control = _active ? _control : null;
            runId = _activeRunId;
        }
        if (control is null) throw new TransferException("not_running", "No transfer is running.");
        control.RequestPause();
        _services.Sink.Publish("log", new { level = "info", message = "Pause requested: running tasks stop after their current chunk." });
        AnnounceStopping(runId, StopKind.Pause);
    }

    /// <summary>
    /// Ruling 140. A stop request changes what the execute screen must show - <see cref="TransferView.Stopping"/>, and with it a Pause
    /// button that has already been spent - but it changes nothing in the run row, which still reads <c>running</c> until the last
    /// keyless table finishes. Without an event the screen has no reason to re-read, so a pause pressed anywhere but in this tab (the
    /// CLI, an agent in a terminal, a second browser) never reaches it: the operator watches a live "Running" with an armed Pause and
    /// presses it again. The spec's requirement is that the screen shows the run's state whoever drove it, and that is this event.
    /// <para>The payload carries the run's real status (still running) and what was asked for; the screen re-reads the view rather
    /// than trusting the payload, so this only has to say "something changed" truthfully.</para>
    /// </summary>
    private void AnnounceStopping(long runId, StopKind kind) =>
        _services.Sink.Publish("transfer_run_changed",
            new { runId, status = EnumText.ToText(RunStatus.Running), stopping = StopText(kind) });

    /// <summary>
    /// Continues the latest paused or failed run from its checkpoints and returns its id.
    /// <para><b>Ruling 129: this takes the same latch <see cref="StartAsync"/> takes, test-and-set under one lock.</b> Merely reading
    /// <c>_active</c>/<c>_starting</c> lets two resumes - a double-click, or the UI button plus <c>dbm transfer resume</c> - both get
    /// past the check and both <see cref="Launch"/>. The second overwrites <c>_control</c>/<c>_current</c>/<c>_active</c>, the engine's
    /// run lock refuses it, and its <see cref="RecordNotRun"/> and <c>finally</c> then write state belonging to the runner that won:
    /// for the rest of the transfer the run row reads <c>paused</c>, <see cref="IsActive"/> reads false and <see cref="Pause"/> answers
    /// <c>not_running</c> while rows are being written - and the operator's next click, Cancel, takes the paused branch and drops the
    /// checkpoint table under the live runner. A second caller is refused <c>busy</c> here and touches nothing.</para>
    /// </summary>
    public long Resume()
    {
        lock (_lock)
        {
            if (_active || _starting) throw new TransferException("busy", "The transfer is already running.");
            _starting = true;
        }
        try
        {
            var run = _services.Transfers.Latest();
            if (run is null || run.Status is not (RunStatus.Paused or RunStatus.Failed))
                throw new TransferException("not_resumable", "There is no paused or failed transfer run to resume.");
            EnsureTargetIsTheOneTheRunStartedIn(run);
            var artifact = _services.Artifacts.Get(PhaseName.Sql, run.SqlVersion)
                ?? throw new TransferException("no_plan", $"SQL plan v{run.SqlVersion} of run {run.Id} is missing.");
            var (srcCs, tgtCs) = ConnectionStrings();
            var engine = new TransferEngine(_services, Json.Deserialize<SqlPlanPayload>(artifact.PayloadJson), srcCs, tgtCs);
            Launch(engine, run.Id, srcCs, tgtCs, RunOrigin.Found(run.Status));
            _services.Sink.Publish("log", new { level = "info", message = $"Resuming transfer run {run.Id} from its checkpoints." });
            return run.Id;
        }
        finally
        {
            // Safe to drop here and not before: Launch set _active under the same lock, so the latch is never released into a gap.
            lock (_lock) _starting = false;
        }
    }

    /// <summary>Running: stop after the current chunk. Paused or failed: cancel now, and drop the checkpoint table.</summary>
    public async Task CancelAsync(CancellationToken ct)
    {
        TransferControl? control;
        long activeRunId;
        lock (_lock)
        {
            control = _active ? _control : null;
            activeRunId = _activeRunId;
        }
        if (control is not null)
        {
            control.RequestCancel();
            _services.Sink.Publish("log", new { level = "warn", message = "Cancel requested: running tasks stop after their current chunk." });
            // Ruling 140, the same door: a cancel asked for from the CLI moves the screen to "cancelling…" and disarms both buttons.
            AnnounceStopping(activeRunId, StopKind.Cancel);
            return;
        }
        var run = _services.Transfers.Latest();
        if (run is null || run.Status is not (RunStatus.Paused or RunStatus.Failed))
            throw new TransferException("not_cancellable", "There is no running, paused or failed transfer to cancel.");
        if (!run.Options.KeepControlTable) await DropControlTableAsync(ConnectionStrings().Target, ct);
        _services.Transfers.SetRunStatus(run.Id, RunStatus.Cancelled);
        _services.Sink.Publish("transfer_run_changed", new { runId = run.Id, status = EnumText.ToText(RunStatus.Cancelled) });
        _services.Workflow.OnTransferFinished("cancelled", null);
    }

    /// <summary>
    /// Server shutdown: hard-stops the background run. This is crash semantics on purpose - the run stays <c>running</c> and the next
    /// process recovers it as <c>paused</c>, resumable from the chunks that committed. Draining instead would hold the shutdown for as
    /// long as the table takes, and a keyless task cannot be drained at all.
    /// </summary>
    public async Task StopAsync()
    {
        _lifetime.Cancel();
        try
        {
            await Current.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception)
        {
            // cancelled or timed out: the process is ending, and the run is recovered as paused on the next start
        }
    }

    public TransferView View()
    {
        TransferRunRow? run;
        IReadOnlyList<TransferTaskRow> taskRows;
        try
        {
            run = _services.Transfers.Latest();
            taskRows = run is null ? [] : _services.Transfers.Tasks(run.Id);
        }
        catch (JsonException ex)
        {
            // Ruling 132. TransferRepo.MapRun deserialises options_json, so a saved record that will not parse throws here - before
            // this method has a row to degrade. It must still answer with a view: the endpoint's Guard maps a JsonException to
            // 400 "Invalid JSON body", which is a GET with no body being told its body is invalid, above a blank execute screen.
            return UnreadableRunView(ex);
        }
        var (approved, planNote) = PlanForView();
        var (plan, runPlanNote) = PlanOfRun(run, approved);
        planNote = runPlanNote ?? planNote;
        var (targetDatabase, targetProblem) = SavedTargetDatabase();
        if (targetProblem is not null) planNote = planNote is null ? targetProblem : planNote + " " + targetProblem;

        List<TransferTaskView> tasks;
        if (run is null)
        {
            tasks = plan is null ? [] : PlanTaskIds(plan).Select((id, i) => new TransferTaskView(id, plan.Tasks[id].Target, i,
                TransferTaskStatus.Pending, null, null, 0, 0, null, null, null, null, null, plan.Tasks[id].DependsOn.ToList(),
                plan.Tasks[id].KeyColumns.Count == 0)).ToList();
        }
        else
        {
            tasks = taskRows.Select(t =>
            {
                var tp = plan?.Tasks.GetValueOrDefault(t.TaskId);
                var (validation, validationNote) = ValidationOf(t);
                return new TransferTaskView(t.TaskId, t.Target, t.Ordinal, t.Status, t.RowsSource, t.RowsBefore, t.RowsDone, t.RowsError,
                    t.StartedAt, t.EndedAt, t.HeartbeatAt, t.Error, validation,
                    tp?.DependsOn.ToList() ?? [], tp is not null && tp.KeyColumns.Count == 0)
                {
                    StatusNote = FinalReportBuilder.StatusNote(t.Status, run.Status),
                    ValidationNote = validationNote,
                };
            }).ToList();
        }

        var totals = new TransferTotals(tasks.Sum(t => t.RowsSource ?? 0), tasks.Sum(t => t.RowsDone), tasks.Sum(t => t.RowsError),
            tasks.Count(t => t.Status == TransferTaskStatus.Done), tasks.Count)
        {
            TasksWithoutSource = tasks.Count(t => t.RowsSource is null),
        };

        bool active;
        string? stopping;
        lock (_lock)
        {
            active = _active;
            stopping = _active && _control is { StopRequested: true } c ? StopText(c.Kind) : null;
        }
        // A target whose saved details cannot be read is a hard blocker for a start - the typed confirmation compares the very name
        // that could not be read - so it is the reason, ahead of every other.
        string? cannotStart = targetProblem ?? WhyNotStart(run, approved, planNote, active);
        string? cannotResume = WhyNotResume(run, active);
        var runView = run is null ? null : new TransferRunView(run.Id, run.SqlVersion, run.Status, run.Options, run.StartedAt, run.EndedAt,
            RunError(run), HasStoredReport(run))
        {
            Notes = RunNotes(run),
        };
        return new TransferView(runView, tasks, totals, active, cannotStart is null, targetDatabase,
            approved?.Version, LastPreflight, new TransferOptions())
        {
            CannotStart = cannotStart,
            CanResume = cannotResume is null,
            CannotResume = cannotResume,
            Stopping = stopping,
            PlanNote = planNote,
        };
    }

    // ------------------------------------------------------------------ start / resume guards

    /// <summary>
    /// Ruling 103's lock, asked for before the run is launched instead of only inside <see cref="TransferEngine.RunAsync"/>. A refusal
    /// there arrives on the background task, where the only honest thing left to do with it is to record the run - so the operator would
    /// be told a transfer started, and then that it broke. Here it is what it is: busy, with the lock's own sentence, and a run row
    /// closed as <c>cancelled</c> so the next start is not blocked by a run that never ran.
    /// <para>The lock is released again immediately and the engine re-takes it, so a runner that appears in between is still refused -
    /// by <see cref="RunBackgroundAsync"/>, which leaves the run <c>paused</c> and carries the same sentence in its notes.</para>
    /// </summary>
    private async Task EnsureNoOtherRunnerAsync(long runId, string targetCs, CancellationToken ct)
    {
        try
        {
            var probe = await RunLock.AcquireAsync(targetCs, runId, ct);
            await probe.DisposeAsync();
        }
        catch (TransferException ex) when (ex.Code == "run_in_progress")
        {
            _services.Transfers.SetRunStatus(runId, RunStatus.Cancelled, Json.Serialize(new { notes = new[] { ex.Message } }));
            _services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(RunStatus.Cancelled) });
            throw new TransferException("busy", ex.Message);
        }
    }

    /// <summary>
    /// A resume continues the run whose checkpoints and <c>__dbm_*</c> control tables live in the target it started in; there is no
    /// "change the target connection and resume" path. <c>POST /api/connection/tgt</c> saves the new connection <b>before</b> the
    /// workflow refuses the change, so the saved target really can end up somewhere else - and a resume against it would find no
    /// checkpoints and load every row again into a database nobody reviewed.
    /// <para>The database the run loaded into is the one discovery recorded and the SQL plan was written against, so the discovered
    /// catalog's <c>ServerMeta</c> is what the saved connection is checked against. When there is none there is nothing to check
    /// against, and that is said rather than taken as a match.</para>
    /// </summary>
    private void EnsureTargetIsTheOneTheRunStartedIn(TransferRunRow run)
    {
        var saved = SavedTargetMeta()
            ?? throw new TransferException("no_connection", "The target connection is not saved, so run "
                                                            + $"{run.Id} cannot be continued against the database it started in.");
        var (discovered, problem) = DiscoveredTarget();
        // Ruling 127, fail closed: "there is nothing to compare against" is not "they match". Failing open here would resume the run
        // into whatever the target connection happens to point at today, which is the one thing this guard exists to stop.
        if (discovered is null)
            throw new TransferException("target_unknown",
                $"Because {problem}, it cannot be confirmed that {saved.Database} on {saved.Server} is the database run {run.Id} "
                + "loaded into. Re-run discovery, or cancel this run and start a new one.");
        if (Same(saved.Database, discovered.Database) && Same(saved.Server, discovered.Server)) return;
        throw new TransferException("target_changed",
            $"The saved target connection now points at {saved.Database} on {saved.Server}, but run {run.Id} loaded into "
            + $"{discovered.Database} on {discovered.Server}. Its checkpoints and the {ControlTable.Name} table live in that database, "
            + "so resuming against this one would load every row again from the beginning. Point the target connection back at "
            + $"{discovered.Database}, or cancel run {run.Id} and start a new one.");
    }

    /// <summary>
    /// Ruling 128, the start-side twin. The typed confirmation compares the name the <b>saved connection</b> reports, and
    /// <c>schema_drift</c> compares a fingerprint that is purely structural - so a target repointed at a different database with the
    /// same schema satisfies both, and a plan generated for one database is loaded into another. What the plan was written against is
    /// the discovered catalog, and that is what the saved connection has to still match.
    /// </summary>
    private void EnsureTargetMatchesTheDiscoveredCatalog(ServerMeta saved)
    {
        var (discovered, problem) = DiscoveredTarget();
        if (discovered is null)
            throw new TransferException("not_ready",
                $"The transfer cannot start because {problem}: the SQL plan was generated against the discovered target, and there is "
                + "nothing to check the saved connection against. Re-run discovery.");
        if (Same(saved.Database, discovered.Database) && Same(saved.Server, discovered.Server)) return;
        throw new TransferException("not_ready",
            $"The saved target connection no longer matches the discovered catalog ({saved.Database} on {saved.Server} against "
            + $"{discovered.Database} on {discovered.Server}); re-run discovery. The SQL plan was generated for "
            + $"{discovered.Database}, and neither the typed confirmation nor the schema-drift check can tell a different database "
            + "with the same schema apart from it.");
    }

    /// <summary>
    /// The target <see cref="ServerMeta"/> discovery recorded - the database the plan was generated against. Null <b>with a sentence
    /// saying why</b>, never a bare null: "nobody recorded one" and "the record will not parse" send an operator to two different
    /// places, and both are refusals rather than matches.
    /// </summary>
    private (ServerMeta? Meta, string? Problem) DiscoveredTarget()
    {
        try
        {
            var meta = _services.Catalog.Get(Side.Tgt)?.Server;
            return meta is null || string.IsNullOrWhiteSpace(meta.Database)
                ? (null, "no discovered target catalog is recorded in this workspace")
                : (meta, null);
        }
        catch (JsonException ex)
        {
            return (null, "the discovered target catalog could not be read (" + Describe(ex) + ")");
        }
    }

    /// <summary>The saved target's server details. A row that will not parse is a refusal, not a 500 (ruling 123 at the start door).</summary>
    private ServerMeta? SavedTargetMeta()
    {
        try
        {
            return _services.Connections.GetMeta(Side.Tgt);
        }
        catch (JsonException ex)
        {
            throw new TransferException("no_connection", "The saved target connection's server details could not be read: "
                                                         + Describe(ex) + " Re-enter the target connection on the Setup screen.");
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ the background run

    /// <summary>
    /// What a launched attempt found the run in, so that a refusal can put it back exactly there (ruling 134). The caller knows; it is
    /// passed in rather than read off the row when the refusal arrives, by which time it is no longer a record of what was found.
    /// </summary>
    /// <param name="CreatedByThisAttempt">True on the fresh-start path, where the attempt created the run itself.</param>
    /// <param name="FoundAs">The status the attempt found, for a run it did not create.</param>
    internal sealed record RunOrigin(bool CreatedByThisAttempt, RunStatus? FoundAs)
    {
        internal static RunOrigin Created { get; } = new(true, null);

        internal static RunOrigin Found(RunStatus status) => new(false, status);
    }

    private void Launch(TransferEngine engine, long runId, string srcCs, string tgtCs, RunOrigin origin)
    {
        var control = new TransferControl();
        control.ChunkCommitted += c => ChunkCommitted?.Invoke(c);
        var secrets = Redactor.SecretsOf(srcCs).Concat(Redactor.SecretsOf(tgtCs)).ToList();
        lock (_lock)
        {
            _control = control;
            _activeRunId = runId;
            _active = true;
            _current = Task.Run(() => RunBackgroundAsync(engine, runId, control, secrets, origin));
        }
    }

    private async Task RunBackgroundAsync(TransferEngine engine, long runId, TransferControl control, IReadOnlyList<string> secrets,
        RunOrigin origin)
    {
        // Ruling 133's seam, subscribed only when a test set one - production pays one null check per launched segment and nothing at
        // all per chunk. It is awaited on the worker on purpose: it holds the run at the instant it fires, so what a test reads there
        // is a live transfer rather than whichever task the service happened to be holding when it looked.
        if (AfterFirstChunk is { } afterFirstChunk)
        {
            int chunks = 0;
            control.ChunkCommitted += _ =>
            {
                if (Interlocked.Increment(ref chunks) == 1) afterFirstChunk().GetAwaiter().GetResult();
            };
        }
        try
        {
            TransferOutcome outcome;
            try
            {
                outcome = await engine.RunAsync(runId, control, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;   // server shutting down: the run stays 'running' and RecoverInterrupted pauses it on the next start
            }
            catch (TransferException ex) when (ex.Code == "run_in_progress")
            {
                // The lock refused this runner after EnsureNoOtherRunnerAsync let it through - another process took the run's lock in
                // between. Nothing of it ran here, so it is not a failed run: it is put back exactly where this attempt found it
                // (ruling 134), carrying the lock's sentence.
                RecordNotRun(runId, origin, ex.Message);
                return;   // paused calls nothing on the workflow (Next() reads the run status)
            }
            catch (Exception ex)
            {
                // A message that scrubbing emptied, or that was never there, becomes the exception's type name: TransferRepo refuses a
                // blank error outright, and a failed run with no reason is a run nobody can act on.
                //
                // CONTRACT ASSERTION AGAINST A TRIGGER UNREACHABLE FROM THIS TASK (rulings 104, 112, 121). TransferEngine.RunAsync
                // catches everything after it takes the run lock and records the run itself, so what can arrive here is only its three
                // pre-lock TransferExceptions (no_run, run_finished, plan_mismatch), an ArgumentNullException, and a lock or connection
                // fault - all of which carry messages, so the type-name fallback below never fires today. It is kept because the day a
                // blank one does arrive, the alternative is an ArgumentException out of the repo and a run left "running" for ever.
                // Pinned by TransferServiceViewTests.A_failure_with_no_message_is_recorded_by_type_name_contract_assertion_…
                string msg = TransferFailure.Describe(ex, t => Redactor.Scrub(t, secrets));
                // Through the merge writer, not a fresh object: this arm owns the status, but a run can already be carrying notes from
                // an earlier segment (a control table that was not ours), and writing {"error": …} over the summary would drop them.
                _services.Transfers.SetRunStatus(runId, RunStatus.Failed,
                    MergeSummary(_services.Transfers.GetRun(runId)?.SummaryJson, error: msg));
                _services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(RunStatus.Failed) });
                outcome = new TransferOutcome(RunStatus.Failed, msg);
            }
            OnFinished(runId, outcome);
        }
        catch (Exception ex)
        {
            // CONTRACT ASSERTION AGAINST A TRIGGER UNREACHABLE FROM THIS TASK (rulings 104, 112, 121). Everything inside is already
            // handled; what is left is the bookkeeping itself - a state database that has gone away under a run that is finishing. It
            // is caught rather than left to fault the background task, because an unobserved faulted task would leave _active true for
            // the life of the process and every later start refused "busy" with no way back.
            _services.Sink.Publish("log", new { level = "error",
                message = "Transfer bookkeeping failed: " + TransferFailure.Describe(ex, t => Redactor.Scrub(t, secrets)) });
        }
        finally
        {
            lock (_lock)
            {
                _active = false;
                _control = null;
            }
        }
    }

    /// <summary>
    /// Ruling 130. Adds <paramref name="note"/> to what the run already carries; it replaces nothing.
    /// <para><c>TransferRepo.SetRunStatus</c> writes <c>COALESCE($Summary, summary_json)</c>, so a non-null summary <b>replaces</b> -
    /// and the summary of a failed run holds the reason it failed. A refusal that did nothing to the run must not be the thing that
    /// erases it. A summary that is not readable JSON is kept verbatim as a note rather than discarded for being inconvenient.</para>
    /// </summary>
    internal static string MergeNote(string? summaryJson, string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return MergeSummary(summaryJson, note: note);
    }

    /// <summary>
    /// <see cref="MergeNote"/>'s general form: sets <c>error</c> when one is given, appends to <c>notes</c> when one is given, and
    /// keeps every other key the run already carried.
    /// </summary>
    internal static string MergeSummary(string? summaryJson, string? error = null, string? note = null)
    {
        JsonObject summary = new();
        string? unreadable = null;
        if (!string.IsNullOrWhiteSpace(summaryJson))
        {
            try
            {
                if (JsonNode.Parse(summaryJson) is JsonObject parsed) summary = parsed;
                else unreadable = summaryJson;
            }
            catch (JsonException)
            {
                unreadable = summaryJson;
            }
        }

        var notes = new List<string>();
        if (summary["notes"] is JsonArray existing)
            notes.AddRange(existing.Select(n => n is JsonValue v && v.TryGetValue(out string? text) ? text : n?.ToJsonString() ?? ""));
        else if (summary["notes"] is { } odd)
            notes.Add(odd.ToJsonString());
        if (unreadable is not null)
            notes.Add("The run's previous summary could not be read as JSON and is kept here verbatim: " + unreadable);
        if (note is not null) notes.Add(note);
        if (error is not null) summary["error"] = JsonValue.Create(error);
        summary["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        return summary.ToJsonString(Json.Options);
    }

    /// <summary>
    /// Rulings 130, 131 and 134: <b>a refused attempt restores what it changed, and changes nothing else. The note always lands.</b>
    /// <para>Ruling 131 is why nothing else is written. Recording a refusal through <c>SetRunStatus(Paused)</c> rewrote <b>five</b>
    /// things an operator reads on a <c>failed</c> run: the status (<c>failed</c> to <c>paused</c>), <c>ended_at</c> (a timestamp to
    /// null, because paused is not terminal), the error line (the failure's own sentence to null, since it is read only for a failed
    /// run), the notes, and <c>CanStart</c> (true to false - the Execute button disabled with "resume or cancel it first"). None of
    /// that happened; a runner was simply told to wait.</para>
    /// <para>Ruling 134 is why a created run is an exception rather than a contradiction: a run reading <c>running</c> with no runner
    /// is not a true record, so closing it is not a rewrite. It never started and loaded nothing, so it closes <c>cancelled</c> - the
    /// same close <see cref="EnsureNoOtherRunnerAsync"/> gives a start the probe refused (ruling 126). Left <c>running</c> it cannot be
    /// cleared at all: <see cref="CancelAsync"/> and <see cref="Resume"/> both take only a paused or failed run, and a new start is
    /// refused because the Ready step has already been approved.</para>
    /// </summary>
    /// <param name="origin">What the attempt found when it launched - passed in, because by the time a refusal arrives the row is no
    /// longer a record of that.</param>
    internal void RecordNotRun(long runId, RunOrigin origin, string why)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var current = _services.Transfers.GetRun(runId);
        RunStatus? restore = origin.CreatedByThisAttempt ? RunStatus.Cancelled
            // A status this attempt moved goes back to what it found. Unreachable today - TransferEngine.RunAsync takes the run lock
            // before it writes "running", so a refused attempt has moved nothing - and kept so that a future caller which does move it
            // first cannot leave the run somewhere neither it nor the operator put it. One caveat if that day comes: TransferRepo's
            // only status writer derives ended_at from the status, so restoring a terminal status re-stamps it to now.
            : origin.FoundAs is { } found && current is not null && current.Status != found ? found
            : null;

        if (restore is { } status) _services.Transfers.SetRunStatus(runId, status, MergeNote(current?.SummaryJson, why));
        else _services.Transfers.UpdateRunSummary(runId, summary => MergeNote(summary, why));

        if (_services.Transfers.GetRun(runId)?.Status is { } published)
            _services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(published) });
        _services.Sink.Publish("log", new { level = "error", message = why });
    }

    /// <summary>
    /// What the workflow is told, and - on the completed path only - the Complete artifact.
    /// <para><b>The report is stored for a completed run and for nothing else</b> (ruling 117). It is the migration's certificate: the
    /// UI links to it, <c>dbm export report</c> renders it, and <c>OnTransferFinished("completed", version)</c> approves the Complete
    /// phase on the strength of it. Storing one for a failed or cancelled run would close the migration over a half-loaded target.
    /// A paused run finishes nothing, so it calls nothing.</para>
    /// </summary>
    private void OnFinished(long runId, TransferOutcome outcome)
    {
        switch (outcome.Status)
        {
            case RunStatus.Completed:
                var run = _services.Transfers.GetRun(runId)!;
                var report = run.SummaryJson is null ? null : Json.Deserialize<FinalReport>(run.SummaryJson);
                string summary = report is null ? $"Transfer run {runId} completed." : FinalReportBuilder.Summary(report);
                int version = Math.Max(1, _services.Artifacts.NextVersion(PhaseName.Complete));
                _services.Artifacts.Add(PhaseName.Complete, version, run.SummaryJson ?? "{}", "script", summary);
                _services.Sink.Publish("artifact_created", new { phase = "complete", version, author = "script" });
                _services.Workflow.OnTransferFinished("completed", version);
                break;
            case RunStatus.Failed:
                _services.Workflow.OnTransferFinished("failed", null);
                break;
            case RunStatus.Cancelled:
                _services.Workflow.OnTransferFinished("cancelled", null);
                break;
        }
    }

    // ------------------------------------------------------------------ pre-flight boundary

    /// <summary>
    /// The one plan fault that is raised before <c>Preflight.RunAsync</c> can report anything: a task key whose value is null. It is
    /// caught here rather than left to <see cref="CouldNotRun"/> so the line can name the artifact and the task, which a
    /// <see cref="NullReferenceException"/>'s message cannot.
    /// </summary>
    private PreflightResult? MalformedPlanResult()
    {
        ApprovedPlan? approved;
        try
        {
            approved = Preflight.LoadApprovedPlan(_services);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return null;   // Preflight.RunAsync reports this one itself, with the same two sentences it uses elsewhere
        }
        if (approved is null) return null;
        var bad = MalformedTasks(approved.Plan);
        if (bad.Count == 0) return null;
        return new PreflightResult(approved.Version, Clock.Now(),
        [
            new PreflightCheck("preflight", false, "error",
                $"The approved SQL plan artifact (sql v{approved.Version}) is malformed: {string.Join(", ", bad)} "
                + $"{(bad.Count == 1 ? "has" : "have")} no task body at all. Nothing else could be checked. Re-generate the SQL phase "
                + "and approve it again."),
        ]);
    }

    /// <summary>Task keys whose value is null. <c>PlanOrder</c> filters by <c>ContainsKey</c>, which such a key satisfies, so every
    /// later <c>plan.Tasks[id].X</c> is a null dereference.</summary>
    internal static List<string> MalformedTasks(SqlPlanPayload plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Tasks.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>The scrubbed message, or the exception's type name when scrubbing itself cannot run or leaves nothing: a line that says
    /// nothing is worse than a bare type name.</summary>
    private string ScrubbedMessage(Exception ex)
    {
        try
        {
            return TransferFailure.Describe(ex, t => Redactor.Scrub(t, Secrets()));
        }
        catch (Exception)
        {
            // Redactor.SecretsOf is itself one of the things that can throw here (5.4 fix-round-1 concern 3).
            return ex.GetType().Name;
        }
    }

    private PreflightResult CouldNotRun(Exception ex)
    {
        string message = ScrubbedMessage(ex);
        return new PreflightResult(0, Clock.Now(),
        [
            new PreflightCheck("preflight", false, "error",
                "The pre-flight checks could not be run: " + message
                + " Nothing about this transfer was checked, so nothing about it is confirmed."),
        ]);
    }

    private List<string> Secrets()
    {
        var secrets = new List<string>();
        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            try
            {
                if (_services.Connections.GetConnectionString(side) is { } cs) secrets.AddRange(Redactor.SecretsOf(cs));
            }
            catch (Exception)
            {
                // A connection string we cannot read holds no secret we could scrub for; the message is scrubbed of the rest.
            }
        }
        return secrets;
    }

    // ------------------------------------------------------------------ view helpers

    private (ApprovedPlan? Approved, string? Note) PlanForView()
    {
        try
        {
            var approved = Preflight.LoadApprovedPlan(_services);
            if (approved is null)
            {
                var phase = _services.Phases.Get(PhaseName.Sql);
                return (null, phase.Status == PhaseStatus.Approved && phase.ApprovedVersion is int v
                    ? $"The SQL phase is approved at v{v}, but that version's artifact is missing from this workspace, so its tasks "
                      + "cannot be shown."
                    : "The SQL phase is not approved yet, so there is no plan to transfer.");
            }
            var bad = MalformedTasks(approved.Plan);
            return (approved, bad.Count == 0 ? null
                : $"The approved SQL plan artifact (sql v{approved.Version}) is malformed: {string.Join(", ", bad)} "
                  + $"{(bad.Count == 1 ? "has" : "have")} no task body and {(bad.Count == 1 ? "is" : "are")} not shown.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return (null, "The approved SQL plan could not be read: " + Describe(ex));
        }
    }

    /// <summary>The plan that describes the latest run's tasks, which is its own SQL version - not necessarily the approved one, since
    /// a restart can follow a re-approval.</summary>
    private (SqlPlanPayload? Plan, string? Note) PlanOfRun(TransferRunRow? run, ApprovedPlan? approved)
    {
        if (run is null || run.SqlVersion == approved?.Version) return (approved?.Plan, null);
        try
        {
            var artifact = _services.Artifacts.Get(PhaseName.Sql, run.SqlVersion);
            return artifact is null
                ? (approved?.Plan, $"SQL plan v{run.SqlVersion}, the version run {run.Id} used, is no longer in this workspace; its "
                                   + "tasks' dependencies and keys are not shown.")
                : (Json.Deserialize<SqlPlanPayload>(artifact.PayloadJson), null);
        }
        catch (JsonException ex)
        {
            return (approved?.Plan, $"SQL plan v{run.SqlVersion}, the version run {run.Id} used, could not be read ({Describe(ex)}); "
                                    + "its tasks' dependencies and keys are not shown.");
        }
    }

    private static IEnumerable<string> PlanTaskIds(SqlPlanPayload plan)
        => TransferEngine.PlanOrder(plan).Where(id => plan.Tasks[id] is not null);

    /// <summary>
    /// Ruling 132, route 3. The run's own row cannot be read, so there is nothing to show and nothing to do - but the screen still
    /// renders, and every field that would otherwise be a bare absence says which saved record is at fault and what to do about it.
    /// </summary>
    private TransferView UnreadableRunView(Exception ex)
    {
        string why = "The latest transfer run could not be read: the options this workspace saved for it are not readable JSON ("
                     + Describe(ex) + "). Nothing is known about that run - not its status, not what it loaded.";
        var (targetDatabase, targetProblem) = SavedTargetDatabase();
        return new TransferView(null, [], new TransferTotals(0, 0, 0, 0, 0), IsActive, false, targetDatabase, null, LastPreflight,
            new TransferOptions())
        {
            CannotStart = why + " Starting a new run while an existing one cannot be read would load into a target that run may still "
                          + "be using; repair or remove its row in the state database first.",
            CanResume = false,
            CannotResume = why,
            PlanNote = targetProblem is null ? why : why + " " + targetProblem,
        };
    }

    /// <summary>
    /// Ruling 132, route 1. The saved target's database name, or null <b>with the sentence that says why</b> - a null here otherwise
    /// reads as "no target saved", which is a different thing from "the target's saved details will not parse".
    /// </summary>
    private (string? Database, string? Problem) SavedTargetDatabase()
    {
        try
        {
            return (_services.Connections.GetMeta(Side.Tgt)?.Database, null);
        }
        catch (JsonException ex)
        {
            return (null, "The saved target connection's server details could not be read (" + Describe(ex)
                          + "), so the target database name is unknown and the typed confirmation cannot be checked against it. "
                          + "Re-test the target connection on the Setup screen.");
        }
    }

    /// <summary>
    /// Ruling 132, route 2. One task's unreadable validation costs that task's validation, not the whole screen - and it says so,
    /// because a null <c>Validation</c> otherwise means nothing was ever checked (5.4's <c>RunValidator.NoValidation</c>).
    /// </summary>
    private static (TaskValidation? Validation, string? Note) ValidationOf(TransferTaskRow task)
    {
        if (task.ValidationJson is null) return (null, null);
        try
        {
            return (Json.Deserialize<TaskValidation>(task.ValidationJson), null);
        }
        catch (JsonException ex)
        {
            return (null, "the validation recorded for this task could not be read (" + Describe(ex)
                          + "), so its row counts and checksums are unknown - this is not a task that was never validated");
        }
    }

    private string? WhyNotStart(TransferRunRow? run, ApprovedPlan? approved, string? planNote, bool active)
    {
        if (active) return "A transfer is already running.";
        if (run?.Status == RunStatus.Paused) return $"Run {run.Id} is paused; resume or cancel it before starting a new one.";
        if (approved is null) return planNote ?? "There is no approved SQL plan to transfer.";
        bool restartable = _services.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Running
                           && run?.Status is RunStatus.Failed or RunStatus.Cancelled;
        var ready = _services.Phases.Get(PhaseName.Ready).Status;
        if (ready == PhaseStatus.AwaitingReview || restartable) return null;
        return $"The transfer starts from the Ready step, which is {EnumText.ToText(ready)}.";
    }

    private static string? WhyNotResume(TransferRunRow? run, bool active)
    {
        if (active) return "A transfer is already running.";
        if (run is null) return "There is no transfer run to resume.";
        return run.Status is RunStatus.Paused or RunStatus.Failed ? null
            : $"Run {run.Id} is {EnumText.ToText(run.Status)}; only a paused or failed run can be resumed.";
    }

    private static string StopText(StopKind kind) => kind switch
    {
        StopKind.Pause => "pausing",
        StopKind.Cancel => "cancelling",
        StopKind.Fail => "failing",
        _ => "stopping",
    };

    /// <summary>
    /// True only when a Complete artifact was <b>stored</b> for this very run (ruling 117). The run id in the stored report is what
    /// ties them together: a later run's report is not this run's, and a completed run whose report was never stored must not offer a
    /// link to one.
    /// </summary>
    private bool HasStoredReport(TransferRunRow run)
    {
        if (run.Status != RunStatus.Completed) return false;
        var artifact = _services.Artifacts.Latest(PhaseName.Complete);
        if (artifact is null) return false;
        try
        {
            return JsonNode.Parse(artifact.PayloadJson)?["runId"]?.GetValue<long>() == run.Id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static List<string> RunNotes(TransferRunRow run)
    {
        if (run.SummaryJson is null) return [];
        try
        {
            return JsonNode.Parse(run.SummaryJson)?["notes"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    private static string? RunError(TransferRunRow run)
    {
        if (run.Status != RunStatus.Failed || run.SummaryJson is null) return null;
        try
        {
            return JsonNode.Parse(run.SummaryJson)?["error"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ shared

    private (string Source, string Target) ConnectionStrings() => (Resolve(Side.Src, "source"), Resolve(Side.Tgt, "target"));

    /// <summary>
    /// Ruling 123's boundary, at the start door. <c>ConnectionRepo.GetConnectionString</c> decrypts, so a connection row protected
    /// under a different Windows user - a copied workspace, a different service account - raises a
    /// <see cref="System.Security.Cryptography.CryptographicException"/> from here. Uncaught it is a bare 500 at the one control the
    /// execute screen exists for: no code, no sentence, and nothing saying the connection is the problem, while
    /// <c>POST /api/transfer/preflight</c> answers the identical fault with a checklist line.
    /// </summary>
    private string Resolve(Side side, string which)
    {
        string? connectionString;
        try
        {
            connectionString = _services.Connections.GetConnectionString(side);
        }
        catch (Exception ex)
        {
            throw new TransferException("no_connection",
                $"The saved {which} connection could not be read: {ScrubbedMessage(ex)} It was saved by a different Windows user, or "
                + "this workspace was copied from another machine; re-enter it on the Setup screen.");
        }
        return connectionString ?? throw new TransferException("no_connection", $"The {which} connection is not saved.");
    }

    private async Task DropControlTableAsync(string targetCs, CancellationToken ct)
    {
        try
        {
            await using var conn = await SqlConnect.OpenAsync(targetCs, ct);
            await ControlTable.DropAsync(conn, ct);
        }
        catch (Exception ex) when (ex is SqlException or TransferException)
        {
            // A checkpoint table that will not drop - or one of that name that is not ours (ruling H1) - must not turn a cancel into a
            // thrown request: the run IS cancelled either way, and the table left standing is a line in the log.
            _services.Sink.Publish("log", new { level = "warn",
                message = $"Could not drop {ControlTable.Name}: " + TransferFailure.Describe(ex, t => Redactor.Scrub(t, Redactor.SecretsOf(targetCs).ToList())) });
        }
    }

    private static string Describe(Exception ex) => TransferFailure.Describe(ex, t => t);
}
