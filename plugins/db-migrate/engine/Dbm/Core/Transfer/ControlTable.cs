using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record Checkpoint(int ChunkNo, string? LastKeyJson, long RowsDone, long RowsError, bool Done)
{
    public static readonly Checkpoint Start = new(0, null, 0, 0, false);
}

/// <summary>
/// dbo.__dbm_checkpoint in the TARGET: written in the same transaction as each chunk (exactly-once).
/// This is the one persistent object the engine creates in a customer database; nothing here touches any other object.
/// </summary>
public static class ControlTable
{
    public const string Name = "[dbo].[__dbm_checkpoint]";

    /// <summary>nvarchar(64): longer ids would be silently truncated by the parameter and collide, so they are refused.</summary>
    private const int MaxTaskIdLength = 64;

    private const string AppLock = "dbm:" + Name;

    /// <summary>
    /// Fixed collation for the text columns. The table must not inherit comparison rules from the customer's database default:
    /// under a case- or accent-insensitive default, "T01" and "t01" would share one checkpoint row (ruling Q5).
    /// </summary>
    private const string Collation = "Latin1_General_100_BIN2";

    // name|type|max_length(bytes)|scale|nullable|collation, in column order — what EnsureAsync creates.
    private const string ExpectedShape =
        "run_id|bigint|8|0|0|,task_id|nvarchar|128|0|0|" + Collation + ",chunk_no|int|4|0|0|,last_key|nvarchar|-1|0|1|" + Collation + "," +
        "rows_done|bigint|8|0|0|,rows_error|bigint|8|0|0|,done|bit|1|0|0|,updated_at|datetime2|7|3|0|;pk=run_id,task_id";

    /// <summary>
    /// Creates the table if absent, then verifies an existing table has exactly the expected shape (else "control_table_mismatch",
    /// leaving that table untouched). Safe to call repeatedly and concurrently: creation is serialised with a database-scoped
    /// application lock held by a transaction, so a racing caller waits and then finds the table.
    /// </summary>
    public static async Task EnsureAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        await ExecAsync(conn, $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @rc int;
            EXEC @rc = sp_getapplock @Resource = N'{AppLock}', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;
            IF @rc < 0
            BEGIN
              ROLLBACK TRANSACTION;
              THROW 50000, N'Timed out waiting to create {Name}.', 1;
            END;
            IF OBJECT_ID(N'{Name}', N'U') IS NULL
              CREATE TABLE {Name} (
                run_id bigint NOT NULL, task_id nvarchar(64) COLLATE {Collation} NOT NULL, chunk_no int NOT NULL,
                last_key nvarchar(max) COLLATE {Collation} NULL,
                rows_done bigint NOT NULL, rows_error bigint NOT NULL, done bit NOT NULL, updated_at datetime2(3) NOT NULL,
                PRIMARY KEY (run_id, task_id));
            COMMIT TRANSACTION;
            """, ct);

        string shape = await ShapeAsync(conn, ct);
        if (!string.Equals(shape, ExpectedShape, StringComparison.Ordinal))
            throw new TransferException("control_table_mismatch",
                $"{Name} already exists in the target but is not a db-migrate checkpoint table; it was left untouched. Rename or drop it and retry.",
                [$"expected: {ExpectedShape}", $"found: {shape}"]);
    }

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
            $"SELECT chunk_no, last_key, rows_done, rows_error, done FROM {Name} WHERE run_id = @r AND task_id = @t", conn);
        AddKey(cmd, runId, taskId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Checkpoint(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4));
    }

    /// <summary>Insert-or-update of one task's checkpoint, inside <paramref name="tx"/> when given (commits and rolls back with the chunk).</summary>
    public static async Task UpsertAsync(SqlConnection conn, SqlTransaction? tx, long runId, string taskId, Checkpoint cp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        CheckTaskId(taskId);
        ArgumentNullException.ThrowIfNull(cp);
        ArgumentOutOfRangeException.ThrowIfNegative(cp.ChunkNo, nameof(cp));
        ArgumentOutOfRangeException.ThrowIfNegative(cp.RowsDone, nameof(cp));
        ArgumentOutOfRangeException.ThrowIfNegative(cp.RowsError, nameof(cp));

        await using var cmd = new SqlCommand($"""
            UPDATE {Name} WITH (UPDLOCK, SERIALIZABLE)
              SET chunk_no = @c, last_key = @k, rows_done = @d, rows_error = @e, done = @f, updated_at = SYSUTCDATETIME()
            WHERE run_id = @r AND task_id = @t;
            IF @@ROWCOUNT = 0
              INSERT {Name} (run_id, task_id, chunk_no, last_key, rows_done, rows_error, done, updated_at)
              VALUES (@r, @t, @c, @k, @d, @e, @f, SYSUTCDATETIME());
            """, conn, tx);
        AddKey(cmd, runId, taskId);
        cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.Int) { Value = cp.ChunkNo });
        cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.NVarChar, -1) { Value = (object?)cp.LastKeyJson ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@d", SqlDbType.BigInt) { Value = cp.RowsDone });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.BigInt) { Value = cp.RowsError });
        cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.Bit) { Value = cp.Done });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Drops the table if present; a no-op otherwise.</summary>
    public static Task DropAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        return ExecAsync(conn, $"IF OBJECT_ID(N'{Name}', N'U') IS NOT NULL DROP TABLE {Name};", ct);
    }

    private static async Task<string> ShapeAsync(SqlConnection conn, CancellationToken ct)
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
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var columns = new List<string>();
        while (await r.ReadAsync(ct))
            columns.Add(string.Join('|', r.GetString(0), r.IsDBNull(1) ? "?" : r.GetString(1),
                r.GetInt16(2).ToString(CultureInfo.InvariantCulture), r.GetByte(3).ToString(CultureInfo.InvariantCulture), r.GetBoolean(4) ? "1" : "0", r.IsDBNull(5) ? "" : r.GetString(5)));
        var pk = new List<string>();
        if (await r.NextResultAsync(ct))
            while (await r.ReadAsync(ct)) pk.Add(r.GetString(0));
        return columns.Count == 0 ? "(table missing)" : string.Join(',', columns) + ";pk=" + string.Join(',', pk);
    }

    private static void CheckTaskId(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (taskId.Length is 0 or > MaxTaskIdLength)
            throw new ArgumentException($"Task id must be 1-{MaxTaskIdLength} characters (got {taskId.Length}).", nameof(taskId));
        // SQL Server ignores trailing spaces in comparisons under every collation, BIN2 included: "T01 " would share T01's row.
        if (char.IsWhiteSpace(taskId[0]) || char.IsWhiteSpace(taskId[^1]))
            throw new ArgumentException($"Task id \"{taskId}\" has leading or trailing whitespace.", nameof(taskId));
    }

    private static void AddKey(SqlCommand cmd, long runId, string taskId)
    {
        cmd.Parameters.Add(new SqlParameter("@r", SqlDbType.BigInt) { Value = runId });
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, MaxTaskIdLength) { Value = taskId });
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch when (conn.State == ConnectionState.Open)
        {
            // A cancelled batch (attention) does not roll back a transaction the batch opened; never leave one on the caller's connection.
            try
            {
                await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", conn);
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception) { /* the original failure is the one worth reporting */ }
            throw;
        }
    }
}
