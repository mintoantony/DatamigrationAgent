using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class WebHostTests
{
    /// <summary>Analysis goes straight to awaiting_review (v0) so review endpoints can be exercised without an agent.</summary>
    private static FakeModules ReviewableModules()
    {
        var modules = new FakeModules();
        modules.Analysis.NeedsAgentResult = false;
        return modules;
    }

    private static async Task<WebTestServer> StartInReviewAsync(TestWorkspace tw, FakeModules modules)
    {
        var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(modules));
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.AwaitingReview);
        return server;
    }

    [Fact]
    public async Task Server_json_is_written_while_running_and_removed_on_shutdown()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var info = ServerControl.ReadInfo(tw.Ws)!;
        Assert.Equal(server.Info, info);
        Assert.True(await ServerControl.IsAliveAsync(info));

        var (status, _) = await server.SendAsync(HttpMethod.Post, "/api/shutdown");
        Assert.Equal(HttpStatusCode.OK, status);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(tw.Ws.ServerJsonPath));
    }

    [Fact]
    public async Task Second_start_for_the_same_workspace_returns_early_while_the_first_holds_the_lock()
    {
        using var tw = new TestWorkspace();
        await using var first = await WebTestServer.StartAsync(tw.Ws);

        using var cts2 = new CancellationTokenSource();
        var factoryCalled = false;
        var onStartedCalled = false;
        var second = WebHost.RunAsync(tw.Ws, 0, cts2.Token,
            w =>
            {
                factoryCalled = true;
                return FakeServices.Factory()(w);
            },
            _ => onStartedCalled = true);

        try
        {
            var result = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebHostStartResult.AlreadyRunning, result);
        }
        finally
        {
            // If exclusivity regressed and `second` actually started serving, this must still shut it down —
            // otherwise a leaked server keeps the workspace directory from being deleted at the end of the test.
            cts2.Cancel();
            try
            {
                await second;
            }
            catch (Exception)
            {
                // already observed above, or cancelled before it ever started
            }
        }

        Assert.False(factoryCalled);
        Assert.False(onStartedCalled);
        Assert.True(await ServerControl.IsAliveAsync(first.Info));
    }

    [Fact]
    public async Task Lock_is_released_when_the_server_exits_so_a_later_start_succeeds()
    {
        using var tw = new TestWorkspace();
        var first = await WebTestServer.StartAsync(tw.Ws);
        await first.DisposeAsync();

        await using var second = await WebTestServer.StartAsync(tw.Ws);

        Assert.True(await ServerControl.IsAliveAsync(second.Info));
    }

    [Fact]
    public async Task A_server_that_fails_to_bind_leaves_job_rows_untouched()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        using var seed = FakeServices.Open(tw.Ws);
        var jobId = seed.Jobs.Enqueue("discover", null);
        seed.Jobs.MarkRunning(jobId);
        seed.Dispose();

        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var occupiedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            WebHost.RunAsync(tw.Ws, occupiedPort, cts.Token, FakeServices.Factory()));

        using var check = FakeServices.Open(tw.Ws);
        Assert.Equal(JobStatus.Running, check.Jobs.Get(jobId)!.Status);
    }

    [Fact]
    public async Task Api_requires_the_token_and_a_loopback_host()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        using var anonymous = server.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/state?t={server.Info.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/pause?t={server.Info.Token}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/api/health")).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        foreign.Headers.Host = $"evil.example:{server.Info.Port}";
        foreign.Headers.Add(TokenGuard.Header, server.Info.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task State_view_has_the_documented_shape()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var state = await server.GetJsonAsync("/api/state");

        Assert.Equal("test-project", state["project"]!["name"]!.GetValue<string>());
        Assert.False(state["project"]!["paused"]!.GetValue<bool>());
        Assert.False(state["project"]!["agentOnline"]!.GetValue<bool>());
        Assert.Equal(8, state["phases"]!.AsArray().Count);
        Assert.Equal("awaiting_review", state["phases"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("setup", state["next"]!["reason"]!.GetValue<string>());
        Assert.False(state["connections"]!["src"]!["saved"]!.GetValue<bool>());
        Assert.False(state["drift"]!["tgt"]!.GetValue<bool>());
        Assert.True(state.AsObject().ContainsKey("transfer"));
        // Ruling 185: before any run there is no run to name and nothing locks the plan, so the object is empty (nulls are omitted).
        Assert.Equal("{}", state["transfer"]!.ToJsonString());
    }

    [Fact]
    public async Task Saved_connections_are_described_without_secrets()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        server.Services.Connections.Save(Side.Src, "Server=src-host;Database=Legacy;User ID=etl;Password=TopSecret99", FakeServices.Meta("src-host", "Legacy"));

        var text = await server.Client.GetStringAsync("/api/state");

        Assert.DoesNotContain("TopSecret99", text);
        var src = JsonNode.Parse(text)!["connections"]!["src"]!;
        Assert.Equal("server=src-host; database=Legacy; auth=sql; user=etl", src["describe"]!.GetValue<string>());
        Assert.Equal("Legacy", src["meta"]!["database"]!.GetValue<string>());
    }

    [Fact]
    public async Task Artifacts_are_served_with_versions_and_payload()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var artifact = await server.GetJsonAsync("/api/artifact/analysis");
        var v0 = await server.GetJsonAsync("/api/artifact/analysis/0");
        var (missing, missingBody) = await server.SendAsync(HttpMethod.Get, "/api/artifact/analysis/5");
        var (unknown, unknownBody) = await server.SendAsync(HttpMethod.Get, "/api/artifact/bogus");

        Assert.Equal("analysis", artifact["phase"]!.GetValue<string>());
        Assert.Equal(0, artifact["versions"]![0]!["version"]!.GetValue<int>());
        Assert.Equal("analyze draft", artifact["current"]!["payload"]!["summary"]!.GetValue<string>());
        Assert.Equal("script", v0["author"]!.GetValue<string>());
        Assert.NotNull(v0["createdAt"]);
        Assert.NotNull(artifact["current"]!["createdAt"]);
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), (missing, missingBody!["error"]!.GetValue<string>()));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_phase"), (unknown, unknownBody!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Feedback_can_be_added_listed_deleted_and_submitted()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var (created, draft) = await server.SendAsync(HttpMethod.Post, "/api/feedback/analysis", new { anchor = "table:src:dbo.CUST", text = "Explain CUST" });
        Assert.Equal(HttpStatusCode.OK, created);
        Assert.Equal("draft", draft!["status"]!.GetValue<string>());
        Assert.Equal(0, draft["version"]!.GetValue<int>());
        Assert.Single((await server.GetJsonAsync("/api/feedback/analysis")).AsArray());

        var id = draft["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Delete, $"/api/feedback/{id}")).Status);
        Assert.Empty((await server.GetJsonAsync("/api/feedback/analysis")).AsArray());

        var (_, second) = await server.SendAsync(HttpMethod.Post, "/api/feedback/analysis", new { text = "General remark" });
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/request-changes")).Status);
        Assert.Equal(PhaseStatus.Reworking, server.Services.Phases.Get(PhaseName.Analysis).Status);
        var (notDraft, body) = await server.SendAsync(HttpMethod.Delete, $"/api/feedback/{second!["id"]!.GetValue<long>()}");
        Assert.Equal((HttpStatusCode.Conflict, "not_draft"), (notDraft, body!["error"]!.GetValue<string>()));
        var (empty, _) = await server.SendAsync(HttpMethod.Post, "/api/feedback/analysis", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, empty);
    }

    [Fact]
    public async Task Request_changes_without_feedback_is_a_conflict()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/request-changes");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approve_blocked_returns_409_with_details()
    {
        using var tw = new TestWorkspace();
        var modules = ReviewableModules();
        modules.Analysis.BlockersFn = _ => ["2 critical findings have no decision"];
        await using var server = await StartInReviewAsync(tw, modules);

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/approve");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("blocked", body!["error"]!.GetValue<string>());
        Assert.Equal("2 critical findings have no decision", body["details"]![0]!.GetValue<string>());
        Assert.Equal(PhaseStatus.AwaitingReview, server.Services.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Approval_guards_run_before_approval()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());
        Func<WebState, PhaseName, CancellationToken, Task<string?>> guard = (st, _, _) =>
            Task.FromResult(st.Services.Ws.Root == tw.Ws.Root ? "Source schema changed since discovery." : null);
        ApprovalGuards.All.Add(guard);
        try
        {
            var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/approve");

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(("guard", "Source schema changed since discovery."), (body!["error"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        }
        finally
        {
            ApprovalGuards.All.Remove(guard);
        }

        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/approve")).Status);
        Assert.Equal(PhaseStatus.Approved, server.Services.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Edit_endpoint_creates_a_human_version()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var (status, result) = await server.SendAsync(HttpMethod.Post, "/api/edit/analysis", new
        {
            phase = "analysis",
            baseVersion = 0,
            ops = new[] { new { op = "replace", path = "/summary", value = "edited by me" } },
        });
        var (wrongPhase, _) = await server.SendAsync(HttpMethod.Post, "/api/edit/analysis", new { phase = "mapping", baseVersion = 0 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal(1, result["version"]!.GetValue<int>());
        var artifact = await server.GetJsonAsync("/api/artifact/analysis");
        Assert.Equal("human", artifact["current"]!["author"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, wrongPhase);
    }

    [Fact]
    public async Task Pause_resume_and_retry_endpoints()
    {
        using var tw = new TestWorkspace();
        var failing = new FakeJobHandler("analyze", _ => throw new InvalidOperationException("analyzer crashed"));
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(handlers: [new FakeJobHandler("discover"), failing]));
        var s = server.Services;

        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/pause")).Status);
        Assert.True(s.Project.Get().Paused);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/resume")).Status);
        Assert.False(s.Project.Get().Paused);

        FakeServices.SaveConnections(s);
        await Wait.UntilAsync(() => s.Workflow.Peek().Reason == "job_failed");
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/retry")).Status);
        await Wait.UntilAsync(() => failing.Runs >= 2);
        var (conflict, _) = await server.SendAsync(HttpMethod.Post, "/api/phase/mapping/retry");
        Assert.Equal(HttpStatusCode.Conflict, conflict);
    }

    [Fact]
    public async Task Connections_are_locked_after_the_transfer_started_and_validated()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var (badSide, _) = await server.SendAsync(HttpMethod.Post, "/api/connections/xyz", new { connectionString = "Server=a" });
        var (noBody, _) = await server.SendAsync(HttpMethod.Post, "/api/connections/src/test", new { connectionString = "" });
        server.Services.Phases.SetStatus(PhaseName.Transfer, PhaseStatus.Running);
        var (locked, body) = await server.SendAsync(HttpMethod.Post, "/api/connections/src", new { connectionString = "Server=a" });

        Assert.Equal(HttpStatusCode.NotFound, badSide);
        Assert.Equal(HttpStatusCode.BadRequest, noBody);
        Assert.Equal((HttpStatusCode.Conflict, "locked"), (locked, body!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Sse_streams_persisted_events_and_replays_after_last_event_id()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        using var http = new HttpClient { BaseAddress = new Uri(server.Info.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

        using var live = await http.GetAsync($"/api/events?t={server.Info.Token}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", live.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await live.Content.ReadAsStreamAsync());
        Assert.Equal("retry: 2000", await reader.ReadLineAsync());

        server.Services.Workflow.SetPaused(true);
        var lines = await ReadUntilAsync(reader, "event: paused");
        Assert.StartsWith("id: ", lines[^3]);
        Assert.Equal("data: {}", lines[^1]);

        using var replay = new HttpRequestMessage(HttpMethod.Get, $"/api/events?t={server.Info.Token}");
        replay.Headers.Add("Last-Event-ID", "0");
        using var replayed = await http.SendAsync(replay, HttpCompletionOption.ResponseHeadersRead);
        using var replayReader = new StreamReader(await replayed.Content.ReadAsStreamAsync());
        Assert.Contains("event: paused", await ReadUntilAsync(replayReader, "event: paused"));
    }

    [Fact]
    public async Task Await_returns_once_the_next_action_changes_and_marks_the_agent_online()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());
        var s = server.Services;

        var pending = server.Client.GetStringAsync("/api/agent/await");
        await Wait.UntilAsync(async () => (await server.GetJsonAsync("/api/state"))["project"]!["agentOnline"]!.GetValue<bool>());
        Assert.False(pending.IsCompleted);

        s.Feedback.Add(PhaseName.Analysis, 0, null, "More detail please");
        s.Workflow.RequestChanges(PhaseName.Analysis);
        var action = JsonNode.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(10)))!;

        Assert.Equal("agent", action["action"]!.GetValue<string>());
        Assert.Equal("rework", action["mode"]!.GetValue<string>());
        Assert.True(File.Exists(action["packet"]!.GetValue<string>()));
        await Wait.UntilAsync(async () => !(await server.GetJsonAsync("/api/state"))["project"]!["agentOnline"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Await_with_a_timeout_returns_the_current_await_action()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var action = await server.GetJsonAsync("/api/agent/await?timeout=1");

        Assert.Equal(("await", "setup"), (action["action"]!.GetValue<string>(), action["reason"]!.GetValue<string>()));
    }

    private static async Task<List<string>> ReadUntilAsync(StreamReader reader, string wanted)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
            lines.Add(line);
            if (line != wanted) continue;
            lines.Add(await reader.ReadLineAsync(cts.Token) ?? "");
            return lines;
        }
    }
}
