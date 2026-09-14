using System.Text;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>A throw-away database "dbm_test_&lt;8 hex&gt;" on the test server; dropped on dispose.</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private TempDatabase(string name)
    {
        Name = name;
        ConnectionString = SqlTestServer.ForDatabase(name);
    }

    public string Name { get; }
    public string ConnectionString { get; }

    /// <summary>Creates "&lt;prefix&gt;_&lt;8 hex&gt;" (default "dbm_test_xxxxxxxx").</summary>
    public static async Task<TempDatabase> CreateAsync(string prefix = "dbm_test")
    {
        var db = new TempDatabase(prefix + "_" + Guid.NewGuid().ToString("N")[..8]);
        await ExecOnMasterAsync($"CREATE DATABASE [{db.Name}]");
        return db;
    }

    /// <summary>Runs a script, splitting batches on lines that are exactly "GO" (case-insensitive, surrounding spaces ignored).</summary>
    public async Task ExecAsync(string script)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public static IReadOnlyList<string> SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }
        if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
        return batches;
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await ExecOnMasterAsync($"""
            IF DB_ID('{Name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Name}];
            END
            """);
    }

    private static async Task ExecOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(SqlTestServer.MasterConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}
