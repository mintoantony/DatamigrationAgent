using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>
/// The exclusive right to execute one transfer run, and to load into its target, held in the target database for the life of a run
/// segment (ruling 103, ruling 209).
/// <para>
/// A run left in status <c>running</c> has to stay resumable, because that is exactly what a crashed process leaves behind - so the
/// run's status cannot tell a crashed runner from a live one. A session-scoped <c>sp_getapplock</c> can: a dead runner's session is
/// gone and its lock with it, so the resume is granted immediately, while a live runner still holds it and the second engine is
/// refused. Without that distinction two runners share one checkpoint, and after the first chunk they simply overwrite each other's
/// <c>UPDATE</c>s - measured as every row loaded exactly twice with both runs reporting success.
/// </para>
/// <para>
/// Two locks, taken in this order on the one session. The <b>run</b> lock (<see cref="ResourceName"/>) excludes two runners of the
/// same run. The <b>target</b> lock (<see cref="TargetResourceName"/>, open item 23) excludes two runs of different ids - two project
/// folders, two terminals, the CLI beside a desktop session - loading into the same database: it is named after the database as the
/// server resolves it (<c>DB_NAME()</c>), never after a run id, so every run that reaches that database contends for it. Beside it the
/// holder writes a one-row <c>##</c> table naming itself (run, project folder, host, process, since), so a refused starter can say
/// what holds the target; the table dies with the session, like the lock.
/// </para>
/// The locks live on a connection of their own, opened without pooling, which does nothing else for the duration: it must not be
/// inside a transaction that commits, roll back with a chunk, or be closed by anything but <see cref="DisposeAsync"/>.
/// </summary>
public sealed class RunLock : IAsyncDisposable
{
    /// <summary>Resource-name prefix; the run id is appended. Database-scoped, so two targets never contend.</summary>
    public const string Prefix = "dbm:transfer_run:";

    /// <summary>Resource-name prefix of the target lock; the target database's own name (<c>DB_NAME()</c>) is appended.</summary>
    public const string TargetPrefix = "dbm:transfer:";

    private readonly SqlConnection _connection;
    private readonly string _targetResource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _released;

    private RunLock(SqlConnection connection, long runId, string targetResource, string server, string database, int sessionId)
    {
        _connection = connection;
        RunId = runId;
        _targetResource = targetResource;
        Server = server;
        Database = database;
        SessionId = sessionId;
    }

    public long RunId { get; }

    /// <summary>The target's server and database as the lock session resolved them (<c>@@SERVERNAME</c>, <c>DB_NAME()</c>).</summary>
    public string Server { get; }

    public string Database { get; }

    /// <summary>The lock session's <c>@@SPID</c>, for the test seam that kills it (open item 20).</summary>
    internal int SessionId { get; }

    public static string ResourceName(long runId) => Prefix + runId.ToString(CultureInfo.InvariantCulture);

    /// <summary>sp_getapplock resource names are at most 255 characters; a database name is at most 128, so this never truncates.</summary>
    public static string TargetResourceName(string database) => TargetPrefix + database;

