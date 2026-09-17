using System.Text.RegularExpressions;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Samples;

/// <summary>The pair of databases `dbm demo` created, with the connection string for each.</summary>
public sealed record DemoResult(string SourceDatabase, string SourceConnectionString, string TargetDatabase, string TargetConnectionString);

/// <summary>At least one demo database exists already and --force was not given.</summary>
public sealed class DemoExistsException(string message) : Exception(message);

/// <summary>Creates (and drops) the LegacyShop → ShopV2 demo pair from the embedded sample scripts.</summary>
public static partial class DemoDatabases
{
    public const string DefaultPrefix = "DbmDemo_";

    [GeneratedRegex("^[A-Za-z0-9_]{0,50}$")]
    private static partial Regex PrefixPattern();

    public static bool IsValidPrefix(string prefix) => PrefixPattern().IsMatch(prefix);

    /// <summary>("&lt;prefix&gt;LegacyShop", "&lt;prefix&gt;ShopV2").</summary>
    public static (string Source, string Target) Names(string prefix) => (prefix + "LegacyShop", prefix + "ShopV2");

    /// <summary>The same server connection string with Initial Catalog replaced.</summary>
    public static string ForDatabase(string serverConnectionString, string database) =>
        new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>Creates both databases and seeds the source. With <paramref name="force"/> existing ones are dropped first.</summary>
    public static async Task<DemoResult> CreateAsync(string serverConnectionString, int scale, string prefix, bool force,
        CancellationToken ct)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be >= 1");
        if (!IsValidPrefix(prefix)) throw new ArgumentException($"Invalid demo prefix '{prefix}'.", nameof(prefix));
        var (source, target) = Names(prefix);

        await using (var master = await SqlConnect.OpenAsync(ForDatabase(serverConnectionString, "master"), ct))
        {
            var existing = new List<string>();
            foreach (var database in new[] { source, target })
            {
                if (await ExistsAsync(master, database, ct)) existing.Add(database);
            }
            if (existing.Count > 0 && !force)
            {
                throw new DemoExistsException($"Database {string.Join(" and ", existing)} already exists. " +
                    "Re-run with --force to drop and recreate the demo databases, or choose another --prefix.");
            }
            SqlConnection.ClearAllPools();   // pooled sessions of a dropped database are dead
            foreach (var database in existing) await DropOneAsync(master, database, ct);
            foreach (var database in new[] { source, target })
            {
                await ExecuteAsync(master, $"CREATE DATABASE {SqlQuote.Ident(database)};", ct);
            }
        }

        var sourceCs = ForDatabase(serverConnectionString, source);
        var targetCs = ForDatabase(serverConnectionString, target);
        await RunScriptAsync(sourceCs, SampleSql.LegacyShopSchema, ct);
        await RunScriptAsync(sourceCs, SampleSql.Seed(scale), ct);
        await RunScriptAsync(targetCs, SampleSql.ShopV2Schema, ct);
        return new DemoResult(source, sourceCs, target, targetCs);
    }

    /// <summary>Drops this prefix's two demo databases when they exist. Never touches any other database.</summary>
    public static async Task DropAsync(string serverConnectionString, string prefix, CancellationToken ct)
    {
        var (source, target) = Names(prefix);
        SqlConnection.ClearAllPools();
        await using var master = await SqlConnect.OpenAsync(ForDatabase(serverConnectionString, "master"), ct);
        foreach (var database in new[] { source, target })
        {
            if (await ExistsAsync(master, database, ct)) await DropOneAsync(master, database, ct);
        }
        SqlConnection.ClearAllPools();
    }

    private static async Task<bool> ExistsAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT DB_ID(@name);", master);
        cmd.Parameters.AddWithValue("@name", database);
        return await cmd.ExecuteScalarAsync(ct) is not (null or DBNull);
    }

    private static Task DropOneAsync(SqlConnection master, string database, CancellationToken ct)
    {
        var ident = SqlQuote.Ident(database);
        return ExecuteAsync(master, $"ALTER DATABASE {ident} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {ident};", ct);
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RunScriptAsync(string connectionString, string script, CancellationToken ct)
    {
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        await SampleSql.ExecuteAsync(conn, script, ct);
    }
}
