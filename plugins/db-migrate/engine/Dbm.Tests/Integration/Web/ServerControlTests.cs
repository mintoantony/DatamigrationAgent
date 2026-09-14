using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dbm.Core;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Integration.Web;

/// <summary>Spawns a real detached `dotnet Dbm.dll serve` process from the test output folder.</summary>
[Trait("Category", "Integration")]
public class ServerControlTests
{
    /// <summary>
    /// Spawns a real, detached dbm server as an innocent bystander (so the process-name check alone can't be what
    /// saves it — it genuinely is named "dotnet"/"Dbm") and points a fabricated, stale server.json at its PID with a
    /// StartedAt far outside any tolerance for the bystander's real OS start time. A separate workspace's own
    /// server.lock is held (simulating "still running") so StopAsync is forced past the clean-shutdown path into
    /// the identify step — where, per T1.7 fix round 3, a positively-mismatched identity is no longer enough to
    /// declare success while the lock is still held: it must keep server.json and throw naming the PID.
    /// </summary>
    [Fact]
    public async Task StopAsync_never_kills_a_live_process_whose_identity_does_not_match_the_recorded_server()
    {
        using var bystanderWs = new TestWorkspace();
        using (FakeServices.Open(bystanderWs.Ws)) { }   // creates the project so `serve` has something to run
        var bodySucceeded = false;
        try
        {
            var bystander = await ServerControl.EnsureRunningAsync(bystanderWs.Ws);

            using var tw = new TestWorkspace();
            tw.Ws.EnsureCreated();
            var fakeInfo = new ServerInfo(FreeLoopbackPort(), bystander.Pid, "tok", DateTimeOffset.UnixEpoch);
            File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(fakeInfo));
            using var heldLock = new FileStream(tw.Ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ServerControl.StopAsync(tw.Ws));
            Assert.Contains(bystander.Pid.ToString(), ex.Message);

            Assert.True(await ServerControl.IsAliveAsync(bystander));
            Assert.True(File.Exists(tw.Ws.ServerJsonPath));
            bodySucceeded = true;
        }
        finally
        {
            // Best-effort cleanup, but only silent after a real test failure: if the assertions above already
            // passed, a failure here (a leaked detached server) must still be reported, not swallowed.
            try
            {
                await ServerControl.StopAsync(bystanderWs.Ws);
            }
            catch (Exception) when (!bodySucceeded)
            {
            }
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task EnsureRunning_spawns_one_detached_server_and_StopAsync_stops_it()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }   // creates the project

        var info = await ServerControl.EnsureRunningAsync(tw.Ws);
        try
        {
            Assert.NotEqual(Environment.ProcessId, info.Pid);
            Assert.True(await ServerControl.IsAliveAsync(info));
            var again = await ServerControl.EnsureRunningAsync(tw.Ws);
            Assert.Equal(info.Pid, again.Pid);
        }
        finally
        {
            await ServerControl.StopAsync(tw.Ws);
        }

        Assert.Null(ServerControl.ReadInfo(tw.Ws));
        Assert.False(await ServerControl.IsAliveAsync(info));
        Assert.Contains("server started", File.ReadAllText(tw.Ws.ServerLogPath));
    }

    /// <summary>
    /// The Claude Code Bash tool reads a command's stdout until EOF. If the detached server inherited the CLI's stdout
    /// pipe, `dbm ui` would never "finish". This runs the CLI exactly like a tool would and requires EOF within 30 s
    /// while the server it started is still alive.
    /// </summary>
    [Fact]
    public async Task A_cli_command_that_starts_the_server_releases_its_output_pipe()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }
        var (file, prefix) = SelfCommand.Get();
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in prefix.Concat(["ui", "--no-browser", "--workspace", tw.Root])) psi.ArgumentList.Add(a);
        psi.Environment["DBM_NO_BROWSER"] = "1";

        try
        {
            using var cli = Process.Start(psi)!;
            var stdout = cli.StandardOutput.ReadToEndAsync();
            var stderr = cli.StandardError.ReadToEndAsync();
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(30));   // EOF ⇒ no inherited pipe
            await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, cli.ExitCode);
            Assert.Contains("\"url\":\"http://127.0.0.1:", output);
            var info = ServerControl.ReadInfo(tw.Ws)!;
            Assert.True(await ServerControl.IsAliveAsync(info));
        }
        finally
        {
            await ServerControl.StopAsync(tw.Ws);
        }
    }
}
