using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

/// <summary>
/// Everything the transfer service answers without a SQL Server: the view before anything exists, and the two faults that reach
/// <see cref="TransferService.PreflightAsync"/> before a single probe is attempted (ruling 123).
/// </summary>
public sealed class TransferServiceViewTests : IDisposable
{
    private readonly XferServices _svc = new();

    private DbmServices S => _svc.Services;

    public void Dispose() => _svc.Dispose();

    private static SqlPlanPayload MiniPlan() => new()
    {
        Order = ["T01"],
        Tasks = new()
        {
            ["T01"] = new TaskPlan
            {
                Target = "app.A",
                SourceQuery = "SELECT 1 AS [X]",
                CountSql = "SELECT COUNT_BIG(*) FROM (SELECT 1 AS [X]) AS q",
                Columns = [new ColumnBinding("X", "X")],
            },
        },
    };

    private void ApproveSql(string payloadJson)
    {
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping }) S.Phases.SetApproved(p, 1, null);
        S.Artifacts.Add(PhaseName.Sql, 1, payloadJson, "script", "plan");
        S.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        S.Phases.SetApproved(PhaseName.Sql, 1, null);
        S.Phases.SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview);
    }

    private void SaveTarget(string database)
    {
        var meta = FakeServices.Meta("tgt-host", database);
        S.Connections.Save(Side.Src, FakeServices.SrcConnection, FakeServices.Meta("src-host", "Legacy"));
        S.Connections.Save(Side.Tgt, FakeServices.TgtConnection, meta);
    }

    private void SaveTargetCatalog(string database) =>
        S.Catalog.Save(Side.Tgt, new CatalogSnapshot(FakeServices.Meta("tgt-host", database), [], new ObjectCounts(0, 0, 0, 0, 0), Clock.Now()),
            "fp-tgt");

    /// <summary>
    /// 5.4 carry-forward 2. <c>PreflightResult.SqlVersion</c> is 0 both for "v0 is approved" and for "there is no approved plan", and
    /// the view must not inherit that ambiguity: a 0 on the execute screen reads as a real version. Null is the absence, and
    /// <c>PlanNote</c> is what says why it is absent.
    /// </summary>
    [Fact]
    public void Without_an_approved_plan_the_view_says_so_instead_of_showing_version_zero()
    {
        var service = new TransferService(S);

        var view = service.View();

        Assert.Null(view.SqlVersion);
        Assert.Empty(view.Tasks);
        Assert.NotNull(view.PlanNote);
        Assert.Contains("not approved", view.PlanNote);
        Assert.Null(view.Run);
        Assert.False(view.CanStart);
        Assert.NotNull(view.CannotStart);
        Assert.False(view.CanResume);
        Assert.NotNull(view.CannotResume);
        Assert.Equal(0, view.Totals.TasksTotal);
    }

    /// <summary>
    /// Ruling 123, the route 5.4's implementer found: <c>PlanOrder</c> filters by <c>ContainsKey</c>, which is true for a key whose
    /// value is null, so <c>"tasks": {"T01": null}</c> reaches <c>PlanCheck</c>'s <c>plan.Tasks[id].Errors</c> and raises a
    /// <see cref="NullReferenceException"/> outside every catch in <c>Preflight.RunAsync</c>. The operator gets a bare 500 from the
    /// execute screen's first button and nothing at all about which artifact is wrong.
    /// </summary>
    [Fact]
    public async Task A_malformed_plan_artifact_is_a_checklist_line_naming_the_task_not_an_exception()
    {
        ApproveSql("""{"order":["T01"],"tasks":{"T01":null}}""");
        var service = new TransferService(S);

        var result = await service.PreflightAsync(new TransferOptions(), default);

        Assert.False(result.Passed);
        var check = result.Checks.Single(c => c.Name == "preflight");
        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.Contains("malformed", check.Detail);
        Assert.Contains("T01", check.Detail);
        Assert.Contains("sql", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(service.View().PlanNote);                                      // and the screen behind it still renders
        Assert.Contains("T01", service.View().PlanNote);
    }

    /// <summary>
    /// Ruling 123's other named route: <c>ConnectionRepo.GetConnectionString</c> decrypts, and a connection row protected under a
    /// different Windows user throws a <see cref="System.Security.Cryptography.CryptographicException"/> from outside every catch in
    /// <c>Preflight.RunAsync</c> (5.4 fix-round-1 concern 3). A 500 loses the whole checklist, including the lines that passed.
    /// </summary>
    [Fact]
    public async Task A_connection_that_cannot_be_decrypted_is_a_checklist_line_not_a_five_hundred()
    {
        ApproveSql(Json.Serialize(MiniPlan()));
        SaveTarget("ShopV2");
        S.Db.Execute("UPDATE connection SET encrypted = 'protected-by-somebody-else' WHERE side = 'src'");
        var service = new TransferService(S);

        var result = await service.PreflightAsync(new TransferOptions(), default);

        Assert.False(result.Passed);
        var check = result.Checks.Single(c => c.Name == "preflight");
        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.NotEmpty(check.Detail);
        Assert.Same(result, service.LastPreflight);
    }

    /// <summary>
    /// Carry-forward 2. A run's checkpoints and its <c>__dbm_*</c> control tables live in the target it started in, and nothing but
    /// that database can continue it. <c>POST /api/connection/tgt</c> writes the new connection before the workflow refuses the change,
    /// so the saved target really can end up pointing somewhere else - and a resume against it would find no checkpoints and reload
    /// every row from zero into a database nobody reviewed.
    /// </summary>
    [Fact]
    public void Resume_refuses_to_continue_a_run_against_a_different_target()
    {
        ApproveSql(Json.Serialize(MiniPlan()));
        SaveTarget("ShopV2");
        SaveTargetCatalog("ShopV2");
        long runId = S.Transfers.CreateRun(1, new TransferOptions(), [("T01", "app.A")]);
        S.Transfers.SetRunStatus(runId, RunStatus.Paused);
        SaveTarget("ShopV3");                                                         // the operator repointed the target
        var service = new TransferService(S);

        var thrown = Record.Exception(() => service.Resume());

        // The harm first: whatever is reported, nothing may be launched against a database this run never loaded into.
        Assert.False(service.IsActive);
        Assert.Equal(RunStatus.Paused, S.Transfers.GetRun(runId)!.Status);
        var ex = Assert.IsType<TransferException>(thrown);
        Assert.Equal("target_changed", ex.Code);
        Assert.Contains("ShopV3", ex.Message);
        Assert.Contains("ShopV2", ex.Message);
    }

    /// <summary>The guard above refuses a changed target, not a resume: with the target unchanged the same call gets past it and is
    /// stopped only by the next thing that is genuinely missing.</summary>
    [Fact]
    public void Resume_of_a_run_whose_target_is_unchanged_gets_past_the_target_guard()
    {
        ApproveSql(Json.Serialize(MiniPlan()));
        SaveTarget("ShopV2");
        SaveTargetCatalog("ShopV2");
        long runId = S.Transfers.CreateRun(2, new TransferOptions(), [("T01", "app.A")]);   // v2: the artifact this run used is gone
        S.Transfers.SetRunStatus(runId, RunStatus.Paused);
        var service = new TransferService(S);

        Assert.Equal("no_plan", Assert.Throws<TransferException>(() => service.Resume()).Code);
        Assert.False(service.IsActive);
    }

    /// <summary>
    /// 5.4 carry-forward 1 / ruling 117: a report exists only where one was <b>stored</b>. The engine records the run <c>completed</c>
    /// and the service stores the Complete artifact after it, so a process killed between the two leaves exactly this row - completed,
    /// with no report. Keying the flag on the status alone puts a "View report" link on the execute screen that leads nowhere, and the
    /// same link over a <b>later</b> run's report would show the operator the wrong migration's certificate.
    /// </summary>
    [Fact]
    public void A_completed_run_whose_report_was_never_stored_does_not_offer_one()
    {
        ApproveSql(Json.Serialize(MiniPlan()));
        long runId = S.Transfers.CreateRun(1, new TransferOptions(), [("T01", "app.A")]);
        S.Transfers.SetRunStatus(runId, RunStatus.Completed);
        var service = new TransferService(S);

        Assert.False(service.View().Run!.HasReport);

        // A Complete artifact that belongs to some other run is not this run's report either.
        S.Artifacts.Add(PhaseName.Complete, 1, Json.Serialize(new { runId = runId + 7 }), "script", "someone else's run");
        Assert.False(service.View().Run!.HasReport);

        S.Artifacts.Add(PhaseName.Complete, 2, Json.Serialize(new { runId }), "script", "this run");
        Assert.True(service.View().Run!.HasReport);
    }

    /// <summary>Three refusals that must each name the thing that is missing rather than share one "no".</summary>
    [Fact]
    public async Task Pause_resume_and_cancel_without_a_run_each_say_what_is_missing()
    {
        var service = new TransferService(S);

        Assert.Equal("not_running", Assert.Throws<TransferException>(() => service.Pause()).Code);
        Assert.Equal("not_resumable", Assert.Throws<TransferException>(() => service.Resume()).Code);
        Assert.Equal("not_cancellable", (await Assert.ThrowsAsync<TransferException>(() => service.CancelAsync(default))).Code);
        Assert.Equal(0, service.RecoverInterrupted());
    }
}
