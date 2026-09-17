using Dbm.Core.Samples;
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

    [Fact]
    public async Task Demo_creates_and_seeds_both_databases()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);

        Assert.Equal(0, r.Exit);
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
        Assert.Equal(0, first.Exit);

        var again = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
        var forced = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--force");

        Assert.Equal(1, again.Exit);
        Assert.Equal("demo_exists", again.Json["error"]!.GetValue<string>());
        Assert.Contains("--force", again.Json["message"]!.GetValue<string>());
        Assert.Equal(0, forced.Exit);
        Assert.Equal(1000, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST"));   // recreated, not seeded twice
    }

    [Fact]
    public async Task Demo_attach_saves_both_connections_and_queues_discovery()
    {
        using var tw = new TestWorkspace();
        using var services = tw.OpenServices();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--attach");

        Assert.Equal(0, r.Exit);
        Assert.True(r.Json["attached"]!.GetValue<bool>());
        Assert.True(services.Connections.Has(Side.Src));
        Assert.True(services.Connections.Has(Side.Tgt));
        Assert.Equal(_prefix + "LegacyShop", services.Connections.GetMeta(Side.Src)!.Database);
        Assert.Equal(_prefix + "ShopV2", services.Connections.GetMeta(Side.Tgt)!.Database);
        Assert.Equal(PhaseStatus.Approved, services.Phases.Get(PhaseName.Setup).Status);
        Assert.Equal(PhaseStatus.Running, services.Phases.Get(PhaseName.Discovery).Status);
        Assert.Contains(services.Jobs.Active(), j => j.Kind == "discover");
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
        await ExecOnMasterAsync($"CREATE DATABASE [{bystander}];");
        try
        {
            await ExecOnMasterAsync($"USE [{bystander}]; CREATE TABLE dbo.Keep (Id int NOT NULL); INSERT INTO dbo.Keep (Id) VALUES (7);");

            var first = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
            var forced = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--force");

            Assert.Equal(0, first.Exit);
            Assert.Equal(0, forced.Exit);
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
                IF DB_ID('{bystander}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{bystander}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{bystander}];
                END
                """);
        }
    }
}
