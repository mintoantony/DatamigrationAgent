using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record Checkpoint(int ChunkNo, string? LastKeyJson, long RowsDone, long RowsError, bool Done)
{
    public static readonly Checkpoint Start = new(0, null, 0, 0, false);
}

/// <summary>
/// Whose checkpoint rows these are (Ruling 212, review HIGH-1). Run ids start at 1 in every project folder and task ids are positional,
/// so (run_id, task_id) alone let a second project's run 1 adopt the first project's checkpoint in a shared target.
/// </summary>
/// <param name="ProjectId">The workspace's own identity (<c>TransferRepo.WorkspaceId</c>, a GUID created once per state database).
/// Empty for rows written by an engine from before this column existed.</param>
/// <param name="Folder">The project folder, so a refusal can say whose rows they are. Never a connection detail.</param>
public sealed record CheckpointOwner(string ProjectId, string? Folder)
{
    /// <summary>The owner of rows written before project identity existed, and of calls made with no owner set (unit tests).</summary>
    public static CheckpointOwner Unowned { get; } = new("", null);
}

/// <summary>
/// dbo.__dbm_checkpoint in the TARGET: written in the same transaction as each chunk (exactly-once).
/// This is the one persistent object the engine creates in a customer database. Nothing here touches any other object, and
/// nothing here writes into or drops a table of that name whose shape has not been verified as ours.
/// <para>Every row belongs to one project (<see cref="CheckpointOwner"/>): checkpoints are read and written under
/// <see cref="CurrentOwner"/>, a finished or cancelled run removes only its own project's rows, and the table is dropped only once it
/// is empty (Ruling 212).</para>
/// </summary>
public static class ControlTable
{
    public const string Name = "[dbo].[__dbm_checkpoint]";

    private static readonly AsyncLocal<CheckpointOwner?> AmbientOwner = new();

    /// <summary>
    /// The project whose checkpoints <see cref="ReadAsync"/> and <see cref="UpsertAsync"/> read and write. Set by
    /// <c>TransferEngine.RunAsync</c> for the whole segment (it flows into every task worker); ambient because the per-chunk callers
    /// already take a run id and a task id, and the owner is the same for every call of a segment. Unset reads as
    /// <see cref="CheckpointOwner.Unowned"/>.
    /// </summary>
    internal static CheckpointOwner? CurrentOwner
    {
        get => AmbientOwner.Value;
        set => AmbientOwner.Value = value;
    }

    private static CheckpointOwner Owner => AmbientOwner.Value ?? CheckpointOwner.Unowned;

    /// <summary>nvarchar(64): longer ids would be silently truncated by the parameter and collide, so they are refused.</summary>
    private const int MaxTaskIdLength = 64;

    private const string AppLock = "dbm:" + Name;

    /// <summary>
    /// Fixed collation for the text columns. The table must not inherit comparison rules from the customer's database default:
    /// under a case- or accent-insensitive default, "T01" and "t01" would share one checkpoint row (ruling Q5).
    /// </summary>
    private const string Collation = "Latin1_General_100_BIN2";

    // name|type|max_length(bytes)|scale|nullable|collation, in column order — what EnsureAsync creates.
    private const string LegacyColumns =
        "run_id|bigint|8|0|0|,task_id|nvarchar|128|0|0|" + Collation + ",chunk_no|int|4|0|0|,last_key|nvarchar|-1|0|1|" + Collation + "," +
        "rows_done|bigint|8|0|0|,rows_error|bigint|8|0|0|,done|bit|1|0|0|,updated_at|datetime2|7|3|0|";

    /// <summary>The shape before Ruling 212: no project columns, keyed (run_id, task_id). EnsureAsync upgrades it in place.</summary>
    private const string LegacyShape = LegacyColumns + ";pk=run_id,task_id";

    private const string ExpectedShape = LegacyColumns + ",project_id|nvarchar|72|0|0|" + Collation
                                          + ",project_folder|nvarchar|800|0|1|" + Collation + ";pk=project_id,run_id,task_id";

    private const string MissingShape = "(table missing)";

