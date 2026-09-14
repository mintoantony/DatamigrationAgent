using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class EventJobRepoTests
{
    [Fact]
    public void Events_append_and_page_by_id()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var events = new EventRepo(db);

        Assert.Equal(0, events.LastId());
        var first = events.Append("paused");
        var second = events.Append("state_changed", new { phase = PhaseName.Analysis, status = PhaseStatus.AwaitingReview });

        Assert.Equal(second, events.LastId());
        var since = events.Since(first);
        Assert.Single(since);
        Assert.Equal("{\"phase\":\"analysis\",\"status\":\"awaiting_review\"}", since[0].PayloadJson);
        Assert.Equal("{}", events.Since(0)[0].PayloadJson);
        Assert.Single(events.Since(0, limit: 1));
    }

    [Fact]
    public void Jobs_queue_claim_finish_and_requeue()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var jobs = new JobRepo(db);

        var a = jobs.Enqueue("discover", PhaseName.Discovery);
        var b = jobs.Enqueue("analyze", PhaseName.Analysis);
        Assert.Equal(a, jobs.NextQueued()!.Id);
        Assert.True(jobs.TryClaim(a));
        Assert.False(jobs.TryClaim(a));
        Assert.Equal(b, jobs.NextQueued()!.Id);
        Assert.Equal(2, jobs.Active().Count);

        jobs.MarkDone(a);
        jobs.MarkRunning(b);
        jobs.MarkFailed(b, "boom");
        var failed = jobs.Get(b)!;
        Assert.Equal((JobStatus.Failed, "boom"), (failed.Status, failed.Error));
        Assert.NotNull(failed.StartedAt);
        Assert.NotNull(failed.EndedAt);
        Assert.Equal(JobStatus.Done, jobs.LatestFor(PhaseName.Discovery)!.Status);
        Assert.Null(jobs.LatestFor(PhaseName.Sql));
        Assert.Equal(new[] { b, a }, jobs.Recent(10).Select(j => j.Id));

        var c = jobs.Enqueue("sqlgen", null);
        jobs.MarkRunning(c);
        Assert.Equal(1, jobs.RequeueStaleRunning());
        Assert.Equal(JobStatus.Queued, jobs.Get(c)!.Status);
        Assert.Null(jobs.Get(c)!.Phase);
    }
}
