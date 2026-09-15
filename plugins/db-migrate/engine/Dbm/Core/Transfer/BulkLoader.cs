using System.Data;
using System.Text;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record ChunkOutcome(long Loaded, IReadOnlyList<RowFailure> Failed, int Restarts);

/// <summary>Loads one DataTable into the target inside the caller's transaction, bisecting failures (V1–V5).</summary>
public sealed class BulkLoader(TaskPlan task, TransferOptions options)
{
    public const string SavepointName = "dbm_b";
    private const int NotifyEvery = 5_000;
    private const int XactAbortBit = 16384;   // @@OPTIONS bit for SET XACT_ABORT

    private readonly TaskPlan _task = task ?? throw new ArgumentNullException(nameof(task));
    private readonly TransferOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Plan defects (no bindings, a binding whose source column is not in <paramref name="rows"/>, a staging binding with no #stg
    /// column) throw <c>TransferException("bad_task")</c> instead of being reported as rejected rows. XACT_ABORT is OFF for the duration
    /// of the load (a server-side row error must stay undoable by savepoint) and the connection's prior setting is restored.</summary>
    public async Task<ChunkOutcome> LoadAsync(TxScope scope, DataTable rows, bool allowRestart, Action<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rows);
        CheckBindings(rows);
        if (rows.Rows.Count == 0) return new ChunkOutcome(0, [], 0);

        bool xactAbortWasOn = await XactAbortIsOnAsync(scope, ct);
        if (xactAbortWasOn) await ExecAsync(scope, "SET XACT_ABORT OFF;", ct);
        bool completed = false;
        try
        {
            var target = new Target(this, scope, rows, allowRestart, progress);
            var result = await Bisector.RunAsync(rows.Rows.Count, target, stopAtFirstFailure: !_options.SkipErrors, ct);
            completed = true;
            return new ChunkOutcome(result.Loaded.Count, result.Failed, target.Restarts);
        }
        finally
        {
            if (xactAbortWasOn)
            {
                try
                {
                    await using var cmd = new SqlCommand("SET XACT_ABORT ON;", scope.Connection, scope.Tx);
                    await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception) when (!completed) { /* the load's own exception is the one to report */ }
            }
        }
    }

    private bool Staging => string.Equals(_task.Mode, "staging_merge", StringComparison.OrdinalIgnoreCase);

    private void CheckBindings(DataTable rows)
    {
        // Without mappings SqlBulkCopy maps columns by ordinal position: values land in the wrong target columns without any error.
        if (_task.Columns.Count == 0)
            throw new TransferException("bad_task", $"Task for {_task.Target} has no column bindings.");
        // A mapping to a missing source column fails every attempt, so bisection would report every row as rejected.
        var missing = _task.Columns.Where(b => !rows.Columns.Contains(b.Source)).Select(b => b.Source).ToList();
        if (missing.Count > 0)
            throw new TransferException("bad_task",
                $"Task for {_task.Target} binds source columns the query does not return: {string.Join(", ", missing)}.", missing);
    }

