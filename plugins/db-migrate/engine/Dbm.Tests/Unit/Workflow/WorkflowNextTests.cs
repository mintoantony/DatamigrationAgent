using System.Text.Json.Nodes;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

public class WorkflowNextTests
{
    [Fact]
    public void Paused_project_awaits_with_reason_paused()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        s.Workflow.SetPaused(true);

        Assert.Equal(new NextAction("await", Reason: "paused"), s.Workflow.Next());
    }

    [Fact]
    public void Setup_awaits_with_the_ui_url_from_server_json()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        File.WriteAllText(tw.Ws.ServerJsonPath, "{\"port\":5123,\"pid\":1,\"token\":\"tok\"}");

        Assert.Equal(new NextAction("await", Reason: "setup", Phase: "setup", Url: "http://127.0.0.1:5123/?t=tok"), s.Workflow.Next());
    }

    [Fact]
    public void Running_phase_awaits_its_job_or_stops_when_the_job_failed()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);

        Assert.Equal(new NextAction("await", Reason: "job", Phase: "discovery"), s.Workflow.Next());

        var job = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(job.Id);
        s.Jobs.MarkFailed(job.Id, "Login failed for user 'x'.");
        Assert.Equal(new NextAction("stop", Reason: "job_failed", Phase: "discovery", Summary: "Login failed for user 'x'."), s.Workflow.Next());
    }

    [Fact]
    public void Drafting_phase_returns_an_agent_action_and_writes_the_packet()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);
        var stalePatch = Path.Combine(tw.Ws.WorkDir, "analysis-v0-draft.patch.json");
        File.WriteAllText(stalePatch, "{}");

        var a = s.Workflow.Next();

        var packetPath = Path.Combine(tw.Ws.WorkDir, "analysis-v0-draft.json").Replace('\\', '/');
        Assert.Equal(new NextAction("agent", Agent: "schema-analyst", Phase: "analysis", Mode: "draft",
            Packet: packetPath, PatchPath: packetPath.Replace(".json", ".patch.json")), a);
        Assert.True(Path.IsPathFullyQualified(a.Packet!));
        Assert.False(File.Exists(stalePatch));

        var packet = JsonNode.Parse(File.ReadAllText(a.Packet!))!;
        Assert.Equal("analysis", packet["phase"]!.GetValue<string>());
        Assert.Equal("draft", packet["mode"]!.GetValue<string>());
        Assert.Equal(0, packet["baseVersion"]!.GetValue<int>());
        Assert.Equal(a.PatchPath, packet["patchPath"]!.GetValue<string>());
        Assert.Equal("schema-analyst", packet["agent"]!.GetValue<string>());
        Assert.Empty(packet["feedback"]!.AsArray());
        Assert.Equal(WorkflowEngine.PatchRules, packet["rules"]!.GetValue<string>());
        Assert.True(packet["data"]!["fake"]!.GetValue<bool>());
    }

    [Fact]
    public void Reworking_phase_packet_carries_only_the_open_feedback()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        var item = s.Feedback.Add(PhaseName.Analysis, 1, "finding:heap:dbo.AUDIT_LOG", "Explain the heap");
        s.Workflow.RequestChanges(PhaseName.Analysis);
        s.Feedback.Add(PhaseName.Analysis, 1, null, "a new draft, not part of this round");

        var a = s.Workflow.Next();

        Assert.Equal(("agent", "rework"), (a.Action, a.Mode));
        Assert.EndsWith("/analysis-v1-rework.json", a.Packet);
        var packet = JsonNode.Parse(File.ReadAllText(a.Packet!))!;
        var only = Assert.Single(packet["feedback"]!.AsArray())!;
        Assert.Equal(item.Id, only["id"]!.GetValue<long>());
        Assert.Equal("finding:heap:dbo.AUDIT_LOG", only["anchor"]!.GetValue<string>());
        Assert.Equal("Explain the heap", only["text"]!.GetValue<string>());
        Assert.Equal(1, packet["data"]!["openFeedback"]!.GetValue<int>());
    }

    [Fact]
    public void Peek_does_not_write_packets()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var a = s.Workflow.Peek();

        Assert.Equal("agent", a.Action);
        Assert.False(File.Exists(a.Packet!));
    }

    [Fact]
    public void Awaiting_review_awaits_review_and_ready_awaits_execute()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);

        Assert.Equal(new NextAction("await", Reason: "review", Phase: "sql", Url: ""), s.Workflow.Next());
        s.ApproveCurrent(PhaseName.Sql);
        Assert.Equal(new NextAction("await", Reason: "execute", Phase: "ready", Url: ""), s.Workflow.Next());
    }

    [Theory]
    [InlineData("running", "await", "transfer")]
    [InlineData("paused", "await", "transfer_paused")]
    [InlineData("failed", "stop", "transfer_failed")]
    [InlineData("cancelled", "stop", "transfer_cancelled")]
    public void Transfer_run_status_drives_the_action(string runStatus, string action, string reason)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.ApproveCurrent(PhaseName.Sql);
        s.Workflow.OnTransferStarted();
        // No engine method yet drives transfer_run rows (that lands with T5.1); this insert is the only way to reach it.
        s.Db.Execute("INSERT INTO transfer_run (sql_version, status, options_json) VALUES (1, 'completed', '{}'), (1, $Status, '{}')",
            new { Status = runStatus });

        var a = s.Workflow.Next();

        Assert.Equal((action, reason, "transfer"), (a.Action, a.Reason, a.Phase));
    }

    [Fact]
    public void All_approved_stops_with_the_report_summary()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.ApproveCurrent(PhaseName.Sql);
        s.Workflow.OnTransferStarted();
        s.Artifacts.Add(PhaseName.Complete, 0, "{}", "script", "Migrated 12,000 rows; 8 rejected.");
        s.Workflow.OnTransferFinished("completed", 0);

        Assert.Equal(new NextAction("stop", Reason: "complete", Summary: "Migrated 12,000 rows; 8 rejected."), s.Workflow.Next());
    }
}
