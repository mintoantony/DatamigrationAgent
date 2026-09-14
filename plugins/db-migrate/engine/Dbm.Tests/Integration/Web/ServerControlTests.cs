using System.Diagnostics;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Integration.Web;

/// <summary>Spawns a real detached `dotnet Dbm.dll serve` process from the test output folder.</summary>
[Trait("Category", "Integration")]
public class ServerControlTests
{
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