    private async Task WriteAsync(TxScope scope, DataRow[] rows, Action<long>? progress, CancellationToken ct)
    {
        if (!Staging)
        {
            var o = SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls;
            if (_task.IdentityInsert) o |= SqlBulkCopyOptions.KeepIdentity;
            if (_options.TableLock) o |= SqlBulkCopyOptions.TableLock;
            if (_options.FireTriggers) o |= SqlBulkCopyOptions.FireTriggers;
            using var bc = NewCopy(scope, SqlQuote.TableKey(_task.Target), o, progress);
            foreach (var b in _task.Columns) bc.ColumnMappings.Add(b.Source, b.Target);
            await bc.WriteToServerAsync(rows, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(_task.StagingDdl) || string.IsNullOrWhiteSpace(_task.MergeSql))
            throw new TransferException("bad_task", $"Task for {_task.Target} is staging_merge but has no StagingDdl/MergeSql.");
        await ExecAsync(scope, "IF OBJECT_ID(N'tempdb..#stg') IS NOT NULL DROP TABLE #stg;", ct);
        await ExecAsync(scope, _task.StagingDdl, ct);
        var stg = await StagingColumnsAsync(scope, ct);
        using (var bc = NewCopy(scope, "#stg", SqlBulkCopyOptions.KeepNulls, progress))
        {
            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unmapped = new List<string>();
            var table = rows[0].Table;
            foreach (var b in _task.Columns)
            {
                if (mapped.Contains(b.Source)) continue;
                if (stg.TryGetValue(b.Target, out var dest) || stg.TryGetValue(b.Source, out dest))
                {
                    bc.ColumnMappings.Add(b.Source, dest);
                    mapped.Add(b.Source);
                }
                else unmapped.Add(b.Target);
            }
            // A bound value with nowhere to go in #stg would reach the target as NULL (or its default) without any error.
            if (unmapped.Count > 0)
                throw new TransferException("bad_task",
                    $"Task for {_task.Target}: #stg has no column for {string.Join(", ", unmapped)}.", unmapped);
            foreach (DataColumn c in table.Columns)
                if (!mapped.Contains(c.ColumnName) && stg.TryGetValue(c.ColumnName, out var dest) && mapped.Add(c.ColumnName))
                    bc.ColumnMappings.Add(c.ColumnName, dest);
            await bc.WriteToServerAsync(rows, ct);
        }
        await ExecAsync(scope, _task.MergeSql, ct);
        await ExecAsync(scope, "DROP TABLE #stg;", ct);
    }

    private static SqlBulkCopy NewCopy(TxScope scope, string destination, SqlBulkCopyOptions o, Action<long>? progress)
    {
        var bc = new SqlBulkCopy(scope.Connection, o, scope.Tx) { DestinationTableName = destination, BulkCopyTimeout = 0, EnableStreaming = true };
        if (progress is not null)
        {
            bc.NotifyAfter = NotifyEvery;
            bc.SqlRowsCopied += (_, e) => progress(e.RowsCopied);
        }
        return bc;
    }

    private static async Task<Dictionary<string, string>> StagingColumnsAsync(TxScope scope, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand("SELECT name FROM tempdb.sys.columns WHERE object_id = OBJECT_ID(N'tempdb..#stg')", scope.Connection, scope.Tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = r.GetString(0);
        return result;
    }

    private static async Task<bool> XactAbortIsOnAsync(TxScope scope, CancellationToken ct)
    {
        await using var cmd = new SqlCommand($"SELECT CAST(@@OPTIONS & {XactAbortBit} AS int)", scope.Connection, scope.Tx);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ExecAsync(TxScope scope, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, scope.Connection, scope.Tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The exception's message plus its inner messages (SqlBulkCopy puts the reason, e.g. the truncation, in the inner one);
    /// never blank: falls back to the exception type name.</summary>
    internal static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            string m = e.Message?.Trim() ?? "";
            if (m.Length == 0 || sb.ToString().Contains(m, StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append(" -> ");
            sb.Append(m);
        }
        return sb.Length > 0 ? sb.ToString() : ex.GetType().FullName ?? ex.GetType().Name;
    }

    private sealed class Target(BulkLoader loader, TxScope scope, DataTable table, bool allowRestart, Action<long>? progress) : IBisectTarget
    {
        private bool _first = true;
        public int Restarts { get; private set; }

        public async Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct)
        {
            var batch = new DataRow[rows.Count];
            for (int i = 0; i < rows.Count; i++) batch[i] = table.Rows[rows[i]];
            var report = _first ? progress : null;   // live progress only for the first (whole-chunk) attempt
            _first = false;
            scope.Tx.Save(SavepointName);
            try
            {
                await loader.WriteAsync(scope, batch, report, ct);
                return LoadAttempt.Success;
            }
            catch (Exception ex) when ((ex is SqlException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                if (await scope.XactStateAsync(ct) == 1)
                {
                    scope.Tx.Rollback(SavepointName);   // V1/V2
                    return new LoadAttempt(false, Describe(ex));
                }
                return new LoadAttempt(false, Describe(ex), Doomed: true);   // V3
            }
        }

        public async Task RestartAsync(CancellationToken ct)
        {
            if (!allowRestart)
                throw new TransferException("tx_ended",
                    "A row error ended the transaction of a single-transaction (keyless) task, so it cannot be bisected. Fix the data or give the task a key and retry.");
            await scope.RestartAsync(ct);
            Restarts++;
        }
    }
}
