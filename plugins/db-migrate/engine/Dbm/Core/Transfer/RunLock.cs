using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>
/// The exclusive right to load into one target database, held in that database for the life of a run segment (rulings 103, 209, 212).
/// <para>
/// A run left in status <c>running</c> has to stay resumable, because that is exactly what a crashed process leaves behind - so the
/// run's status cannot tell a crashed runner from a live one. A session-scoped <c>sp_getapplock</c> can: a dead runner's session is
/// gone and its lock with it, so the resume is granted immediately, while a live runner still holds it and the second engine is
/// refused. Without that distinction two runners share one checkpoint, and after the first chunk they simply overwrite each other's
/// <c>UPDATE</c>s - measured as every row loaded exactly twice with both runs reporting success.
/// </para>
/// <para>
/// One lock, named after the target database as the server resolves it (<see cref="TargetResourceName"/>, <c>DB_NAME()</c>) and never
/// after a run id: it excludes a second runner of the same run and a run of any other id or project folder alike (Ruling 212, review
/// MED-1 - the run-scoped lock it replaces was taken first and answered two projects that were both on run 1 with a sentence naming
/// neither). Beside it the holder writes a one-row <c>##</c> table naming itself - project, run, folder, host, process, since - so a
/// refused starter can say what holds the target, and in the same-run wording when it is its own run; the table dies with the session,
/// like the lock. The lock excludes only while a segment executes: a paused or crashed run holds nothing, which is why the checkpoint
/// table's rows carry their project too (<see cref="ControlTable.ForeignUnfinishedAsync"/>).
/// </para>
/// The lock lives on a connection of its own, opened without pooling, which does nothing else for the duration: it must not be inside
/// a transaction that commits, roll back with a chunk, or be closed by anything but <see cref="DisposeAsync"/>.
/// </summary>
public sealed class RunLock : IAsyncDisposable
{
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

    /// <summary>sp_getapplock resource names are at most 255 characters; a database name is at most 128, so this never truncates.</summary>
    public static string TargetResourceName(string database) => TargetPrefix + database;

    /// <summary>What a runner of <paramref name="runId"/> calls itself in the holder record.</summary>
    public static string HolderText(CheckpointOwner? owner, long runId)
        => owner?.Folder is { } folder
            ? string.Create(CultureInfo.InvariantCulture, $"run {runId} of the project in {folder}")
            : string.Create(CultureInfo.InvariantCulture, $"transfer run {runId}");

