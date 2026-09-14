using System.Diagnostics;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

/// <summary>Several tests here shorten ServerControl's internal static timeouts to keep the lock-held-throughout
/// scenarios fast; ProcessStateCollection keeps them from racing another test class over the same shared statics.</summary>
[Collection(ProcessStateCollection.Name)]
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
    /// once server.json is gone, StopAsync's task must still not complete within a further bounded wait — asserted
    /// by requiring WaitAsync to throw TimeoutException, not by polling IsCompleted (which round-1's buggy 200 ms
    /// poll could slip past a 50 ms-granularity IsCompleted check often enough to pass by accident).
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
        await Assert.ThrowsAsync<TimeoutException>(() => stopTask.WaitAsync(TimeSpan.FromSeconds(2)));

        release.Set();
        Assert.True(await stopTask.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Null(ServerControl.ReadInfo(tw.Ws));
    }

    [Fact]
    public async Task StopAsync_throws_when_there_is_no_recorded_server_but_the_lock_is_still_held()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        var saved = ServerControl.LockReleaseTimeout;
        ServerControl.LockReleaseTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ServerControl.StopAsync(tw.Ws));

            Assert.Contains("starting or shutting down", ex.Message);
        }
        finally
        {
            ServerControl.LockReleaseTimeout = saved;
        }
    }

    /// <summary>The recorded PID having exited does not, by itself, prove the server is gone: something else may
    /// now hold the lock (a legitimate new start racing in, say). StopAsync must not report success just because
    /// there was nothing to kill.</summary>
    [Fact]
    public async Task StopAsync_throws_when_the_recorded_pid_has_exited_but_the_lock_is_still_held()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        exited.WaitForExit();
        var deadPid = exited.Id;

        File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(new ServerInfo(1, deadPid, "tok", DateTimeOffset.UtcNow)));

        var savedLock = ServerControl.LockReleaseTimeout;
        var savedKill = ServerControl.KillGraceTimeout;
        ServerControl.LockReleaseTimeout = TimeSpan.FromMilliseconds(300);
        ServerControl.KillGraceTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ServerControl.StopAsync(tw.Ws));

            Assert.Contains(deadPid.ToString(), ex.Message);
            Assert.True(File.Exists(tw.Ws.ServerJsonPath));
        }
        finally
        {
            ServerControl.LockReleaseTimeout = savedLock;
            ServerControl.KillGraceTimeout = savedKill;
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_throws_when_the_lock_is_held_and_no_healthy_server_ever_answers()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        var saved = ServerControl.LockReleaseTimeout;
        ServerControl.LockReleaseTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ServerControl.EnsureRunningAsync(tw.Ws));

            Assert.Contains("starting or shutting down", ex.Message);
        }
        finally
        {
            ServerControl.LockReleaseTimeout = saved;
        }
    }

    /// <summary>
    /// Regression test for T1.7 fix round 4, item 1: EnsureRunningAsync must find a healthy server even if reading
    /// server.json fails on the very first attempt (simulated here by deleting it briefly) — it must retry rather
    /// than treat "still holds the lock" as "nothing to wait for but a lock release". A SpawnOverrides entry that
    /// fails the test if invoked proves it never tries to start a second server alongside the healthy one.
    /// </summary>
    [Fact]
    public async Task EnsureRunningAsync_finds_a_healthy_server_that_missed_one_health_check_without_spawning()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        var originalServerJson = File.ReadAllText(tw.Ws.ServerJsonPath);

        ServerControl.SpawnOverrides[tw.Ws.Root] = (_, _) =>
        {
            Assert.Fail("EnsureRunningAsync must not spawn a new server while the existing one is still healthy");
            return Task.CompletedTask;
        };
        try
        {
            // The real server still holds server.lock throughout; only server.json's readability is disrupted, to
            // simulate one missed/failed health check on an otherwise live server.
            File.Delete(tw.Ws.ServerJsonPath);
            var restore = Task.Run(async () =>
            {
                await Task.Delay(300);
                File.WriteAllText(tw.Ws.ServerJsonPath, originalServerJson);
            });

            var found = await ServerControl.EnsureRunningAsync(tw.Ws);

            await restore;
            Assert.Equal(server.Info.Pid, found.Pid);
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
        }
    }
}
