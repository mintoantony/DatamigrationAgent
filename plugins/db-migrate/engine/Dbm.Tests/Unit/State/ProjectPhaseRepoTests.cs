using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class ProjectPhaseRepoTests
{
    [Fact]
    public void Init_creates_the_project_and_one_phase_per_name_in_order()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var project = new ProjectRepo(db);
        var phases = new PhaseRepo(db);

        Assert.False(project.Exists());
        project.Init("shop");

        Assert.True(project.Exists());
        var p = project.Get();
        Assert.Equal("shop", p.Name);
        Assert.False(p.Paused);
        Assert.Null(p.AgentSeenAt);
        var all = phases.All();
        Assert.Equal(Enum.GetValues<PhaseName>(), all.Select(r => r.Name));
        Assert.Equal(Enumerable.Range(0, 8), all.Select(r => r.Ordinal));
        Assert.Equal(PhaseStatus.AwaitingReview, all[0].Status);
        Assert.All(all.Skip(1), r => Assert.Equal(PhaseStatus.Pending, r.Status));
        Assert.Throws<InvalidOperationException>(() => project.Init("again"));
    }

    [Fact]
    public void Pause_agent_touch_and_settings_round_trip()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var project = new ProjectRepo(db);
        project.Init("shop");

        project.SetPaused(true);
        project.TouchAgent();
        Assert.Equal(0.85, project.GetSettings().AutoAcceptScore);
        project.SaveSettings(new ProjectSettings { SampleValues = false, AutoAcceptScore = 0.9 });

        var p = project.Get();
        Assert.True(p.Paused);
        Assert.NotNull(p.AgentSeenAt);
        Assert.True(DateTimeOffset.UtcNow - p.AgentSeenAt!.Value < TimeSpan.FromMinutes(1));
        var settings = project.GetSettings();
        Assert.False(settings.SampleValues);
        Assert.Equal(0.9, settings.AutoAcceptScore);
        Assert.Equal(100_000, settings.ProfileSampleRows);
    }

    [Fact]
    public void Phase_status_versions_and_approval()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        new ProjectRepo(db).Init("shop");
        var phases = new PhaseRepo(db);

        phases.SetStatus(PhaseName.Analysis, PhaseStatus.Drafting);
        phases.SetCurrentVersion(PhaseName.Analysis, 2);
        Assert.Equal((PhaseStatus.Drafting, (int?)2), (phases.Get(PhaseName.Analysis).Status, phases.Get(PhaseName.Analysis).CurrentVersion));

        phases.SetApproved(PhaseName.Analysis, 2, "abc:def");
        var approved = phases.Get(PhaseName.Analysis);
        Assert.Equal(PhaseStatus.Approved, approved.Status);
        Assert.Equal(2, approved.ApprovedVersion);
        Assert.Equal("abc:def", approved.ApprovedFingerprint);

        phases.ClearApproval(PhaseName.Analysis);
        var cleared = phases.Get(PhaseName.Analysis);
        Assert.Null(cleared.ApprovedVersion);
        Assert.Null(cleared.ApprovedFingerprint);
        Assert.Equal(2, cleared.CurrentVersion);
    }
}
