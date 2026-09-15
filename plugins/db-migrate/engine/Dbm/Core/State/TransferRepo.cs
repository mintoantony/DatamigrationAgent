using System.Globalization;
using Dbm.Core.Transfer;
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed record TransferRunRow(long Id, int SqlVersion, RunStatus Status, TransferOptions Options,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? SummaryJson);

public sealed record TransferTaskRow(long RunId, string TaskId, string Target, int Ordinal, TransferTaskStatus Status,
    long? RowsSource, long? RowsBefore, long RowsDone, long RowsError, string? LastKeyJson, DateTimeOffset? HeartbeatAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Error, string? ValidationJson);

public sealed record ErrorRowEntry(long Id, long RunId, string TaskId, string? KeyJson, string? RowJson, string Error, DateTimeOffset Ts);

/// <summary>
/// SQLite mirror of transfer runs (the target control table is the source of truth for checkpoints).
/// Readers return null / empty for an unknown id; every mutator of an existing run or task throws
/// <see cref="TransferException"/> ("unknown_run" / "unknown_task") instead of silently changing nothing.
/// </summary>
public sealed class TransferRepo(StateDb db)
{
    private const string RunCols = "id, sql_version, status, options_json, started_at, ended_at, summary_json";
    private const string TaskCols = "run_id, task_id, target, ordinal, status, rows_source, rows_before, rows_done, rows_error, " +
                                    "last_key_json, heartbeat_at, started_at, ended_at, error, validation_json";

