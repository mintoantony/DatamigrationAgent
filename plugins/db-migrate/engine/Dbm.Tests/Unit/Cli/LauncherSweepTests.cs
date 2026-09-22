using System.Diagnostics;
using System.Text;

namespace Dbm.Tests.Unit.Cli;

/// <summary>
/// Open item 39: a publish that crashed leaves engine/dist.tmp.&lt;id&gt; (~30 MB) behind, and a swap that crashed its
/// dist.old.&lt;id&gt;; the next build of either launcher must sweep the ones older than the lock's stale age (15 minutes).
/// Each test copies the real bin/dbm or bin/dbm.cmd into a throwaway plugin tree with an empty engine/dist/Dbm.dll and no
/// Dbm.csproj, then forces a build: the launcher sweeps, the real `dotnet publish` fails at once on the missing project,
/// and the launcher gives up. Nothing is built and the repository's own engine/ is never touched.
/// </summary>
[Trait("Category", "Launcher")]
public sealed class LauncherSweepTests : IDisposable
{
    private static readonly DateTime Aged = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbm-launcher-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A fact that needs Git Bash's (or the system's) sh: skipped, with the reason, when there is none.</summary>
    public sealed class ShFactAttribute : FactAttribute
    {
        public ShFactAttribute() { if (Sh is null) Skip = "No sh found (Git for Windows' usr\\bin\\sh.exe, or /bin/sh)."; }
    }

    /// <summary>A fact for the cmd launcher: Windows only.</summary>
    public sealed class CmdFactAttribute : FactAttribute
    {
        public CmdFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "bin/dbm.cmd runs on Windows only."; }
    }

    private static readonly string? Sh = FindSh();

    private static string? FindSh()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/sh") ? "/bin/sh" : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "sh.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var git = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "sh.exe");
        return File.Exists(git) ? git : null;
    }

    private static string RepoBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plugins", "db-migrate", "bin", "dbm")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "plugins", "db-migrate", "bin");
    }

    private string Engine => Path.Combine(_root, "engine");

    /// <summary>The throwaway plugin tree, with an aged and a fresh dist.tmp/dist.old pair and, if asked, an engine/dist.</summary>
    private void MakePlugin(bool withDist)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        foreach (var name in new[] { "dbm", "dbm.cmd" }) File.Copy(Path.Combine(RepoBin(), name), Path.Combine(bin, name));
        Directory.CreateDirectory(Path.Combine(_root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(_root, ".claude-plugin", "plugin.json"), "{\n  \"name\": \"db-migrate\",\n  \"version\": \"9.9.9\"\n}\n");
        Directory.CreateDirectory(Path.Combine(Engine, "Dbm", "wwwroot"));
        if (withDist)
        {
            var dist = Directory.CreateDirectory(Path.Combine(Engine, "dist")).FullName;
            File.WriteAllBytes(Path.Combine(dist, "Dbm.dll"), []);
            File.WriteAllText(Path.Combine(dist, "VERSION"), "9.9.9");
        }
        foreach (var (name, time) in new[]
                 {
                     ("dist.tmp.aged1", Aged), ("dist.old.aged1", Aged),
                     ("dist.tmp.fresh1", DateTime.UtcNow), ("dist.old.fresh1", DateTime.UtcNow),
                 })
        {
            var d = Directory.CreateDirectory(Path.Combine(Engine, name)).FullName;
            File.WriteAllText(Path.Combine(d, "Dbm.dll"), "leftover");
            File.SetLastWriteTimeUtc(Path.Combine(d, "Dbm.dll"), time);
            Directory.SetLastWriteTimeUtc(d, time);
        }
    }

    private bool Left(string name) => Directory.Exists(Path.Combine(Engine, name));

    /// <summary>Runs the launcher with `version`, returning its stderr. DBM_REBUILD=1 forces the build when dist exists.</summary>
    private async Task<string> RunAsync(bool sh)
    {
        ProcessStartInfo psi;
        if (sh)
        {
            psi = new ProcessStartInfo(Sh!);
            psi.ArgumentList.Add(Path.Combine(_root, "bin", "dbm").Replace('\\', '/'));
            psi.ArgumentList.Add("version");
            // Git Bash puts its /usr/bin first; without it Windows' find.exe would answer the launcher's find.
            psi.Environment["PATH"] = Path.GetDirectoryName(Sh!) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        else
        {
            psi = new ProcessStartInfo("cmd.exe", "/d /c \"\"" + Path.Combine(_root, "bin", "dbm.cmd") + "\" version\"");
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.Environment["DBM_REBUILD"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("the launcher did not finish within 3 minutes: " + await stderr);
        }
        await stdout;
        return await stderr;
    }

    private static int Count(string text, string line)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(line, i, StringComparison.Ordinal)) >= 0) { n++; i += line.Length; }
        return n;
    }

    private void AssertSwept(string who, string err, char sep, bool distPresent)
    {
        var sb = new StringBuilder();
        void Check(bool ok, string what) { if (!ok) sb.Append(who).Append(": ").Append(what).Append('\n'); }

        Check(!Left("dist.tmp.aged1"), "left engine/dist.tmp.aged1 (2020) behind: a crashed publish's leftover was not swept");
        Check(Count(err, "dbm: removing engine" + sep + "dist.tmp.aged1, left behind by a build that crashed.") == 1,
            "did not announce the removal of dist.tmp.aged1 exactly once");
        Check(Left("dist.tmp.fresh1"), "removed dist.tmp.fresh1, which is younger than the lock's stale age (a live build's)");
        Check(Left("dist.old.fresh1"), "removed dist.old.fresh1, which is younger than the lock's stale age");
        if (distPresent)
        {
            Check(!Left("dist.old.aged1"), "left engine/dist.old.aged1 (2020) behind although engine/dist is in place");
            Check(Count(err, "dbm: removing engine" + sep + "dist.old.aged1, left behind by a build that crashed.") == 1,
                "did not announce the removal of dist.old.aged1 exactly once");
        }
        else
        {
            Check(Left("dist.old.aged1"),
                "removed dist.old.aged1 while engine/dist is missing: it may be the previous build a failed swap-back named");
        }
        Assert.True(sb.Length == 0, sb + "stderr was:\n" + err);
    }

    [ShFact]
    public async Task Sh_launcher_sweeps_aged_dist_tmp_and_dist_old_and_keeps_fresh_ones()
    {
        MakePlugin(withDist: true);
        var err = await RunAsync(sh: true);
        AssertSwept("bin/dbm (sh)", err, '/', distPresent: true);
    }

    [ShFact]
    public async Task Sh_launcher_keeps_an_aged_dist_old_while_engine_dist_is_missing()
    {
        MakePlugin(withDist: false);
        var err = await RunAsync(sh: true);
        AssertSwept("bin/dbm (sh)", err, '/', distPresent: false);
    }

    [CmdFact]
    public async Task Cmd_launcher_sweeps_aged_dist_tmp_and_dist_old_and_keeps_fresh_ones()
    {
        MakePlugin(withDist: true);
        var err = await RunAsync(sh: false);
        AssertSwept("bin/dbm.cmd", err, '\\', distPresent: true);
    }

    [CmdFact]
    public async Task Cmd_launcher_keeps_an_aged_dist_old_while_engine_dist_is_missing()
    {
        MakePlugin(withDist: false);
        var err = await RunAsync(sh: false);
        AssertSwept("bin/dbm.cmd", err, '\\', distPresent: false);
    }
}
