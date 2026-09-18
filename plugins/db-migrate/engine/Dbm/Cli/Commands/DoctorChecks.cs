using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Crypto;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>
/// The project- and installation-level checks appended to <see cref="DoctorCommand.RunChecks"/> (T6.2):
/// can this user encrypt and decrypt, is this project's server healthy, and does engine/dist match plugin.json.
/// </summary>
public static class DoctorChecks
{
    public const string ProbeText = "dbm-doctor-probe";

    /// <summary>protector + dist only: no network calls, so `doctor --quiet` stays fast for the SessionStart hook.</summary>
    public static IReadOnlyList<DoctorCommand.Check> Quick(string baseDirectory) => [Protector(), Dist(baseDirectory)];

    /// <summary>The quick checks plus this workspace's server status.</summary>
    public static async Task<IReadOnlyList<DoctorCommand.Check>> AllAsync(Workspace ws, string baseDirectory, CancellationToken ct) =>
        [Protector(), await ServerAsync(ws, ct), Dist(baseDirectory)];

    /// <summary>Encrypts and decrypts a probe value with this user's protector (DPAPI on Windows, AES-GCM key file elsewhere).</summary>
    public static DoctorCommand.Check Protector(Func<ISecretProtector>? factory = null)
    {
        try
        {
            var protector = (factory ?? SecretProtector.ForCurrentUser)();
            var sealedText = protector.Protect(ProbeText);
            if (sealedText.Contains(ProbeText, StringComparison.Ordinal))
                return new("protector", false, "the protector returned the plaintext: connection strings would not be encrypted");
            if (protector.Unprotect(sealedText) != ProbeText)
                return new("protector", false, $"decrypting a freshly encrypted value returned different text. {Hint()}");
            var colon = sealedText.IndexOf(':');
            return new("protector", true, $"{(colon > 0 ? sealedText[..colon] : "protector")} round-trip");
        }
        catch (Exception ex)
        {
            return new("protector", false, $"{ex.GetType().Name}: {ex.Message}. {Hint()}");
        }
    }

    /// <summary>Whether this workspace's server is running; never a failure, because it is started on demand.</summary>
    public static async Task<DoctorCommand.Check> ServerAsync(Workspace ws, CancellationToken ct)
    {
        if (!ws.Exists) return new("server", true, $"no project in {ws.Root}");
        var info = ServerControl.ReadInfo(ws);
        if (info is null) return new("server", true, "not running (starts on demand)");
        return await ServerControl.IsAliveAsync(info, ct)
            ? new("server", true, $"running on {info.BaseUrl} (pid {info.Pid})")
            : new("server", true, $"stale server.json: pid {info.Pid} on port {info.Port} does not answer; it restarts on demand");
    }

    /// <summary>engine/dist/VERSION against .claude-plugin/plugin.json; skipped (ok) when not running from engine/dist.</summary>
    public static DoctorCommand.Check Dist(string baseDirectory)
    {
        var dist = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)));
        if (!string.Equals(dist.Name, "dist", StringComparison.OrdinalIgnoreCase) || dist.Parent?.Parent is null)
            return new("dist", true, "development build (not engine/dist)");

        // A check that could not run is never ok (T6.2 review F6).
        var manifest = Path.Combine(dist.Parent.Parent.FullName, ".claude-plugin", "plugin.json");
        if (!File.Exists(manifest)) return new("dist", false, $"cannot check: no plugin.json at {manifest}");

        string? pluginVersion;
        try
        {
            pluginVersion = JsonNode.Parse(File.ReadAllText(manifest))?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new("dist", false, $"cannot check: plugin.json is unreadable: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(pluginVersion)) return new("dist", false, "cannot check: plugin.json has no version");

        var versionFile = Path.Combine(dist.FullName, "VERSION");
        string distVersion;
        try
        {
            distVersion = File.ReadAllText(versionFile).Trim();
        }
        catch (FileNotFoundException)
        {
            distVersion = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("dist", false, $"cannot check: engine/dist/VERSION is unreadable: {ex.Message}");
        }
        if (distVersion.Length == 0)
            return new("dist", false, $"engine/dist has no VERSION file (plugin.json is {pluginVersion}). {Remedy}");
        return distVersion == pluginVersion
            ? new("dist", true, $"engine/dist VERSION {distVersion} matches plugin.json (a source change at the same version is not detected)")
            : new("dist", false, $"engine/dist is {distVersion} but plugin.json is {pluginVersion}. {Remedy}");
    }

    /// <summary>A stale dist reaches doctor only when no .NET SDK is installed (with one, the launcher rebuilds first),
    /// so the first remedy must work without an SDK, and none may use shell-specific syntax (Ruling 169).</summary>
    private const string Remedy =
        "Update the plugin: claude plugin update db-migrate@db-migrate, then restart Claude Code. " +
        "With the .NET 8 SDK installed the launcher rebuilds engine/dist by itself.";

    private static string Hint() => OperatingSystem.IsWindows()
        ? "DPAPI needs an interactive Windows user profile."
        : $"Check that {Path.Combine(UserHome.Dir, "key")} is 32 bytes and readable only by you (chmod 600).";
}
