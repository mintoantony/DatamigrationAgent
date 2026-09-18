using System.Net;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

/// <summary>
/// Ruling 185 (open item 36). The review loop no longer ends at the first Execute: Analysis, Mapping and SQL can be reopened - and
/// discovery re-run and the connections changed - after a run that completed or was cancelled, and after a failed one once it is
/// cancelled. Never under a running or paused run. Every phase x every run state is pinned here.
/// </summary>
public sealed class ReopenAfterRunTests
{
    public enum RunState { NoRun, Running, Paused, Completed, Failed, Cancelled }

    /// <summary>SQL approved (Ready awaiting execute), then the transfer put in <paramref name="state"/> the way the service leaves it.</summary>
    private static long? Drive(DbmServices s, RunState state)
    {
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.ApproveCurrent(PhaseName.Sql);
        if (state == RunState.NoRun) return null;
        long runId = s.Transfers.CreateRun(s.Phases.Get(PhaseName.Sql).ApprovedVersion!.Value, new TransferOptions(), [("T01", "app.A")]);
        s.Workflow.OnTransferStarted();
        switch (state)
        {
            case RunState.Paused:
                s.Transfers.SetRunStatus(runId, RunStatus.Paused);
                break;
            case RunState.Failed:
                s.Transfers.SetRunStatus(runId, RunStatus.Failed, """{"error":"T01: boom"}""");
                s.Workflow.OnTransferFinished("failed", null);
                break;
            case RunState.Cancelled:
                s.Transfers.SetRunStatus(runId, RunStatus.Cancelled);
                s.Workflow.OnTransferFinished("cancelled", null);
                break;
            case RunState.Completed:
                s.Transfers.SetRunStatus(runId, RunStatus.Completed, $$"""{"runId":{{runId}}}""");
                s.Artifacts.Add(PhaseName.Complete, 1, $$"""{"runId":{{runId}}}""", "script", "report of the first run");
                s.Workflow.OnTransferFinished("completed", 1);
                break;
        }
        return runId;
    }

    private static bool Allowed(RunState state) => state is RunState.NoRun or RunState.Completed or RunState.Cancelled;

    public static IEnumerable<object[]> Matrix() =>
        from phase in new[] { PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql }
        from state in Enum.GetValues<RunState>()
        select new object[] { phase, state };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Reopen_follows_the_run_state(PhaseName phase, RunState state)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        Drive(s, state);

        var thrown = Record.Exception(() => s.Workflow.Reopen(phase));

