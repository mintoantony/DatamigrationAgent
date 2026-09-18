using System.Reflection;
using System.Runtime.InteropServices;
using Dbm.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Dbm.Cli.Commands;

public sealed class DoctorCommand : ICommand
{
    public sealed record Check(string Name, bool Ok, string Detail);

    public string Name => "doctor";
    public string Help => "Check runtime, drivers and user profile (--quiet: silent when healthy; --rebuild: launcher rebuilds first)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        // --rebuild is handled by bin/dbm(.cmd) before this process starts; accepted here so it is not an error.
        var quiet = args.Flag("quiet");
        // T6.2: --quiet runs on every session start, so it skips the check that touches the network.
        var checks = RunChecks()
            .Concat(quiet
                ? DoctorChecks.Quick(AppContext.BaseDirectory)
                : await DoctorChecks.AllAsync(ctx.Workspace(), AppContext.BaseDirectory, CancellationToken.None))
            .ToList();
        var ok = checks.All(c => c.Ok);
        if (quiet)
        {
            var (exit, lines) = QuietReport(checks);
            foreach (var line in lines) ctx.Out.WriteLine(line);
            return exit;
        }
        if (!ok) return Output.Write(ctx, new { ok, checks }, 1);
        return Output.Ok(ctx, new { ok, checks });
    }

    /// <summary>What `doctor --quiet` prints and returns: nothing and 0 when every check passed, otherwise one line per
    /// failing check and 1. The SessionStart hook passes these lines to Claude as context.</summary>
    internal static (int Exit, IReadOnlyList<string> Lines) QuietReport(IReadOnlyList<Check> checks)
    {
        var failing = checks.Where(c => !c.Ok).Select(c => $"dbm doctor: {c.Name}: {c.Detail}").ToList();
        return (failing.Count == 0 ? 0 : 1, failing);
    }

    public static IReadOnlyList<Check> RunChecks() =>
    [
        Probe("runtime", () => (Environment.Version.Major >= 8, RuntimeInformation.FrameworkDescription)),
        Probe("aspnetcore", () =>
        {
            var v = VersionOf(typeof(WebApplication).Assembly);
            return (Major(v) >= 8, v);
        }),
        Probe("sqlclient", () => (true, VersionOf(typeof(SqlConnection).Assembly))),
        Probe("sqlite", () =>
        {
            using var c = new SqliteConnection("Data Source=:memory:");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "select sqlite_version()";
            return (true, Convert.ToString(cmd.ExecuteScalar()) ?? "?");
        }),
        Probe("home", () =>
        {
            var dir = UserHome.Dir;
            var probe = Path.Combine(dir, $".doctor-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, dir);
        }),
    ];

    private static Check Probe(string name, Func<(bool Ok, string Detail)> check)
    {
        try
        {
            var (ok, detail) = check();
            return new Check(name, ok, detail);
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string VersionOf(Assembly assembly) =>
        (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? assembly.GetName().Version?.ToString()
         ?? "unknown").Split('+')[0];

    private static int Major(string version) =>
        int.TryParse(version.Split('.')[0], out var m) ? m : 0;
}
