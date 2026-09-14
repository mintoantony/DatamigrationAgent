using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

/// <summary>Shortens ServerControl.StartTimeout for the workspace_locked test; ProcessStateCollection keeps that
/// from racing another test class over the same shared static.</summary>
[Collection(ProcessStateCollection.Name)]
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

    /// <summary>
    /// The lock holder here is not a dbm server at all (a plain FileStream) and never will be, so WaitForHealthyOwnerAsync
    /// (bounded by the shortened StartTimeout) must give up and report a real failure instead of alreadyRunning:true
    /// with an unreachable URL.
    /// </summary>
    [Fact]
    public async Task Serve_reports_workspace_locked_when_the_lock_holder_never_becomes_a_healthy_server()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }   // creates the project so RequireProject succeeds
        var saved = ServerControl.StartTimeout;
        ServerControl.StartTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var r = await CliRunner.RunAsync(tw.Ws, null, "serve");

            Assert.Equal(1, r.Exit);
            Assert.Equal("workspace_locked", r.Json["error"]!.GetValue<string>());
        }
        finally
        {
            ServerControl.StartTimeout = saved;
        }
    }
}
