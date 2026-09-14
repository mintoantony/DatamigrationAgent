using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class ServerCommandTests
{
    [Fact]
    public async Task Stop_without_a_server_reports_nothing_stopped()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "stop");

        Assert.Equal(0, r.Exit);
        Assert.False(r.Json["stopped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stop_shuts_down_a_running_server()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "stop");

        Assert.True(r.Json["stopped"]!.GetValue<bool>());
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(ServerControl.ReadInfo(tw.Ws));
    }

    [Fact]
    public async Task Ui_and_serve_reuse_the_running_server()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var ui = await CliRunner.RunAsync(tw.Ws, factory, "ui", "--no-browser");
        var serve = await CliRunner.RunAsync(tw.Ws, factory, "serve");

        Assert.Equal(server.Info.UiUrl, ui.Json["url"]!.GetValue<string>());
        Assert.True(serve.Json["alreadyRunning"]!.GetValue<bool>());
        Assert.Equal(server.Info.UiUrl, serve.Json["url"]!.GetValue<string>());
    }
}
