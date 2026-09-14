using System.Text.Json.Nodes;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

public class WorkflowPatchTests
{
    private static Patch SummaryPatch(string phase, int baseVersion, string summary, params FeedbackResponse[] responses) =>
        new(phase, baseVersion, [new PatchOp("replace", "/summary", JsonValue.Create(summary))], responses.ToList(), summary);

    [Fact]
    public void Agent_patch_on_a_draft_creates_v1_and_awaits_review()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "Executive summary"));

        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(1, r.Version);
        var v1 = s.Artifacts.Get(PhaseName.Analysis, 1)!;
        Assert.Equal("agent", v1.Author);
        Assert.Equal("Executive summary", v1.Summary);
        var payload = JsonNode.Parse(v1.PayloadJson)!;
        Assert.Equal("Executive summary", payload["summary"]!.GetValue<string>());
        Assert.Equal(2, payload["items"]!["b"]!.GetValue<int>());
        var row = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal((PhaseStatus.AwaitingReview, (int?)1), (row.Status, row.CurrentVersion));
    }

    [Fact]
    public void Base_version_must_match_the_current_version()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 3, "x"));

        Assert.False(r.Ok);
        Assert.Equal("baseVersion 3 does not match the current version 0", r.Errors.Single());
    }

    [Fact]
    public void Agent_patches_are_refused_outside_drafting_or_reworking()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 1, "x"));

        Assert.False(r.Ok);
        Assert.Contains("is awaiting_review; agent patches are accepted only while drafting or reworking", r.Errors.Single());
    }

    [Fact]
    public void Rework_needs_a_valid_response_for_every_open_item()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        var f1 = s.Feedback.Add(PhaseName.Analysis, 1, null, "one");
        var f2 = s.Feedback.Add(PhaseName.Analysis, 1, null, "two");
        s.Workflow.RequestChanges(PhaseName.Analysis);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 1, "v2",
            new FeedbackResponse(f1.Id, "done", "ok"),
            new FeedbackResponse(999, "addressed", "x")));

        Assert.False(r.Ok);
        Assert.Contains($"response for feedback {f1.Id}: status must be 'addressed' or 'declined'", r.Errors);
        Assert.Contains("feedback 999 is not an open item of this phase", r.Errors);
        Assert.Contains($"missing response for feedback {f2.Id}", r.Errors);
        Assert.Equal(PhaseStatus.Reworking, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Null(s.Artifacts.Get(PhaseName.Analysis, 2));
    }

    [Fact]
    public void Rework_responses_are_recorded_against_the_new_version()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        var f1 = s.Feedback.Add(PhaseName.Analysis, 1, "narrative", "Add detail");
        var f2 = s.Feedback.Add(PhaseName.Analysis, 1, null, "Rename things");
        s.Workflow.RequestChanges(PhaseName.Analysis);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 1, "v2",
            new FeedbackResponse(f1.Id, "addressed", "Added detail."),
            new FeedbackResponse(f2.Id, "declined", "Out of scope.")));

        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(2, r.Version);
        var a = s.Feedback.Get(f1.Id)!;
        var b = s.Feedback.Get(f2.Id)!;
        Assert.Equal((FeedbackStatus.Addressed, "Added detail.", (int?)2), (a.Status, a.Response, a.RespondedVersion));
        Assert.Equal((FeedbackStatus.Declined, "Out of scope.", (int?)2), (b.Status, b.Response, b.RespondedVersion));
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public void Module_validation_errors_reject_and_warnings_pass_through()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.ValidateFn = p => p["summary"]!.GetValue<string>().Length < 5
            ? new PayloadCheck(["summary too short"], [])
            : new PayloadCheck([], ["consider listing risks"]);
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToAnalysisDraft(s);

        var bad = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "x"));
        var good = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "A proper summary"));

        Assert.Equal(new[] { "summary too short" }, bad.Errors);
        Assert.True(good.Ok);
        Assert.Equal(new[] { "consider listing risks" }, good.Warnings);
    }

    [Fact]
    public void The_payload_is_stored_after_validation_so_module_annotations_persist()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.ValidateFn = p =>
        {
            p["validatedBy"] = "module";   // e.g. SqlModule writes Custom/Errors/Warnings during Validate
            return PayloadCheck.Pass();
        };
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToAnalysisDraft(s);

        var agent = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "agent version"));
        var human = s.Workflow.HumanEdit(SummaryPatch("analysis", 1, "human version"));

        Assert.True(agent.Ok && human.Ok);
        Assert.Equal("module", JsonNode.Parse(s.Artifacts.Get(PhaseName.Analysis, 1)!.PayloadJson)!["validatedBy"]!.GetValue<string>());
        Assert.Equal("module", JsonNode.Parse(s.Artifacts.Get(PhaseName.Analysis, 2)!.PayloadJson)!["validatedBy"]!.GetValue<string>());
    }

    [Fact]
    public void Op_errors_are_reported_with_the_op_index()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var r = s.Workflow.ApplyPatch(new Patch("analysis", 0, [new PatchOp("replace", "/nope", JsonValue.Create(1))], []));

        Assert.StartsWith("op 0 (replace /nope): ", r.Errors.Single());
    }

    [Fact]
    public void Dry_run_validates_but_writes_nothing()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);
        var lastEvent = s.Events.LastId();

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "Executive summary"), dryRun: true);

        Assert.True(r.Ok);
        Assert.Null(r.Version);
        Assert.Equal(1, s.Artifacts.NextVersion(PhaseName.Analysis));
        Assert.Equal(PhaseStatus.Drafting, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Equal(lastEvent, s.Events.LastId());
    }

    [Fact]
    public void Patches_are_accepted_while_paused()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);
        s.Workflow.SetPaused(true);

        Assert.True(s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "done while paused")).Ok);
        Assert.Equal("paused", s.Workflow.Next().Reason);
    }

    [Fact]
    public void Unknown_phases_and_phases_without_a_module_are_rejected()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        Assert.Equal(new[] { "unknown phase 'nope'" }, s.Workflow.ApplyPatch(SummaryPatch("nope", 0, "x")).Errors);
        Assert.Equal(new[] { "phase 'discovery' does not accept patches" }, s.Workflow.ApplyPatch(SummaryPatch("discovery", 0, "x")).Errors);
    }

    [Fact]
    public void Human_edit_creates_a_human_version_and_stays_in_review()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        var r = s.Workflow.HumanEdit(SummaryPatch("analysis", 1, "Edited by a human"));

        Assert.True(r.Ok);
        Assert.Equal(2, r.Version);
        Assert.Equal("human", s.Artifacts.Get(PhaseName.Analysis, 2)!.Author);
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Analysis).Status);

        s.Feedback.Add(PhaseName.Analysis, 2, null, "rework please");
        s.Workflow.RequestChanges(PhaseName.Analysis);
        var refused = s.Workflow.HumanEdit(SummaryPatch("analysis", 2, "again"));
        Assert.Contains("direct edits are allowed only while it awaits review", refused.Errors.Single());
    }
}
