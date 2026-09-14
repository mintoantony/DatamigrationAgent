using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dbm.Core;
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
    /// Spawns a real, detached dbm server as an innocent bystander (so the process-name check alone can't be what
    /// saves it — it genuinely is named "dotnet"/"Dbm") and points a fabricated, stale server.json at its PID with a
    /// StartedAt far outside any tolerance for the bystander's real OS start time. The workspace's own server.lock is
    /// held (simulating "still running") so StopAsync is forced past the clean-shutdown path into the identify step.
    /// </summary>
    [Fact]
    public async Task StopAsync_never_kills_a_live_process_whose_identity_does_not_match_the_recorded_server()
    {
        using var bystanderWs = new TestWorkspace();
        using (FakeServices.Open(bystanderWs.Ws)) { }   // creates the project so `serve` has something to run
        var bystander = await ServerControl.EnsureRunningAsync(bystanderWs.Ws);
        try
        {
            using var tw = new TestWorkspace();
            tw.Ws.EnsureCreated();
            var fakeInfo = new ServerInfo(FreeLoopbackPort(), bystander.Pid, "tok", DateTimeOffset.UnixEpoch);
            File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(fakeInfo));
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            await ServerControl.StopAsync(tw.Ws);

            Assert.True(await ServerControl.IsAliveAsync(bystander));
            Assert.False(File.Exists(tw.Ws.ServerJsonPath));
        }
        finally
        {
            await ServerControl.StopAsync(bystanderWs.Ws);
        }
    }

    /// <summary>
    /// StopAsync must wait for server.lock to be released — i.e. for RunAsync's job/pump drain to actually finish —
    /// not merely for server.json to disappear (server.json is deleted before the drain, per T1.7 fix round 1).
    /// A job handler that ignores cancellation and blocks synchronously simulates a job still actively executing.
    /// </summary>
    [Fact]
    public async Task StopAsync_waits_for_the_lock_to_release_not_just_for_server_json_to_disappear()
    {
        using var tw = new TestWorkspace();
        var slow = new FakeJobHandler("discover", _ =>
        {
            Thread.Sleep(1500);
            return new JobResult(null, "slow job finished");
        });
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(handlers: [slow]));
        server.Services.Jobs.Enqueue("discover", null);
        await Wait.UntilAsync(() => slow.Runs >= 1);

        var sw = Stopwatch.StartNew();
        await ServerControl.StopAsync(tw.Ws);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 1000,
            $"StopAsync returned after only {sw.ElapsedMilliseconds} ms while a job handler was still blocking the drain");
        Assert.Null(ServerControl.ReadInfo(tw.Ws));
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
