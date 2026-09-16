using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>One target transaction that can be replaced after a transaction-ending error (V3). Dispose = rollback unless committed.
/// BeginTransaction (even with IsolationLevel.Unspecified) resets the session isolation level to READ COMMITTED and SqlClient leaves it
/// there afterwards, so the caller's level is read before the first transaction and re-applied on every ending path — commit, rollback and
/// dispose (ruling 68). The connection belongs to the caller and outlives this scope: it must end at the level the caller chose.</summary>
public sealed class TxScope : IAsyncDisposable
{
    private readonly short _callerLevel;
    private readonly List<string> _whenReleased = [];
    private bool _finished;
    private bool _restored;

    private TxScope(SqlConnection connection, SqlTransaction tx, short callerLevel)
    {
        Connection = connection;
        Tx = tx;
        _callerLevel = callerLevel;
    }

    public SqlConnection Connection { get; }
    public SqlTransaction Tx { get; private set; }
    public int Restarts { get; private set; }

    public static async Task<TxScope> BeginAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        short level = await SessionIsolationLevelAsync(conn, ct);
        return new(conn, (SqlTransaction)await conn.BeginTransactionAsync(ct), level);
    }

    /// <summary>1 = usable (savepoint rollback possible). A failed probe means the server transaction is gone: 0.</summary>
    public async Task<int> XactStateAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand("SELECT XACT_STATE()", Connection, Tx);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return 0;
        }
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await SafeRollbackAsync();
        Tx = (SqlTransaction)await Connection.BeginTransactionAsync(ct);
        Restarts++;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await Tx.CommitAsync(ct);
        _finished = true;
        await RestoreSessionStateAsync();
    }

    /// <summary>Never throws.</summary>
    public async Task RollbackAsync()
    {
        await SafeRollbackAsync();
        _finished = true;
        await RestoreSessionStateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished) await SafeRollbackAsync();
        else await Tx.DisposeAsync();
        await RestoreSessionStateAsync();
    }

    private async Task SafeRollbackAsync()
    {
        try { await Tx.RollbackAsync(); }
        catch (Exception) { /* V3: the server may already have ended it; a broken connection is handled by the caller */ }
        try { await Tx.DisposeAsync(); }
        catch (Exception) { }
    }

    /// <summary>sys.dm_exec_sessions.transaction_isolation_level: 0 unspecified, 1 read uncommitted, 2 read committed,
    /// 3 repeatable read, 4 serializable, 5 snapshot. A session can always read its own row.</summary>
    private static async Task<short> SessionIsolationLevelAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;", conn);
        return Convert.ToInt16(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Queues a session SET for the moment this scope lets go of the transaction. A transaction the server has already destroyed
    /// cannot carry a command, and SqlClient refuses a command without one while the connection still has a pending local transaction — so
    /// a caller that has to give the session a setting back on that path hands the statement here instead of losing it.</summary>
    internal void RestoreWhenReleased(string setStatement) => _whenReleased.Add(setStatement);

    /// <summary>Re-applies the level the caller's session had before the first transaction, plus anything queued by
    /// <see cref="RestoreWhenReleased"/>. Runs once, on whichever path ends the scope, with the transaction already released. A failure
    /// here can only mean the connection is gone — in which case its session state no longer exists either — and reporting it would turn a
    /// committed transaction into a thrown commit, so it is swallowed.</summary>
    private async Task RestoreSessionStateAsync()
    {
        if (_restored) return;
        _restored = true;
        string? level = _callerLevel switch
        {
            1 => "READ UNCOMMITTED", 2 => "READ COMMITTED", 3 => "REPEATABLE READ", 4 => "SERIALIZABLE", 5 => "SNAPSHOT",
            _ => null,   // 0 "unspecified": nothing meaningful to re-apply
        };
        var statements = _whenReleased.ToList();
        if (level is not null) statements.Insert(0, $"SET TRANSACTION ISOLATION LEVEL {level};");
        foreach (var sql in statements)
        {
            if (Connection.State != System.Data.ConnectionState.Open) return;
            try
            {
                await using var cmd = new SqlCommand(sql, Connection);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception) { }
        }
    }
}
