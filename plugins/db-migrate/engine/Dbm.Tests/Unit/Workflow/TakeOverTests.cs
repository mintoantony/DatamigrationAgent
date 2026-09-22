using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

/// <summary>
/// Open item 37, Ruling 195: a phase left in drafting or reworking (a patch rejected twice) can be taken over from the UI. It goes
/// back to awaiting_review on its current version, open feedback stays open, and the next `dbm next` awaits the human instead of
/// dispatching the agent again. A patch the agent delivers afterwards is refused - that is the discarded work.
/// </summary>
public class TakeOverTests
{
    public static TheoryData<PhaseName, PhaseStatus> Stuck => new()
    {
        { PhaseName.Analysis, PhaseStatus.Drafting }, { PhaseName.Analysis, PhaseStatus.Reworking },
        { PhaseName.Mapping, PhaseStatus.Drafting }, { PhaseName.Mapping, PhaseStatus.Reworking },
        { PhaseName.Sql, PhaseStatus.Drafting }, { PhaseName.Sql, PhaseStatus.Reworking },
    };

    /// <summary>Drafting at the script's v0, or reworking v1 with one open comment.</summary>
    private static void DriveTo(DbmServices s, PhaseName phase, PhaseStatus status)
    {
        if (status == PhaseStatus.Reworking)
        {
            FakeServices.DriveToReview(s, phase);
            s.Feedback.Add(phase, 1, null, "Rename the staging table");
            s.Workflow.RequestChanges(phase);
        }
        else if (phase == PhaseName.Analysis)
        {
            FakeServices.DriveToAnalysisDraft(s);
        }
        else
        {
            FakeServices.DriveToReview(s, phase - 1);
            s.ApproveCurrent(phase - 1);
            FakeServices.CompleteNextJob(s, FakeServices.Draft());
        }
        Assert.Equal(status, s.Phases.Get(phase).Status);
    }

    [Theory]
    [MemberData(nameof(Stuck))]
    public void Taking_over_returns_the_phase_to_review_on_its_current_version_and_next_awaits_the_human(PhaseName phase, PhaseStatus stuck)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        DriveTo(s, phase, stuck);
        var version = s.Phases.Get(phase).CurrentVersion;
        Assert.Equal("agent", s.Workflow.Next().Action);

        s.Workflow.TakeOver(phase);

        var row = s.Phases.Get(phase);
        Assert.True(row.Status == PhaseStatus.AwaitingReview && row.CurrentVersion == version,
            $"after Take over {phase.Text()} must await review on v{version}, but it is {EnumText.ToText(row.Status)} on v{row.CurrentVersion}");
        var next = s.Workflow.Next();
        Assert.True(next is { Action: "await", Reason: "review" } && next.Phase == phase.Text(),
            $"after Take over `dbm next` must await the human's review of {phase.Text()}, but it answered {Json.Serialize(next)}");
    }

    [Theory]
    [MemberData(nameof(Stuck))]
    public void Taking_over_keeps_open_feedback_open_and_refuses_the_agents_late_patch(PhaseName phase, PhaseStatus stuck)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        DriveTo(s, phase, stuck);
        var open = s.Feedback.List(phase, FeedbackStatus.Open).Select(f => f.Id).ToList();

        s.Workflow.TakeOver(phase);

        Assert.Equal(open, s.Feedback.List(phase, FeedbackStatus.Open).Select(f => f.Id).ToList());
        var late = FakeServices.ApplySummary(s, phase, "the agent's patch, written before the take-over");
        Assert.False(late.Ok, $"the agent's patch after Take over must be refused (its work was discarded), but it stored v{late.Version}");
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(phase).Status);
    }

    [Fact]
    public void After_taking_over_the_human_can_edit_and_request_changes_again()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        DriveTo(s, PhaseName.Mapping, PhaseStatus.Reworking);

        s.Workflow.TakeOver(PhaseName.Mapping);

        var edit = s.Workflow.HumanEdit(new Dbm.Core.Patching.Patch("mapping", 1,
            [new Dbm.Core.Patching.PatchOp("replace", "/summary", System.Text.Json.Nodes.JsonValue.Create("fixed by hand"))], []));
        Assert.True(edit.Ok, string.Join("; ", edit.Errors));
        s.Workflow.RequestChanges(PhaseName.Mapping);   // the comment still open is sent again
        Assert.Equal(PhaseStatus.Reworking, s.Phases.Get(PhaseName.Mapping).Status);
    }

    [Theory]
    [InlineData(PhaseName.Setup)]
    [InlineData(PhaseName.Discovery)]
    [InlineData(PhaseName.Ready)]
    public void Only_a_review_phase_can_be_taken_over(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var ex = Record.Exception(() => s.Workflow.TakeOver(phase));

        Assert.True(ex is WorkflowException, $"Take over of {phase.Text()} must be refused, got {ex?.GetType().Name ?? "no refusal"}");
    }

    [Theory]
    [MemberData(nameof(ApprovedOrAwaiting))]
    public void A_phase_that_is_not_drafting_or_reworking_cannot_be_taken_over(PhaseStatus status)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        if (status == PhaseStatus.Approved) s.ApproveCurrent(PhaseName.Analysis);

        var ex = Record.Exception(() => s.Workflow.TakeOver(PhaseName.Analysis));

        Assert.True(ex is WorkflowException, $"Take over of an {EnumText.ToText(status)} phase must be refused, got {ex?.GetType().Name ?? "no refusal"}");
        Assert.Equal(status, s.Phases.Get(PhaseName.Analysis).Status);
    }

    public static TheoryData<PhaseStatus> ApprovedOrAwaiting => new() { PhaseStatus.AwaitingReview, PhaseStatus.Approved };
}
