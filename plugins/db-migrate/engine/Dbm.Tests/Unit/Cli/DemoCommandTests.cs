using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Unit.Cli;

public class DemoCommandTests
{
    [Fact]
    public async Task Demo_without_server_is_a_usage_error()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo");

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
        Assert.Contains("--server", r.Json["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("bad;name")]
    [InlineData("has space")]
    [InlineData("x]y")]
    public async Task Demo_rejects_an_unsafe_prefix(string prefix)
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere", "--prefix", prefix);

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    public async Task Demo_rejects_a_scale_outside_the_range(string scale)
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere", "--scale", scale);

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Demo_attach_without_a_project_fails_before_touching_sql()
    {
        using var tw = new TestWorkspace();

        // "Server=nowhere" would time out if the command connected first: the project check must come first.
        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere;Connect Timeout=1", "--attach");

        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", r.Json["error"]!.GetValue<string>());
        Assert.False(File.Exists(tw.Ws.StateDbPath), "demo --attach created a project instead of refusing: nothing may be initialised by a refusal.");
    }

    [Fact]
    public async Task Demo_attach_after_the_transfer_started_is_locked_before_touching_sql()
    {
        using var tw = new TestWorkspace();
        using (var services = tw.OpenServices()) services.Phases.SetStatus(PhaseName.Transfer, PhaseStatus.Running);

        // As above: a connection attempt would answer sql_error, so "locked" proves the phase check ran first.
        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere;Connect Timeout=1", "--attach");

        Assert.Equal(1, r.Exit);
        Assert.Equal("locked", r.Json["error"]!.GetValue<string>());
        Assert.Contains("transfer has started", r.Json["message"]!.GetValue<string>());
    }

    private static readonly ServerMeta AnyMeta = new("srv", "master", "v", "16.0", 16, "e", "c", "c", 160, "sql login");

    /// <summary>Open item 38: --attach without --server and with nothing saved says where the server comes from instead.</summary>
    [Fact]
    public async Task Demo_attach_without_server_and_without_a_saved_connection_points_to_the_setup_screen()
    {
        using var tw = new TestWorkspace();
        using (tw.OpenServices()) { }

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--attach");

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
        var message = r.Json["message"]!.GetValue<string>();
        Assert.True(message.Contains("Setup screen", StringComparison.Ordinal) && message.Contains("--server", StringComparison.Ordinal),
            $"demo --attach with no --server and no saved connection must name both ways to give it a server; it said: {message}");
    }

    /// <summary>
    /// Open item 38: --attach without --server uses the connection saved in the project (entered in the browser). The saved
    /// string names an unreachable server, so reaching sql_error proves it was used; its password must not come back.
    /// </summary>
    [Fact]
    public async Task Demo_attach_without_server_uses_the_saved_connection_and_never_prints_its_password()
    {
        using var tw = new TestWorkspace();
        using (var services = tw.OpenServices())
            services.Connections.Save(Side.Src, "Server=nowhere;Database=master;User ID=demo;Password=Secr3tPass;Connect Timeout=1", AnyMeta);

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--attach");

        Assert.True(r.Exit == 1 && r.Json["error"]?.GetValue<string>() == "sql_error",
            $"demo --attach did not try the saved connection (expected sql_error from the unreachable saved server): {r.Out}");
        Assert.Contains("No database was created or dropped.", r.Json["message"]!.GetValue<string>());
        Assert.DoesNotContain("Secr3tPass", r.Out);
    }

    [Fact]
    public async Task Demo_against_an_unreachable_server_is_a_sql_error_that_says_nothing_was_touched()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere;Connect Timeout=1");

        Assert.Equal(1, r.Exit);
        Assert.Equal("sql_error", r.Json["error"]!.GetValue<string>());
        Assert.Contains("No database was created or dropped.", r.Json["message"]!.GetValue<string>());
    }

    [Fact]
    public void Names_add_the_prefix()
    {
        Assert.Equal(("DbmDemo_LegacyShop", "DbmDemo_ShopV2"), DemoDatabases.Names(DemoDatabases.DefaultPrefix));
        Assert.Equal(("LegacyShop", "ShopV2"), DemoDatabases.Names(""));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Team_01_", true)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    public void IsValidPrefix_allows_only_letters_digits_and_underscore(string prefix, bool expected) =>
        Assert.Equal(expected, DemoDatabases.IsValidPrefix(prefix));

    [Fact]
    public void IsValidPrefix_rejects_more_than_50_characters() => Assert.False(DemoDatabases.IsValidPrefix(new string('a', 51)));

    [Fact]
    public void ForDatabase_replaces_only_the_database()
    {
        var cs = DemoDatabases.ForDatabase(
            "Server=srv1;Database=master;Integrated Security=true;TrustServerCertificate=true", "DbmDemo_ShopV2");

        var b = new SqlConnectionStringBuilder(cs);
        Assert.Equal("DbmDemo_ShopV2", b.InitialCatalog);
        Assert.Equal("srv1", b.DataSource);
        Assert.True(b.IntegratedSecurity, $"ForDatabase dropped Integrated Security from the connection string: '{cs}'.");
        Assert.True(b.TrustServerCertificate, $"ForDatabase dropped TrustServerCertificate from the connection string: '{cs}'.");
    }

    [Fact]
    public void ConnectionView_never_shows_the_password()
    {
        var view = Json.ToNode(DemoCommand.ConnectionView("DbmDemo_LegacyShop",
            "Server=srv1;Database=DbmDemo_LegacyShop;User ID=demo;Password=Secr3tPass;TrustServerCertificate=true"));

        Assert.DoesNotContain("Secr3tPass", view.ToJsonString());
        Assert.Contains("***", view["connectionString"]!.GetValue<string>());
        Assert.Equal("DbmDemo_LegacyShop", view["database"]!.GetValue<string>());
        Assert.Contains("user=demo", view["describe"]!.GetValue<string>());
    }
}
