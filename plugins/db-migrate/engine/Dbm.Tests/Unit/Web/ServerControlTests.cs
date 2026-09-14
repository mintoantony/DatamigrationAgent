using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dbm.Core;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class ServerControlTests
{
    [Fact]
    public void Start_time_matches_allows_startup_latency_but_rejects_a_pid_reused_after_startedAt()
    {
        var current = Process.GetCurrentProcess();
        var recordedShortlyAfterStart = new ServerInfo(1, current.Id, "t", current.StartTime.ToUniversalTime().AddSeconds(2));
        var recordedLongBeforeStart = new ServerInfo(1, current.Id, "t", current.StartTime.ToUniversalTime().AddSeconds(-60));

        Assert.True(ServerControl.StartTimeMatches(current, recordedShortlyAfterStart));
        Assert.False(ServerControl.StartTimeMatches(current, recordedLongBeforeStart));
    }

    [Fact]
    public async Task StopAsync_does_not_kill_a_live_process_whose_identity_does_not_match_the_recorded_server()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();

        // `timeout` needs a real console and exits at once under the test host's redirected stdio; `ping` does not.
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var fakeInfo = new ServerInfo(FreeLoopbackPort(), other.Id, "tok", DateTimeOffset.UnixEpoch);
            File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(fakeInfo));

            await ServerControl.StopAsync(tw.Ws);

            Assert.False(other.HasExited);
            Assert.False(File.Exists(tw.Ws.ServerJsonPath));
        }
        finally
        {
            if (!other.HasExited) other.Kill();
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
}
