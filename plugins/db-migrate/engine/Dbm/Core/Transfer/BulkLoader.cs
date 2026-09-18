using System.Data;
using System.Text;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>Which of the three things happened to the task's <c>MergeSql</c> for one chunk (ruling 89).</summary>
public enum MergeStatus
{
    /// <summary>The task has no <c>MergeSql</c>: there was never a merge to run.</summary>
    NotApplicable,

    /// <summary>The task has a <c>MergeSql</c> and no attempt survived to run it, so nothing was merged and no count exists.</summary>
    DidNotRun,

    /// <summary>It ran. <see cref="ChunkOutcome.MergeRowsAffected"/> is what it reported, null only when it reported no count at all.</summary>
    Ran,
}

public sealed record ChunkOutcome(long Loaded, IReadOnlyList<RowFailure> Failed, int Restarts)
{
    /// <summary>Whether the task's <c>MergeSql</c> ran for this chunk, so that a merge which moved nothing is never confused with one
    /// that never executed (ruling 89). Required for the same reason as <see cref="Attempted"/>: the default would be the answer that
    /// says nothing happened.</summary>
    public required MergeStatus MergeStatus { get; init; }

    /// <summary>The rows handed to <see cref="BulkLoader.LoadAsync"/>. <c>Loaded + Failed.Count == Attempted</c> only when the run is
    /// complete; in stop mode it ends at the first failed row, so the difference is rows that were never attempted — in neither list,
    /// in no error_row, and not in the target. A caller that commits such an outcome silently drops them. Required, with no default:
    /// 0 is exactly the value that would hide those rows, so every construction has to say what was handed over (ruling 90).</summary>
    public required long Attempted { get; init; }

    /// <summary>Rows the task's <c>MergeSql</c> reported affected, summed over the attempts that survived; null when the task has no
    /// <c>MergeSql</c> (<see cref="MergeStatus.NotApplicable"/>), when no attempt survived to run it or nothing was staged
    /// (<see cref="MergeStatus.DidNotRun"/>), or when a merge ran and reported no count at all (ruling 74). <see cref="MergeStatus"/>
    /// says which of the three it is; this number alone cannot. A merge that moved nothing shows as 0 beside a non-zero
    /// <see cref="Loaded"/>. The loader does not judge it — a custom merge may legitimately filter, and T5.4's row-count/checksum
    /// validation is the real net.</summary>
    public long? MergeRowsAffected { get; init; }
}

/// <summary>Loads one DataTable into the target inside the caller's transaction, bisecting failures (V1–V5).</summary>
/// <param name="shape">The target's live shape, when the caller has it (it does: T5.3 loads it two lines before constructing the
/// loader). Optional only so that <c>new BulkLoader(task, options)</c> keeps working; without it the loader cannot tell a binding to a
/// column the target does not have, or to an identity column, from bad data, and says so when a chunk fails as a whole.</param>
public sealed class BulkLoader(TaskPlan task, TransferOptions options, TargetShape? shape = null)
{
    public const string SavepointName = "dbm_b";
    private const int NotifyEvery = 5_000;
    private const int XactAbortBit = 16384;   // @@OPTIONS bit for SET XACT_ABORT

    private readonly TaskPlan _task = task ?? throw new ArgumentNullException(nameof(task));
    private readonly TransferOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TargetShape? _shape = shape;