    /// <summary>
    /// Takes the run's lock and its target's lock, or throws <c>TransferException("run_in_progress")</c> naming the run - or the target
    /// and what holds it. Never waits: a held lock means another runner is alive right now, and waiting for it would only queue a second
    /// writer behind the first.
    /// </summary>
    /// <param name="holder">What this runner is, in words, for a refused starter to read ("run 7 of the project in D:\x"). Never a
    /// secret: it is shown to whoever else tries to load into this target.</param>
    public static async Task<RunLock> AcquireAsync(string targetConnectionString, long runId, CancellationToken ct, string? holder = null)
    {
        ArgumentNullException.ThrowIfNull(targetConnectionString);
        // Unpooled: the session IS the lock. Returned to a pool it would outlive DisposeAsync with its ## holder table, and a pooled
        // session that a failed release left holding the lock would refuse the next run.
        var connection = await SqlConnect.OpenAsync(Unpooled(targetConnectionString), ct);
        try
        {
            if (!await GetAppLockAsync(connection, ResourceName(runId), ct))
                throw new TransferException("run_in_progress",
                    $"Transfer run {runId} is already being run: {ResourceName(runId)} is held by another session in the target. "
                    + "Two runners on one run share one checkpoint and load every row twice, so the second one is refused. "
                    + "A runner that crashed has already released it, so a resume after a crash is not affected.");

            var (server, database, dbId, spid) = await IdentityAsync(connection, ct);
            string target = TargetResourceName(database);
            if (!await GetAppLockAsync(connection, target, ct))
            {
                string held = await ReadHolderAsync(connection, dbId, ct);
                throw new TransferException("run_in_progress",
                    $"The target database {database} on {server} is already being loaded by another transfer: {held}. Two transfers "
                    + "into one database load every table twice, so this one is refused before it copies a row. The target is free "
                    + "again as soon as that transfer finishes, pauses or its process ends.");
            }
            await WriteHolderAsync(connection, dbId, holder ?? $"transfer run {runId.ToString(CultureInfo.InvariantCulture)}", ct);
            return new RunLock(connection, runId, target, server, database, spid);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Open item 20. Null while this session still holds both locks; otherwise the sentence saying they are gone. The lock connection
    /// is otherwise idle for the whole run, and anything that drops it - an idle timeout in between, a failover, a KILL - frees the
    /// target silently: a second runner could start and load beside this one. This is the cheap check that notices: one
    /// <c>APPLOCK_MODE</c> round trip on the lock session, serialised because parallel tasks ask at once. Never throws.
    /// </summary>
    public async Task<string?> LostAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(
                "SELECT APPLOCK_MODE(N'public', @t, N'Session'), APPLOCK_MODE(N'public', @r, N'Session')", _connection) { CommandTimeout = 15 };
            cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 255) { Value = _targetResource });
            cmd.Parameters.Add(new SqlParameter("@r", SqlDbType.NVarChar, 255) { Value = ResourceName(RunId) });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && r.GetString(0) == "Exclusive" && r.GetString(1) == "Exclusive") return null;
            return $"The lock on the target database {Database} was lost: its session no longer holds {_targetResource}.";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
        {
            return $"The lock on the target database {Database} was lost: its session is gone ({TransferFailure.Describe(ex, t => t)}).";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;
            // A session lock dies with its session, and this session is unpooled, so closing it would do. Release explicitly as well:
            // it costs two round trips and says so in the server's own terms if the lock was somehow not ours any more.
            foreach (var resource in new[] { _targetResource, ResourceName(RunId) })
            {
                try
                {
                    await using var cmd = new SqlCommand("sp_releaseapplock", _connection)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 30,
                    };
                    cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resource });
                    cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (Exception)
                {
                    // The connection is being closed anyway, and closing it releases the lock. Throwing here would turn a finished run
                    // into a thrown run.
                }
            }
        }
        await _connection.DisposeAsync();
    }

    private static string Unpooled(string cs) => new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString;

    private static async Task<bool> GetAppLockAsync(SqlConnection connection, string resource, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30,
        };
        cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resource });
        cmd.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.NVarChar, 32) { Value = "Exclusive" });
        cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
        cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = 0 });
        var result = new SqlParameter("@__result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        cmd.Parameters.Add(result);
        await cmd.ExecuteNonQueryAsync(ct);
        return Convert.ToInt32(result.Value, CultureInfo.InvariantCulture) >= 0;
    }

    private static async Task<(string Server, string Database, int DbId, int Spid)> IdentityAsync(SqlConnection connection,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT COALESCE(@@SERVERNAME, CAST(SERVERPROPERTY('ServerName') AS nvarchar(256))), DB_NAME(), DB_ID(), @@SPID", connection)
        {
            CommandTimeout = 30,
        };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The target's identity query returned no row.");
        return (r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1), Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture));
    }

    /// <summary>Global temporary, so another session can read it; named by DB_ID (an integer, never text) so two databases on one
    /// server keep two tables.</summary>
    private static string HolderTable(int dbId) => "##dbm_transfer_holder_" + dbId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Best effort: the lock is what excludes; the table only lets a refused starter say who holds it. A holder table left by a
    /// session that no longer holds the lock (it cannot, since this one now does) is replaced.</summary>
    private static async Task WriteHolderAsync(SqlConnection connection, int dbId, string holder, CancellationToken ct)
    {
        string table = HolderTable(dbId);
        try
        {
            await using var cmd = new SqlCommand($"""
                IF OBJECT_ID(N'tempdb..{table}') IS NOT NULL DROP TABLE {table};
                CREATE TABLE {table} (holder nvarchar(2000) NOT NULL);
                INSERT {table} (holder) VALUES (@h);
                """, connection) { CommandTimeout = 30 };
            string text = string.Create(CultureInfo.InvariantCulture,
                $"{holder} (host {Environment.MachineName}, process {Environment.ProcessId}, since {Clock.NowText()})");
            cmd.Parameters.Add(new SqlParameter("@h", SqlDbType.NVarChar, 2000) { Value = text.Length > 2000 ? text[..2000] : text });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException)
        {
            // tempdb refused (a locked-down server): the refused side says it could not read the holder, which is the truth.
        }
    }

    private static async Task<string> ReadHolderAsync(SqlConnection connection, int dbId, CancellationToken ct)
    {
        string table = HolderTable(dbId);
        try
        {
            await using var cmd = new SqlCommand(
                $"IF OBJECT_ID(N'tempdb..{table}') IS NOT NULL SELECT TOP (1) holder FROM {table};", connection) { CommandTimeout = 10 };
            if (await cmd.ExecuteScalarAsync(ct) is string text && !string.IsNullOrWhiteSpace(text)) return text;
        }
        catch (SqlException)
        {
            // fall through: say what is not known rather than nothing
        }
        return "its lock is held by another session, which did not say what it is";
    }
}
