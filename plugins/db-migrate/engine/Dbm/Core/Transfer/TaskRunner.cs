using System.Data;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record TaskResult(string TaskId, TransferTaskStatus Status, string? Error);

/// <summary>
/// Turns an exception into something a run can actually record. <see cref="TransferRepo.UpdateTaskStatus"/> and
/// <see cref="TransferRepo.AddErrorRow"/> refuse a blank error with <c>ArgumentException</c>, which is deliberately not a
/// <see cref="TransferException"/> and so escapes every <c>catch (TransferException)</c> above them: one exception carrying an empty
/// message would otherwise end the whole migration. A blank message must cost a sentence, not a run.
/// </summary>
internal static class TransferFailure
{
    public const string NoMessage = "the exception carried no message";

    public static string NonBlank(string? text, string fallback)
        => string.IsNullOrWhiteSpace(text) ? fallback : text;

    /// <summary>The scrubbed message, or - when it is blank, or scrubbing emptied it - what threw, named.</summary>
    public static string Describe(Exception ex, Func<string, string> scrub)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(scrub);
        string scrubbed = NonBlank(scrub(ex.Message ?? ""), "");
        if (scrubbed.Length > 0) return scrubbed;
        string what = ex is TransferException te ? $"{ex.GetType().Name} \"{te.Code}\"" : ex.GetType().Name;
        return $"{what} ({NoMessage})";
    }
}

internal sealed class RunContext
{
    public required DbmServices Services { get; init; }
    public required long RunId { get; init; }
    public required SqlPlanPayload Plan { get; init; }
    public required string SourceCs { get; init; }
    public required string TargetCs { get; init; }
    public required TransferOptions Options { get; init; }
    public required TransferControl Control { get; init; }
    public required TransferProgress Progress { get; init; }
    public required IReadOnlyList<string> Secrets { get; init; }

    private readonly List<string> _notes = [];

    public TransferRepo Repo => Services.Transfers;

    /// <summary>
    /// What this run has to carry out with it because nothing else will: a control table that was not ours, rejected rows that were
    /// counted but never recorded. They go into the run's summary_json as well as the log, so the absence says why it is absent.
    /// </summary>
    public IReadOnlyList<string> Notes
    {
        get { lock (_notes) return _notes.ToList(); }
    }

    public void AddNote(string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        lock (_notes) _notes.Add(note);
    }

    public string Scrub(string text) => Redactor.Scrub(text, Secrets);

    public string Describe(Exception ex) => TransferFailure.Describe(ex, Scrub);

    public void Log(string level, string message, bool persist = true)
        => Services.Sink.Publish("log", new { level, message = Scrub(message) }, persist);

    public void SetTaskStatus(string taskId, TransferTaskStatus status, string? error = null)
    {
        Repo.UpdateTaskStatus(RunId, taskId, status, error);
        Services.Sink.Publish("transfer_task_changed", new { runId = RunId, taskId, status = EnumText.ToText(status) });
        Progress.SetStatus(taskId, status);
    }
}

/// <summary>Runs one task: checkpointed keyset chunks (one target transaction per chunk) or a single-transaction keyless load.</summary>
internal sealed class TaskRunner(RunContext rc)
{
    /// <summary>What an error row says instead of leaving a keyed task's key silently null - which would read as "this task has no key".</summary>
    internal const string KeyFailedNote = "the row key could not be encoded";

    private const string NoKeyTypes = KeyFailedNote + ": the key column types were never read";

    /// <summary>
    /// Ruling 192 (open item 30): under skip-and-log, a task whose first this-many chunks - or its whole source, if that is fewer
    /// chunks - loaded no row at all while rejecting rows is failed as <c>bad_task</c>. A constraint violation is a per-row reject
    /// (ruling 147), so a plan defect that surfaces as one - an FK column bound to the wrong expression, a CHECK no row satisfies -
    /// otherwise completes with the whole table rejected, one single-row attempt per row, under a headline that reads as success.
    /// Ruling 201 exempts the re-run Ruling 186 lets a human confirm: a table that held rows before the run, whose rejects are all
    /// duplicate keys (see <see cref="LoadedNothingReason"/>).
    /// </summary>
    internal const int ZeroLoadChunks = 3;

