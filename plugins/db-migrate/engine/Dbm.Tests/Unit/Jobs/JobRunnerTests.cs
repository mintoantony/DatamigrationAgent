using System.Text.Json.Nodes;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Jobs;

public class JobRunnerTests
{
    private sealed class SlowHandler(string kind) : IJobHandler
    {
        private int _runs;
        public string Kind => kind;
        public int Runs => Volatile.Read(ref _runs);

        public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            await Task.Delay(30, ct);
            return new JobResult(null, null);
        }
    }

    [Fact]
    public async Task Discover_then_analyze_run_in_order()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);

        var ran = await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        Assert.Equal(2, ran);
        Assert.Equal(PhaseStatus.Approved, s.Phases.Get(PhaseName.Discovery).Status);
        Assert.Equal(PhaseStatus.Drafting, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Equal("script", s.Artifacts.Get(PhaseName.Analysis, 0)!.Author);
        var types = s.Events.Since(0, 1000).Select(e => e.Type).ToList();
        Assert.Equal(2, types.Count(t => t == "job_started"));
        Assert.Equal(2, types.Count(t => t == "job_done"));
        Assert.All(s.Jobs.Recent(10), j => Assert.Equal(JobStatus.Done, j.Status));
    }

    [Fact]
    public async Task A_job_without_a_handler_fails_and_stops_the_orchestrator()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws, handlers: [new FakeJobHandler("discover")]);
        FakeServices.SaveConnections(s);

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        var job = s.Jobs.LatestFor(PhaseName.Analysis)!;
        Assert.Equal((JobStatus.Failed, "no handler for analyze"), (job.Status, job.Error));
        Assert.Equal(PhaseStatus.Running, s.Phases.Get(PhaseName.Analysis).Status);
        var next = s.Workflow.Next();
        Assert.Equal(("stop", "job_failed", "no handler for analyze"), (next.Action, next.Reason, next.Summary));
        Assert.Contains(s.Events.Since(0), e => e.Type == "job_failed");
    }

    [Fact]
    public async Task Handler_errors_are_scrubbed_of_saved_secrets()
    {
        using var tw = new TestWorkspace();
        var leaky = new FakeJobHandler("discover", _ => throw new InvalidOperationException("Login failed; password TopSecret99 rejected"));
        using var s = FakeServices.Open(tw.Ws, handlers: [leaky]);
        s.Connections.Save(Side.Src, "Server=a;Database=b;User ID=u;Password=TopSecret99", FakeServices.Meta("a", "b"));
        s.Connections.Save(Side.Tgt, FakeServices.TgtConnection, FakeServices.Meta("t", "ShopV2"));
        s.Workflow.OnConnectionsSaved();

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        Assert.Equal("Login failed; password *** rejected", s.Jobs.LatestFor(PhaseName.Discovery)!.Error);
        Assert.DoesNotContain(s.Events.Since(0, 1000), e => e.PayloadJson.Contains("TopSecret99"));
    }

    [Fact]
    public async Task A_draft_that_the_workflow_rejects_fails_the_job_and_stores_nothing()
    {
        using var tw = new TestWorkspace();
        var noDraft = new FakeJobHandler("analyze", _ => new JobResult(null, null));
        using var s = FakeServices.Open(tw.Ws, handlers: [new FakeJobHandler("discover"), noDraft]);
        FakeServices.SaveConnections(s);

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        var job = s.Jobs.LatestFor(PhaseName.Analysis)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("returned no draft", job.Error);
        Assert.Null(s.Artifacts.Latest(PhaseName.Analysis));
    }

    [Fact]
    public async Task Log_calls_publish_log_events()
    {
        using var tw = new TestWorkspace();
        var chatty = new FakeJobHandler("discover", ctx =>
        {
            ctx.Log("extracting 12 tables");
            return new JobResult(null, null);
        });
        using var s = FakeServices.Open(tw.Ws, handlers: [chatty]);
        FakeServices.SaveConnections(s);

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        var log = s.Events.Since(0, 1000).Single(e => e.Type == "log" && e.PayloadJson.Contains("extracting"));
        var payload = JsonNode.Parse(log.PayloadJson)!;
        Assert.Equal("info", payload["level"]!.GetValue<string>());
        Assert.Equal("extracting 12 tables", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_loop_requeues_stale_jobs_and_runs_until_cancelled()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        s.Jobs.MarkRunning(s.Jobs.NextQueued()!.Id);   // left "running" by a dead server
        using var cts = new CancellationTokenSource();

        var loop = new JobRunner(s).RunLoopAsync(cts.Token);
        await Wait.UntilAsync(() => s.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.Drafting);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Done, s.Jobs.LatestFor(PhaseName.Discovery)!.Status);
    }

    [Fact]
    public async Task Two_runners_never_run_the_same_job_twice()
    {
        using var tw = new TestWorkspace();
        var slow = new SlowHandler("noop");
        using var a = FakeServices.Open(tw.Ws, handlers: [slow]);
        using var b = FakeServices.Open(tw.Ws, handlers: [slow]);
        for (var i = 0; i < 6; i++) a.Jobs.Enqueue("noop", null);

        var counts = await Task.WhenAll(
            Task.Run(() => new JobRunner(a).RunPendingAsync(CancellationToken.None)),
            Task.Run(() => new JobRunner(b).RunPendingAsync(CancellationToken.None)));

        Assert.Equal(6, counts.Sum());
        Assert.Equal(6, slow.Runs);
    }
}
