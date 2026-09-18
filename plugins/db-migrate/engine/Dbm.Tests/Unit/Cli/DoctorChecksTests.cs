using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Cli;
using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.Crypto;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class DoctorChecksTests
{
    private sealed class FakeProtector(Func<string, string> protect, Func<string, string> unprotect) : ISecretProtector
    {
        public string Protect(string plaintext) => protect(plaintext);
        public string Unprotect(string protectedText) => unprotect(protectedText);
    }

    /// <summary>Builds &lt;root&gt;/plugin/{.claude-plugin/plugin.json, engine/dist/VERSION} and returns the dist path.</summary>
    private static string MakeDist(string root, string? pluginVersion, string? distVersion)
    {
        var plugin = Path.Combine(root, "plugin");
        var dist = Directory.CreateDirectory(Path.Combine(plugin, "engine", "dist")).FullName;
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        if (pluginVersion is not null)
        {
            File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"),
                "{\n  \"name\": \"db-migrate\",\n  \"version\": \"" + pluginVersion + "\"\n}\n");
        }
        if (distVersion is not null) File.WriteAllText(Path.Combine(dist, "VERSION"), distVersion);
        return dist + Path.DirectorySeparatorChar;   // the shape of AppContext.BaseDirectory
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Protector_round_trips_for_the_current_user()
    {
        var check = DoctorChecks.Protector();

        Assert.Equal("protector", check.Name);
        Assert.True(check.Ok, check.Detail);
        Assert.Contains("round-trip", check.Detail);
    }

    [Fact]
    public void Protector_reports_the_reason_when_it_throws()
    {
        var check = DoctorChecks.Protector(() => throw new InvalidOperationException("no key store"));

        Assert.False(check.Ok, "protector check passed although the protector threw: " + check.Detail);
        Assert.Contains("no key store", check.Detail);
    }

    [Fact]
    public void Protector_fails_when_nothing_is_encrypted()
    {
        var check = DoctorChecks.Protector(() => new FakeProtector(p => p, p => p));

        Assert.False(check.Ok, "protector check passed over a protector that stores the plaintext: " + check.Detail);
        Assert.Contains("plaintext", check.Detail);
    }

    [Fact]
    public void Protector_fails_when_decryption_returns_other_text()
    {
        var check = DoctorChecks.Protector(() => new FakeProtector(
            p => "fake:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p)), _ => "something else"));

        Assert.False(check.Ok, "protector check passed although decryption returned different text: " + check.Detail);
        Assert.Contains("different text", check.Detail);
    }

    [Fact]
    public async Task Server_check_says_so_without_a_project()
    {
        using var tw = new TestWorkspace();

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.Equal("server", check.Name);
        Assert.True(check.Ok);
        Assert.Contains("no project", check.Detail);
    }

    [Fact]
    public async Task Server_check_says_not_running_for_a_project_without_server_json()
    {
        using var tw = new TestWorkspace();
        using (tw.OpenServices()) { }

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains("not running", check.Detail);
    }

    [Fact]
    public async Task Server_check_finds_a_running_server()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains($"running on {server.Info.BaseUrl}", check.Detail);
        Assert.DoesNotContain(server.Info.Token, check.Detail);
    }

    [Fact]
    public async Task Server_check_reports_a_stale_server_json()
    {
        using var tw = new TestWorkspace();
        using (tw.OpenServices()) { }
        File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(new ServerInfo(FreePort(), 999_999, "tok", Clock.Now())));

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains("stale server.json", check.Detail);
        Assert.DoesNotContain("tok", check.Detail);
    }

    [Fact]
    public void Dist_check_passes_when_the_versions_match()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.3.0", "0.3.0"));

        Assert.Equal("dist", check.Name);
        Assert.True(check.Ok);
        Assert.Contains("0.3.0 matches", check.Detail);
    }

    [Fact]
    public void Dist_check_fails_on_a_version_mismatch()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.4.0", "0.3.0"));

        Assert.False(check.Ok, "dist check passed over a stale VERSION: " + check.Detail);
        Assert.Contains("0.3.0", check.Detail);
        Assert.Contains("0.4.0", check.Detail);
    }

    [Fact]
    public void Dist_check_tells_a_user_without_an_sdk_to_update_the_plugin()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.4.0", "0.3.0"));

        // A stale dist is only ever seen by a user with no .NET SDK (with one, the launcher rebuilds first),
        // so the remedy must work without an SDK and without shell-specific syntax.
        Assert.Contains("claude plugin update db-migrate@db-migrate", check.Detail);
        Assert.Contains("restart Claude Code", check.Detail);
        Assert.DoesNotContain("DBM_REBUILD=", check.Detail);
    }

    [Fact]
    public void Dist_check_cannot_pass_without_plugin_json()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, null, "0.3.0"));

        Assert.False(check.Ok, "dist check passed although it could not run (no plugin.json): " + check.Detail);
        Assert.Contains("cannot check", check.Detail);
        Assert.Contains("plugin.json", check.Detail);
    }

    [Fact]
    public void Dist_check_fails_on_an_unreadable_plugin_json()
    {
        using var tw = new TestWorkspace();
        var dist = MakeDist(tw.Root, "0.3.0", "0.3.0");
        File.WriteAllText(Path.Combine(tw.Root, "plugin", ".claude-plugin", "plugin.json"), "{ not json");

        var check = DoctorChecks.Dist(dist);

        Assert.False(check.Ok, "dist check passed although plugin.json could not be parsed: " + check.Detail);
        Assert.Contains("unreadable", check.Detail);
    }

    [Fact]
    public void Dist_check_fails_when_VERSION_cannot_be_read()
    {
        using var tw = new TestWorkspace();
        var dist = MakeDist(tw.Root, "0.3.0", null);
        Directory.CreateDirectory(Path.Combine(dist, "VERSION"));   // reading a directory throws on every OS

        var check = DoctorChecks.Dist(dist);

        Assert.False(check.Ok, "dist check passed although VERSION could not be read: " + check.Detail);
        Assert.Contains("cannot check", check.Detail);
        Assert.Contains("VERSION", check.Detail);
    }

    [Fact]
    public void Quick_checks_the_dist_for_the_session_start_hook()
    {
        using var tw = new TestWorkspace();

        var checks = DoctorChecks.Quick(MakeDist(tw.Root, "0.4.0", "0.3.0"));

        Assert.Equal(new[] { "protector", "dist" }, checks.Select(c => c.Name));
        Assert.False(checks.Single(c => c.Name == "dist").Ok, "doctor --quiet passed over a stale dist: Quick did not run the dist check");
    }

    [Fact]
    public void Dist_check_fails_without_a_version_file()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.4.0", null));

        Assert.False(check.Ok, "dist check passed without a VERSION file: " + check.Detail);
        Assert.Contains("VERSION", check.Detail);
    }

    [Fact]
    public void Dist_check_is_silent_for_a_development_build()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(Path.Combine(tw.Root, "bin", "Debug", "net8.0") + Path.DirectorySeparatorChar);

        Assert.True(check.Ok);
        Assert.Contains("development build", check.Detail);
    }

    [Fact]
    public async Task Doctor_reports_the_machine_and_project_checks()
    {
        var stdout = new StringWriter();

        var exit = await CliApp.RunAsync(["doctor"], stdout, new StringWriter());

        Assert.Equal(0, exit);
        var json = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(new[] { "runtime", "aspnetcore", "sqlclient", "sqlite", "home", "protector", "server", "dist" },
            json["checks"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
    }
}