    /// <summary>
    /// Ruling 207 (open item 45): the most rows any of a task's first <see cref="ZeroLoadChunks"/> chunks holds, whatever the chunk size,
    /// so the zero-load guard judges a task on about 3,000 rows instead of three full chunks (300,000 single-row rejects at the default
    /// 100,000). Keyset paging does not persist a chunk size - the next read starts after the checkpoint's last key - so a small chunk
    /// resumes like any other, and the size is chosen from the checkpoint's chunk number, the same on a resume as the first time.
    /// </summary>
    internal const int SmallChunkRows = 1_000;

    /// <summary>The rows to read for the chunk after <paramref name="chunksDone"/> committed (keyed) or loaded (keyless) chunks.</summary>
    internal static int ChunkRows(int chunkSize, int chunksDone)
        => chunksDone < ZeroLoadChunks ? Math.Min(chunkSize, SmallChunkRows) : chunkSize;

    private static readonly AsyncLocal<Action<string, int>?> AfterChunkTransactionHook = new();

    /// <summary>
    /// Test seam. Invoked with (taskId, chunkNo) at the one instant that defines chunk atomicity: the chunk's rows and its checkpoint
    /// have both just become durable, or neither has. A test throws here to stage a crash in exactly that window, so that a checkpoint
    /// written outside the chunk transaction shows up as a chunk loaded twice on resume. Ruling 114: this is raised from inside
    /// <see cref="TxScope.CommitAsync(CancellationToken, Func{CancellationToken, Task})"/>, on the statement after the COMMIT, so the
    /// rule this used to state as a comment - that nothing may come between the commit and the seam - is now a property of the code
    /// rather than a request to the next reader: the callback is the expression <c>_ =&gt; RaiseAfterChunk(id, next.ChunkNo)</c>, so
    /// <c>RunKeyedAsync</c> has no statement position between the two at all. Null in production, and held in an
    /// <see cref="AsyncLocal{T}"/> so one test's seam cannot reach another test's run.
    /// </summary>
    internal static Action<string, int>? AfterChunkTransaction
    {
        get => AfterChunkTransactionHook.Value;
        set => AfterChunkTransactionHook.Value = value;
    }

    /// <summary>
    /// The seam's whole body, so that the callback <c>RunKeyedAsync</c> hands to <c>CommitAsync</c> is a single expression and there is
    /// no statement position anywhere in the chunk loop between the COMMIT and the raise (ruling 114). What is left of the gap is this
    /// method's own body - and a write put here would have to be given the connection, the run id and the checkpoint as new parameters
    /// first, which is a change to the seam's signature rather than a line moved past a comment.
    /// </summary>
    private static Task RaiseAfterChunk(string taskId, int chunkNo)
    {
        AfterChunkTransaction?.Invoke(taskId, chunkNo);
        return Task.CompletedTask;
    }

    /// <param name="Number">The row's SQL Server error number (<see cref="RowFailure.ErrorNumber"/>), null when its failure carried none;
    /// stored beside the text (Ruling 208).</param>
    internal sealed record ErrorRecord(string? Key, string Row, string Error, int? Number = null);

    private sealed record Pass(TransferTaskStatus Status, Checkpoint Checkpoint);