    /// <summary>
    /// Creates the table if absent, then verifies an existing table has exactly the expected shape (else "control_table_mismatch",
    /// leaving that table untouched). Safe to call repeatedly and concurrently: creation and the check run under a database-scoped
    /// application lock owned by a transaction, so a racing caller waits and then finds the table.
    /// The caller's session settings (XACT_ABORT, isolation level) are left as they were.
    /// </summary>
    public static async Task EnsureAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        await UnderAppLockAsync(conn, async tx =>
        {
            await ExecAsync(conn, tx, $"""
                IF OBJECT_ID(N'{Name}', N'U') IS NULL
                  CREATE TABLE {Name} (
                    run_id bigint NOT NULL, task_id nvarchar(64) COLLATE {Collation} NOT NULL, chunk_no int NOT NULL,
                    last_key nvarchar(max) COLLATE {Collation} NULL,
                    rows_done bigint NOT NULL, rows_error bigint NOT NULL, done bit NOT NULL, updated_at datetime2(3) NOT NULL,
                    project_id nvarchar(36) COLLATE {Collation} NOT NULL, project_folder nvarchar(400) COLLATE {Collation} NULL,
                    PRIMARY KEY (project_id, run_id, task_id));
                """, ct);
            string shape = await ShapeAsync(conn, tx, ct);
            if (string.Equals(shape, LegacyShape, StringComparison.Ordinal))
            {
                // A table an earlier engine created (a run paused across the upgrade). Its rows keep an empty project: whose they are
                // was never recorded, so they are claimed only by a resume of the same run (ClaimLegacyAsync), and until then they
                // count as another project's (ForeignUnfinishedAsync), never as rows a fresh run may adopt.
                await ExecAsync(conn, tx, $"ALTER TABLE {Name} ADD project_id nvarchar(36) COLLATE {Collation} NOT NULL "
                                          + $"CONSTRAINT DF___dbm_checkpoint_project_id DEFAULT N'', "
                                          + $"project_folder nvarchar(400) COLLATE {Collation} NULL;", ct);
                await ExecAsync(conn, tx, $"""
                    DECLARE @pk sysname = (SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'{Name}', N'U') AND type = 'PK');
                    DECLARE @sql nvarchar(400) = N'ALTER TABLE {Name} DROP CONSTRAINT ' + QUOTENAME(@pk);
                    EXEC sys.sp_executesql @sql;
                    """, ct);
                await ExecAsync(conn, tx, $"ALTER TABLE {Name} ADD PRIMARY KEY (project_id, run_id, task_id);", ct);
                shape = await ShapeAsync(conn, tx, ct);
            }
            if (!string.Equals(shape, ExpectedShape, StringComparison.Ordinal))
                throw Mismatch(shape, "it was left untouched. Rename or drop it and retry.");
        }, ct);
    }

    /// <summary>
    /// Ruling 212 (b). The first unfinished checkpoint in the target that is not <paramref name="owner"/>'s, described for a refusal
    /// ("run 1 of the project in D:\x"), or null when there is none or no table. Unfinished rows of another project mean that project's
    /// run is paused, failed or crashed part-way through loading this target; a run of ours now would load beside it and, where the
    /// plans share tables, into rows it has half-loaded.
    /// </summary>
    public static async Task<string?> ForeignUnfinishedAsync(SqlConnection conn, CheckpointOwner owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(owner);
        await using var cmd = new SqlCommand($"""
            IF OBJECT_ID(N'{Name}', N'U') IS NOT NULL AND COL_LENGTH(N'{Name}', N'project_id') IS NOT NULL
              EXEC sys.sp_executesql N'SELECT TOP (1) project_id, project_folder, run_id FROM {Name}
                                       WHERE project_id <> @p AND done = 0 ORDER BY updated_at DESC', N'@p nvarchar(36)', @p = @p;
            """, conn);
        cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 36) { Value = owner.ProjectId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string run = r.GetInt64(2).ToString(CultureInfo.InvariantCulture);
        if (r.GetString(0).Length == 0) return $"run {run} of a project whose folder an earlier db-migrate version did not record";
        return r.IsDBNull(1) ? $"run {run} of another project" : $"run {run} of the project in {r.GetString(1)}";
    }

    /// <summary>
    /// Rows an earlier engine wrote for <paramref name="runId"/> (empty project) become <paramref name="owner"/>'s. Called only on a
    /// resume segment - a run with recorded progress - so a fresh run never adopts a row it did not write.
    /// </summary>
    public static async Task ClaimLegacyAsync(SqlConnection conn, CheckpointOwner owner, long runId, IReadOnlyCollection<string> taskIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.ProjectId.Length == 0 || taskIds.Count == 0) return;
        foreach (var taskId in taskIds)
        {
            CheckTaskId(taskId);
            await using var cmd = new SqlCommand($"""
                IF OBJECT_ID(N'{Name}', N'U') IS NOT NULL
                  UPDATE {Name} SET project_id = @p, project_folder = @f WHERE project_id = N'' AND run_id = @r AND task_id = @t;
                """, conn);
            AddKey(cmd, runId, taskId);
            cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 36) { Value = owner.ProjectId });
            cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.NVarChar, 400) { Value = (object?)Folder(owner) ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Ruling 212 (c): a finished or cancelled run's end. Deletes <paramref name="owner"/>'s rows (every run of that project: at most
    /// one of its runs is ever live, and an abandoned one's rows are dead) and drops the table only when nothing else is left in it.
    /// Returns true when the table is gone (or was never there); false when another project's rows kept it. A table of that name that
    /// is not ours is refused as in <see cref="DropAsync"/>.
    /// </summary>
    public static async Task<bool> ReleaseAsync(SqlConnection conn, CheckpointOwner owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(owner);
        bool dropped = true;
        await UnderAppLockAsync(conn, async tx =>
        {
            string shape = await ShapeAsync(conn, tx, ct);
            if (shape == MissingShape) return;
            if (!string.Equals(shape, ExpectedShape, StringComparison.Ordinal))
                throw Mismatch(shape, "it was not dropped.");
            await using (var del = new SqlCommand($"DELETE FROM {Name} WHERE project_id = @p;", conn, tx))
            {
                del.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 36) { Value = owner.ProjectId });
                await del.ExecuteNonQueryAsync(ct);
            }
            await using var left = new SqlCommand($"SELECT COUNT_BIG(*) FROM {Name};", conn, tx);
            if (Convert.ToInt64(await left.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
            {
                dropped = false;
                return;
            }
            await ExecAsync(conn, tx, $"DROP TABLE {Name};", ct);
        }, ct);
        return dropped;
    }

    private static string? Folder(CheckpointOwner owner)
        => owner.Folder is { Length: > 400 } f ? f[..400] : owner.Folder;

    public static async Task<bool> ExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        await using var cmd = new SqlCommand($"SELECT CASE WHEN OBJECT_ID(N'{Name}', N'U') IS NULL THEN 0 ELSE 1 END", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    /// <summary>
    /// The task's checkpoint, or null when the table exists and holds no row for (runId, taskId).
    /// A missing table is not "no checkpoint": the query fails with a SqlException (invalid object name).
    /// </summary>
    public static async Task<Checkpoint?> ReadAsync(SqlConnection conn, long runId, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        CheckTaskId(taskId);
        await using var cmd = new SqlCommand(
            $"SELECT chunk_no, last_key, rows_done, rows_error, done FROM {Name} WHERE project_id = @p AND run_id = @r AND task_id = @t", conn);
        AddKey(cmd, runId, taskId);
        AddOwner(cmd, Owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Checkpoint(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4));
    }

    /// <summary>
    /// Insert-or-update of one task's checkpoint, inside <paramref name="tx"/> when given (commits and rolls back with the chunk).
    /// No range-locking hints: one runner owns a (run, task) key, and hints would serialise different tasks' first upserts (ruling L1).
    /// A same-key insert race, if it ever happened, fails loudly on the primary key rather than corrupting anything.
    /// </summary>
    public static async Task UpsertAsync(SqlConnection conn, SqlTransaction? tx, long runId, string taskId, Checkpoint cp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        CheckTaskId(taskId);
        ArgumentNullException.ThrowIfNull(cp);
        ArgumentOutOfRangeException.ThrowIfNegative(cp.ChunkNo, nameof(cp));
        ArgumentOutOfRangeException.ThrowIfNegative(cp.RowsDone, nameof(cp));
        ArgumentOutOfRangeException.ThrowIfNegative(cp.RowsError, nameof(cp));

        await using var cmd = new SqlCommand($"""
            UPDATE {Name}
              SET chunk_no = @c, last_key = @k, rows_done = @d, rows_error = @e, done = @f, updated_at = SYSUTCDATETIME()
            WHERE project_id = @p AND run_id = @r AND task_id = @t;
            IF @@ROWCOUNT = 0
              INSERT {Name} (run_id, task_id, chunk_no, last_key, rows_done, rows_error, done, updated_at, project_id, project_folder)
              VALUES (@r, @t, @c, @k, @d, @e, @f, SYSUTCDATETIME(), @p, @pf);
            """, conn, tx);
        AddKey(cmd, runId, taskId);
        AddOwner(cmd, Owner);
        cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.Int) { Value = cp.ChunkNo });
        cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.NVarChar, -1) { Value = (object?)cp.LastKeyJson ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@d", SqlDbType.BigInt) { Value = cp.RowsDone });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.BigInt) { Value = cp.RowsError });
        cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.Bit) { Value = cp.Done });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Drops the table if it is ours; a no-op when absent. A table of that name with any other shape is a customer's object:
    /// "control_table_mismatch", and it is left untouched (ruling H1). Check and drop run under the same application lock.
    /// </summary>
    public static async Task DropAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        await UnderAppLockAsync(conn, async tx =>
        {
            string shape = await ShapeAsync(conn, tx, ct);
            if (shape == MissingShape) return;
            if (!string.Equals(shape, ExpectedShape, StringComparison.Ordinal))
                throw Mismatch(shape, "it was not dropped.");
            await ExecAsync(conn, tx, $"DROP TABLE {Name};", ct);
        }, ct);
    }

    /// <summary>Task-id identity shared with the run repo (Q5): 1-64 characters, no leading or trailing whitespace.</summary>
    internal static void CheckTaskId(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (taskId.Length is 0 or > MaxTaskIdLength)
            throw new ArgumentException($"Task id must be 1-{MaxTaskIdLength} characters (got {taskId.Length}).", nameof(taskId));
        // SQL Server ignores trailing spaces in comparisons under every collation, BIN2 included: "T01 " would share T01's row.
        if (char.IsWhiteSpace(taskId[0]) || char.IsWhiteSpace(taskId[^1]))
            throw new ArgumentException($"Task id \"{taskId}\" has leading or trailing whitespace.", nameof(taskId));
    }

    private static TransferException Mismatch(string shape, string outcome)
        => new("control_table_mismatch",
            $"{Name} already exists in the target but is not a db-migrate checkpoint table; {outcome}",
            [$"expected: {ExpectedShape}", $"found: {shape}"]);

    /// <summary>
    /// Runs <paramref name="body"/> in a client-side transaction holding the exclusive application lock, commits on success and
    /// rolls back on any failure. No SET XACT_ABORT is issued, so the caller's setting survives; with XACT_ABORT OFF a statement
    /// error does not doom the transaction, which is why every failure path rolls back explicitly.
    /// BeginTransaction (even with IsolationLevel.Unspecified) resets the session isolation level to READ COMMITTED and SqlClient
    /// leaves it there after commit, so the caller's level is read first and re-applied on every path, failures included (ruling 68).
    /// </summary>
    private static async Task UnderAppLockAsync(SqlConnection conn, Func<SqlTransaction, Task> body, CancellationToken ct)
    {
        short callerLevel = await SessionIsolationLevelAsync(conn, ct);
        bool failed = false;
        try
        {
            var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Unspecified, ct);
            await RunLockedAsync(conn, tx, body, ct);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            // On a failure path the original exception is the one to report; on success a failed restore must not pass silently.
            await RestoreIsolationLevelAsync(conn, callerLevel, swallowErrors: failed);
        }
    }

    /// <summary>sys.dm_exec_sessions.transaction_isolation_level: 0 unspecified, 1 read uncommitted, 2 read committed,
    /// 3 repeatable read, 4 serializable, 5 snapshot. A session can always read its own row.</summary>
    private static async Task<short> SessionIsolationLevelAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;", conn);
        return Convert.ToInt16(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task RestoreIsolationLevelAsync(SqlConnection conn, short level, bool swallowErrors)
    {
        string? name = level switch
        {
            1 => "READ UNCOMMITTED", 2 => "READ COMMITTED", 3 => "REPEATABLE READ", 4 => "SERIALIZABLE", 5 => "SNAPSHOT",
            _ => null,   // 0 "unspecified": nothing meaningful to re-apply
        };
        if (name is null || conn.State != ConnectionState.Open) return;
        try
        {
            await using var cmd = new SqlCommand($"SET TRANSACTION ISOLATION LEVEL {name};", conn);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception) when (swallowErrors) { /* the original failure is the one worth reporting */ }
    }

    private static async Task RunLockedAsync(SqlConnection conn, SqlTransaction tx, Func<SqlTransaction, Task> body, CancellationToken ct)
    {
        try
        {
            await ExecAsync(conn, tx, $"""
                DECLARE @rc int;
                EXEC @rc = sp_getapplock @Resource = N'{AppLock}', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;
                IF @rc < 0 THROW 50000, N'Timed out waiting for the lock on {Name}.', 1;
                """, ct);
            await body(tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await RollbackQuietlyAsync(conn, tx);
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    private static async Task RollbackQuietlyAsync(SqlConnection conn, SqlTransaction tx)
    {
        // Prefer the transaction object; if the server already ended it (e.g. an error inside a trigger) or a cancelled batch
        // left SqlClient's view stale, fall back to a plain rollback so no transaction is ever left on the caller's connection.
        try { if (tx.Connection is not null) await tx.RollbackAsync(CancellationToken.None); }
        catch (Exception) { /* the original failure is the one worth reporting */ }
        if (conn.State != ConnectionState.Open) return;
        try
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", conn);
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception) { /* as above */ }
    }

    private static async Task<string> ShapeAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        // Two plain result sets (no STRING_AGG, so it also runs on SQL Server 2016), joined client-side.
        await using var cmd = new SqlCommand($"""
            SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.scale, c.is_nullable, c.collation_name
            FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'{Name}', N'U') ORDER BY c.column_id;
            SELECT c.name
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'{Name}', N'U') AND i.is_primary_key = 1 ORDER BY ic.key_ordinal;
            """, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var columns = new List<string>();
        while (await r.ReadAsync(ct))
            columns.Add(string.Join('|', r.GetString(0), r.IsDBNull(1) ? "?" : r.GetString(1),
                r.GetInt16(2).ToString(CultureInfo.InvariantCulture), r.GetByte(3).ToString(CultureInfo.InvariantCulture), r.GetBoolean(4) ? "1" : "0", r.IsDBNull(5) ? "" : r.GetString(5)));
        var pk = new List<string>();
        if (await r.NextResultAsync(ct))
            while (await r.ReadAsync(ct)) pk.Add(r.GetString(0));
        return columns.Count == 0 ? MissingShape : string.Join(',', columns) + ";pk=" + string.Join(',', pk);
    }

    private static void AddKey(SqlCommand cmd, long runId, string taskId)
    {
        cmd.Parameters.Add(new SqlParameter("@r", SqlDbType.BigInt) { Value = runId });
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, MaxTaskIdLength) { Value = taskId });
    }

    private static void AddOwner(SqlCommand cmd, CheckpointOwner owner)
    {
        cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 36) { Value = owner.ProjectId });
        cmd.Parameters.Add(new SqlParameter("@pf", SqlDbType.NVarChar, 400) { Value = (object?)Folder(owner) ?? DBNull.Value });
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