    /// <summary>Plan defects throw <c>TransferException("bad_task")</c> instead of being reported as rejected rows: no bindings, a binding
    /// whose source column is not in <paramref name="rows"/>, a binding in <see cref="TargetShape.NormalizedPrefix"/>'s namespace, a
    /// staging binding with no #stg column, and — when a shape was given — a binding to a column the target does not have or to an
    /// identity column while <c>IdentityInsert</c> is off. So does a chunk in which every row failed on its own with one identical error,
    /// which is a broken load rather than a chunk of individually bad rows (H2) - unless that error is a constraint violation, which is
    /// the server's verdict on each row and comes back as rejected rows (ruling 147); a chunk of one row is exempt, because with one row
    /// there is nothing to tell the two apart. A direct task carrying a <c>MergeSql</c> is refused too: the direct path never runs one.
    /// A <paramref name="scope"/> whose transaction has already ended is refused as <c>tx_ended</c>, and
    /// one whose session state an earlier chunk could not restore as <c>session_state</c>; neither loads a row, empty chunk or not.
    /// XACT_ABORT is OFF for the duration of the load (a server-side row error must stay undoable by savepoint) and the connection's prior
    /// setting is restored. <see cref="ChunkOutcome.Attempted"/> is the row count handed over: in stop mode the run ends at the first
    /// failed row, so <c>Loaded + Failed.Count</c> can be less than it and the difference is rows nothing knows about — the caller must
    /// roll back rather than commit such an outcome.
    /// <para>With <paramref name="allowRestart"/> true, <paramref name="scope"/>'s transaction belongs to the loader between entry and
    /// return: a transaction-ending error rolls it back and starts a new one (V3), any number of times, and rows the caller wrote in it
    /// before the call are gone without notice — a non-zero <see cref="ChunkOutcome.Restarts"/> means the transaction handed in no longer
    /// exists. Such a caller writes to the scope's transaction only after <c>LoadAsync</c> returns (ruling 113); detecting caller writes
    /// is not cheap, so this contract is the only guard. The other side of the same rule: with <paramref name="allowRestart"/> false the
    /// loader never rolls the scope's transaction back itself — a transaction-ending row error surfaces as <c>tx_ended</c> and what
    /// becomes of the caller's own rows in that transaction is its own rollback's business — so a caller that must hold earlier rows in
    /// the same transaction (T5.3's keyless path, one <see cref="TxScope"/> for every chunk) passes false.</para></summary>
    public async Task<ChunkOutcome> LoadAsync(TxScope scope, DataTable rows, bool allowRestart, Action<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rows);
        if (scope.PoisonedBy is { } poison)
            throw new TransferException("session_state",
                $"Session state on this connection could not be restored after an earlier chunk, so it must not be used for another " +
                $"load: {Describe(poison)}", [Describe(poison)]);
        CheckBindings(rows);

        // A scope whose transaction the server (or the caller) has already ended cannot be loaded into: the savepoint would fail, the
        // bisector would read that as a doomed transaction, and RestartAsync would open a brand-new one on the caller's connection --
        // reporting success over a transaction that no longer exists, and silently discarding everything the caller did in it. Ahead of
        // the empty-chunk short-circuit, so that the refusal is not row-conditional: a dead scope is refused whatever the chunk holds,
        // and a caller cannot get a clean answer out of it by handing it nothing (ruling 110).
        if (await scope.XactStateAsync(ct) != 1)
            throw new TransferException("tx_ended",
                $"The transaction this scope was given for {_task.Target} has already ended, so the chunk cannot be loaded into it. " +
                "Begin a new transaction and retry the chunk.");
        if (rows.Rows.Count == 0) return Empty();

        bool xactAbortWasOn = await XactAbortIsOnAsync(scope, ct);
        if (xactAbortWasOn) await ExecAsync(scope, "SET XACT_ABORT OFF;", ct);
        ChunkOutcome outcome;
        try
        {
            var target = new Target(this, scope, rows, allowRestart, progress);
            var result = await Bisector.RunAsync(rows.Rows.Count, target, stopAtFirstFailure: !_options.SkipErrors, ct);
            if (rows.Rows.Count > 1 && result.UniformError is not null) throw WholeChunkFailed(rows.Rows.Count, result.UniformError);
            outcome = new ChunkOutcome(result.Loaded.Count, result.Failed, target.Restarts)
            {
                Attempted = rows.Rows.Count,
                MergeStatus = target.MergeStatus,
                MergeRowsAffected = target.MergeRowsAffected,
            };
        }
        catch
        {
            if (xactAbortWasOn) await RestoreXactAbortAsync(scope);
            throw;
        }
        // Outside the try, so that a restore that cannot put XACT_ABORT back never discards a chunk whose rows are in the
        // transaction (ruling 93); it poisons the scope instead, and the next load on this connection refuses.
        if (xactAbortWasOn) await RestoreXactAbortAsync(scope);
        return outcome;

