using System.Diagnostics;
using Dbm.Core.Jobs;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class ServerControlTests
{
    [Fact]
    public void Start_time_matches_pins_the_tolerance_boundary()
    {
        var current = Process.GetCurrentProcess();
        var start = current.StartTime.ToUniversalTime();
        var tolerance = ServerControl.StartTimeTolerance;
        var margin = TimeSpan.FromMilliseconds(500);

        // Match iff process.StartTime <= info.StartedAt + tolerance, i.e. info.StartedAt >= process.StartTime - tolerance.
        var justInside = new ServerInfo(1, current.Id, "t", start - tolerance + margin);
        var justOutside = new ServerInfo(1, current.Id, "t", start - tolerance - margin);

        Assert.True(ServerControl.StartTimeMatches(current, justInside));
        Assert.False(ServerControl.StartTimeMatches(current, justOutside));
    }

    /// <summary>
    /// StopAsync must wait for server.lock to be released — i.e. for RunAsync's job/pump drain to actually finish —
    /// not merely for server.json to disappear (server.json is deleted before the drain starts, per T1.7 fix round
    /// 1). A job handler blocked on a ManualResetEventSlim (rather than a timed sleep) makes the assertion exact:
    /// StopAsync must not have completed while the job is provably still running, with no timing margin to flake.
    /// </summary>
    [Fact]
    public async Task StopAsync_waits_for_the_lock_to_release_not_just_for_server_json_to_disappear()
    {
        using var tw = new TestWorkspace();
        using var release = new ManualResetEventSlim(false);
        var slow = new FakeJobHandler("discover", _ =>
        {
            release.Wait();
            return new JobResult(null, "slow job finished");
        });
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(handlers: [slow]));
        server.Services.Jobs.Enqueue("discover", null);
        await Wait.UntilAsync(() => slow.Runs >= 1);

        var stopTask = ServerControl.StopAsync(tw.Ws);
        // server.json is deleted immediately on cancellation, well before the still-blocked job unblocks and the
        // drain can finish — so seeing it gone is proof the job is (still) the only thing StopAsync is waiting on.
        await Wait.UntilAsync(() => !File.Exists(tw.Ws.ServerJsonPath));
        Assert.False(stopTask.IsCompleted, "StopAsync returned before the still-running job handler was released");

        release.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(ServerControl.ReadInfo(tw.Ws));
    }
}
