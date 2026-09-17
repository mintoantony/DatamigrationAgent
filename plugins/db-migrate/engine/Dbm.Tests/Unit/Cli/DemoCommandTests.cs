using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.Samples;
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
