using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

/// <summary>
/// Open item 1, Ruling 194: every approve names the version the reviewer saw, and a version that is no longer the current one is
/// refused - a stale tab must not sign off a version its human never displayed.
/// </summary>
public class ApproveVersionTests
{
    public static TheoryData<PhaseName> Reviewable => new() { PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql };

    /// <summary>Approves naming <paramref name="seen"/> and demands the stale_version refusal, saying what happened instead.</summary>
    private static WorkflowException StaleRefusal(DbmServices s, PhaseName phase, int seen)
    {
        var current = s.Phases.Get(phase).CurrentVersion;
        var ex = Record.Exception(() => s.Workflow.Approve(phase, seen));
        var row = s.Phases.Get(phase);
        var what = ex is null
            ? $"it was accepted - {phase.Text()} is now {EnumText.ToText(row.Status)} with approved v{row.ApprovedVersion}, a version the reviewer never saw"
            : $"{ex.GetType().Name} (code {(ex as WorkflowException)?.Code ?? "none"}): {ex.Message}";
        Assert.True(ex is WorkflowException { Code: WorkflowException.StaleVersion },
            $"Approve naming v{seen} while {phase.Text()} is at v{current} must be refused as stale_version, but {what}");
        return (WorkflowException)ex!;
    }

    [Theory]
    [MemberData(nameof(Reviewable))]
    public void Approving_the_current_version_approves_it(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, phase);

        s.Workflow.Approve(phase, seenVersion: 1);

        var row = s.Phases.Get(phase);
        Assert.Equal((PhaseStatus.Approved, (int?)1), (row.Status, row.ApprovedVersion));
    }

    [Theory]
    [MemberData(nameof(Reviewable))]
    public void Approving_a_version_that_is_no_longer_current_is_refused_and_changes_nothing(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, phase);
        // The tab shows v1; a direct edit from another tab stores v2 while the phase still awaits review.
        var edit = s.Workflow.HumanEdit(new Patch(phase.Text(), 1, [new PatchOp("replace", "/summary", JsonValue.Create("v2 from the other tab"))], []));
        Assert.True(edit.Ok, string.Join("; ", edit.Errors));

        var ex = StaleRefusal(s, phase, seen: 1);

        Assert.Contains("v1", ex.Message);
        Assert.Contains("v2", ex.Message);
        var row = s.Phases.Get(phase);
        Assert.True(row.Status == PhaseStatus.AwaitingReview && row.ApprovedVersion is null,
            $"a stale approve signed off {phase.Text()}: status {row.Status}, approved v{row.ApprovedVersion}");
    }

    [Theory]
    [MemberData(nameof(Reviewable))]
    public void Approving_a_version_newer_than_the_current_one_is_refused(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, phase);

        StaleRefusal(s, phase, seen: 7);

        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(phase).Status);
    }

    [Fact]
    public void The_version_check_runs_before_the_approval_blockers()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Mapping.BlockersFn = _ => ["dbo.CUST.FAX_NO is unmapped"];
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToReview(s, PhaseName.Mapping);

        // The blockers describe v1; the reviewer saw v0, so the answer is "review the current version", not v1's blockers.
        StaleRefusal(s, PhaseName.Mapping, seen: 0);
    }
}
