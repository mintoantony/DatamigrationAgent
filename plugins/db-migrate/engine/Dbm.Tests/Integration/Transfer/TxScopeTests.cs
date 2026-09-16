using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class TxScopeTests
{
    private static async Task ExecAsync(SqlConnection conn, string sql, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<short> LevelAsync(SqlConnection conn, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand("SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;", conn, tx);
        return Convert.ToInt16((await cmd.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("SERIALIZABLE", (short)4)]
    [InlineData("REPEATABLE READ", (short)3)]
    [InlineData("READ UNCOMMITTED", (short)1)]
    [InlineData("SNAPSHOT", (short)5)]
    public async Task The_callers_session_isolation_level_survives_every_TxScope_path(string name, short level)
    {
        // Harm: BeginTransaction resets the session to READ COMMITTED (2) and SqlClient leaves it there. The connection is the
        // caller's own long-lived target connection, so every later read on it would silently run at the wrong isolation level.
        await using var db = await TempDatabase.CreateAsync("dbm_txscope");
        await db.ExecAsync("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
        await db.ExecAsync("CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY);");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"SET TRANSACTION ISOLATION LEVEL {name};");
        Assert.Equal(level, await LevelAsync(conn));

        var seen = new List<(string Path, short Level)>();

        await using (var s = await TxScope.BeginAsync(conn, default)) await s.CommitAsync(default);
        seen.Add(("commit", await LevelAsync(conn)));

        await using (var s = await TxScope.BeginAsync(conn, default)) await s.RollbackAsync();
        seen.Add(("rollback", await LevelAsync(conn)));

        await using (var s = await TxScope.BeginAsync(conn, default)) { }
        seen.Add(("dispose", await LevelAsync(conn)));

        await using (var s = await TxScope.BeginAsync(conn, default))
        {
            s.Tx.Save("p");
            await ExecAsync(conn, "INSERT dbo.T VALUES (1);", s.Tx);
            s.Tx.Rollback("p");
            await s.CommitAsync(default);
        }
        seen.Add(("savepoint rollback + commit", await LevelAsync(conn)));

        await using (var s = await TxScope.BeginAsync(conn, default))
        {
            await s.RestartAsync(default);
            await s.CommitAsync(default);
        }
        seen.Add(("restart + commit", await LevelAsync(conn)));

        var wrong = seen.Where(x => x.Level != level).Select(x => $"{x.Path} -> {x.Level}").ToList();
        Assert.True(wrong.Count == 0, $"the caller set {name} ({level}); TxScope left the session at: {string.Join("; ", wrong)}");
        Assert.Equal(0L, await db.CountAsync("dbo.T"));
    }

    [Fact]
    public async Task A_scope_restarted_after_it_has_finished_still_gives_the_isolation_level_back()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_txscope");
        await db.ExecAsync("CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY);");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;");

        short afterFirst, afterRestart, afterSecond;
        await using (var s = await TxScope.BeginAsync(conn, default))
        {
            await s.CommitAsync(default);
            afterFirst = await LevelAsync(conn);
            await s.RestartAsync(default);      // a second transaction on a scope that has already finished
            afterRestart = await LevelAsync(conn, s.Tx);
            await s.CommitAsync(default);
            afterSecond = await LevelAsync(conn);
        }
        // Harm: the restore is spent after the first finish, so the second BeginTransaction leaves the caller's own
        // long-lived connection at READ COMMITTED for the rest of its life, with nothing left to put it back.
        Assert.Equal((short)4, afterFirst);
        Assert.Equal((short)2, afterRestart);   // the new transaction did reset the session, so there is something to restore
        Assert.Equal((short)4, afterSecond);
        Assert.Equal((short)4, await LevelAsync(conn));
    }
}