        // Nothing was staged, so for a staging task nothing merged: DidNotRun, not NotApplicable (which asserts there is no MergeSql)
        // and not Ran (which asserts a merge executed and, with a null count, reported none) -- ruling 111.
        ChunkOutcome Empty() => new(0, [], 0)
        {
            Attempted = 0,
            MergeStatus = Staging ? MergeStatus.DidNotRun : MergeStatus.NotApplicable,
            MergeRowsAffected = null,
        };
    }

    private bool Staging => string.Equals(_task.Mode, "staging_merge", StringComparison.OrdinalIgnoreCase);

    /// <summary>A whole chunk failing one identical way is a broken load, not N bad rows: fail the task, which is recoverable and says
    /// why, rather than shredding a customer's table into error rows and reporting "completed with N rejected". Which failures reach
    /// here is <see cref="BisectResult.UniformError"/>'s rule (ruling 147): an invalid column or object, a broken MergeSql, a conversion,
    /// a truncation, NULL into NOT NULL, a permission - and any failure that carried no server error - fail the task; a chunk whose rows
    /// all fail alike on a FOREIGN KEY, CHECK, PRIMARY KEY or UNIQUE constraint does not, because each of those is the server's verdict on
    /// one row's values, and it comes back as rejected rows. The message says exactly what the rows failed on.</summary>
    private TransferException WholeChunkFailed(int rowCount, string error)
    {
        string unbound = _shape is null
            ? " No target shape was given to this loader, so a binding to a missing or identity target column could not be named."
            : "";
        return new TransferException("bad_task",
            $"Every one of the {rowCount} rows in this chunk for {_task.Target} failed on its own with the same error, so the load is " +
            $"broken rather than the rows.{unbound} The error was: {error}", [error]);
    }

    private void CheckBindings(DataTable rows)
    {
        // Without mappings SqlBulkCopy maps columns by ordinal position: values land in the wrong target columns without any error.
        if (_task.Columns.Count == 0)
            throw new TransferException("bad_task", $"Task for {_task.Target} has no column bindings.");
        // The direct path never runs a MergeSql, and the outcome could only report NotApplicable for it -- the value whose own
        // documentation says the task has no MergeSql. Rather than a merge that cannot say why it did not run, the task is refused
        // with the way out in the message (ruling 111). The generator writes a MergeSql only for staging_merge, so this is a
        // hand-edited or carried-over task.
        if (!Staging && !string.IsNullOrWhiteSpace(_task.MergeSql))
            throw new TransferException("bad_task",
                $"Task for {_task.Target} is direct but carries a MergeSql; direct tasks never merge — remove it or use staging_merge.");
        // A mapping to a missing source column fails every attempt, so bisection would report every row as rejected.
        var missing = _task.Columns.Where(b => !rows.Columns.Contains(b.Source)).Select(b => b.Source).ToList();
        if (missing.Count > 0)
            throw new TransferException("bad_task",
                $"Task for {_task.Target} binds source columns the query does not return: {string.Join(", ", missing)}.", missing);
        // TargetShape.Normalize writes its per-binding rounded values into columns of its own, so a source column in that namespace
        // would be overwritten before the load and the wrong value would reach the target with no error (ruling 91).
        var reserved = _task.Columns
            .Where(b => b.Source.StartsWith(TargetShape.NormalizedPrefix, StringComparison.Ordinal)
                     || b.Target.StartsWith(TargetShape.NormalizedPrefix, StringComparison.Ordinal))
            .Select(b => b.Source.StartsWith(TargetShape.NormalizedPrefix, StringComparison.Ordinal) ? b.Source : b.Target)
            .Distinct(StringComparer.Ordinal).ToList();
        if (reserved.Count > 0)
            throw new TransferException("bad_task",
                $"Task for {_task.Target} binds columns named in the \"{TargetShape.NormalizedPrefix}\" namespace, which " +
                $"TargetShape.Normalize writes its own rounded values into: {string.Join(", ", reserved)}. Rename them in the source " +
                "query.", reserved);
        if (_shape is null) return;

        // The target side of a binding, which only the shape can check. Both of these load silently and wrongly: a target column that
        // does not exist makes SqlBulkCopy reject every row, and an identity column without KeepIdentity discards the bound values.
        var unknown = _task.Columns.Where(b => _shape.Find(b.Target) is null).Select(b => b.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
            throw new TransferException("bad_task",
                $"Task for {_task.Target} binds target columns that {_task.Target} does not have: {string.Join(", ", unknown)}.", unknown);
        if (_task.IdentityInsert) return;
        var identity = _task.Columns.Where(b => _shape.Find(b.Target)!.IsIdentity).Select(b => b.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (identity.Count > 0)
            throw new TransferException("bad_task",
                $"Task for {_task.Target} binds source values to identity column {string.Join(", ", identity)} while identityInsert is " +
                "off, so the server would discard every bound value and assign its own. Either turn identityInsert on for this task, or " +
                "drop the binding and let the server assign the value.", identity);
    }

    /// <summary>The DataTable column a binding's values come from: <see cref="TargetShape.Normalize"/> writes each binding's rounded
    /// values into a column of its own and never touches the source column, so that one wins when it is there.</summary>
    private static string SourceColumn(DataTable table, ColumnBinding binding)
    {
        string normalized = TargetShape.NormalizedColumn(binding);
        return table.Columns.Contains(normalized) ? normalized : binding.Source;
    }

    /// <summary>The #stg column mappings for one chunk: every binding, from its normalised column when it has one, plus any remaining
    /// table column (the key aliases) whose name matches a #stg column. Extracted only so that "each destination is mapped once" can be
    /// asserted — SqlBulkCopy accepts two mappings to one destination and silently keeps one of them. Two bindings that want the same #stg
    /// column are refused rather than one of them dropped: only one could ever be loaded, and a mapping a human wrote must not vanish
    /// without saying so (ruling 100). One source column bound to two targets is carried into both #stg columns, exactly as the direct
    /// path carries it into both target columns (ruling 109).</summary>
    internal static (List<SqlBulkCopyColumnMapping> Mappings, List<string> Unmapped) StagingMappings(
        string target, DataTable table, IReadOnlyList<ColumnBinding> bindings, IReadOnlyDictionary<string, string> staging)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(staging);
        var mappings = new List<SqlBulkCopyColumnMapping>();
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // #stg column -> the source already going there
        var unmapped = new List<string>();
        foreach (var b in bindings)
        {
            // Every binding is carried, including a second one on a source column that already has a mapping: one source column bound
            // to two targets is the fan-out H3 and ruling 73 allow, SqlBulkCopy takes one source to two destinations, and the staging
            // path must put the same values in the target as the direct path does for the same task (ruling 109).
            string source = SourceColumn(table, b);
            if (staging.TryGetValue(b.Target, out var dest) || staging.TryGetValue(b.Source, out dest))
            {
                if (taken.TryGetValue(dest, out var first))
                    throw new TransferException("bad_task",
                        $"Task for {target} binds two source columns to the one #stg column {dest}: {first} and {b.Source}. Only one of " +
                        "them can be loaded, so the task is refused rather than dropping the other without saying so.",
                        [dest, first, b.Source]);
                mapped.Add(source);
                taken[dest] = b.Source;
                mappings.Add(new SqlBulkCopyColumnMapping(source, dest));
            }
            else unmapped.Add(b.Target);
        }
        // Whatever is left that #stg has a column for: the key aliases. The three cases this method distinguishes, in one place:
        //  - a trailing table column whose #stg destination a binding has already claimed is SKIPPED, not refused -- for a binding read
        //    from its normalised column that column is its own raw source, which nobody wrote as a mapping, and mapping it as well would
        //    point two mappings at one destination (ruling 92). Nothing a human wrote is lost: the derived mapping wins;
        //  - a second binding on one source column, pointing at its own #stg column, is CARRIED (ruling 109, above);
        //  - two bindings that want the same #stg column are REFUSED (ruling 100, above), because only one could ever be loaded.
        foreach (DataColumn c in table.Columns)
            if (!mapped.Contains(c.ColumnName) && staging.TryGetValue(c.ColumnName, out var dest) && !taken.ContainsKey(dest)
                && mapped.Add(c.ColumnName))
            {
                taken[dest] = c.ColumnName;
                mappings.Add(new SqlBulkCopyColumnMapping(c.ColumnName, dest));
            }
        return (mappings, unmapped);
    }

    /// <summary>Returns the rows the task's MergeSql reported affected, or null when there is no merge (ruling 74).</summary>
    private async Task<long?> WriteAsync(TxScope scope, DataRow[] rows, Action<long>? progress, CancellationToken ct)
    {
        var table = rows[0].Table;
        if (!Staging)
        {
            var o = SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls;
            if (_task.IdentityInsert) o |= SqlBulkCopyOptions.KeepIdentity;
            if (_options.TableLock) o |= SqlBulkCopyOptions.TableLock;
            if (_options.FireTriggers) o |= SqlBulkCopyOptions.FireTriggers;
            using var bc = NewCopy(scope, SqlQuote.TableKey(_task.Target), o, progress);
            foreach (var b in _task.Columns) bc.ColumnMappings.Add(SourceColumn(table, b), b.Target);
            await bc.WriteToServerAsync(rows, ct);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_task.StagingDdl) || string.IsNullOrWhiteSpace(_task.MergeSql))
            throw new TransferException("bad_task", $"Task for {_task.Target} is staging_merge but has no StagingDdl/MergeSql.");
        await ExecAsync(scope, "IF OBJECT_ID(N'tempdb..#stg') IS NOT NULL DROP TABLE #stg;", ct);
        await ExecAsync(scope, _task.StagingDdl, ct);
        var stg = await StagingColumnsAsync(scope, ct);
        using (var bc = NewCopy(scope, "#stg", SqlBulkCopyOptions.KeepNulls, progress))
        {
            var (mappings, unmapped) = StagingMappings(_task.Target, table, _task.Columns, stg);
            // A bound value with nowhere to go in #stg would reach the target as NULL (or its default) without any error.
            if (unmapped.Count > 0)
                throw new TransferException("bad_task",
                    $"Task for {_task.Target}: #stg has no column for {string.Join(", ", unmapped)}.", unmapped);
            foreach (var m in mappings) bc.ColumnMappings.Add(m);
            await bc.WriteToServerAsync(rows, ct);
        }
        int affected = await ExecAsync(scope, _task.MergeSql, ct);
        await ExecAsync(scope, "DROP TABLE #stg;", ct);
        return affected;   // negative when the merge counts nothing (SET NOCOUNT ON)
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

    /// <summary>Gives the caller's connection its XACT_ABORT setting back. When the server transaction is gone there is nowhere to issue
    /// the SET from here — a command carrying that transaction fails with "The server failed to resume the transaction", and while the
    /// scope still holds the transaction object SqlClient refuses a command that carries none (both measured at this point in the load).
    /// So it is handed to the scope, which issues it once it has released the transaction and the bare connection accepts it again.
    /// If the SET is refused outright, the caller's chunk is not thrown away for it: the scope is poisoned instead (ruling 93), on the
    /// failed path as well as the successful one — the session state is equally unrestored either way, and a caller that catches,
    /// restarts and retries on the same scope would otherwise inherit XACT_ABORT OFF in silence (ruling 112).
    /// <para>No route reaches the catch below from a client: three agents have driven every transaction state, and in each one where
    /// <see cref="TxScope.XactStateAsync"/> is not 0 the SET succeeds, while the one state that refuses it (a closed connection)
    /// returns 0 and takes the queue branch above. It is kept as a contract assertion, not a guarded harm — see the note on its
    /// test.</para></summary>
    private static async Task RestoreXactAbortAsync(TxScope scope)
    {
        const string SetOn = "SET XACT_ABORT ON;";
        if (await scope.XactStateAsync(CancellationToken.None) == 0) { scope.RestoreWhenReleased(SetOn); return; }
        try
        {
            await using var cmd = new SqlCommand(SetOn, scope.Connection, scope.Tx);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            scope.RestoreWhenReleased(SetOn);   // one more try when the scope releases the transaction
            scope.Poison(ex);                   // and until then nothing else may load on this connection, however this load ended
        }
    }

    private static async Task<int> ExecAsync(TxScope scope, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, scope.Connection, scope.Tx) { CommandTimeout = 0 };
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The server errors that are a verdict on one row's values against a rule (ruling 147): 547 (FOREIGN KEY or CHECK conflict), 2627
    /// (PRIMARY KEY or UNIQUE constraint) and 2601 (duplicate key in a unique index). Nothing else is here on purpose - 515, NULL into
    /// NOT NULL, most of all: an unmapped NOT NULL column is a plan defect, and read as a row fault it would shred a table into rejects.
    /// </summary>
    internal static readonly IReadOnlySet<int> RowFaultErrors = new HashSet<int> { 547, 2627, 2601 };

    /// <summary>
    /// Ruling 147's rule, and nothing but the rule: at least one error, and every one of them a constraint violation. No numbers is
    /// false - a failure that carried no server error is a client-side or a non-SQL fault, and "nothing said otherwise" must never read
    /// as "the rows are individually bad", or H2 would switch off for all of them.
    /// </summary>
    internal static bool IsRowFault(IReadOnlyList<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        return errorNumbers.Count > 0 && errorNumbers.All(RowFaultErrors.Contains);
    }

    /// <summary>
    /// Whether a failed attempt was a row fault: the rule applied to the numbers of every server <b>error</b> anywhere in the chain.
    /// <para>"Error" is load-bearing. Measured on SQL Server 2025, every constraint violation out of SqlBulkCopy arrives as 547 (or
    /// 2627) followed by 3621, "The statement has been terminated.", at severity class 0. Class 0-10 is an informational message, not an
    /// error, and counting it would make every constraint violation fail the rule - the fix would do nothing at all. So messages at
    /// class 10 or below are left out, and the rule is otherwise applied exactly as ruled.</para>
    /// </summary>
    internal static bool IsRowFault(Exception ex) => IsRowFault(ErrorNumbers(ex));

    /// <summary>The numbers of the server errors (class above 10) in <paramref name="ex"/> and every inner exception; empty when the
    /// chain holds no SqlException at all, as SqlBulkCopy's client-side truncation and NULL checks do not.</summary>
    internal static List<int> ErrorNumbers(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var numbers = new List<int>();
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SqlException sql)
                foreach (SqlError error in sql.Errors)
                    if (error.Class > 10) numbers.Add(error.Number);
        return numbers;
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
        private long _confirmed;         // rows of this chunk that are in the transaction and have not been rolled back
        private long? _merged;           // rows the merges of surviving attempts reported; null once one reported no count at all
        private bool _mergeRan;          // ... and whether any of them ran, which 0 cannot say (ruling 89)

        public int Restarts { get; private set; }

        public MergeStatus MergeStatus => !loader.Staging ? MergeStatus.NotApplicable : _mergeRan ? MergeStatus.Ran : MergeStatus.DidNotRun;

        public long? MergeRowsAffected => _mergeRan ? _merged : null;

        public async Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct)
        {
            var batch = new DataRow[rows.Count];
            for (int i = 0; i < rows.Count; i++) batch[i] = table.Rows[rows[i]];
            bool saved = false;
            try
            {
                // Inside the try: a Save that cannot be taken — a transaction the server has already ended — is a failed attempt for
                // the bisector to recover from, not a raw SqlException out of LoadAsync. LoadAsync refuses a scope that arrives in
                // that state, so what is left here is a transaction that dies during the load.
                scope.Tx.Save(SavepointName);
                saved = true;
                long? merged = await loader.WriteAsync(scope, batch, progress is null ? null : InFlight, ct);
                _confirmed += batch.Length;
                if (merged is long m)
                {
                    _merged = m < 0 || (_mergeRan && _merged is null) ? null : (_mergeRan ? _merged : 0) + m;
                    _mergeRan = true;
                }
                Report(_confirmed);
                return LoadAttempt.Success;
            }
            catch (Exception ex) when ((ex is SqlException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                bool rowFault = IsRowFault(ex);         // ruling 147: a constraint violation is N bad rows, never a broken load
                if (saved && await scope.XactStateAsync(ct) == 1)
                {
                    scope.Tx.Rollback(SavepointName);   // V1/V2
                    Report(_confirmed);                 // the attempt's rows are gone again: never leave the caller counting them
                    return new LoadAttempt(false, Describe(ex)) { RowFault = rowFault };
                }
                return new LoadAttempt(false, Describe(ex), Doomed: true) { RowFault = rowFault };   // V3
            }
        }

        public async Task RestartAsync(CancellationToken ct)
        {
            if (!allowRestart)
                throw new TransferException("tx_ended",
                    "A row error ended the transaction of a single-transaction (keyless) task, so it cannot be bisected. Fix the data or give the task a key and retry.");
            await scope.RestartAsync(ct);
            Restarts++;
            // The new transaction holds none of this chunk: the bisector reloads the confirmed rows next, which counts them again.
            _confirmed = 0;
            _merged = null;
            _mergeRan = false;
            Report(0);
        }

        /// <summary>Live rows-copied inside one attempt, on top of what is already confirmed. Withdrawn if the attempt is rolled back.</summary>
        private void InFlight(long copied) => Report(_confirmed + copied);

        private void Report(long rows) => progress?.Invoke(rows);
    }
}
