using System.Net;
using System.Text;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.Web;

public sealed class TransferEndpointsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private long SeedRunLeftRunning()
    {
        var s = _workspace.OpenServices();
        long runId = s.Transfers.CreateRun(1, new TransferOptions(), [("T01", "app.A"), ("T02", "app.B")]);
        s.Transfers.UpdateTaskStatus(runId, "T01", TransferTaskStatus.Running);
        return runId;   // CreateRun leaves the run "running": exactly what a killed server process leaves behind
    }

    /// <summary>
    /// 5.3's ordering, made load-bearing by ruling 103: <c>RecoverInterrupted</c> runs once at server start, before any endpoint can
    /// reach <c>RunAsync</c>. A run left "running" by a dead process is the same row as a run being executed right now, so until it is
    /// flipped to "paused" the execute screen shows a live transfer that will never move again and offers no way to resume it.
    /// </summary>
    [Fact]
    public async Task A_server_start_recovers_a_run_left_running_as_paused()
    {
        long runId = SeedRunLeftRunning();

        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var view = await server.GetJsonAsync("/api/transfer");

        Assert.Equal(runId, (long?)view["run"]!["id"]);
        Assert.Equal("paused", (string?)view["run"]!["status"]);
        Assert.False((bool?)view["active"]);
        Assert.Equal("paused", (string?)view["tasks"]![0]!["status"]);   // the orphaned task travels with its run
    }

    [Fact]
    public async Task A_refusal_is_a_409_carrying_the_services_own_code()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (startStatus, startBody) = await server.SendAsync(HttpMethod.Post, "/api/transfer/start",
            new { options = new { chunkSize = 100 }, confirmTarget = "ShopV2" });
        Assert.Equal(HttpStatusCode.Conflict, startStatus);
        Assert.Equal("not_ready", (string?)startBody!["error"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)startBody["message"]));

        var (pauseStatus, pauseBody) = await server.SendAsync(HttpMethod.Post, "/api/transfer/pause");
        Assert.Equal(HttpStatusCode.Conflict, pauseStatus);
        Assert.Equal("not_running", (string?)pauseBody!["error"]);

        var (resumeStatus, resumeBody) = await server.SendAsync(HttpMethod.Post, "/api/transfer/resume");
        Assert.Equal(HttpStatusCode.Conflict, resumeStatus);
        Assert.Equal("not_resumable", (string?)resumeBody!["error"]);
    }

    /// <summary>Ruling 123 at the HTTP boundary: pre-flight answers a checklist, whatever went wrong, because an exception loses every
    /// line that already passed - including the one naming the problem.</summary>
    [Fact]
    public async Task Preflight_with_nothing_configured_answers_a_checklist_not_a_five_hundred()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/transfer/preflight", new { });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False((bool?)body!["passed"]);
        Assert.NotEmpty(body["checks"]!.AsArray());
        Assert.All(body["checks"]!.AsArray(), c => Assert.False(string.IsNullOrWhiteSpace((string?)c!["detail"])));
    }

    [Fact]
    public async Task Errors_of_a_workspace_with_no_run_are_an_empty_list()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var body = await server.GetJsonAsync("/api/transfer/errors?task=T02&limit=10");

        Assert.Empty(body.AsArray());
    }

    /// <summary>
    /// F2 / ruling 123 at the start door. The same workspace answers <c>/api/transfer/preflight</c> with a 200 checklist naming the
    /// fault; <c>/api/transfer/start</c> must not answer the same fault with a bare 500. The Execute button is the one control the
    /// whole screen exists for, and a 500 gives the operator no code, no sentence and no hint that the connection is the problem.
    /// </summary>
    [Fact]
    public async Task A_start_whose_connection_cannot_be_decrypted_is_a_conflict_not_a_five_hundred()
    {
        var s = _workspace.OpenServices();
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping }) s.Phases.SetApproved(p, 1, null);
        s.Artifacts.Add(PhaseName.Sql, 1, """{"order":["T01"],"tasks":{"T01":{"target":"app.A","sourceQuery":"SELECT 1 AS [X]","columns":[{"source":"X","target":"X"}],"countSql":"SELECT COUNT_BIG(*)"}}}""",
            "script", "plan");
        s.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        s.Phases.SetApproved(PhaseName.Sql, 1, null);
        s.Phases.SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview);
        s.Connections.Save(Side.Src, FakeServices.SrcConnection, FakeServices.Meta("src-host", "Legacy"));
        s.Connections.Save(Side.Tgt, FakeServices.TgtConnection, FakeServices.Meta("tgt-host", "ShopV2"));
        s.Catalog.Save(Side.Tgt, new CatalogSnapshot(FakeServices.Meta("tgt-host", "ShopV2"), [], new ObjectCounts(0, 0, 0, 0, 0), Clock.Now()), "fp");
        s.Db.Execute("UPDATE connection SET encrypted = 'protected-by-somebody-else' WHERE side = 'src'");

        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var (preflightStatus, preflightBody) = await server.SendAsync(HttpMethod.Post, "/api/transfer/preflight", new { });
        var (startStatus, startBody) = await server.SendAsync(HttpMethod.Post, "/api/transfer/start",
            new { options = new { }, confirmTarget = "ShopV2" });

        Assert.Equal(HttpStatusCode.OK, preflightStatus);
        Assert.False((bool?)preflightBody!["passed"]);
        Assert.Equal(HttpStatusCode.Conflict, startStatus);
        Assert.Equal("no_connection", (string?)startBody!["error"]);
        string message = (string?)startBody["message"] ?? "";
        Assert.Contains("source", message);
        Assert.DoesNotContain("protected-by-somebody-else", message);
    }

    /// <summary>
    /// Ruling 132, route 1. A saved record that will not parse must cost its own line, not the screen. Measured before the fix:
    /// <c>GET /api/transfer</c> answered <b>400 bad_request "Invalid JSON body: …"</b> - a GET that has no body, telling the operator
    /// their body is invalid, with a blank execute screen behind it.
    /// </summary>
    [Fact]
    public async Task A_target_connection_whose_saved_details_will_not_parse_still_answers_a_view()
    {
        var s = _workspace.OpenServices();
        s.Connections.Save(Side.Tgt, FakeServices.TgtConnection, FakeServices.Meta("tgt-host", "ShopV2"));
        s.Db.Execute("UPDATE connection SET server_meta_json = '{not json' WHERE side = 'tgt'");
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Get, "/api/transfer");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null((string?)body!["targetDatabase"]);
        Assert.Contains("target connection", (string?)body["cannotStart"] ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target connection", (string?)body["planNote"] ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ruling 132, route 2: one task's unreadable validation costs that task's validation, not the whole screen - and the
    /// task says so, because a null validation otherwise means nothing was ever checked.</summary>
    [Fact]
    public async Task A_task_whose_saved_validation_will_not_parse_still_answers_a_view()
    {
        var s = _workspace.OpenServices();
        long runId = s.Transfers.CreateRun(1, new TransferOptions(), [("T01", "app.A")]);
        s.Transfers.SetTaskValidation(runId, "T01", "{not json");
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Get, "/api/transfer");

        Assert.Equal(HttpStatusCode.OK, status);
        var task = body!["tasks"]!.AsArray()[0]!;
        Assert.Null(task["validation"]);
        Assert.Contains("could not be read", (string?)task["validationNote"] ?? "");
    }

    /// <summary>Ruling 132, route 3: <c>TransferRepo.MapRun</c> deserialises <c>options_json</c>, so a corrupt one throws on
    /// <c>View()</c>'s first line, before there is a row to degrade. Guarded around the repo call, per the scope ruling.</summary>
    [Fact]
    public async Task A_run_whose_saved_options_will_not_parse_still_answer_a_view()
    {
        var s = _workspace.OpenServices();
        long runId = s.Transfers.CreateRun(1, new TransferOptions(), [("T01", "app.A")]);
        s.Db.Execute("UPDATE transfer_run SET options_json = '{not json' WHERE id = $Id", new { Id = runId });
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Get, "/api/transfer");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["run"]);
        Assert.Contains("could not be read", (string?)body["planNote"] ?? "");
        Assert.False((bool?)body["canStart"]);
        Assert.Contains("could not be read", (string?)body["cannotStart"] ?? "");
    }

    [Fact]
    public async Task A_malformed_body_is_a_bad_request_not_a_default_start()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        using var content = new StringContent("{not json", Encoding.UTF8, "application/json");

        using var response = await server.Client.PostAsync("/api/transfer/start", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"error\":\"bad_request\"", await response.Content.ReadAsStringAsync());
    }
}
