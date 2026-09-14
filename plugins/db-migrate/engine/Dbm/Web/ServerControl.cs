using System.Collections.Concurrent;
using System.Diagnostics;
using Dbm.Core;

namespace Dbm.Web;

/// <summary>Finds, starts (detached) and stops the per-workspace server.</summary>
public static class ServerControl
{
    /// <summary>How long EnsureRunningAsync waits for a spawned server (or a healthy owner it did not spawn itself)
    /// to answer. Mutable (not readonly) so tests can shorten it — matches LockReleaseTimeout/KillGraceTimeout.</summary>
    public static TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

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

        // A previous server for this workspace might still be draining (server.json already gone, but server.lock
        // still held — server.json is deleted before the drain, not after) or about to finish starting (lock held,
        // server.json not written yet). But a *healthy* server holds the lock for its entire life, so waiting for
        // the lock to release is not a valid proxy for "no server is running" — a live server that merely misses
        // one health check (busy with a heavy job) must still be found here, not mistaken for one that's gone. Poll
        // both signals together and return the moment a healthy answer arrives, however the lock looks.
        var (healthy, lockFree) = await WaitUntilHealthyOrLockFreeAsync(ws, LockReleaseTimeout, ct);
        if (healthy is not null) return healthy;
        if (!lockFree)
            throw new InvalidOperationException(
                $"a dbm server for this workspace is still starting or shutting down; see {ws.ServerLogPath}.");

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

    /// <summary>
    /// Polls until either a healthy server answers (returned immediately, regardless of the lock) or
    /// <paramref name="timeout"/> passes with <c>server.lock</c> still held. Always re-checks health one final
    /// time right at the deadline — before deciding the lock is the answer — so a server that becomes healthy (or
    /// a lock that frees) on the very last poll is never missed.
    /// </summary>
    private static async Task<(ServerInfo? Healthy, bool LockFree)> WaitUntilHealthyOrLockFreeAsync(
        Workspace ws, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var info = ReadInfo(ws);
            if (info is not null && await IsAliveAsync(info, ct)) return (info, false);
            if (ProbeLockFree(ws)) return (null, true);
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(200, ct);
        }