    public async Task<TaskResult> RunAsync(TransferTaskRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        string id = row.TaskId;
        var task = rc.Plan.Tasks[id];
        rc.SetTaskStatus(id, TransferTaskStatus.Running);
        try
        {
            await using var tgt = await SqlConnect.OpenAsync(rc.TargetCs, ct);
            var stored = await ControlTable.ReadAsync(tgt, rc.RunId, id, ct);
            var cp = ResumePoint(row, stored);
            if (cp.Done)
            {
                Mirror(id, cp);   // committed before a crash; only the mirror and PostSql may be missing
            }
            else
            {
                // Ruling 106, which overrides the brief's ordering: task PreSql runs on the segment that STARTS the task, never on a
                // resume. It is operator-authored text carried verbatim by the generator, so a DELETE there - the natural thing for a
                // human to write - would wipe what the previous segment loaded, and the run would still report completed. A task with
                // no checkpoint row has committed nothing, so re-running it there is safe; with one, it is not.
                if (stored is null) await TargetOps.ExecAllAsync(tgt, task.PreSql, ct);
                var shape = await TargetShape.LoadAsync(tgt, task.Target, ct);
                // Ruling 73: the loader has to know the target it is loading into, or a binding to a missing column and a binding to an
                // identity column become a chunk of rejected rows and a table of silently renumbered ids respectively.
                var loader = new BulkLoader(task, rc.Options, shape);
                var pass = task.KeyColumns.Count > 0
                    ? await RunKeyedAsync(id, task, tgt, shape, loader, cp, row.RowsBefore, ct)
                    : await RunKeylessAsync(id, task, tgt, shape, loader, row.RowsBefore, ct);
                if (pass.Status == TransferTaskStatus.Paused)
                {
                    rc.SetTaskStatus(id, TransferTaskStatus.Paused);
                    rc.Log("info", $"{id} {task.Target}: paused after {pass.Checkpoint.RowsDone:N0} rows loaded, "
                        + $"{pass.Checkpoint.RowsError:N0} rejected.");
                    return new TaskResult(id, TransferTaskStatus.Paused, null);
                }
                cp = pass.Checkpoint;
            }
            await TargetOps.ExecAllAsync(tgt, task.PostSql, ct);
            rc.SetTaskStatus(id, TransferTaskStatus.Done);
            ReportDone(id, task, row.RowsSource, cp);
            return new TaskResult(id, TransferTaskStatus.Done, null);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);   // crash semantics: the task stays 'running' until recovery
        }
        catch (Exception ex)
        {
            string msg = rc.Describe(ex);
            rc.SetTaskStatus(id, TransferTaskStatus.Failed, msg);
            rc.Log("error", $"{id} {task.Target}: {msg}");
            return new TaskResult(id, TransferTaskStatus.Failed, msg);
        }
    }

    /// <summary>
    /// The target control table is the resume point, and the SQLite mirror is only a copy of it. A task with no checkpoint row but
    /// recorded progress would resume from the very beginning and load every one of those rows a second time - on a target without a
    /// unique key nothing objects, so the run "succeeds" with the loaded rows duplicated. That is a stop, not a restart.
    /// </summary>
    private static Checkpoint ResumePoint(TransferTaskRow row, Checkpoint? stored)
    {
        if (stored is not null) return stored;
        if (row.RowsDone == 0 && row.RowsError == 0 && row.LastKeyJson is null) return Checkpoint.Start;
        throw new TransferException("checkpoint_lost",
            $"Task {row.TaskId} ({row.Target}) has no checkpoint row in {ControlTable.Name}, but this run already recorded "
            + $"{row.RowsDone:N0} rows loaded and {row.RowsError:N0} rejected. Resuming would load them a second time. The checkpoint "
            + "table was dropped or the target was replaced since the last segment; start a new run against a known target state.");
    }

    private async Task<Pass> RunKeyedAsync(string id, TaskPlan task, SqlConnection tgt, TargetShape shape, BulkLoader loader,
        Checkpoint cp, long? rowsBefore, CancellationToken ct)
    {
        int chunkSize = Math.Max(1, task.ChunkSize ?? rc.Options.ChunkSize);
        KeyValue? last = cp.LastKeyJson is null ? null : KeyCodec.Decode(cp.LastKeyJson);
        IReadOnlyList<KeyType>? types = last?.Types;
        var rejects = new RejectTally();
        await using var src = await SqlConnect.OpenAsync(rc.SourceCs, ct);
        while (true)
        {
            if (rc.Control.StopRequested) return new Pass(TransferTaskStatus.Paused, cp);

            int size = ChunkRows(chunkSize, cp.ChunkNo);
            DataTable table;
            await using (var cmd = ChunkPlanner.Command(src, task, last, size))
            await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
            {
                // The key values are the reader's own, typed by GetFieldType: KeyCodec matches the .NET type to the SQL type exactly,
                // so an int handed over for a bigint key is "bad_key". Nothing here converts them.
                types ??= KeyCodec.TypesOf(reader, task.KeyColumns);
                table = ChunkReader.NewTable(reader);
                await ChunkReader.FillAsync(reader, table, size, ct);
            }

            if (table.Rows.Count == 0)
            {
                cp = cp with { Done = true };
                await ControlTable.UpsertAsync(tgt, null, rc.RunId, id, cp, ct);
                Mirror(id, cp);
                // The source ended exactly on a chunk boundary: the chunks read were the whole source (ruling 192). A task that already
                // reached ZeroLoadChunks was judged when that chunk committed, and a resume past that judgement must not be judged again.
                if (cp.ChunkNo < ZeroLoadChunks) ThrowIfLoadedNothing(id, task.Target, rowsBefore, cp, rejects);
                return new Pass(TransferTaskStatus.Done, cp);
            }

            var newLast = KeyCodec.FromRow(table.Rows[table.Rows.Count - 1], task.KeyColumns, types);
            // BulkLoader does not normalize by itself: without this, datetime2(n)/time/datetimeoffset values are truncated where the
            // server's CAST rounds, and smalldatetime values are rounded on the fractional second (V8).
            shape.Normalize(table, task.Columns);
            bool lastChunk = table.Rows.Count < size;
            ChunkOutcome outcome;
            Checkpoint next;
            await using (var scope = await TxScope.BeginAsync(tgt, ct))
            {
                outcome = await loader.LoadAsync(scope, table, allowRestart: true, n => rc.Progress.InFlight(id, n), ct);
                if (outcome.Failed.Count > 0 && !rc.Options.SkipErrors)
                {
                    await scope.RollbackAsync();
                    var first = Capture(task, table, [outcome.Failed[0]], types);
                    // This chunk rolls back and the checkpoint does not move, so every retry of the failed run reaches this same source
                    // row again. Error rows beyond the checkpoint's own count are that row from an earlier attempt: record it once, or
                    // the operator is shown one bad row N times and cannot tell a retry from N bad rows.
                    if (rc.Repo.ErrorRowCount(rc.RunId, id) <= cp.RowsError) Write(id, first);
                    throw new TransferException("row_rejected", $"Row {first[0].Key ?? "(key unknown)"} rejected: {first[0].Error}");
                }
                CheckAccounted(id, cp.ChunkNo + 1, outcome);
                next = new Checkpoint(cp.ChunkNo + 1, KeyCodec.Encode(newLast), cp.RowsDone + outcome.Loaded,
                    cp.RowsError + outcome.Failed.Count, lastChunk);
                await ControlTable.UpsertAsync(scope.Connection, scope.Tx, rc.RunId, id, next, ct);
                // Ruling 114: the seam is raised from inside CommitAsync, on the statement after the COMMIT, rather than on the line
                // after this one. The instant it fires is the instant the chunk's rows and its checkpoint are both durable or neither
                // is. The callback is an expression, not a block, so this loop has no statement position between the two at all - the
                // rule the old comment here asked the next reader to keep is now something the shape of the code keeps for them.
                await scope.CommitAsync(ct, _ => RaiseAfterChunk(id, next.ChunkNo));
            }

            cp = next;
            last = newLast;
            Write(id, Capture(task, table, outcome.Failed, types));   // after commit: a rolled-back chunk never leaves error rows
            Mirror(id, cp);
            rc.Control.OnChunkCommitted(new ChunkCommit(id, task.Target, cp.ChunkNo, cp.RowsDone, cp.RowsError)
            {
                MergeStatus = outcome.MergeStatus,      // ruling 89: the status the loader reported, not one inferred from the count
                MergeRowsAffected = outcome.MergeRowsAffected,
            });
            rc.Log("info", $"{id} {task.Target}: chunk {cp.ChunkNo} committed ({cp.RowsDone:N0} rows, {cp.RowsError:N0} rejected"
                + MergeNote(outcome.MergeStatus, outcome.MergeRowsAffected) + ")", persist: false);
            // Ruling 192, judged after the commit: the rejected rows are recorded, as skip-and-log promised, and the checkpoint is past
            // them, so Resume carries on from the next chunk (the operator's "these rows really are bad") and never re-judges the same
            // chunks, while Reopen is the way to fix the plan. A crash (or a hard stop) between chunk K's commit and this line skips
            // the judgement for good: the resumed segment starts past chunk K and never meets it. That costs the early stop, not data -
            // the task then runs on as it did before ruling 192, and the report still names it if it loads nothing (review F6).
            rejects.Add(outcome.Failed);
            if (cp.ChunkNo == ZeroLoadChunks || (lastChunk && cp.ChunkNo < ZeroLoadChunks))
                ThrowIfLoadedNothing(id, task.Target, rowsBefore, cp, rejects);
            if (lastChunk) return new Pass(TransferTaskStatus.Done, cp);
        }
    }

    private async Task<Pass> RunKeylessAsync(string id, TaskPlan task, SqlConnection tgt, TargetShape shape, BulkLoader loader,
        long? rowsBefore, CancellationToken ct)
    {
        int chunkSize = Math.Max(1, task.ChunkSize ?? rc.Options.ChunkSize);
        await using var src = await SqlConnect.OpenAsync(rc.SourceCs, ct);
        await using var cmd = new SqlCommand(task.SourceQuery, src) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        await using var scope = await TxScope.BeginAsync(tgt, ct);
        long loaded = 0, rejected = 0;
        int chunks = 0;
        // Ruling 89: the status is the authority and the count is only ever read through it. Seeded from the task so that a keyless
        // task whose source returned no rows at all - no chunk, so no outcome to read - reports "the merge did not run" instead of the
        // false "merge affected 0" that an accumulator starting at zero used to produce.
        var mergeStatus = string.IsNullOrWhiteSpace(task.MergeSql) ? MergeStatus.NotApplicable : MergeStatus.DidNotRun;
        long mergedTotal = 0;
        bool mergeCountUnknown = false;
        var errors = new List<ErrorRecord>();
        var rejects = new RejectTally();
        async Task GuardAsync()
        {
            if (LoadedNothingReason(task.Target, chunks, loaded, rejected, rowsBefore, rejects, null, keyed: false) is not { } why) return;
            await scope.RollbackAsync();
            throw new TransferException("bad_task", why);
        }
        while (true)
        {
            if (rc.Control.Kind is StopKind.Cancel or StopKind.Fail)
            {
                await scope.RollbackAsync();   // nothing of a keyless task is committed before it completes
                return new Pass(TransferTaskStatus.Paused, Checkpoint.Start);
            }
            int size = ChunkRows(chunkSize, chunks);
            var table = ChunkReader.NewTable(reader);
            int n = await ChunkReader.FillAsync(reader, table, size, ct);
            if (n == 0) break;
            shape.Normalize(table, task.Columns);   // the keyless path needs its own call; the loader does not round by itself (V8)
            long before = loaded;
            var outcome = await loader.LoadAsync(scope, table, allowRestart: false, r => rc.Progress.InFlight(id, before + r), ct);
            if (outcome.Failed.Count > 0 && !rc.Options.SkipErrors)
            {
                await scope.RollbackAsync();
                var first = Capture(task, table, [outcome.Failed[0]], null);
                // A keyless task commits nothing until it finishes, so it always restarts from zero: any error row already recorded
                // for it is this same row from an earlier attempt.
                if (rc.Repo.ErrorRowCount(rc.RunId, id) == 0) Write(id, first);
                throw new TransferException("row_rejected", $"A row was rejected: {first[0].Error}");
            }
            CheckAccounted(id, chunks + 1, outcome);
            loaded += outcome.Loaded;
            rejected += outcome.Failed.Count;
            chunks++;
            if (outcome.MergeStatus == MergeStatus.Ran)
            {
                // Sum only over the chunks whose merge actually ran. One that ran and reported no count makes the task's total unknown;
                // a chunk whose merge did not run contributes nothing and must not be read as a zero.
                mergeStatus = MergeStatus.Ran;
                if (outcome.MergeRowsAffected is long m) mergedTotal += m; else mergeCountUnknown = true;
            }
            errors.AddRange(Capture(task, table, outcome.Failed, null));
            rc.Progress.InFlight(id, loaded);
            rejects.Add(outcome.Failed);
            // Ruling 192. Nothing of a keyless task is committed before it completes, so the judgement rolls the whole task back: a
            // resume restarts it from zero and meets the same verdict, and Reopen is the way on.
            if (chunks == ZeroLoadChunks) await GuardAsync();
            if (n < size) break;
        }
        if (chunks < ZeroLoadChunks) await GuardAsync();   // the source ended within the first chunks: that was all of it
        long? merged = mergeStatus == MergeStatus.Ran && !mergeCountUnknown ? mergedTotal : null;
        var done = new Checkpoint(chunks, null, loaded, rejected, true);
        await ControlTable.UpsertAsync(scope.Connection, scope.Tx, rc.RunId, id, done, ct);
        await scope.CommitAsync(ct);
        Write(id, errors);
        Mirror(id, done);
        rc.Control.OnChunkCommitted(new ChunkCommit(id, task.Target, done.ChunkNo, done.RowsDone, done.RowsError)
        {
            MergeStatus = mergeStatus,
            MergeRowsAffected = merged,
        });
        rc.Log("info", $"{id} {task.Target}: committed in one transaction ({done.RowsDone:N0} rows, {done.RowsError:N0} rejected"
            + MergeNote(mergeStatus, merged) + ")", persist: false);
        return new Pass(TransferTaskStatus.Done, done);
    }

    /// <summary>The keyed path's judgement (ruling 192), with the task's recorded error rows as the fallback for the rejects this
    /// segment's tally never saw - a task resumed after a pause, whose earlier chunks were rejected by an earlier segment.</summary>
    private void ThrowIfLoadedNothing(string id, string target, long? rowsBefore, Checkpoint cp, RejectTally rejects)
    {
        var why = LoadedNothingReason(target, cp.ChunkNo, cp.RowsDone, cp.RowsError, rowsBefore, rejects,
            () => rc.Repo.ErrorNumberCounts(rc.RunId, id), keyed: true);
        if (why is not null) throw new TransferException("bad_task", why);
    }

    /// <summary>
    /// Ruling 192's verdict on a task's first chunks, or null when they loaded a row or rejected none. The reason says that every row
    /// was rejected, names the most common error with its number, and says what Resume and Reopen do for this kind of task.
    /// <para>Ruling 201: also null for the re-run Ruling 186 lets a human confirm - a target table that already held rows
    /// (<paramref name="rowsBefore"/> above 0) - when every reject is a duplicate key (2627 or 2601): those rows are already there, which
    /// is what the confirmation said would happen, not a plan defect. The duplicates must be <b>shown</b>: from the segment's tally when
    /// it covers every reject, otherwise from the error numbers of all of the task's <paramref name="recorded"/> rejects (Ruling 208).
    /// Anything short of that - a reject that is not a duplicate, a recorded reject with no number (every row recorded before migration
    /// step 2 has none), fewer recorded rows than rejects - is judged as before. An FK or CHECK reject (547) in a re-run still stops.</para>
    /// </summary>
    /// <param name="recorded">The task's recorded rejects counted by error number, read only when the tally does not cover every
    /// reject; null when there are none to read (a keyless task records nothing before it completes, and its one segment's tally covers
    /// everything).</param>
    internal static string? LoadedNothingReason(string target, int chunks, long rowsDone, long rowsError, long? rowsBefore,
        RejectTally rejects, Func<IReadOnlyList<ErrorNumberCount>>? recorded, bool keyed)
    {
        ArgumentNullException.ThrowIfNull(rejects);
        if (rowsDone != 0 || rowsError <= 0) return null;
        bool covered = rejects.Rows >= rowsError;
        IReadOnlyList<ErrorNumberCount>? groups = covered ? null : recorded?.Invoke();
        long recordedRows = groups?.Sum(g => g.Rows) ?? 0;
        bool recordedDuplicatesOnly = groups is not null && recordedRows >= rowsError
                                      && groups.All(g => g.Rows == 0 || g.Number is 2627 or 2601);
        if (rowsBefore > 0 && (covered ? rejects.OnlyDuplicateKeys : recordedDuplicatesOnly)) return null;

        string first = chunks == 1 ? "the first chunk" : Inv($"the first {chunks} chunks");
        string common;
        if (!covered && recordedRows > 0 && MostCommon(groups!.Select(g => (g.Number, g.Rows, g.FirstError))) is { } top)
        {
            // Ruling 192 review F3: the tally is this segment's only; the recorded rows cover the chunks earlier segments rejected.
            // Grouped by number (Ruling 208), so duplicates naming different values are one error (re-review N2).
            common = Inv($" The most common recorded error, on {top.Rows:N0} of {recordedRows:N0} recorded rows, was ")
                     + (top.Number is int n ? Inv($"error {n}") : "an error recorded with no server error number") + ": " + top.Text;
        }
        else
        {
            common = rejects.MostCommon() is { } c
                ? Inv($" The most common error, on {c.Rows:N0} of {rejects.Rows:N0} rows")
                  + (rejects.Rows < rowsError ? " rejected since this segment of the run began" : "") + ", was "
                  + (c.Number is int n ? Inv($"error {n}") : "an error with no server error number") + ": " + c.Text
                : "";
        }
        string way = keyed
            ? " The rejected rows are recorded. Reopen Mapping or SQL to fix the plan; if these rows really are bad, Resume carries on "
              + "from the next chunk."
            : " This task has no key, so it loads in one transaction and nothing of it was kept. Reopen Mapping or SQL to fix the plan.";
        return "Every row of " + first + " of " + target + Inv($" was rejected ({rowsError:N0} rows) and none loaded, so the plan is the ")
               + "likelier cause than the data - a column bound to the wrong expression, or a constraint no source row satisfies."
               + common + way;
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>The error number with the most rows, ties to the lowest number; the entry with no number wins only with strictly more
    /// rows than every number (a number says more than its absence). Null when there is nothing with a row.</summary>
    internal static (int? Number, long Rows, string Text)? MostCommon(IEnumerable<(int? Number, long Rows, string Text)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.Where(e => e.Rows > 0).ToList();
        (int? Number, long Rows, string Text)? best = null;
        foreach (var e in list.Where(e => e.Number is not null).OrderBy(e => e.Number))
            if (best is null || e.Rows > best.Value.Rows) best = e;
        foreach (var e in list.Where(e => e.Number is null))
            if (best is null || e.Rows > best.Value.Rows) best = e;
        return best;
    }

    /// <summary>The rejected rows of a task's first chunks, counted by error number, with the first text seen for each (ruling 192).</summary>
    internal sealed class RejectTally
    {
        private readonly Dictionary<int, (long Rows, string Text)> _byNumber = [];
        private (long Rows, string Text)? _noNumber;

        public long Rows { get; private set; }

        /// <summary>Ruling 201: at least one reject, and every one of them a duplicate key (2627 or 2601) by its server error number.</summary>
        public bool OnlyDuplicateKeys => Rows > 0 && _noNumber is null && _byNumber.Keys.All(n => n is 2627 or 2601);

        public void Add(IEnumerable<RowFailure> failures)
        {
            ArgumentNullException.ThrowIfNull(failures);
            foreach (var f in failures)
            {
                Rows++;
                if (f.ErrorNumber is int n)
                    _byNumber[n] = _byNumber.TryGetValue(n, out var e) ? (e.Rows + 1, e.Text) : (1, f.Error);
                else
                    _noNumber = _noNumber is { } e ? (e.Rows + 1, e.Text) : (1, f.Error);
            }
        }

        /// <summary><see cref="TaskRunner.MostCommon"/> over this tally. Null when nothing was added.</summary>
        public (int? Number, long Rows, string Text)? MostCommon()
        {
            var entries = _byNumber.Select(kv => ((int?)kv.Key, kv.Value.Rows, kv.Value.Text)).ToList();
            if (_noNumber is { } x) entries.Add((null, x.Rows, x.Text));
            return TaskRunner.MostCommon(entries);
        }
    }

    /// <summary>
    /// Ruling 73/74: the merge count is reported, never judged - a custom merge may legitimately filter, and T5.4's row-count and
    /// checksum validation is the net. What it must not do is read as 0 when nobody counted, so per ruling 89 the sentence is chosen by
    /// <see cref="ChunkOutcome.MergeStatus"/> and never inferred from the count: a null count means "no merge in this mode", "it never
    /// ran" or "it ran and reported nothing", which are three different things, and only the status can tell them apart.
    /// </summary>
    internal static string MergeNote(MergeStatus status, long? mergeRows) => status switch
    {
        MergeStatus.NotApplicable => "",
        MergeStatus.DidNotRun => ", the merge did not run",
        _ => mergeRows is long m ? $", merge affected {m:N0}" : ", the merge ran and reported no row count",
    };

    /// <summary>
    /// A contract assertion, not a guard with a route behind it (ruling 104). Rulings 73/74 state that
    /// <see cref="ChunkOutcome.Loaded"/> + <see cref="ChunkOutcome.Failed"/> need not equal <see cref="ChunkOutcome.Attempted"/>, and if
    /// that ever became reachable, committing such a chunk would advance the checkpoint past rows in neither list - not loaded, not
    /// rejected, in no error_row, not in the target - with every counter still agreeing. Today no route reaches it: stop mode rolls back
    /// and throws above, skip mode attempts every row. Deleting both call sites fails no test, and that is expected; this exists so that
    /// a future loader change cannot make the state reachable silently. It costs three lines inside the transaction, so it fails safe.
    /// </summary>
    internal static void CheckAccounted(string taskId, int chunkNo, ChunkOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        long accounted = outcome.Loaded + outcome.Failed.Count;
        if (accounted == outcome.Attempted) return;
        throw new TransferException("rows_unaccounted",
            $"Chunk {chunkNo} of task {taskId} handed {outcome.Attempted:N0} rows to the loader but only {accounted:N0} came back "
            + $"accounted for: {outcome.Attempted - accounted:N0} rows are in neither the loaded nor the rejected list. Committing this "
            + "chunk would advance the checkpoint past rows nothing knows about.");
    }

    private void ReportDone(string id, TaskPlan task, long? rowsSource, Checkpoint cp)
    {
        // "0 rows" must not be able to mean both "the source is empty" and "nobody ever counted it".
        string source = rowsSource is null ? "the source count is unknown" : $"the source count said {rowsSource.Value:N0}";
        if (cp.RowsDone + cp.RowsError == 0 && rowsSource > 0)
            rc.Log("warn", $"{id} {task.Target}: finished with no rows loaded and no rows rejected, although {source}.");

        // Ruling 105. A chunk's rows and its checkpoint commit together, but the error rows go to the mirror just after, while the
        // checkpoint already counts them. A crash in that window costs no data - the counts stay right and nothing is duplicated or
        // skipped - but the run would otherwise tell the operator "N rejected" and hand them fewer than N rows, permanently and
        // silently. This runner is the one component holding both numbers, so this is where they get compared.
        long recorded = rc.Repo.ErrorRowCount(rc.RunId, id);
        if (cp.RowsError > recorded)
        {
            string note = $"{id} {task.Target}: {cp.RowsError - recorded:N0} of {cp.RowsError:N0} rejected rows were counted but never "
                + "recorded - the run was interrupted between a chunk committing and its rejected rows being written down. The rows "
                + "themselves loaded correctly; what is lost is which rows were rejected, and it cannot be recovered by resuming.";
            rc.AddNote(note);
            rc.Log("warn", note);
        }

        rc.Log("info", $"{id} {task.Target}: done - {cp.RowsDone:N0} loaded, {cp.RowsError:N0} rejected ({source}).");
    }

    private void Mirror(string id, Checkpoint cp)
    {
        rc.Repo.UpdateTaskProgress(rc.RunId, id, cp.RowsDone, cp.RowsError, cp.LastKeyJson);
        rc.Progress.Committed(id, cp.RowsDone, cp.RowsError);
    }

    /// <summary>
    /// A rejected row with its key, or - when the key could not be produced - with the reason in its error text. A null key on a keyed
    /// task would otherwise be indistinguishable from the null key of a keyless task, and the one row the operator has to go and look at
    /// would be unidentifiable.
    /// </summary>
    internal static List<ErrorRecord> Capture(TaskPlan task, DataTable table, IEnumerable<RowFailure> failures, IReadOnlyList<KeyType>? types)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(failures);
        var list = new List<ErrorRecord>();
        bool keyed = task.KeyColumns.Count > 0;
        foreach (var f in failures)
        {
            var row = table.Rows[f.Row];
            string? key = null;
            string? note = null;
            if (keyed)
            {
                if (types is null)
                {
                    note = NoKeyTypes;
                }
                else
                {
                    try { key = KeyCodec.Display(KeyCodec.FromRow(row, task.KeyColumns, types), task.KeyColumns); }
                    catch (TransferException ex) { note = $"{KeyFailedNote}: {ex.Message}"; }
                }
            }
            string error = TransferFailure.NonBlank(f.Error, Bisector.NoErrorText);
            list.Add(new ErrorRecord(key, RowSnapshot.Json(row, task.Columns), note is null ? error : $"{error} [{note}]", f.ErrorNumber));
        }
        return list;
    }

    /// <summary>The text an error row is recorded with: scrubbing can empty a message whose whole content was a secret, and
    /// <see cref="TransferRepo.AddErrorRow"/> refuses a blank one with an ArgumentException that would end the run over one row.</summary>
    internal static string ErrorText(string? raw, Func<string, string> scrub)
    {
        ArgumentNullException.ThrowIfNull(scrub);
        return TransferFailure.NonBlank(scrub(raw ?? ""), Bisector.NoErrorText);
    }

    private void Write(string id, IEnumerable<ErrorRecord> records)
    {
        foreach (var e in records) rc.Repo.AddErrorRow(rc.RunId, id, e.Key, e.Row, ErrorText(e.Error, rc.Scrub), e.Number);
    }
}
