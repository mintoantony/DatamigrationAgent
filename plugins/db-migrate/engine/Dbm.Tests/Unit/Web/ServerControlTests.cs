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
        var deadPid = await RunToExitAsync();

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
            Assert.Contains("exited", ex.Message);
            Assert.True(File.Exists(tw.Ws.ServerJsonPath));
        }
        finally
        {
            ServerControl.LockReleaseTimeout = savedLock;
            ServerControl.KillGraceTimeout = savedKill;
        }
    }

    /// <summary>Spawns a trivial, immediately-exiting process (cross-platform: cmd.exe on Windows, /bin/sh
    /// elsewhere) and returns its PID once it has exited — a PID guaranteed not to belong to any running process.</summary>
    private static async Task<int> RunToExitAsync()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        return process.Id;
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

    /// <summary>
    /// The ordered drain-wait test T1.7 fix round 4 was supposed to add but didn't: a previous server's lock is
    /// still held (draining), then released, and EnsureRunningAsync must proceed to spawn — exactly once, not
    /// zero (stuck) and not more than once (a duplicate spawn racing the first).
    /// </summary>
    [Fact]
    public async Task EnsureRunningAsync_waits_for_a_draining_lock_to_release_then_spawns_exactly_once()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(300);
            heldLock.Dispose();
        });

        WebTestServer? spawned = null;
        var spawnCount = 0;
        ServerControl.SpawnOverrides[tw.Ws.Root] = async (ws, _) =>
        {
            Interlocked.Increment(ref spawnCount);
            spawned = await WebTestServer.StartAsync(ws, FakeServices.Factory());
        };
        try
        {
            var info = await ServerControl.EnsureRunningAsync(tw.Ws);

            await release;
            Assert.Equal(1, spawnCount);
            Assert.Equal(spawned!.Info.Pid, info.Pid);
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
            if (spawned is not null) await spawned.DisposeAsync();
        }
    }

    /// <summary>Nothing in the default suite exercised the "becomes healthy partway through the wait" path before
    /// this — reverting WaitForHealthyOwnerAsync to a single check (no retry loop) kept every prior test green.</summary>
    [Fact]
    public async Task WaitForHealthyOwnerAsync_finds_an_owner_that_becomes_healthy_partway_through_the_wait()
    {
        using var tw = new TestWorkspace();
        WebTestServer? server = null;
        var spawnLater = Task.Run(async () =>
        {
            await Task.Delay(300);
            server = await WebTestServer.StartAsync(tw.Ws);
        });
        try
        {
            var owner = await ServerControl.WaitForHealthyOwnerAsync(tw.Ws);

            await spawnLater;
            Assert.NotNull(owner);
            Assert.Equal(server!.Info.Pid, owner!.Pid);
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Regression test for T1.7 fix round 5, item 1: a stop that has to kill a hung process frees server.lock and
    /// deletes server.json from two independently-polling 200 ms loops, not atomically — so a caller must not
    /// read "lock free, server.json still there" as a crash the instant it observes the lock go free. Shortens
    /// ReconnectGraceWindow so the test doesn't need to wait out the real default.
    /// </summary>
    [Fact]
    public async Task WaitForCleanStopAsync_treats_a_lock_release_followed_promptly_by_server_json_deletion_as_stopped()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(new ServerInfo(1, 4321, "tok", DateTimeOffset.UtcNow)));
        var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var saved = ServerControl.ReconnectGraceWindow;
        ServerControl.ReconnectGraceWindow = TimeSpan.FromSeconds(2);
        try
        {
            var killThenCleanUp = Task.Run(async () =>
            {
                await Task.Delay(300);
                heldLock.Dispose();   // simulates the kill freeing the lock
                await Task.Delay(100);
                File.Delete(tw.Ws.ServerJsonPath);   // simulates StopAsync's own slightly-delayed cleanup
            });

            var stopped = await ServerControl.WaitForCleanStopAsync(tw.Ws, 4321, TimeSpan.FromSeconds(3));

            await killThenCleanUp;
            Assert.True(stopped);
        }
        finally
        {
            ServerControl.ReconnectGraceWindow = saved;
        }
    }

    /// <summary>The other half of the same regression: a crash frees the lock too, but nobody ever deletes
    /// server.json, so this must still resolve to "not stopped" (reconnect) once the grace window passes.</summary>
    [Fact]
    public async Task WaitForCleanStopAsync_treats_a_lock_release_with_server_json_never_removed_as_a_crash()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(new ServerInfo(1, 4321, "tok", DateTimeOffset.UtcNow)));
        var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var saved = ServerControl.ReconnectGraceWindow;
        ServerControl.ReconnectGraceWindow = TimeSpan.FromMilliseconds(500);
        try
        {
            var release = Task.Run(async () =>
            {
                await Task.Delay(200);
                heldLock.Dispose();
            });

            var stopped = await ServerControl.WaitForCleanStopAsync(tw.Ws, 4321, TimeSpan.FromSeconds(3));

            await release;
            Assert.False(stopped);
            Assert.True(File.Exists(tw.Ws.ServerJsonPath));
        }
        finally
        {
            ServerControl.ReconnectGraceWindow = saved;
        }
    }
}
