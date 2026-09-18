using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Samples;

/// <summary>The pair of databases `dbm demo` created, with the connection string for each.</summary>
public sealed record DemoResult(string SourceDatabase, string SourceConnectionString, string TargetDatabase, string TargetConnectionString);

/// <summary>At least one demo database exists already and --force was not given.</summary>
public sealed class DemoExistsException(string message) : Exception(message);

/// <summary>A server error part-way through; the message names every step already done, the one that failed and the remedy.</summary>
public sealed class DemoFailedException(string message, SqlException inner) : Exception(message, inner);

/// <summary>Creates (and drops) the LegacyShop → ShopV2 demo pair from the embedded sample scripts.</summary>
public static partial class DemoDatabases
{
    public const string DefaultPrefix = "DbmDemo_";

    /// <summary>
    /// Test seam, keyed by exact database name: text appended to that database's CREATE DATABASE statement, so a test
    /// can make one step fail on the server itself (e.g. " COLLATE No_Such_Collation"). Keyed by name like
    /// ServerControl.SpawnOverrides, so a test only ever affects the databases its own prefix names.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, string> CreateDatabaseSuffixOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// Test seams, keyed by exact database name like <see cref="CreateDatabaseSuffixOverrides"/>: the statement run in
    /// place of that database's DROP DATABASE (after SET SINGLE_USER has succeeded), and in place of the ALTER DATABASE
    /// ... SET MULTI_USER that puts it back when the drop fails. A test sets them to a THROW to make that one step fail
    /// on the server.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, string> DropStatementOverrides = new(StringComparer.Ordinal);

    /// <inheritdoc cref="DropStatementOverrides"/>
    internal static readonly ConcurrentDictionary<string, string> MultiUserStatementOverrides = new(StringComparer.Ordinal);

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
        var sourceCs = ForDatabase(serverConnectionString, source);
        var targetCs = ForDatabase(serverConnectionString, target);

        // Every step that changed the instance, in order, so a failure can say exactly what it left behind.
        var done = new List<string>();
        var step = "connecting to the server";
        try
        {
            await using (var master = await SqlConnect.OpenAsync(ForDatabase(serverConnectionString, "master"), ct))
            {
                step = $"checking whether {source} and {target} exist";
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
                foreach (var database in existing)
                {
                    step = $"dropping {database}";
                    await DropOneAsync(master, database, ct);
                    done.Add($"dropped {database}");
                }
                foreach (var database in new[] { source, target })
                {
                    step = $"creating {database}";
                    var suffix = CreateDatabaseSuffixOverrides.TryGetValue(database, out var s) ? s : "";
                    await ExecuteAsync(master, $"CREATE DATABASE {SqlQuote.Ident(database)}{suffix};", ct);
                    done.Add($"created {database}");
                }
            }

            step = $"creating the LegacyShop tables in {source}";
            await RunScriptAsync(sourceCs, SampleSql.LegacyShopSchema, ct);
            done.Add($"created the LegacyShop tables in {source}");
            step = $"seeding {source} at scale {scale}";
            await RunScriptAsync(sourceCs, SampleSql.Seed(scale), ct);
            done.Add($"seeded {source}");
            step = $"creating the ShopV2 tables in {target}";
            await RunScriptAsync(targetCs, SampleSql.ShopV2Schema, ct);
        }
        catch (DropFailedException ex)
        {
            throw new DemoFailedException(FailureMessage(done, step, ex.Drop.Message, ex.Note), ex.Drop);
        }
        catch (SqlException ex)
        {
            throw new DemoFailedException(FailureMessage(done, step, ex.Message), ex);
        }
        return new DemoResult(source, sourceCs, target, targetCs);
    }

    /// <summary>
    /// "Created X, then failed creating Y: &lt;server text&gt;. Re-run with --force to start over." — every step that
    /// changed the instance, the step that failed, the server's words and the remedy; or, when nothing had changed
    /// yet, says so. <paramref name="note"/> (a sentence about the state the failed step left behind) goes before the
    /// closing sentence.
    /// </summary>
    internal static string FailureMessage(IReadOnlyList<string> done, string step, string serverText, string? note = null)
    {
        var reason = serverText.Trim().TrimEnd('.');
        var state = note is null ? "" : note + " ";
        if (done.Count == 0) return $"Failed {step}: {reason}. {state}No database was created or dropped.";
        var history = string.Join(", ", done);
        return $"{char.ToUpperInvariant(history[0])}{history[1..]}, then failed {step}: {reason}. {state}Re-run with --force to start over.";
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

    /// <summary>
    /// SET SINGLE_USER (to end every other session), then DROP. When the DROP fails the database is still there, so it
    /// is put back in MULTI_USER mode before the failure is reported (open item 34): a demo database nobody else can
    /// open would otherwise outlive the error unannounced. The <see cref="DropFailedException"/> says whether that worked.
    /// </summary>
    private static async Task DropOneAsync(SqlConnection master, string database, CancellationToken ct)
    {
        var ident = SqlQuote.Ident(database);
        await ExecuteAsync(master, $"ALTER DATABASE {ident} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", ct);
        try
        {
            await ExecuteAsync(master, DropStatementOverrides.TryGetValue(database, out var d) ? d : $"DROP DATABASE {ident};", ct);
        }
        catch (SqlException dropFailed)
        {
            var restore = MultiUserStatementOverrides.TryGetValue(database, out var m) ? m : $"ALTER DATABASE {ident} SET MULTI_USER;";
            string note;
            try
            {
                // Not ct: a cancelled command must still get its database back.
                await ExecuteAsync(master, restore, CancellationToken.None);
                note = $"{database} was set back to multi-user mode.";
            }
            catch (SqlException restoreFailed)
            {
                note = $"{database} was left in single-user mode, and setting it back failed: " +
                    $"{restoreFailed.Message.Trim().TrimEnd('.')}. Undo it with: ALTER DATABASE {ident} SET MULTI_USER.";
            }
            throw new DropFailedException(dropFailed, note);
        }
    }

    /// <summary>A DROP DATABASE that failed after SET SINGLE_USER; <see cref="Note"/> says which mode the database is in.</summary>
    internal sealed class DropFailedException(SqlException drop, string note) : Exception($"{drop.Message.Trim().TrimEnd('.')}. {note}", drop)
    {
        public SqlException Drop { get; } = drop;
        public string Note { get; } = note;
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
