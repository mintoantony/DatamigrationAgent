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

    public TransferRepo Repo => Services.Transfers;

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

    internal sealed record ErrorRecord(string? Key, string Row, string Error);

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
            var cp = ResumePoint(row, await ControlTable.ReadAsync(tgt, rc.RunId, id, ct));
            if (cp.Done)
            {
                Mirror(id, cp);   // committed before a crash; only the mirror and PostSql may be missing
            }
            else
            {
                await TargetOps.ExecAllAsync(tgt, task.PreSql, ct);
                var shape = await TargetShape.LoadAsync(tgt, task.Target, ct);
                // Ruling 73: the loader has to know the target it is loading into, or a binding to a missing column and a binding to an
                // identity column become a chunk of rejected rows and a table of silently renumbered ids respectively.
                var loader = new BulkLoader(task, rc.Options, shape);
                var pass = task.KeyColumns.Count > 0
                    ? await RunKeyedAsync(id, task, tgt, shape, loader, cp, ct)
                    : await RunKeylessAsync(id, task, tgt, shape, loader, ct);
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
        Checkpoint cp, CancellationToken ct)
    {
        int chunkSize = Math.Max(1, task.ChunkSize ?? rc.Options.ChunkSize);
        KeyValue? last = cp.LastKeyJson is null ? null : KeyCodec.Decode(cp.LastKeyJson);
        IReadOnlyList<KeyType>? types = last?.Types;
        await using var src = await SqlConnect.OpenAsync(rc.SourceCs, ct);
        while (true)
        {
            if (rc.Control.StopRequested) return new Pass(TransferTaskStatus.Paused, cp);

            DataTable table;
            await using (var cmd = ChunkPlanner.Command(src, task, last, chunkSize))
            await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
            {
                // The key values are the reader's own, typed by GetFieldType: KeyCodec matches the .NET type to the SQL type exactly,
                // so an int handed over for a bigint key is "bad_key". Nothing here converts them.
                types ??= KeyCodec.TypesOf(reader, task.KeyColumns);
                table = ChunkReader.NewTable(reader);
                await ChunkReader.FillAsync(reader, table, chunkSize, ct);
            }

            if (table.Rows.Count == 0)
            {
                cp = cp with { Done = true };
                await ControlTable.UpsertAsync(tgt, null, rc.RunId, id, cp, ct);
                Mirror(id, cp);
                return new Pass(TransferTaskStatus.Done, cp);
            }

            var newLast = KeyCodec.FromRow(table.Rows[table.Rows.Count - 1], task.KeyColumns, types);
            // BulkLoader does not normalize by itself: without this, datetime2(n)/time/datetimeoffset values are truncated where the
            // server's CAST rounds, and smalldatetime values are rounded on the fractional second (V8).
            shape.Normalize(table, task.Columns);
            bool lastChunk = table.Rows.Count < chunkSize;
            ChunkOutcome outcome;
            Checkpoint next;
            await using (var scope = await TxScope.BeginAsync(tgt, ct))
            {
                outcome = await loader.LoadAsync(scope, table, allowRestart: true, n => rc.Progress.InFlight(id, n), ct);
                if (outcome.Failed.Count > 0 && !rc.Options.SkipErrors)
                {
                    await scope.RollbackAsync();
                    var first = Capture(task, table, [outcome.Failed[0]], types);
                    Write(id, first);
                    throw new TransferException("row_rejected", $"Row {first[0].Key ?? "(key unknown)"} rejected: {first[0].Error}");
                }
                CheckAccounted(id, cp.ChunkNo + 1, outcome);
                next = new Checkpoint(cp.ChunkNo + 1, KeyCodec.Encode(newLast), cp.RowsDone + outcome.Loaded,
                    cp.RowsError + outcome.Failed.Count, lastChunk);
                await ControlTable.UpsertAsync(scope.Connection, scope.Tx, rc.RunId, id, next, ct);
                await scope.CommitAsync(ct);
            }

            cp = next;
            last = newLast;
            Write(id, Capture(task, table, outcome.Failed, types));   // after commit: a rolled-back chunk never leaves error rows
            Mirror(id, cp);
            rc.Control.OnChunkCommitted(new ChunkCommit(id, task.Target, cp.ChunkNo, cp.RowsDone, cp.RowsError)
            {
                MergeRowsAffected = outcome.MergeRowsAffected,
            });
            rc.Log("info", $"{id} {task.Target}: chunk {cp.ChunkNo} committed ({cp.RowsDone:N0} rows, {cp.RowsError:N0} rejected"
                + MergeNote(task, outcome.MergeRowsAffected) + ")", persist: false);
            if (lastChunk) return new Pass(TransferTaskStatus.Done, cp);
        }
    }

    private async Task<Pass> RunKeylessAsync(string id, TaskPlan task, SqlConnection tgt, TargetShape shape, BulkLoader loader,
        CancellationToken ct)
    {
        int chunkSize = Math.Max(1, task.ChunkSize ?? rc.Options.ChunkSize);
        await using var src = await SqlConnect.OpenAsync(rc.SourceCs, ct);
        await using var cmd = new SqlCommand(task.SourceQuery, src) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        await using var scope = await TxScope.BeginAsync(tgt, ct);
        long loaded = 0, rejected = 0;
        int chunks = 0;
        long? merged = string.IsNullOrWhiteSpace(task.MergeSql) ? null : 0L;
        var errors = new List<ErrorRecord>();
        while (true)
        {
            if (rc.Control.Kind is StopKind.Cancel or StopKind.Fail)
            {
                await scope.RollbackAsync();   // nothing of a keyless task is committed before it completes
                return new Pass(TransferTaskStatus.Paused, Checkpoint.Start);
            }
            var table = ChunkReader.NewTable(reader);
            int n = await ChunkReader.FillAsync(reader, table, chunkSize, ct);
            if (n == 0) break;
            shape.Normalize(table, task.Columns);   // the keyless path needs its own call; the loader does not round by itself (V8)
            long before = loaded;
            var outcome = await loader.LoadAsync(scope, table, allowRestart: false, r => rc.Progress.InFlight(id, before + r), ct);
            if (outcome.Failed.Count > 0 && !rc.Options.SkipErrors)
            {
                await scope.RollbackAsync();
                var first = Capture(task, table, [outcome.Failed[0]], null);
                Write(id, first);
                throw new TransferException("row_rejected", $"A row was rejected: {first[0].Error}");
            }
            CheckAccounted(id, chunks + 1, outcome);
            loaded += outcome.Loaded;
            rejected += outcome.Failed.Count;
            chunks++;
            if (merged is not null || !string.IsNullOrWhiteSpace(task.MergeSql))
                merged = merged is long s && outcome.MergeRowsAffected is long m ? s + m : null;
            errors.AddRange(Capture(task, table, outcome.Failed, null));
            rc.Progress.InFlight(id, loaded);
            if (n < chunkSize) break;
        }
        var done = new Checkpoint(chunks, null, loaded, rejected, true);
        await ControlTable.UpsertAsync(scope.Connection, scope.Tx, rc.RunId, id, done, ct);
        await scope.CommitAsync(ct);
        Write(id, errors);
        Mirror(id, done);
        rc.Control.OnChunkCommitted(new ChunkCommit(id, task.Target, done.ChunkNo, done.RowsDone, done.RowsError)
        {
            MergeRowsAffected = merged,
        });
        rc.Log("info", $"{id} {task.Target}: committed in one transaction ({done.RowsDone:N0} rows, {done.RowsError:N0} rejected"
            + MergeNote(task, merged) + ")", persist: false);
        return new Pass(TransferTaskStatus.Done, done);
    }

    /// <summary>
    /// Ruling 73/74: the merge count is reported, never judged - a custom merge may legitimately filter, and T5.4's row-count and
    /// checksum validation is the net. What it must not do is read as 0 when nobody counted: a task with no MergeSql says nothing, and
    /// a merge that reported no count says that instead of a number.
    /// </summary>
    private static string MergeNote(TaskPlan task, long? mergeRows)
        => string.IsNullOrWhiteSpace(task.MergeSql) ? ""
            : mergeRows is long m ? $", merge affected {m:N0}"
            : ", the merge reported no row count";

    /// <summary>
    /// In stop mode the bisector ends at the first failed row, so the rows after it are in neither list: not loaded, not rejected, in no
    /// error_row and not in the target (<see cref="ChunkOutcome.Attempted"/> is what was handed over). Committing such a chunk and moving
    /// the checkpoint past those rows drops them from the migration with every counter still agreeing. Refuse the chunk so it rolls back.
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
            list.Add(new ErrorRecord(key, RowSnapshot.Json(row, task.Columns), note is null ? error : $"{error} [{note}]"));
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
        foreach (var e in records) rc.Repo.AddErrorRow(rc.RunId, id, e.Key, e.Row, ErrorText(e.Error, rc.Scrub));
    }
}
