using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Transfer;

/// <summary>
/// Ruling 183 (final review I-1). <c>TransferEngine.Finish</c> records the run <c>completed</c> before the workflow is told, so a crash
/// in between - or a finisher that throws - leaves Transfer <c>running</c> over a completed run: Start, Resume, Cancel and Reopen all
/// refuse, and <c>dbm next</c> answers <c>await/transfer</c> for ever. These tests build exactly the state the reviewer reproduced and
/// prove each door that must reconcile it does.
/// </summary>
public sealed class RunFinisherTests
{
    /// <summary>The reviewer's state: every phase up to Ready approved, Transfer running, Complete pending, the latest run completed
    /// with its report in summary_json, and no Complete artifact.</summary>
    internal static long WedgeAfterCompletedRun(DbmServices s)
    {
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql, PhaseName.Ready })
            s.Phases.SetApproved(p, 1, null);
        s.Phases.SetStatus(PhaseName.Transfer, PhaseStatus.Running);
        s.Phases.SetStatus(PhaseName.Complete, PhaseStatus.Pending);
        long runId = s.Transfers.CreateRun(1, new TransferOptions(), [("T01", "app.A")]);
        s.Transfers.SetTaskCounts(runId, "T01", 10, 0);
        s.Transfers.UpdateTaskProgress(runId, "T01", 10, 0, null);
        s.Transfers.UpdateTaskStatus(runId, "T01", TransferTaskStatus.Done);
        var run = s.Transfers.GetRun(runId)!;
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, s.Transfers.Tasks(runId), _ => [], Clock.Now(), [], _ => 0);
        s.Transfers.SetRunStatus(runId, RunStatus.Completed, Json.Serialize(report));
        return runId;
    }

    private static void AssertFinished(DbmServices s, long runId, string door)
    {
        Assert.True(s.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Approved,
            $"{door}: the Transfer phase still reads {EnumText.ToText(s.Phases.Get(PhaseName.Transfer).Status)} over completed run {runId} - the wedge was not reconciled");
        var complete = s.Phases.Get(PhaseName.Complete);
        Assert.Equal(PhaseStatus.Approved, complete.Status);
        var artifact = s.Artifacts.Get(PhaseName.Complete, complete.ApprovedVersion!.Value)!;
        Assert.Contains($"\"runId\":{runId}", artifact.PayloadJson);
        Assert.True(s.Artifacts.List(PhaseName.Complete).Count == 1,
            $"{door}: {s.Artifacts.List(PhaseName.Complete).Count} final reports stored for one run - a retry stored the report again");
    }

    [Fact]
    public async Task Server_start_finishes_a_completed_run_the_workflow_never_heard_about()
    {
        using var tw = new TestWorkspace();
        long runId;
        using (var s = FakeServices.Open(tw.Ws)) runId = WedgeAfterCompletedRun(s);

        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());

        AssertFinished(server.Services, runId, "server start");
        var next = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "next");
        Assert.Equal("stop", next.Json["action"]!.GetValue<string>());
        Assert.Equal("complete", next.Json["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dbm_next_finishes_it_when_the_server_is_already_up()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());
        long runId = WedgeAfterCompletedRun(server.Services);   // the window opens after this server's own start-up check

        var next = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "next");

        Assert.True(next.Json["reason"]!.GetValue<string>() == "complete",
            "dbm next answered " + next.Out + " - a completed run left the orchestrator waiting on a transfer that has ended");
        AssertFinished(server.Services, runId, "dbm next");
    }

    [Fact]
    public async Task Dbm_status_finishes_it_without_a_server()
    {
        using var tw = new TestWorkspace();
        long runId;
        using (var s = FakeServices.Open(tw.Ws)) runId = WedgeAfterCompletedRun(s);

        var status = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "status");

        Assert.Equal(0, status.Exit);
        Assert.True(status.Json["next"]!["reason"]!.GetValue<string>() == "complete",
            "dbm status answered " + status.Out + " - a completed run the workflow never recorded");
        using var check = FakeServices.Open(tw.Ws);
        AssertFinished(check, runId, "dbm status");
    }

    [Fact]
    public void Finishing_twice_stores_one_report_and_leaves_the_second_caller_nothing_to_do()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        long runId = WedgeAfterCompletedRun(s);

        long? first = RunFinisher.Reconcile(s);
        long? second = RunFinisher.Reconcile(s);
        bool third = RunFinisher.Finish(s, runId);

        AssertFinished(s, runId, "a second finish");
        Assert.Equal(runId, first);
        Assert.Null(second);
        Assert.False(third);
    }

    [Fact]
    public void A_report_stored_before_the_workflow_was_told_is_reused_not_stored_again()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        long runId = WedgeAfterCompletedRun(s);
        s.Artifacts.Add(PhaseName.Complete, 1, s.Transfers.GetRun(runId)!.SummaryJson!, "script", "stored before the crash");

        long? reconciled = RunFinisher.Reconcile(s);

        AssertFinished(s, runId, "a report already stored");
        Assert.Equal(runId, reconciled);
        Assert.Equal(1, s.Phases.Get(PhaseName.Complete).ApprovedVersion);
    }

    [Fact]
    public void A_failed_or_cancelled_latest_run_is_left_to_the_operator()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        long runId = WedgeAfterCompletedRun(s);
        s.Transfers.SetRunStatus(runId, RunStatus.Failed, """{"error":"boom"}""");

        Assert.Null(RunFinisher.Reconcile(s));

        Assert.Equal(PhaseStatus.Running, s.Phases.Get(PhaseName.Transfer).Status);
        Assert.Empty(s.Artifacts.List(PhaseName.Complete));
    }
}
