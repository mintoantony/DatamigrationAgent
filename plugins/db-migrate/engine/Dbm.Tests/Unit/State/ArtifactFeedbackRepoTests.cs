using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class ArtifactFeedbackRepoTests
{
    [Fact]
    public void Artifacts_are_versioned_per_phase()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var artifacts = new ArtifactRepo(db);

        Assert.Equal(0, artifacts.NextVersion(PhaseName.Mapping));
        var v0 = artifacts.Add(PhaseName.Mapping, 0, "{\"a\":1}", "script", "draft");
        artifacts.Add(PhaseName.Mapping, 1, "{\"a\":2}", "agent", null);
        artifacts.Add(PhaseName.Sql, 0, "{}", "script", null);

        Assert.True(v0.Id > 0);
        Assert.Equal(2, artifacts.NextVersion(PhaseName.Mapping));
        Assert.Equal("{\"a\":2}", artifacts.Latest(PhaseName.Mapping)!.PayloadJson);
        Assert.Equal("draft", artifacts.Get(PhaseName.Mapping, 0)!.Summary);
        Assert.Null(artifacts.Get(PhaseName.Mapping, 5));
        Assert.Equal(new[] { (0, "script"), (1, "agent") }, artifacts.List(PhaseName.Mapping).Select(m => (m.Version, m.Author)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => artifacts.Add(PhaseName.Mapping, 1, "{}", "agent", null));
    }

    [Fact]
    public void Feedback_lifecycle_draft_open_responded()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var feedback = new FeedbackRepo(db);

        var a = feedback.Add(PhaseName.Analysis, 1, "finding:heap:dbo.AUDIT_LOG", "Explain the heap");
        var b = feedback.Add(PhaseName.Analysis, 1, null, "General remark");
        feedback.Add(PhaseName.Mapping, 1, null, "Other phase");

        Assert.Equal(FeedbackStatus.Draft, a.Status);
        Assert.True(feedback.DeleteDraft(b.Id));
        Assert.Null(feedback.Get(b.Id));
        Assert.Equal(1, feedback.SubmitDrafts(PhaseName.Analysis));
        Assert.False(feedback.DeleteDraft(a.Id));
        Assert.Single(feedback.List(PhaseName.Analysis, FeedbackStatus.Open));

        feedback.Respond(a.Id, FeedbackStatus.Addressed, "Explained.", 2);

        var responded = feedback.Get(a.Id)!;
        Assert.Equal(FeedbackStatus.Addressed, responded.Status);
        Assert.Equal("Explained.", responded.Response);
        Assert.Equal(2, responded.RespondedVersion);
        Assert.Equal("finding:heap:dbo.AUDIT_LOG", responded.Anchor);
        Assert.Empty(feedback.List(PhaseName.Analysis, FeedbackStatus.Open));
        Assert.Single(feedback.List(PhaseName.Analysis));
    }
}
