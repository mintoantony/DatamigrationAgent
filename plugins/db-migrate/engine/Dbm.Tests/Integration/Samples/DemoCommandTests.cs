using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Samples;

/// <summary>`dbm demo` against the test server. Every test uses its own prefix and drops exactly those two databases.</summary>
[Trait("Category", "Integration")]
public sealed class DemoCommandTests : IAsyncLifetime
{
    private readonly string _prefix = "DbmT" + Guid.NewGuid().ToString("N")[..8] + "_";

    private string SourceCs => DemoDatabases.ForDatabase(SqlTestServer.ConnectionString, _prefix + "LegacyShop");
    private string TargetCs => DemoDatabases.ForDatabase(SqlTestServer.ConnectionString, _prefix + "ShopV2");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => DemoDatabases.DropAsync(SqlTestServer.ConnectionString, _prefix, CancellationToken.None);

    /// <summary>Exit 0, or a failure that prints the JSON the CLI actually answered.</summary>
    private static void AssertOk(CliResult r) => Assert.True(r.Exit == 0, $"expected exit 0, got {r.Exit}: {r.Out}");

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var master = new SqlConnection(SqlTestServer.MasterConnectionString);
        await master.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_ID(@name);", master);
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteScalarAsync() is not (null or DBNull);
    }

    private static async Task ExecOnMasterAsync(string sql)
    {
        await using var master = new SqlConnection(SqlTestServer.MasterConnectionString);
        await master.OpenAsync();
        await using var cmd = new SqlCommand(sql, master) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Runs `dbm demo` with the target's CREATE DATABASE made to fail on the server (an unknown collation).</summary>
    private async Task<CliResult> RunWithTargetCreateFailingAsync(TestWorkspace tw, params string[] extra)
    {
        var target = _prefix + "ShopV2";
        DemoDatabases.CreateDatabaseSuffixOverrides[target] = " COLLATE No_Such_Collation";
        try
        {
            return await CliRunner.RunAsync(tw.Ws, null,
                ["demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, .. extra]);
        }
        finally
        {
            DemoDatabases.CreateDatabaseSuffixOverrides.TryRemove(target, out _);
        }
    }

    [Fact]
    public async Task Demo_creates_and_seeds_both_databases()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);

        AssertOk(r);
        Assert.True(r.Json["ok"]!.GetValue<bool>());
        Assert.Equal(_prefix + "LegacyShop", r.Json["source"]!["database"]!.GetValue<string>());
        Assert.Equal(_prefix + "ShopV2", r.Json["target"]!["database"]!.GetValue<string>());
        Assert.False(r.Json["attached"]!.GetValue<bool>());
        Assert.Equal(1000, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST"));
        Assert.Equal(200, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.PROD"));
        Assert.Equal(4, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_STATUS"));
        Assert.Equal(3005, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_HDR"));
        Assert.Equal(6, await ScalarAsync(TargetCs,
            "SELECT COUNT_BIG(*) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = 'app'"));
        Assert.Equal(0, await ScalarAsync(TargetCs, "SELECT COUNT_BIG(*) FROM app.Customers"));
    }

    [Fact]
    public async Task Demo_refuses_to_overwrite_without_force_and_recreates_with_it()
    {
        using var tw = new TestWorkspace();
        var first = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
        AssertOk(first);

        var again = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
        var forced = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--force");

        Assert.Equal(1, again.Exit);
        Assert.Equal("demo_exists", again.Json["error"]!.GetValue<string>());
        Assert.Contains("--force", again.Json["message"]!.GetValue<string>());
        AssertOk(forced);
        Assert.Equal(1000, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST"));   // recreated, not seeded twice
    }

    [Fact]
    public async Task Demo_attach_saves_both_connections_and_queues_discovery()
    {
        using var tw = new TestWorkspace();
        using var services = tw.OpenServices();
        var setupBefore = EnumText.ToText(services.Phases.Get(PhaseName.Setup).Status);
        var lastEventBefore = services.Events.LastId();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--attach");

        AssertOk(r);
        Assert.True(r.Json["attached"]!.GetValue<bool>());
        Assert.True(services.Connections.Has(Side.Src));
        Assert.True(services.Connections.Has(Side.Tgt));
        Assert.Equal(_prefix + "LegacyShop", services.Connections.GetMeta(Side.Src)!.Database);
        Assert.Equal(_prefix + "ShopV2", services.Connections.GetMeta(Side.Tgt)!.Database);
        Assert.Equal(PhaseStatus.Approved, services.Phases.Get(PhaseName.Setup).Status);
        Assert.Equal(PhaseStatus.Running, services.Phases.Get(PhaseName.Discovery).Status);
        Assert.Contains(services.Jobs.Active(), j => j.Kind == "discover");

        // POST /api/connections/{side} publishes state_changed {phase: setup, status: <Setup as saved>} before the
        // workflow reacts (CoreEndpoints.cs). The workflow's own {setup, approved} must not stand in for it.
        var changes = services.Events.Since(lastEventBefore)
            .Where(e => e.Type == "state_changed")
            .Select(e => JsonNode.Parse(e.PayloadJson)!)
            .Select(p => (Phase: p["phase"]?.GetValue<string>(), Status: p["status"]?.GetValue<string>()))
            .ToList();
        var seen = string.Join(", ", changes.Select(c => $"{{{c.Phase}, {c.Status}}}"));
        var endpointEvent = changes.FindIndex(c => c.Phase == "setup" && c.Status == setupBefore);
        var approved = changes.FindIndex(c => c.Phase == "setup" && c.Status == "approved");
        Assert.True(endpointEvent >= 0,
            $"demo --attach published no state_changed {{setup, {setupBefore}}} after saving the connections; state_changed events: [{seen}]");
        Assert.True(endpointEvent < approved,
            $"the connections' state_changed must precede the workflow's {{setup, approved}}; state_changed events: [{seen}]");
    }

    /// <summary>
    /// Ruling 157: a failure after the first database exists names both databases, what happened to each, the
    /// server's own text and the remedy. The target's CREATE DATABASE is made to fail on the server itself.
    /// </summary>
    [Fact]
    public async Task Demo_that_fails_creating_the_target_names_what_it_created_and_the_remedy()
    {
        using var tw = new TestWorkspace();

        var r = await RunWithTargetCreateFailingAsync(tw);

        Assert.Equal(1, r.Exit);
        Assert.Equal("sql_error", r.Json["error"]!.GetValue<string>());
        var message = r.Json["message"]!.GetValue<string>();
        Assert.StartsWith($"Created {_prefix}LegacyShop, then failed creating {_prefix}ShopV2: ", message);
        Assert.Contains("No_Such_Collation", message);   // the server's own text
        Assert.EndsWith(". Re-run with --force to start over.", message);
        Assert.True(await DatabaseExistsAsync(_prefix + "LegacyShop"), $"the message says {_prefix}LegacyShop was created, but it is not there: {message}");
        Assert.False(await DatabaseExistsAsync(_prefix + "ShopV2"), $"the message says creating {_prefix}ShopV2 failed, but it exists: {message}");
    }

    [Fact]
    public async Task Demo_force_that_fails_creating_the_target_names_what_it_dropped_and_created()
    {
        using var tw = new TestWorkspace();
        AssertOk(await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix));

        var r = await RunWithTargetCreateFailingAsync(tw, "--force");

        Assert.Equal(1, r.Exit);
        Assert.Equal("sql_error", r.Json["error"]!.GetValue<string>());
        var message = r.Json["message"]!.GetValue<string>();
        Assert.StartsWith(
            $"Dropped {_prefix}LegacyShop, dropped {_prefix}ShopV2, created {_prefix}LegacyShop, then failed creating {_prefix}ShopV2: ",
            message);
        Assert.EndsWith(". Re-run with --force to start over.", message);
        Assert.False(await DatabaseExistsAsync(_prefix + "ShopV2"), $"the message says creating {_prefix}ShopV2 failed, but it exists: {message}");
    }

    /// <summary>
    /// The database boundary, asserted rather than trusted. The bystander is named
    /// "&lt;prefix&gt;LegacyShop_Keep", so its name begins with this test's demo prefix *and* with the demo source
    /// name: any `LIKE '&lt;prefix&gt;%'` enumeration, or any "drop the source and anything derived from it" —
    /// the obvious wrong implementations of --force and of DropAsync — would destroy it. Exact-name matching
    /// leaves it alone, which is what the assertions below check, before and after each half of the boundary.
    /// </summary>
    [Fact]
    public async Task Demo_force_and_drop_touch_only_the_two_exact_names()
    {
        using var tw = new TestWorkspace();
        var bystander = _prefix + "LegacyShop_Keep";
        var ident = SqlQuote.Ident(bystander);
        await ExecOnMasterAsync($"CREATE DATABASE {ident};");
        try
        {
            await ExecOnMasterAsync($"USE {ident}; CREATE TABLE dbo.Keep (Id int NOT NULL); INSERT INTO dbo.Keep (Id) VALUES (7);");

            var first = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
            var forced = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--force");

            AssertOk(first);
            AssertOk(forced);
            Assert.True(await DatabaseExistsAsync(bystander),
                $"--force dropped the bystander database '{bystander}': it must touch only {_prefix}LegacyShop and {_prefix}ShopV2.");
            Assert.Equal(7, await ScalarAsync(SqlTestServer.ForDatabase(bystander), "SELECT Id FROM dbo.Keep"));

            await DemoDatabases.DropAsync(SqlTestServer.ConnectionString, _prefix, CancellationToken.None);

            Assert.False(await DatabaseExistsAsync(_prefix + "LegacyShop"));
            Assert.False(await DatabaseExistsAsync(_prefix + "ShopV2"));
            Assert.True(await DatabaseExistsAsync(bystander),
                $"DropAsync dropped the bystander database '{bystander}': it must drop only the two exact demo names.");
            Assert.Equal(7, await ScalarAsync(SqlTestServer.ForDatabase(bystander), "SELECT Id FROM dbo.Keep"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecOnMasterAsync($"""
                IF DB_ID({SqlQuote.Literal(bystander)}) IS NOT NULL
                BEGIN
                    ALTER DATABASE {ident} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {ident};
                END
                """);
        }
    }
}
