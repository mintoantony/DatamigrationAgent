using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>
/// The exclusive right to execute one transfer run, held in the target database for the life of a run segment (ruling 103).
/// <para>
/// A run left in status <c>running</c> has to stay resumable, because that is exactly what a crashed process leaves behind - so the
/// run's status cannot tell a crashed runner from a live one. A session-scoped <c>sp_getapplock</c> can: a dead runner's session is
/// gone and its lock with it, so the resume is granted immediately, while a live runner still holds it and the second engine is
/// refused. Without that distinction two runners share one checkpoint, and after the first chunk they simply overwrite each other's
/// <c>UPDATE</c>s - measured as every row loaded exactly twice with both runs reporting success.
/// </para>
/// The lock lives on a connection of its own, which does nothing else for the duration: it must not be inside a transaction that
/// commits, roll back with a chunk, or be closed by anything but <see cref="DisposeAsync"/>.
/// </summary>
public sealed class RunLock : IAsyncDisposable
{
    /// <summary>Resource-name prefix; the run id is appended. Database-scoped, so two targets never contend.</summary>
    public const string Prefix = "dbm:transfer_run:";

    private readonly SqlConnection _connection;
    private bool _released;

    private RunLock(SqlConnection connection, long runId)
    {
        _connection = connection;
        RunId = runId;
    }

    public long RunId { get; }

    public static string ResourceName(long runId) => Prefix + runId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Takes the run's lock, or throws <c>TransferException("run_in_progress")</c> naming the run. Never waits: a held lock means
    /// another runner is alive right now, and waiting for it would only queue a second writer behind the first.
    /// </summary>
    public static async Task<RunLock> AcquireAsync(string targetConnectionString, long runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetConnectionString);
        var connection = await SqlConnect.OpenAsync(targetConnectionString, ct);
        try
        {
            await using var cmd = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30,
            };
            cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ResourceName(runId) });
            cmd.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.NVarChar, 32) { Value = "Exclusive" });
            cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
            cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = 0 });
            var result = new SqlParameter("@__result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            cmd.Parameters.Add(result);
            await cmd.ExecuteNonQueryAsync(ct);

            if (Convert.ToInt32(result.Value, CultureInfo.InvariantCulture) < 0)
                throw new TransferException("run_in_progress",
                    $"Transfer run {runId} is already being run: {ResourceName(runId)} is held by another session in the target. "
                    + "Two runners on one run share one checkpoint and load every row twice, so the second one is refused. "
                    + "A runner that crashed has already released it, so a resume after a crash is not affected.");
            return new RunLock(connection, runId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;
            // A session lock dies with its session, which is what makes a crashed runner release automatically. Release it explicitly
            // as well, because this connection goes back to the pool: a pooled session still holding the lock would refuse the next run.
            try
            {
                await using var cmd = new SqlCommand("sp_releaseapplock", _connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 30,
                };
                cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ResourceName(RunId) });
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
}
