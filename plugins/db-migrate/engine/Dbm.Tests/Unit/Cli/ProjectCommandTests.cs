using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class ProjectCommandTests
{
    [Fact]
    public async Task Init_creates_the_project_starts_the_server_and_prints_the_url()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory(initProject: false);
        WebTestServer? server = null;
        ServerControl.SpawnOverrides[tw.Ws.Root] = async (ws, _) => server = await WebTestServer.StartAsync(ws, factory);
        try
        {
            var first = await CliRunner.RunAsync(tw.Ws, factory, "init", "shop", "--no-browser");
            var second = await CliRunner.RunAsync(tw.Ws, factory, "init");

            Assert.Equal(0, first.Exit);
            Assert.True(first.Json["ok"]!.GetValue<bool>());
            Assert.True(first.Json["created"]!.GetValue<bool>());
            Assert.Equal(server!.Info.UiUrl, first.Json["url"]!.GetValue<string>());
            Assert.Equal(tw.Ws.Root, first.Json["workspace"]!.GetValue<string>());
            Assert.True(File.Exists(Path.Combine(tw.Ws.Dir, ".gitignore")));
            Assert.Equal("shop", server.Services.Project.Get().Name);
            Assert.False(second.Json["created"]!.GetValue<bool>());
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
            if (server is not null) await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Status_prints_the_current_phase_and_next_action()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }

        var r = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "status");

        Assert.Equal(0, r.Exit);
        Assert.Equal("test-project", r.Json["name"]!.GetValue<string>());
        Assert.Equal("setup", r.Json["phase"]!.GetValue<string>());
        Assert.Equal("awaiting_review", r.Json["status"]!.GetValue<string>());
        Assert.Equal("await", r.Json["next"]!["action"]!.GetValue<string>());
        Assert.Equal("", r.Json["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pause_and_resume_toggle_the_project()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        var paused = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "pause");
        Assert.True(s.Project.Get().Paused);
        var resumed = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "resume");

        Assert.True(paused.Json["paused"]!.GetValue<bool>());
        Assert.False(resumed.Json["paused"]!.GetValue<bool>());
        Assert.False(s.Project.Get().Paused);
        Assert.Contains(s.Events.Since(0), e => e.Type == "resumed");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("next")]
    [InlineData("pause")]
    [InlineData("apply", "x.json")]
    public async Task Project_commands_fail_with_no_project_in_an_empty_folder(params string[] args)
    {
        using var tw = new TestWorkspace();
        File.WriteAllText(Path.Combine(tw.Root, "x.json"), "{\"phase\":\"analysis\",\"baseVersion\":0}");

        var r = await CliRunner.RunAsync(tw.Ws, null, args.Select(a => a == "x.json" ? Path.Combine(tw.Root, a) : a).ToArray());

        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", r.Json["error"]!.GetValue<string>());
        Assert.False(File.Exists(tw.Ws.StateDbPath));
    }
}
