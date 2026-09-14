using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

public class WorkflowTransitionTests
{
    private static PhaseStatus StatusOf(DbmServices s, PhaseName p) => s.Phases.Get(p).Status;

    private static List<string> EventTypes(DbmServices s) => s.Events.Since(0, 10_000).Select(e => e.Type).ToList();

    [Fact]
    public void Saving_one_side_changes_nothing()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        s.Connections.Save(Side.Src, FakeServices.SrcConnection, FakeServices.Meta("src-host", "Legacy"));
        s.Workflow.OnConnectionsSaved();

        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Setup));
        Assert.Empty(s.Jobs.Active());
    }

    [Fact]
    public void Saving_both_sides_approves_setup_and_queues_discover()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        FakeServices.SaveConnections(s);

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Setup));
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        var job = Assert.Single(s.Jobs.Active());
        Assert.Equal(("discover", (PhaseName?)PhaseName.Discovery), (job.Kind, job.Phase));
        Assert.Contains("state_changed", EventTypes(s));
    }

    [Fact]
    public void Resaving_connections_marks_started_later_phases_stale()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        FakeServices.SaveConnections(s);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Analysis));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Mapping));
        Assert.Equal("discover", Assert.Single(s.Jobs.Active()).Kind);
    }

    [Fact]
    public void Discover_done_approves_discovery_with_the_fingerprint_and_queues_analyze()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        FakeServices.SetCatalogFingerprints(s, "aa", "bb");

        FakeServices.CompleteNextJob(s);

        var discovery = s.Phases.Get(PhaseName.Discovery);
        Assert.Equal(PhaseStatus.Approved, discovery.Status);
        Assert.Equal("aa:bb", discovery.ApprovedFingerprint);
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Analysis));
        Assert.Equal("analyze", Assert.Single(s.Jobs.Active()).Kind);
    }

    [Theory]
    [InlineData(true, PhaseStatus.Drafting)]
    [InlineData(false, PhaseStatus.AwaitingReview)]
    public void Module_job_done_stores_the_script_draft_as_v0(bool needsAgent, PhaseStatus expected)
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.NeedsAgentResult = needsAgent;
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.SaveConnections(s);
        FakeServices.CompleteNextJob(s);

        FakeServices.CompleteNextJob(s, FakeServices.Draft("rules found 3 risks"));

        var row = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal(expected, row.Status);
        Assert.Equal(0, row.CurrentVersion);
        var artifact = s.Artifacts.Get(PhaseName.Analysis, 0)!;
        Assert.Equal("script", artifact.Author);
        Assert.Equal("rules found 3 risks", artifact.Summary);
        Assert.Contains("artifact_created", EventTypes(s));
    }

    [Fact]
    public void Failed_job_keeps_the_phase_running_and_publishes_job_failed()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        var job = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(job.Id);
        s.Jobs.MarkFailed(job.Id, "timeout");

        s.Workflow.OnJobFailed(job, "timeout");

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        var last = s.Events.Since(0).Last();
        Assert.Equal("job_failed", last.Type);
        Assert.Contains("\"error\":\"timeout\"", last.PayloadJson);
        Assert.Contains("\"phase\":\"discovery\"", last.PayloadJson);
    }

    [Fact]
    public void Result_of_a_superseded_job_is_ignored()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        var first = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(first.Id);
        FakeServices.SaveConnections(s);   // queues a newer discover

        s.Workflow.OnJobDone(first, null, null);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        Assert.Contains(s.Events.Since(0), e => e.Type == "log" && e.PayloadJson.Contains("Ignored"));
    }

    [Fact]
    public void Request_changes_needs_feedback_then_moves_to_reworking()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        var ex = Assert.Throws<WorkflowException>(() => s.Workflow.RequestChanges(PhaseName.Analysis));
        Assert.Contains("at least one feedback item", ex.Message);
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Analysis));

        s.Feedback.Add(PhaseName.Analysis, 1, null, "More detail on risks");
        s.Workflow.RequestChanges(PhaseName.Analysis);

        Assert.Equal(PhaseStatus.Reworking, StatusOf(s, PhaseName.Analysis));
        Assert.Single(s.Feedback.List(PhaseName.Analysis, FeedbackStatus.Open));
        Assert.Contains("feedback_changed", EventTypes(s));
    }

    [Fact]
    public void Approving_analysis_records_the_fingerprint_and_starts_mapping()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        FakeServices.SetCatalogFingerprints(s, "s1", "t1");

        s.Workflow.Approve(PhaseName.Analysis);

        var analysis = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal(PhaseStatus.Approved, analysis.Status);
        Assert.Equal(1, analysis.ApprovedVersion);
        Assert.Equal("s1:t1", analysis.ApprovedFingerprint);
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Mapping));
        Assert.Equal("automap", Assert.Single(s.Jobs.Active()).Kind);
    }

    [Fact]
    public void Approving_mapping_starts_sql_and_approving_sql_readies_execution()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        FakeServices.DriveToReview(s, PhaseName.Sql);

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Mapping));
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Sql));
        Assert.Contains(s.Jobs.Recent(10), j => j.Kind == "sqlgen");
        s.Workflow.Approve(PhaseName.Sql);
        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Sql));
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Ready));
    }

    [Fact]
    public void Approval_blockers_throw_with_details_and_change_nothing()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Mapping.BlockersFn = _ => ["dbo.CUST.FAX_NO is unmapped"];
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToReview(s, PhaseName.Mapping);

        var ex = Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Mapping));

        Assert.Equal(new[] { "dbo.CUST.FAX_NO is unmapped" }, ex.Details);
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Mapping));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Sql));
    }

    [Fact]
    public void Only_a_reviewable_phase_awaiting_review_can_be_approved()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Analysis));
        Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Setup));
        Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Ready));
    }

    [Fact]
    public void Reopen_restores_the_approved_version_and_marks_later_phases_stale()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);

        s.Workflow.Reopen(PhaseName.Analysis);

        var analysis = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal(PhaseStatus.AwaitingReview, analysis.Status);
        Assert.Equal(1, analysis.CurrentVersion);
        Assert.Null(analysis.ApprovedVersion);
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Mapping));
        Assert.Null(s.Phases.Get(PhaseName.Mapping).ApprovedVersion);
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Sql));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Ready));
        Assert.Throws<WorkflowException>(() => s.Workflow.Reopen(PhaseName.Mapping));   // stale, not approved
    }

    [Fact]
    public void Approving_a_reopened_phase_reruns_its_stale_successor_with_carry_over()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.Workflow.Reopen(PhaseName.Mapping);

        s.Workflow.Approve(PhaseName.Mapping);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Sql));
        Assert.Equal("sqlgen", Assert.Single(s.Jobs.Active()).Kind);
        Assert.Equal(1, s.Workflow.CarryOver(PhaseName.Sql)!.Version);
        FakeServices.CompleteNextJob(s, FakeServices.Draft("sql redraft"));
        var sql = s.Phases.Get(PhaseName.Sql);
        Assert.Equal(PhaseStatus.Drafting, sql.Status);
        Assert.Equal(2, sql.CurrentVersion);
    }

    [Fact]
    public void Changes_upstream_are_refused_once_the_transfer_started()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.Workflow.Approve(PhaseName.Sql);

        s.Workflow.OnTransferStarted();

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Ready));
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Transfer));
        Assert.Throws<WorkflowException>(() => s.Workflow.Reopen(PhaseName.Analysis));
        Assert.Throws<WorkflowException>(() => FakeServices.SaveConnections(s));
        Assert.Throws<WorkflowException>(() => s.Workflow.Rediscover());
    }

    [Fact]
    public void Completed_transfer_approves_transfer_and_complete()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        s.Workflow.OnTransferStarted();
        s.Artifacts.Add(PhaseName.Complete, 0, "{}", "script", "8 rows rejected");

        s.Workflow.OnTransferFinished("failed", null);
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Transfer));
        s.Workflow.OnTransferFinished("completed", 0);

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Transfer));
        var complete = s.Phases.Get(PhaseName.Complete);
        Assert.Equal(PhaseStatus.Approved, complete.Status);
        Assert.Equal(0, complete.ApprovedVersion);
    }

    [Fact]
    public void Pause_and_resume_publish_events()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        s.Workflow.SetPaused(true);
        Assert.True(s.Project.Get().Paused);
        s.Workflow.SetPaused(false);

        Assert.False(s.Project.Get().Paused);
        Assert.Equal(new[] { "paused", "resumed" }, EventTypes(s).Where(t => t is "paused" or "resumed"));
    }

    [Fact]
    public void Retry_requeues_the_failed_job_of_a_running_phase()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        var job = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(job.Id);
        s.Jobs.MarkFailed(job.Id, "boom");

        s.Workflow.RetryJob(PhaseName.Discovery);

        Assert.Equal("discover", s.Jobs.NextQueued()!.Kind);
        Assert.Throws<WorkflowException>(() => s.Workflow.RetryJob(PhaseName.Discovery));   // already queued
        Assert.Throws<WorkflowException>(() => s.Workflow.RetryJob(PhaseName.Mapping));     // pending
    }

    [Fact]
    public void Retry_reruns_the_job_of_a_drafting_phase()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        s.Workflow.RetryJob(PhaseName.Analysis);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Analysis));
        Assert.Equal("analyze", Assert.Single(s.Jobs.Active()).Kind);
        FakeServices.CompleteNextJob(s, FakeServices.Draft("second script draft"));
        var row = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal((PhaseStatus.Drafting, (int?)1), (row.Status, row.CurrentVersion));
        Assert.Equal("second script draft", s.Artifacts.Get(PhaseName.Analysis, 1)!.Summary);
    }

    [Fact]
    public void Rediscover_marks_analysis_to_ready_stale()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        Assert.Throws<WorkflowException>(() => s.Workflow.Rediscover());   // setup not done
        FakeServices.DriveToReview(s, PhaseName.Mapping);

        s.Workflow.Rediscover();

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Analysis));
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Mapping));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Sql));
    }
}
