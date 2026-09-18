using System.Net;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

/// <summary>
/// Ruling 186 (open item 27, HIGH). A new run loads every table from the start; into a table that already holds rows and has no key
/// that doubles it, and the report then said "validated". Before 186 one click on the default options did exactly that after a cancel.
/// A new run into a non-empty target now needs Truncate target first, or a confirmation naming every non-empty table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NewRunGuardTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    private static readonly TransferOptions Skip = new() { ChunkSize = 1000, Parallelism = 1, ErrorMode = "skip" };

    internal static async Task PrepareAsync(DbmServices s, string srcCs, TempDatabase tgt)
    {
        foreach (var (side, cs) in new[] { (Side.Src, srcCs), (Side.Tgt, tgt.ConnectionString) })
        {
            var meta = await SqlConnect.ProbeAsync(cs, default);
            s.Connections.Save(side, cs, meta);
            await using var conn = await SqlConnect.OpenAsync(cs, default);
            var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, default);
            s.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
        }
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping }) s.Phases.SetApproved(p, 1, null);
        s.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(TransferEngineTests.Plan()), "script", "test plan");
        s.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        s.Phases.SetApproved(PhaseName.Sql, 1, null);
        s.Phases.SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview);
    }

    private static async Task<TempDatabase> TargetWithRowsAsync()
    {
        var tgt = await TempDatabase.CreateAsync("dbm_guard_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("INSERT app.Parent (Id, Name) VALUES (1, N'Parent 1'); INSERT app.Log (Msg) VALUES (N'event 1'), (N'event 2');");
        return tgt;
    }

    [Fact]
    public async Task Pre_flight_names_the_non_empty_tables_and_which_of_them_have_no_key()
    {
        await using var tgt = await TargetWithRowsAsync();
        using var svc = new XferServices();
        await PrepareAsync(svc.Services, fx.Src.ConnectionString, tgt);

        var pre = await new TransferService(svc.Services).PreflightAsync(Skip, default);

        var rows = pre.Checks.Single(c => c.Name == "target_rows");
        Assert.True(rows.Detail.Contains("No primary key or unique index on app.Log: rows will be loaded again — duplicates", StringComparison.Ordinal),
            "target_rows speaks only of keys: " + rows.Detail);
        Assert.DoesNotContain("app.Parent: rows will be loaded again", rows.Detail);
        Assert.Equal([new NonEmptyTarget("app.Parent", 1, false), new NonEmptyTarget("app.Log", 2, true)], pre.NonEmptyTargets);
    }

    [Fact]
    public async Task A_new_run_into_a_non_empty_target_needs_truncate_or_a_confirmation_naming_every_table()
    {
        await using var tgt = await TargetWithRowsAsync();
        using var svc = new XferServices();
        await PrepareAsync(svc.Services, fx.Src.ConnectionString, tgt);
        var service = new TransferService(svc.Services);

        var refused = await Record.ExceptionAsync(() => service.StartAsync(Skip, tgt.Name, default));
        Assert.True(refused is TransferException { Code: "target_not_empty" },
            "a new run into app.Log (2 rows, no key) with the default options was not refused: " + (refused?.Message ?? "it started"));
        var ex = (TransferException)refused!;
        Assert.Contains("app.Log has no primary key or unique index, so rows will be loaded again — duplicates", ex.Message);
        Assert.Contains("app.Log: 2 rows (no key: rows will be loaded again — duplicates)", ex.Details);
        Assert.Null(svc.Services.Transfers.Latest());                                    // refused before a run row exists
        Assert.Equal(2, await tgt.CountAsync("app.Log"));

        var partly = await Record.ExceptionAsync(() => service.StartAsync(Skip, tgt.Name, default, ["app.Parent"]));
        Assert.True(partly is TransferException { Code: "target_not_empty" }, "a confirmation that leaves app.Log out was accepted");

        long runId = await service.StartAsync(Skip, tgt.Name, default, ["app.Parent", "app.Log"]);
        await service.Current;
        Assert.Equal(RunStatus.Completed, svc.Services.Transfers.GetRun(runId)!.Status);
        Assert.Equal(702, await tgt.CountAsync("app.Log"));                               // what the operator confirmed, by name
        Assert.Contains(FinalReportBuilder.NotEmptyBefore, svc.Services.Artifacts.Latest(PhaseName.Complete)!.Summary);
    }

    [Fact]
    public async Task Truncate_target_first_needs_no_confirmation()
    {
        await using var tgt = await TargetWithRowsAsync();
        using var svc = new XferServices();
        await PrepareAsync(svc.Services, fx.Src.ConnectionString, tgt);
        var service = new TransferService(svc.Services);

        long runId = await service.StartAsync(Skip with { TruncateTarget = true }, tgt.Name, default);
        await service.Current;

        Assert.Equal(RunStatus.Completed, svc.Services.Transfers.GetRun(runId)!.Status);
        Assert.Equal(700, await tgt.CountAsync("app.Log"));
        Assert.Contains("row counts validated", svc.Services.Artifacts.Latest(PhaseName.Complete)!.Summary);
    }

    /// <summary>
    /// Ruling 190 (re-review N-1). Start reused a passing pre-flight under 15 minutes old for the same options and SQL version, and
    /// the guard read its non-empty list - taken while the target was still empty. After a completed run, Reopen SQL and approve the
    /// same version, a plain Start loaded app.Log (keyless) a second time. The guard now counts the plan's tables live at Start.
    /// </summary>
    [Fact]
    public async Task A_new_run_after_complete_and_reopen_is_refused_even_inside_the_pre_flight_window()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_guard_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, ws =>
        {
            var opened = DbmServices.Open(ws);
            if (!opened.Project.Exists()) opened.Project.Init("guard");
            return opened;
        });
        var s = server.Services;
        await PrepareAsync(s, fx.Src.ConnectionString, tgt);

        var (pre, _) = await server.SendAsync(HttpMethod.Post, "/api/transfer/preflight", new { options = Skip });
        Assert.Equal(HttpStatusCode.OK, pre);
        var (first, firstBody) = await server.SendAsync(HttpMethod.Post, "/api/transfer/start", new { options = Skip, confirmTarget = tgt.Name });
        Assert.True(first == HttpStatusCode.OK, firstBody?.ToJsonString());
        await Wait.UntilAsync(() => s.Phases.Get(PhaseName.Complete).Status == PhaseStatus.Approved, 60_000);
        Assert.Equal(700, await tgt.CountAsync("app.Log"));
        s.Workflow.Reopen(PhaseName.Sql);
        s.ApproveCurrent(PhaseName.Sql);                                              // the same SQL version, well inside 15 minutes

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/transfer/start", new { options = Skip, confirmTarget = tgt.Name });

        Assert.True(status == HttpStatusCode.Conflict && (string?)body?["error"] == "target_not_empty",
            $"a plain Start after a completed run and a reopen answered {(int)status}: {body?.ToJsonString()} - app.Log (no key) is loaded a second time");
        Assert.Equal(1, s.Transfers.Latest()!.Id);                                     // refused before a run row exists
        Assert.Equal(700, await tgt.CountAsync("app.Log"));
        // The cached checklist was not reused either: the start re-ran pre-flight, and the screen now shows the tables as non-empty.
        var shown = (await server.GetJsonAsync("/api/transfer"))["preflight"]!["nonEmptyTargets"]!.AsArray();
        Assert.True(shown.Any(t => (string?)t!["target"] == "app.Log"),
            "the start reused the pre-flight taken before run 1, which still says the target is empty: " + shown.ToJsonString());
    }

    /// <summary>Ruling 190, the live half: rows that reach a target table after a passing pre-flight - with nothing in the workflow
    /// changing - are still seen by the guard at Start.</summary>
    [Fact]
    public async Task Rows_written_after_the_pre_flight_are_counted_at_start()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_guard_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        await PrepareAsync(svc.Services, fx.Src.ConnectionString, tgt);
        var service = new TransferService(svc.Services);
        Assert.Empty((await service.PreflightAsync(Skip, default)).NonEmptyTargets);
        await tgt.ExecAsync("INSERT app.Log (Msg) VALUES (N'written by someone else');");

        var refused = await Record.ExceptionAsync(() => service.StartAsync(Skip, tgt.Name, default));

        Assert.True(refused is TransferException { Code: "target_not_empty" },
            "a row written after the pre-flight was not seen at Start: " + (refused?.Message ?? "the run started"));
        Assert.Null(svc.Services.Transfers.Latest());
    }

    [Fact]
    public async Task The_api_refuses_with_a_409_code_the_screen_shows_and_accepts_the_named_confirmation()
    {
        await using var tgt = await TargetWithRowsAsync();
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, ws =>
        {
            var opened = DbmServices.Open(ws);
            if (!opened.Project.Exists()) opened.Project.Init("guard");
            return opened;
        });
        await PrepareAsync(server.Services, fx.Src.ConnectionString, tgt);

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/transfer/start",
            new { options = Skip, confirmTarget = tgt.Name });
        Assert.True(status == HttpStatusCode.Conflict, $"start answered {(int)status}: {body?.ToJsonString()}");
        Assert.Equal("target_not_empty", (string?)body!["error"]);
        Assert.Contains(body["details"]!.AsArray(), d => ((string?)d)!.StartsWith("app.Log: 2 rows (no key", StringComparison.Ordinal));

        var (ok, started) = await server.SendAsync(HttpMethod.Post, "/api/transfer/start",
            new { options = Skip, confirmTarget = tgt.Name, confirmNonEmpty = new[] { "app.Parent", "app.Log" } });
        Assert.True(ok == HttpStatusCode.OK, "the named confirmation was refused: " + started?.ToJsonString());
        await Wait.UntilAsync(() => server.Services.Transfers.Latest()?.Status == RunStatus.Completed, 60_000);
    }
}
