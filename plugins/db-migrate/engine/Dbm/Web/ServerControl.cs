using System.Collections.Concurrent;
using System.Diagnostics;
using Dbm.Core;

namespace Dbm.Web;

/// <summary>Finds, starts (detached) and stops the per-workspace server.</summary>
public static class ServerControl
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Test seam, keyed by workspace root: replaces spawning a real process.</summary>
    internal static readonly ConcurrentDictionary<string, Func<Workspace, CancellationToken, Task>> SpawnOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    public static ServerInfo? ReadInfo(Workspace ws)
    {
        try
        {
            return File.Exists(ws.ServerJsonPath) ? Json.Deserialize<ServerInfo>(File.ReadAllText(ws.ServerJsonPath)) : null;
        }
        catch (Exception)
        {
            return null;   // half-written or corrupt: treat as not running
        }
    }

    /// <summary>GET /api/health with a 1.5 s timeout; true only if the answering process is the one in server.json.</summary>
    public static async Task<bool> IsAliveAsync(ServerInfo info, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.BaseUrl}/api/health");
            request.Headers.Add("X-Dbm-Token", info.Token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return body?["pid"]?.GetValue<int>() == info.Pid;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public static async Task<ServerInfo> EnsureRunningAsync(Workspace ws, CancellationToken ct = default)
    {
        var info = ReadInfo(ws);
        if (info is not null && await IsAliveAsync(info, ct)) return info;

        if (SpawnOverrides.TryGetValue(ws.Root, out var spawn)) await spawn(ws, ct);
        else Spawn(ws);

        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct);
            info = ReadInfo(ws);
            if (info is not null && await IsAliveAsync(info, ct)) return info;
        }
        throw new InvalidOperationException($"The dbm server did not start within {StartTimeout.TotalSeconds:0} s; see {ws.ServerLogPath}");
    }

    /// <summary>POST /api/shutdown, wait ≤ 5 s for the server to remove server.json; kill it if it does not.</summary>
    public static async Task StopAsync(Workspace ws)
    {
        var info = ReadInfo(ws);
        if (info is null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{info.BaseUrl}/api/shutdown");
            request.Headers.Add("X-Dbm-Token", info.Token);
            using var _ = await http.SendAsync(request);
        }
        catch (Exception)
        {
            // not answering: fall through to the kill below
        }

        for (var i = 0; i < 25; i++)
        {
            if (ReadInfo(ws) is not { } current || current.Pid != info.Pid) return;   // stopped cleanly (or replaced)
            await Task.Delay(200);
        }

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.Id != Environment.ProcessId && IsSameServer(process) && StartTimeMatches(process, info))
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // already exited
        }
        if (ReadInfo(ws) is { } stale && stale.Pid == info.Pid) File.Delete(ws.ServerJsonPath);
    }

    public static void OpenBrowser(string url)
    {
        if (Environment.GetEnvironmentVariable("DBM_NO_BROWSER") == "1" || string.IsNullOrEmpty(url)) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url)?.Dispose();
            else Process.Start("xdg-open", url)?.Dispose();
        }
        catch (Exception)
        {
            // no browser available (headless box): the URL is printed by the caller
        }
    }

    /// <summary>
    /// Starts `serve --workspace &lt;root&gt; --detached` so that it survives the calling command and — critically — does NOT
    /// inherit the caller's stdout/stderr pipes (otherwise a tool that reads the command's output until EOF, such as
    /// Claude Code's Bash tool, would hang until the server exits).
    /// </summary>
    private static void Spawn(Workspace ws)
    {
        var (file, prefix) = SelfCommand.Get();
        var args = prefix.Concat(["serve", "--workspace", ws.Root, "--detached"]).ToList();
        if (OperatingSystem.IsWindows())
        {
            // ShellExecute creates the process with bInheritHandles = FALSE and its own hidden console.
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = ws.Root,
                Arguments = string.Join(' ', args.Select(WindowsQuote)),
            };
            Process.Start(psi)?.Dispose();
        }
        else
        {
            // The shell exits at once; nohup'd child gets /dev/null for all three standard streams.
            var command = $"nohup {PosixQuote(file)} {string.Join(' ', args.Select(PosixQuote))} </dev/null >/dev/null 2>&1 &";
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = ws.Root };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            using var sh = Process.Start(psi);
            sh?.WaitForExit(5000);
        }
    }

    internal static string PosixQuote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
    internal static string WindowsQuote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static bool IsSameServer(Process process)
    {
        try
        {
            var name = process.ProcessName;
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.Equals("Dbm", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// A process can only be the one that wrote <paramref name="info"/> if the OS started it at or before
    /// <see cref="ServerInfo.StartedAt"/> (the server records that timestamp itself, after it has already been
    /// running for a little while). If the PID was reused for an unrelated process after the real server crashed,
    /// that process necessarily started later than the stale <c>server.json</c> it happens to match on PID alone.
    /// A tolerance absorbs clock-source differences between <see cref="Process.StartTime"/> and <see cref="Clock"/>
    /// plus how long process/service startup can legitimately take before <see cref="ServerInfo"/> is created.
    /// </summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(30);

    internal static bool StartTimeMatches(Process process, ServerInfo info)
    {
        try
        {
            return process.StartTime.ToUniversalTime() <= info.StartedAt.UtcDateTime + StartTimeTolerance;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