    /// <summary>
    /// Takes the target's lock, or throws <c>TransferException("run_in_progress")</c>: in the same-run wording when the holder is this
    /// project's run <paramref name="runId"/>, otherwise naming the target and what holds it. Never waits: a held lock means another
    /// runner is alive right now, and waiting for it would only queue a second writer behind the first.
    /// </summary>
    /// <param name="owner">Who is asking: its project identity decides the wording of a refusal, and its folder is what a refused
    /// starter elsewhere reads. Never a secret. Null for a caller with no project (tests).</param>
    public static async Task<RunLock> AcquireAsync(string targetConnectionString, long runId, CancellationToken ct,
        CheckpointOwner? owner = null)
    {
        ArgumentNullException.ThrowIfNull(targetConnectionString);
        // Unpooled: the session IS the lock. Returned to a pool it would outlive DisposeAsync with its ## holder table, and a pooled
        // session that a failed release left holding the lock would refuse the next run.
        var connection = await SqlConnect.OpenAsync(Unpooled(targetConnectionString), ct);
        try
        {
            var (server, database, dbId, spid) = await IdentityAsync(connection, ct);
            string target = TargetResourceName(database);
            if (!await GetAppLockAsync(connection, target, ct))
            {
                var held = await ReadHolderAsync(connection, dbId, ct);
                bool sameRun = held is { } h && owner is not null && h.ProjectId == owner.ProjectId && h.RunId == runId;
                throw new TransferException("run_in_progress", sameRun
                    ? $"Transfer run {runId} is already being run: {held!.Value.Text} holds the target database {database}. Two runners "
                      + "on one run share one checkpoint and load every row twice, so the second one is refused. A runner that crashed "
                      + "has already released it, so a resume after a crash is not affected."
                    : $"The target database {database} on {server} is already being loaded by another transfer: "
                      + (held?.Text ?? "its lock is held by another session, which did not say what it is") + ". Two transfers into one "
                      + "database load every table twice, so this one is refused before it copies a row. The target is free again as "
                      + "soon as that transfer finishes, pauses or its process ends.");
            }
            await WriteHolderAsync(connection, dbId, owner, runId, ct);
            return new RunLock(connection, runId, target, server, database, spid);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Open item 20. Null while this session still holds the lock; otherwise the sentence saying it is gone. The lock connection is
    /// otherwise idle for the whole run, and anything that drops it - an idle timeout in between, a failover, a KILL - frees the
    /// target silently: a second runner could start and load beside this one. This is the cheap check that notices: one
    /// <c>APPLOCK_MODE</c> round trip on the lock session, serialised because parallel tasks ask at once. Never throws. The sentence
    /// can quote a server message; the caller scrubs it.
    /// </summary>
    public async Task<string?> LostAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("SELECT APPLOCK_MODE(N'public', @t, N'Session')", _connection) { CommandTimeout = 15 };
            cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 255) { Value = _targetResource });
            if (await cmd.ExecuteScalarAsync(ct) is "Exclusive") return null;
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
            // it costs one round trip and says so in the server's own terms if the lock was somehow not ours any more.
            try
            {
                await using var cmd = new SqlCommand("sp_releaseapplock", _connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 30,
                };
                cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = _targetResource });
                cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception)
            {
                // The connection is being closed anyway, and closing it releases the lock. Throwing here would turn a finished run
                // into a thrown run.
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
    private static async Task WriteHolderAsync(SqlConnection connection, int dbId, CheckpointOwner? owner, long runId, CancellationToken ct)
    {
        string table = HolderTable(dbId);
        try
        {
            await using var cmd = new SqlCommand($"""
                IF OBJECT_ID(N'tempdb..{table}') IS NOT NULL DROP TABLE {table};
                CREATE TABLE {table} (project_id nvarchar(36) NOT NULL, run_id bigint NOT NULL, holder nvarchar(2000) NOT NULL);
                INSERT {table} (project_id, run_id, holder) VALUES (@p, @r, @h);
                """, connection) { CommandTimeout = 30 };
            string text = string.Create(CultureInfo.InvariantCulture,
                $"{HolderText(owner, runId)} (host {Environment.MachineName}, process {Environment.ProcessId}, since {Clock.NowText()})");
            cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 36) { Value = owner?.ProjectId ?? "" });
            cmd.Parameters.Add(new SqlParameter("@r", SqlDbType.BigInt) { Value = runId });
            cmd.Parameters.Add(new SqlParameter("@h", SqlDbType.NVarChar, 2000) { Value = text.Length > 2000 ? text[..2000] : text });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException)
        {
            // tempdb refused (a locked-down server): the refused side says it could not read the holder, which is the truth.
        }
    }

    private static async Task<(string ProjectId, long RunId, string Text)?> ReadHolderAsync(SqlConnection connection, int dbId,
        CancellationToken ct)
    {
        string table = HolderTable(dbId);
        try
        {
            await using var cmd = new SqlCommand(
                $"IF OBJECT_ID(N'tempdb..{table}') IS NOT NULL SELECT TOP (1) project_id, run_id, holder FROM {table};", connection)
            {
                CommandTimeout = 10,
            };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !string.IsNullOrWhiteSpace(r.GetString(2))) return (r.GetString(0), r.GetInt64(1), r.GetString(2));
        }
        catch (SqlException)
        {
            // fall through: say what is not known rather than nothing
        }
        return null;
    }
}