    private readonly StateDb _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>Creates a "running" run with its tasks "pending", ordinal = index in <paramref name="tasks"/>. Options are persisted normalized.</summary>
    public long CreateRun(int sqlVersion, TransferOptions options, IReadOnlyList<(string TaskId, string Target)> tasks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tasks);
        // A run with no tasks satisfies "every task is done" and would complete as a successful migration of nothing (ruling L2).
        if (tasks.Count == 0) throw new ArgumentException("A transfer run needs at least one task.", nameof(tasks));
        var normalized = options.Normalized();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (taskId, target) in tasks)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("Every task needs a non-blank task id.", nameof(tasks));
            // One identity rule shared with the checkpoint table (rulings Q5, L2): 1-64 characters, ordinal, no surrounding whitespace.
            ControlTable.CheckTaskId(taskId);
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException($"Task {taskId} has a blank target.", nameof(tasks));
            if (!ids.Add(taskId)) throw new ArgumentException($"Task id {taskId} appears more than once.", nameof(tasks));
        }

        return _db.InTransaction(() =>
        {
            _db.Execute("INSERT INTO transfer_run (sql_version, status, options_json, started_at) VALUES ($SqlVersion, $Status, $Options, $Now)",
                new { SqlVersion = sqlVersion, Status = EnumText.ToText(RunStatus.Running), Options = Json.Serialize(normalized), Now = Clock.NowText() });
            long id = _db.Scalar<long>("SELECT last_insert_rowid()");
            for (int i = 0; i < tasks.Count; i++)
                _db.Execute("INSERT INTO transfer_task (run_id, task_id, target, ordinal, status) VALUES ($RunId, $TaskId, $Target, $Ordinal, $Status)",
                    new { RunId = id, TaskId = tasks[i].TaskId, Target = tasks[i].Target, Ordinal = i, Status = EnumText.ToText(TransferTaskStatus.Pending) });
            return id;
        });
    }

    public TransferRunRow? Latest()
        => _db.Query($"SELECT {RunCols} FROM transfer_run ORDER BY id DESC LIMIT 1", MapRun).FirstOrDefault();

    public TransferRunRow? GetRun(long runId)
        => _db.Query($"SELECT {RunCols} FROM transfer_run WHERE id = $Id", MapRun, new { Id = runId }).FirstOrDefault();

    /// <summary>ended_at is set for completed|failed|cancelled and cleared otherwise; "running" clears summary_json,
    /// any other status keeps the existing summary unless a new one is given.</summary>
    public void SetRunStatus(long runId, RunStatus status, string? summaryJson = null)
    {
        bool terminal = status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
        int n = _db.Execute("UPDATE transfer_run SET status = $Status, ended_at = $EndedAt, " +
                            "summary_json = CASE WHEN $Status = 'running' THEN NULL ELSE COALESCE($Summary, summary_json) END WHERE id = $Id",
            new { Status = EnumText.ToText(status), EndedAt = terminal ? Clock.NowText() : null, Summary = summaryJson, Id = runId });
        if (n == 0) throw UnknownRun(runId);
    }

    /// <summary>Tasks of a run in ordinal order; empty when the run does not exist (check <see cref="GetRun"/> to tell the two apart).</summary>
    public IReadOnlyList<TransferTaskRow> Tasks(long runId)
        => _db.Query($"SELECT {TaskCols} FROM transfer_task WHERE run_id = $RunId ORDER BY ordinal", MapTask, new { RunId = runId });

    public TransferTaskRow? Task(long runId, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _db.Query($"SELECT {TaskCols} FROM transfer_task WHERE run_id = $RunId AND task_id = $TaskId", MapTask,
            new { RunId = runId, TaskId = taskId }).FirstOrDefault();
    }

    public void SetTaskCounts(long runId, string taskId, long? rowsSource, long? rowsBefore)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        RequireTask(runId, taskId, _db.Execute(
            "UPDATE transfer_task SET rows_source = $RowsSource, rows_before = $RowsBefore WHERE run_id = $RunId AND task_id = $TaskId",
            new { RowsSource = rowsSource, RowsBefore = rowsBefore, RunId = runId, TaskId = taskId }));
    }

    /// <summary>Sets the counters and last key and stamps heartbeat_at = now.</summary>
    public void UpdateTaskProgress(long runId, string taskId, long rowsDone, long rowsError, string? lastKeyJson)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        RequireTask(runId, taskId, _db.Execute(
            "UPDATE transfer_task SET rows_done = $RowsDone, rows_error = $RowsError, last_key_json = $LastKey, heartbeat_at = $Now " +
            "WHERE run_id = $RunId AND task_id = $TaskId",
            new { RowsDone = rowsDone, RowsError = rowsError, LastKey = lastKeyJson, Now = Clock.NowText(), RunId = runId, TaskId = taskId }));
    }

    /// <summary>
    /// started_at is stamped the first time a task runs; ended_at is set by done|failed and cleared by running.
    /// A failed task must say why (<paramref name="error"/> non-blank); any other status takes no new error.
    /// A stored error is cleared only by running; pending, paused and done keep it (the brief's contract, ruling Q4).
    /// </summary>
    public void UpdateTaskStatus(long runId, string taskId, TransferTaskStatus status, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (status == TransferTaskStatus.Failed && string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("A failed task needs a non-blank error.", nameof(error));
        if (status != TransferTaskStatus.Failed && error is not null)
            throw new ArgumentException($"An error is only recorded for a failed task (status was {EnumText.ToText(status)}).", nameof(error));

        RequireTask(runId, taskId, _db.Execute(
            "UPDATE transfer_task SET status = $Status, heartbeat_at = $Now, " +
            "started_at = CASE WHEN $Status = 'running' AND started_at IS NULL THEN $Now ELSE started_at END, " +
            "ended_at = CASE WHEN $Status IN ('done', 'failed') THEN $Now WHEN $Status = 'running' THEN NULL ELSE ended_at END, " +
            "error = CASE WHEN $Status = 'failed' THEN $Error WHEN $Status = 'running' THEN NULL ELSE error END " +
            "WHERE run_id = $RunId AND task_id = $TaskId",
            new { Status = EnumText.ToText(status), Now = Clock.NowText(), Error = error, RunId = runId, TaskId = taskId }));
    }

    public void SetTaskValidation(long runId, string taskId, string validationJson)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(validationJson);
        RequireTask(runId, taskId, _db.Execute(
            "UPDATE transfer_task SET validation_json = $Json WHERE run_id = $RunId AND task_id = $TaskId",
            new { Json = validationJson, RunId = runId, TaskId = taskId }));
    }

    /// <summary>Records a rejected row; the task must exist (no orphan error rows).</summary>
    public void AddErrorRow(long runId, string taskId, string? keyJson, string? rowJson, string error)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("An error row needs a non-blank error.", nameof(error));
        RequireTask(runId, taskId, _db.Execute(
            "INSERT INTO error_row (run_id, task_id, key_json, row_json, error, ts) " +
            "SELECT $RunId, $TaskId, $KeyJson, $RowJson, $Error, $Ts WHERE EXISTS (SELECT 1 FROM transfer_task WHERE run_id = $RunId AND task_id = $TaskId)",
            new { RunId = runId, TaskId = taskId, KeyJson = keyJson, RowJson = rowJson, Error = error, Ts = Clock.NowText() }));
    }

    /// <summary>Oldest first; <paramref name="limit"/> is clamped to 1..10000.</summary>
    public IReadOnlyList<ErrorRowEntry> ErrorRows(long runId, string? taskId = null, int limit = 100)
        => _db.Query("SELECT id, run_id, task_id, key_json, row_json, error, ts FROM error_row " +
                     "WHERE run_id = $RunId AND ($TaskId IS NULL OR task_id = $TaskId) ORDER BY id LIMIT $Limit",
            r => new ErrorRowEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Str(r, 3), Str(r, 4), r.GetString(5), Ts(r, 6)!.Value),
            new { RunId = runId, TaskId = taskId, Limit = Math.Clamp(limit, 1, 10_000) });

    /// <summary>Rejected rows recorded for an existing task; an unknown run or task throws "unknown_task" rather than reading as 0 (ruling L2).</summary>
    public long ErrorRowCount(long runId, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        long n = _db.Scalar<long>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM transfer_task WHERE run_id = $RunId AND task_id = $TaskId) " +
            "THEN (SELECT COUNT(*) FROM error_row WHERE run_id = $RunId AND task_id = $TaskId) ELSE -1 END",
            new { RunId = runId, TaskId = taskId });
        if (n < 0) throw new TransferException("unknown_task", $"Transfer task {taskId} of run {runId} does not exist.");
        return n;
    }

    /// <summary>
    /// Server start, when no transfer is executing in this process: every run left "running" by a dead process becomes "paused",
    /// and every task left "running" under a running or paused run becomes "paused" (a paused run can still have had a task
    /// draining when the process died).
    /// <para><b>Returns only the number of runs changed running → paused.</b> Orphaned "running" tasks under an already-"paused"
    /// run are also repaired but are NOT counted, so a return of 0 does not mean no row changed.</para>
    /// <para>Deliberately not repaired: a "running" task under a terminal (completed/failed/cancelled) run. That state is
    /// unreachable through this repo's engine flow, so it means a bug or a hand-edited database; rewriting it (e.g. to failed)
    /// would hide that and could turn a completed run's report into a failed one.</para>
    /// </summary>
    public int RecoverInterrupted() => _db.InTransaction(() =>
    {
        _db.Execute("UPDATE transfer_task SET status = 'paused' WHERE status = 'running' " +
                    "AND run_id IN (SELECT id FROM transfer_run WHERE status IN ('running', 'paused'))");
        return _db.Execute("UPDATE transfer_run SET status = 'paused' WHERE status = 'running'");
    });

    private static void RequireTask(long runId, string taskId, int affected)
    {
        if (affected == 0) throw new TransferException("unknown_task", $"Transfer task {taskId} of run {runId} does not exist.");
    }

    private static TransferException UnknownRun(long runId) => new("unknown_run", $"Transfer run {runId} does not exist.");

    private static TransferRunRow MapRun(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), EnumText.Parse<RunStatus>(r.GetString(2)), Json.Deserialize<TransferOptions>(r.GetString(3)),
        Ts(r, 4), Ts(r, 5), Str(r, 6));

    private static TransferTaskRow MapTask(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), EnumText.Parse<TransferTaskStatus>(r.GetString(4)),
        Long(r, 5), Long(r, 6), r.GetInt64(7), r.GetInt64(8), Str(r, 9), Ts(r, 10), Ts(r, 11), Ts(r, 12), Str(r, 13), Str(r, 14));

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static DateTimeOffset? Ts(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