        if (Allowed(state))
        {
            Assert.True(thrown is null, $"reopening {phase.Text()} after {state} was refused: {thrown?.Message} - the review loop ends at the first Execute");
            Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(phase).Status);
            foreach (var later in WorkflowEngine.Order.Where(p => p > phase && p <= PhaseName.Ready))
                Assert.Equal(PhaseStatus.Stale, s.Phases.Get(later).Status);
            Assert.True(s.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Pending,
                $"after reopening {phase.Text()} the Transfer phase reads {EnumText.ToText(s.Phases.Get(PhaseName.Transfer).Status)}: the re-approved plan could not start a new run");
            Assert.Equal(PhaseStatus.Pending, s.Phases.Get(PhaseName.Complete).Status);
            Assert.Equal("review", s.Workflow.Peek().Reason);
        }
        else
        {
            Assert.True(thrown is WorkflowException,
                $"reopening {phase.Text()} under a {state} run was allowed ({thrown?.Message ?? "no refusal"}) - the plan changed under a run still using it");
            var refusal = (WorkflowException)thrown!;
            Assert.Equal(PhaseStatus.Approved, s.Phases.Get(phase).Status);
            Assert.NotEqual(PhaseStatus.Pending, s.Phases.Get(PhaseName.Transfer).Status);
            Assert.Contains(state switch
            {
                RunState.Running => "Pause it and cancel it first",
                RunState.Paused => "is paused",
                _ => "failed",
            }, refusal.Message);
        }
    }

    [Theory]
    [MemberData(nameof(RunStates))]
    public void Rediscovery_and_new_connections_follow_the_same_rule(RunState state)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        Drive(s, state);

        var rediscover = Record.Exception(() => s.Workflow.Rediscover());
        using var tw2 = new TestWorkspace();
        using var s2 = FakeServices.Open(tw2.Ws);
        Drive(s2, state);
        var reconnect = Record.Exception(() => FakeServices.SaveConnections(s2));

        foreach (var (what, thrown, services) in new[] { ("re-discovery", rediscover, s), ("reconnect", reconnect, s2) })
        {
            if (Allowed(state))
            {
                Assert.True(thrown is null, $"{what} after {state} was refused: {thrown?.Message}");
                Assert.Equal(PhaseStatus.Running, services.Phases.Get(PhaseName.Discovery).Status);
                Assert.Equal(PhaseStatus.Pending, services.Phases.Get(PhaseName.Transfer).Status);
                Assert.Equal(PhaseStatus.Pending, services.Phases.Get(PhaseName.Complete).Status);
            }
            else
            {
                Assert.IsType<WorkflowException>(thrown);
            }
        }
    }

    public static IEnumerable<object[]> RunStates() => Enum.GetValues<RunState>().Select(st => new object[] { st });

    [Fact]
    public void Earlier_reports_and_runs_stay_in_the_workspace_after_a_reopen()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        long runId = Drive(s, RunState.Completed)!.Value;

        s.Workflow.Reopen(PhaseName.Mapping);

        Assert.Equal(runId, s.Transfers.Latest()!.Id);
        Assert.Single(s.Artifacts.List(PhaseName.Complete));
        Assert.Equal(1, s.Phases.Get(PhaseName.Complete).CurrentVersion);   // the report screen still has the last report to show
    }

    [Fact]
    public void Dbm_next_after_a_cancel_or_a_failure_names_every_way_out()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        Drive(s, RunState.Cancelled);
        var cancelled = s.Workflow.Peek();
        using var tw2 = new TestWorkspace();
        using var s2 = FakeServices.Open(tw2.Ws);
        Drive(s2, RunState.Failed);
        var failed = s2.Workflow.Peek();

        Assert.Equal("transfer_cancelled", cancelled.Reason);
        Assert.True(cancelled.Summary!.Contains("reopen Analysis, Mapping or SQL", StringComparison.Ordinal)
                    && cancelled.Summary.Contains("Truncate target first", StringComparison.Ordinal),
            "stop/transfer_cancelled says only: " + cancelled.Summary);
        Assert.Equal("transfer_failed", failed.Reason);
        Assert.True(failed.Summary!.Contains("T01: boom", StringComparison.Ordinal) && failed.Summary.Contains("Resume", StringComparison.Ordinal)
                    && failed.Summary.Contains("reopen Analysis, Mapping or SQL", StringComparison.Ordinal),
            "stop/transfer_failed says only: " + failed.Summary);
    }

    [Fact]
    public void After_a_completed_run_the_execute_screen_says_how_to_run_again()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        Drive(s, RunState.Completed);

        var view = new TransferService(s).View();

        Assert.False(view.CanStart);
        Assert.True(view.CannotStart?.Contains("reopen Analysis, Mapping or SQL", StringComparison.Ordinal) == true,
            "after a completed run the Execute screen says only: " + view.CannotStart);
    }

    /// <summary>The server route: a failed run is cancelled by the reopen (through Cancel's door), and a refused reopen cancels nothing.</summary>
    [Fact]
    public async Task Reopen_from_the_ui_cancels_a_failed_run_first_and_a_refused_one_leaves_it_alone()
    {
        using var tw = new TestWorkspace();
        long runId;
        using (var s = FakeServices.Open(tw.Ws))
        {
            runId = Drive(s, RunState.Failed)!.Value;
            // The fake target host does not exist; the cancel's best-effort target work fails fast instead of after 15 s.
            s.Connections.Save(Side.Tgt, FakeServices.TgtConnection + ";Connect Timeout=1", FakeServices.Meta("tgt-host", "ShopV2"));
        }
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());

        var (refused, refusal) = await server.SendAsync(HttpMethod.Post, "/api/phase/discovery/reopen");
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Equal(RunStatus.Failed, server.Services.Transfers.GetRun(runId)!.Status);

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/mapping/reopen");

        Assert.True(status == HttpStatusCode.OK, $"reopen after a failed run answered {(int)status}: {body?.ToJsonString()}");
        var run = server.Services.Transfers.GetRun(runId)!;
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Contains("Cancelled because mapping was reopened.", run.SummaryJson);
        Assert.Contains("T01: boom", run.SummaryJson);                                  // the failure's own reason is kept
        Assert.Equal(PhaseStatus.AwaitingReview, server.Services.Phases.Get(PhaseName.Mapping).Status);
        Assert.Equal(PhaseStatus.Pending, server.Services.Phases.Get(PhaseName.Transfer).Status);
        Assert.NotNull(refusal);
    }

    [Theory]
    [InlineData(RunState.NoRun, false)]
    [InlineData(RunState.Running, true)]
    [InlineData(RunState.Paused, true)]
    [InlineData(RunState.Completed, false)]
    [InlineData(RunState.Failed, false)]
    [InlineData(RunState.Cancelled, false)]
    public async Task The_state_view_tells_the_review_screens_whether_they_may_offer_reopen(RunState state, bool locked)
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) Drive(s, state);
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());
        if (state == RunState.Running)
            server.Services.Db.Execute("UPDATE transfer_run SET status = 'running'");   // RecoverInterrupted paused it on start

        var transfer = (await server.GetJsonAsync("/api/state"))["transfer"]!;

        Assert.Equal(locked, transfer["changesLocked"] is not null);
        if (state != RunState.NoRun) Assert.Equal(EnumText.ToText(Enum.Parse<RunStatus>(state.ToString())), transfer["runStatus"]!.GetValue<string>());
    }
}
