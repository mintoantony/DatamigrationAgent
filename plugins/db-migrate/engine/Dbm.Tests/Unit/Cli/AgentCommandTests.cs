using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

/// <summary>Shortens ServerControl.StartTimeout for the workspace_locked test; ProcessStateCollection keeps that
/// from racing another test class over the same shared static.</summary>
[Collection(ProcessStateCollection.Name)]
public class AgentCommandTests
{
    private static string WritePatch(TestWorkspace tw, string json)
    {
        var path = Path.Combine(tw.Root, $"patch-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Next_prints_the_agent_action_and_touches_the_agent()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.Drafting);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "next");

        Assert.Equal(0, r.Exit);
        Assert.Equal("agent", r.Json["action"]!.GetValue<string>());
        Assert.Equal("schema-analyst", r.Json["agent"]!.GetValue<string>());
        Assert.True(File.Exists(r.Json["packet"]!.GetValue<string>()));
        Assert.NotNull(server.Services.Project.Get().AgentSeenAt);
    }

    [Fact]
    public async Task Apply_accepts_a_valid_patch_and_rejects_the_same_patch_twice()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.DriveToAnalysisDraft(s);
        var patch = WritePatch(tw, """{"phase":"analysis","baseVersion":0,"ops":[{"op":"replace","path":"/summary","value":"v1"}],"responses":[]}""");

        var dry = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", patch, "--dry-run");
        var ok = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", patch);
        var again = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", patch);

        Assert.Equal(0, dry.Exit);
        Assert.Null(dry.Json["version"]);
        Assert.Equal(0, ok.Exit);
        Assert.Equal(1, ok.Json["version"]!.GetValue<int>());
        Assert.Equal(1, again.Exit);
        Assert.Equal("patch_rejected", again.Json["error"]!.GetValue<string>());
        Assert.Contains("awaiting_review", again.Json["errors"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_reports_missing_and_malformed_files()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }

        var missing = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", Path.Combine(tw.Root, "nope.json"));
        var malformed = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", WritePatch(tw, "{oops"));
        var usage = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply");

        Assert.Equal("not_found", missing.Json["error"]!.GetValue<string>());
        Assert.Equal("invalid_patch", malformed.Json["error"]!.GetValue<string>());
        Assert.Equal("usage", usage.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Artifact_prints_the_payload_or_one_slice()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.DriveToAnalysisDraft(s);
        var factory = FakeServices.Factory();

        var whole = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis");
        var slice = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--path", "/items/b");
        var missingVersion = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--version", "9");
        var missingPath = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--path", "/nope");
        var badPhase = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "bogus");

        Assert.Equal("script draft", whole.Json["payload"]!["summary"]!.GetValue<string>());
        Assert.Equal("script", whole.Json["author"]!.GetValue<string>());
        Assert.Equal(2, slice.Json["value"]!.GetValue<int>());
        Assert.Equal("/items/b", slice.Json["path"]!.GetValue<string>());
        Assert.Equal("not_found", missingVersion.Json["error"]!.GetValue<string>());
        Assert.Equal("not_found", missingPath.Json["error"]!.GetValue<string>());
        Assert.Equal("usage", badPhase.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Artifact_path_without_the_leading_slash_is_the_same_pointer()
    {
        // Git Bash rewrites an argument starting with '/' into a Windows path (/tables/x -> C:/Program Files/Git/tables/x),
        // so the playbooks pass pointers without it (Task 6.4 finding F-2).
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.DriveToAnalysisDraft(s);
        var factory = FakeServices.Factory();

        var slice = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--path", "items/b");

        Assert.True(slice.Json["error"] is null, $"a pointer without its leading '/' was refused: {slice.Json.ToJsonString()}");
        Assert.Equal(2, slice.Json["value"]!.GetValue<int>());
        Assert.Equal("/items/b", slice.Json["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Feedback_lists_items_optionally_by_status()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws))
        {
            s.Feedback.Add(PhaseName.Analysis, 0, "narrative", "Shorter please");
            s.Feedback.Add(PhaseName.Analysis, 0, null, "Another");
            s.Feedback.SubmitDrafts(PhaseName.Analysis);
            s.Feedback.Add(PhaseName.Analysis, 0, null, "Still a draft");
        }

        var all = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "feedback", "analysis");
        var open = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "feedback", "analysis", "--status", "open");

        Assert.Equal(3, all.Json.AsArray().Count);
        Assert.Equal(2, open.Json.AsArray().Count);
        Assert.Equal("narrative", open.Json[0]!["anchor"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_jobs_runs_everything_queued()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.SaveConnections(s);

        var r = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "run-jobs");

        Assert.Equal(2, r.Json["ran"]!.GetValue<int>());
        using var check = FakeServices.Open(tw.Ws);
        Assert.Equal(PhaseStatus.Drafting, check.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Run_jobs_reports_skipped_when_a_healthy_server_already_holds_the_lock()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "run-jobs");

        Assert.Equal(0, r.Exit);
        Assert.True(r.Json["ok"]!.GetValue<bool>());
        Assert.Equal(0, r.Json["ran"]!.GetValue<int>());
        Assert.Equal("server_running", r.Json["skipped"]!.GetValue<string>());
    }

    /// <summary>The lock holder here is not a dbm server at all (a plain FileStream) and never will be, so
    /// run-jobs must fail loudly rather than tell the agent its jobs are handled when nothing will run them.</summary>
    [Fact]
    public async Task Run_jobs_reports_workspace_locked_when_the_holder_never_becomes_a_healthy_server()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }
        var saved = ServerControl.StartTimeout;
        ServerControl.StartTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var r = await CliRunner.RunAsync(tw.Ws, null, "run-jobs");

            Assert.Equal(1, r.Exit);
            Assert.Equal("workspace_locked", r.Json["error"]!.GetValue<string>());
        }
        finally
        {
            ServerControl.StartTimeout = saved;
        }
    }

    [Fact]
    public async Task Await_returns_the_next_action_after_a_human_change()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.NeedsAgentResult = false;
        var factory = FakeServices.Factory(modules);
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.AwaitingReview);

        var waiting = CliRunner.RunAsync(tw.Ws, factory, "await", "--timeout", "30");
        await Wait.UntilAsync(async () => (await server.GetJsonAsync("/api/state"))["project"]!["agentOnline"]!.GetValue<bool>());
        Assert.False(waiting.IsCompleted);
        server.Services.Feedback.Add(PhaseName.Analysis, 0, null, "Explain the heap");
        server.Services.Workflow.RequestChanges(PhaseName.Analysis);
        var r = await waiting.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, r.Exit);
        Assert.Equal(("agent", "rework"), (r.Json["action"]!.GetValue<string>(), r.Json["mode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Await_timeout_returns_the_current_await_action()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "await", "--timeout", "1");

        Assert.Equal(new NextAction("await", Reason: "setup", Phase: "setup", Url: server.Info.UiUrl),
            Dbm.Core.Json.Deserialize<NextAction>(r.Out));
    }

    /// <summary>Open item 11: this flaked because the TEST stopped the server too early, not because the command is wrong. It waited for
    /// <c>agentOnline</c>, but <c>dbm await</c> touches agent_seen_at (which alone makes the agent online) BEFORE it finds the server and
    /// opens its long-poll. When the server stopped inside that window, the command found no server and - correctly for a command that
    /// has no server to talk to - started a new one, which is not the scenario under test. So the test now waits for the long-poll
    /// itself: the <c>agent_presence</c> event <c>AgentPresence.Enter</c> publishes when an /api/agent/await opens. The agent is marked
    /// seen first, as <c>dbm next</c> leaves it, and the command starts half a second late, as on a loaded machine: the old wait passes
    /// at once and loses the race every time (measured). A respawn is refused by
    /// a spawn seam that fails loudly, so a lost race names itself instead of starting a real server process from the test.</summary>
    [Fact]
    public async Task Await_reports_server_stopped_when_the_server_shuts_down()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        var server = await WebTestServer.StartAsync(tw.Ws, factory);
        ServerControl.SpawnOverrides[tw.Ws.Root] = (_, _) => throw new InvalidOperationException(
            "dbm await found no server and tried to start one: the test stopped the server before the long-poll was open");
        try
        {
            server.Services.Project.TouchAgent();   // as `dbm next` leaves it: agentOnline is already true
            using var events = new HttpClient { BaseAddress = new Uri(server.Info.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
            using var stream = await events.GetAsync($"/api/events?t={server.Info.Token}", HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());

            // The command starts late, as it does on a loaded machine: a wait that does not observe the long-poll loses the race here.
            var waiting = Task.Run(async () => { await Task.Delay(500); return await CliRunner.RunAsync(tw.Ws, factory, "await"); });
            await LongPollOpenAsync(reader).WaitAsync(TimeSpan.FromSeconds(15));
            await server.DisposeAsync();
            var r = await waiting.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(r.Exit == 0, "dbm await did not report a clean stop: " + r.Out);
            Assert.Equal(("stop", "server_stopped"), (r.Json["action"]!.GetValue<string>(), r.Json["reason"]!.GetValue<string>()));
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
        }
    }

    /// <summary>Returns once the SSE stream carries <c>agent_presence</c> with online true - published only when an /api/agent/await
    /// opens (AgentPresence.Enter), never by a CLI touch of agent_seen_at.</summary>
    private static async Task LongPollOpenAsync(StreamReader reader)
    {
        var presence = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) presence = line == "event: agent_presence";
            else if (presence && line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"online\":true", StringComparison.Ordinal)) return;
        }
        throw new InvalidOperationException("the event stream ended before the long-poll opened");
    }
}
