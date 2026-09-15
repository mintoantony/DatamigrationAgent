using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>One target transaction that can be replaced after a transaction-ending error (V3). Dispose = rollback unless committed.</summary>
public sealed class TxScope : IAsyncDisposable
{
    private bool _finished;

    private TxScope(SqlConnection connection, SqlTransaction tx)
    {
        Connection = connection;
        Tx = tx;
    }

    public SqlConnection Connection { get; }
    public SqlTransaction Tx { get; private set; }
    public int Restarts { get; private set; }

    public static async Task<TxScope> BeginAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        return new(conn, (SqlTransaction)await conn.BeginTransactionAsync(ct));
    }

    /// <summary>1 = usable (savepoint rollback possible). A failed probe means the server transaction is gone: 0.</summary>
    public async Task<int> XactStateAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand("SELECT XACT_STATE()", Connection, Tx);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
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
    }

    /// <summary>Never throws.</summary>
    public async Task RollbackAsync()
    {
        await SafeRollbackAsync();
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished) await SafeRollbackAsync();
        else await Tx.DisposeAsync();
    }

    private async Task SafeRollbackAsync()
    {
        try { await Tx.RollbackAsync(); }
        catch (Exception) { /* V3: the server may already have ended it; a broken connection is handled by the caller */ }
        try { await Tx.DisposeAsync(); }
        catch (Exception) { }
    }
}