        var final = ReadInfo(ws);
        if (final is not null && await IsAliveAsync(final, ct)) return (final, false);
        return (null, ProbeLockFree(ws));
    }

    /// <summary>How long to wait for server.lock to be released after asking the server to shut down.</summary>
    internal static TimeSpan LockReleaseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for server.lock to be released after killing an identified server process.</summary>
    internal static TimeSpan KillGraceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// POST /api/shutdown, then wait for <c>server.lock</c> to be released — the process has actually finished
    /// <see cref="WebHost.RunAsync"/>, including draining its job/event loops — rather than for server.json to
    /// disappear, which a killed process never gets the chance to delete itself, and a starting process may not
    /// have written yet. The lock is the sole authority on "is anything still running here": every path below,
    /// whatever it does with the recorded PID, ends in one final lock probe before it is allowed to declare
    /// success, and <c>server.json</c> is deleted only at that single point once the probe confirms it. If the
    /// lock is still held and nothing here can identify or safely stop whatever holds it, this throws naming the
    /// PID rather than silently reporting success. Returns whether anything was actually here to stop — probed
    /// (server.json present, or the lock already held) before the shutdown POST, not re-derived by the caller from
    /// server.json's state afterwards, which reflects the drain having started rather than whether one was running.
    /// </summary>
    public static async Task<bool> StopAsync(Workspace ws)
    {
        var info = ReadInfo(ws);
        var wasRunning = info is not null || !ProbeLockFree(ws);

        if (info is not null)
        {
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
        }

        if (await WaitForLockReleaseAsync(ws, LockReleaseTimeout))
        {
            DeleteStaleInfo(ws, info);
            return wasRunning;
        }

        // The lock is still held. Without a recorded PID there is nothing to identify or kill, but a held lock
        // means a server genuinely is still running (starting up, or still draining) — never report success.
        if (info is null)
            throw new InvalidOperationException(
                $"a dbm server for this workspace is still starting or shutting down; see {ws.ServerLogPath}.");

        Process? process;
        try
        {
            process = Process.GetProcessById(info.Pid);
        }
        catch (ArgumentException)
        {
            process = null;   // the recorded PID has exited; something else may now hold the lock
        }

        var killed = false;
        if (process is not null)
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId)
                {
                    bool identifiedAsServer;
                    try
                    {
                        identifiedAsServer = IsSameServer(process) && StartTimeMatches(process, info);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"dbm server PID {info.Pid} is still running and could not be identified ({ex.Message}); leaving it running.");
                    }

                    if (identifiedAsServer)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                            killed = true;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"dbm server PID {info.Pid} is still running and could not be stopped (elevated?): {ex.Message}");
                        }
                    }
                    // else: the recorded PID is positively a different process (crashed and reused) — nothing to
                    // kill; fall through to the final lock probe below rather than assuming that makes it safe to
                    // declare done.
                }
            }
        }

        // Whether we killed a confirmed match, found the recorded PID already gone, or found it belongs to an
        // unrelated process: the lock, not the PID check, is what decides "stopped". If it is still held, something
        // is still running and this must not report success.
        if (!await WaitForLockReleaseAsync(ws, KillGraceTimeout))
        {
            throw killed
                ? new InvalidOperationException($"dbm server PID {info.Pid} is still running and could not be stopped.")
                : new InvalidOperationException(
                    $"PID {info.Pid} is not the dbm server for this workspace, and server.lock is still held by an " +
                    $"unidentified process; see {ws.ServerLogPath}.");
        }

        DeleteStaleInfo(ws, info);
        return wasRunning;
    }

    /// <summary>Deletes server.json only once a lock probe has confirmed the server is gone, and only if it still
    /// names the process this call was tracking (not one that started in the meantime).</summary>
    private static void DeleteStaleInfo(Workspace ws, ServerInfo? info)
    {
        if (info is null) return;
        if (ReadInfo(ws) is { } current && current.Pid == info.Pid) File.Delete(ws.ServerJsonPath);
    }

    /// <summary>True once server.lock can be opened exclusively — the definitive "the server process has fully
    /// exited RunAsync" signal, unlike server.json's disappearance which a killed process never gets to do itself.
    /// A missing workspace directory is checked explicitly and read as unambiguously "free": WebHost.RunAsync
    /// always creates it before acquiring the lock, so there is nothing to hold it if it doesn't exist. That is
    /// a narrower claim than "the open failed with DirectoryNotFoundException" — the same exception also fires for
    /// a missing *ancestor* (an unmounted drive mid-operation), which is not this carve-out and must still surface.
    /// Beyond that, only a genuine sharing/lock conflict means "held"; any other error (disk error, access denied)
    /// surfaces instead of being read as "still running".</summary>
    internal static bool ProbeLockFree(Workspace ws)
    {
        if (!Directory.Exists(ws.Dir)) return true;
        try
        {
            using var probe = new FileStream(ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException ex) when (WebHost.IsLockHeldByAnotherProcess(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Waits up to <see cref="StartTimeout"/> for server.json to name a healthy server — used by callers that
    /// couldn't take server.lock themselves (its holder is mid-start: DI build and Kestrel bind happen before
    /// server.json is written) to distinguish "someone else is legitimately starting" from "the lock is stuck".
    /// </summary>
    internal static async Task<ServerInfo?> WaitForHealthyOwnerAsync(Workspace ws, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            var owner = ReadInfo(ws);
            if (owner is not null && await IsAliveAsync(owner, ct)) return owner;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(200, ct);
        }
    }

    private static async Task<bool> WaitForLockReleaseAsync(Workspace ws, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (ProbeLockFree(ws)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(200, ct);
        }
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
    /// running for a little while — startup latency only ever pushes <see cref="ServerInfo.StartedAt"/> later
    /// relative to the process's real OS start time, so it can only help a genuine match). If the PID was reused
    /// for an unrelated process after the real server crashed, that process necessarily started later than the
    /// stale <c>server.json</c> it happens to match on PID alone. The tolerance exists only to absorb clock-source
    /// skew between <see cref="Process.StartTime"/> and <see cref="Clock"/>; it is deliberately small — false
    /// negatives are safe (the caller reports the PID and refuses to act), false positives are not (they authorise
    /// killing an unrelated process tree).
    /// </summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Throws if <see cref="Process.StartTime"/> can't be read (e.g. access denied for an elevated
    /// process) — the caller must treat that as "can't confirm identity", not as a silent non-match.</summary>
    internal static bool StartTimeMatches(Process process, ServerInfo info) =>
        process.StartTime.ToUniversalTime() <= info.StartedAt.UtcDateTime + StartTimeTolerance;
}
