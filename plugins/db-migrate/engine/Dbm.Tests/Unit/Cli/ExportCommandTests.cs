using System.Net;
using System.Net.Sockets;
using System.Text;
using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class ExportCommandTests
{
    private const string AnalysisPayload = """{"summary":"demo export marker","findings":[]}""";

    /// <summary>Gives the workspace an analysis v1 to export.</summary>
    private static void SeedAnalysis(TestWorkspace tw)
    {
        using var services = tw.OpenServices();
        services.Artifacts.Add(PhaseName.Analysis, 1, AnalysisPayload, "script", "analysis v1");
        services.Phases.SetCurrentVersion(PhaseName.Analysis, 1);
        services.Phases.SetStatus(PhaseName.Analysis, PhaseStatus.AwaitingReview);
    }

    /// <summary>Runs <paramref name="body"/> with the CLI's "start the server" step answered by an in-process WebHost.</summary>
    private static async Task WithServerAsync(TestWorkspace tw, Func<Task> body)
    {
        WebTestServer? server = null;
        ServerControl.SpawnOverrides[tw.Ws.Root] = async (ws, _) => server = await WebTestServer.StartAsync(ws, w => DbmServices.Open(w));
        try
        {
            await body();
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
            if (server is not null) await server.DisposeAsync();
        }
    }

    /// <summary>Exit 0, or a failure that prints the JSON the CLI actually answered.</summary>
    private static void AssertOk(CliResult r) => Assert.True(r.Exit == 0, $"expected exit 0, got {r.Exit}: {r.Out}");

    /// <summary>The --out copy at <paramref name="copy"/> is byte-for-byte the saved export, and the CLI reported its real size.</summary>
    private static void AssertSameExport(byte[] saved, string copy, CliResult r, string site)
    {
        Assert.True(File.Exists(copy), $"{site} reported a path but wrote no file at {copy}.");
        var written = File.ReadAllBytes(copy);
        Assert.True(written.Length == saved.Length,
            $"{site} wrote {written.Length} bytes to {copy}; the saved export under .dbmigrate/exports has {saved.Length}.");
        Assert.True(written.AsSpan().SequenceEqual(saved), $"{site} wrote a file of the right size whose content differs from the saved export.");
        Assert.True(r.Json["bytes"]!.GetValue<long>() == written.Length,
            $"{site} reported bytes={r.Json["bytes"]} for a file of {written.Length} bytes.");
    }

    [Fact]
    public async Task Export_analysis_writes_a_standalone_html_into_the_exports_folder()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis");

            AssertOk(r);
            Assert.Equal("analysis", r.Json["what"]!.GetValue<string>());
            Assert.Equal("analysis-v1.html", r.Json["file"]!.GetValue<string>());
            var path = r.Json["path"]!.GetValue<string>();
            Assert.Equal(Path.Combine(tw.Ws.ExportsDir, "analysis-v1.html"), path);
            var html = await File.ReadAllTextAsync(path);
            Assert.Contains("DBM_EXPORT", html);
            Assert.Contains("demo export marker", html);
            Assert.Equal(new FileInfo(path).Length, r.Json["bytes"]!.GetValue<long>());
        });
    }

    [Fact]
    public async Task Export_copies_the_file_to_an_out_directory_or_an_out_file()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);
        var directory = Directory.CreateDirectory(Path.Combine(tw.Root, "out")).FullName;
        var file = Path.Combine(tw.Root, "reports", "analysis.html");
        var saved = Path.Combine(tw.Ws.ExportsDir, "analysis-v1.html");

        await WithServerAsync(tw, async () =>
        {
            // Each export stamps its own exportedAt, so each copy is compared with the saved copy of the same run.
            var toDirectory = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", directory);
            AssertOk(toDirectory);
            Assert.True(File.Exists(saved), "--out moved the export instead of copying it: the .dbmigrate/exports copy must survive.");
            var savedAfterDirectory = await File.ReadAllBytesAsync(saved);
            Assert.Contains("demo export marker", Encoding.UTF8.GetString(savedAfterDirectory));
            Assert.Equal(Path.Combine(directory, "analysis-v1.html"), toDirectory.Json["path"]!.GetValue<string>());
            AssertSameExport(savedAfterDirectory, Path.Combine(directory, "analysis-v1.html"), toDirectory, $"--out {directory}");

            var toFile = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", file);
            AssertOk(toFile);
            Assert.True(File.Exists(saved), "--out moved the export instead of copying it: the .dbmigrate/exports copy must survive.");
            var savedAfterFile = await File.ReadAllBytesAsync(saved);
            Assert.Equal(file, toFile.Json["path"]!.GetValue<string>());
            AssertSameExport(savedAfterFile, file, toFile, $"--out {file}");
        });
    }

    [Fact]
    public async Task Export_to_an_unwritable_out_names_the_path_and_keeps_the_saved_copy()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);
        var blocker = Path.Combine(tw.Root, "blocker");
        await File.WriteAllTextAsync(blocker, "a file where --out needs a directory");

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", Path.Combine(blocker, "analysis.html"));

            Assert.Equal(1, r.Exit);
            Assert.Equal("internal", r.Json["error"]!.GetValue<string>());   // Ruling 159
            Assert.Contains(blocker, r.Json["message"]!.GetValue<string>());
            var saved = Path.Combine(tw.Ws.ExportsDir, "analysis-v1.html");
            Assert.True(File.Exists(saved), $"a failed --out lost the saved export {saved}.");
            Assert.Contains("demo export marker", await File.ReadAllTextAsync(saved));
        });
    }

    [Fact]
    public async Task Export_reports_a_non_json_answer_as_server_error_naming_the_request()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);
        var html = "<!DOCTYPE html><html><head><title>502 Bad Gateway</title></head><body>" + new string('x', 300) + "</body></html>";
        await using var fake = HtmlAnsweringServer.Start(html);
        await File.WriteAllTextAsync(tw.Ws.ServerJsonPath,
            Json.Serialize(new ServerInfo(fake.Port, Environment.ProcessId, "fake-token", DateTimeOffset.UtcNow)));

        var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis");

        Assert.Equal(1, r.Exit);
        Assert.Equal("server_error", r.Json["error"]!.GetValue<string>());
        var message = r.Json["message"]!.GetValue<string>();
        Assert.Contains($"POST http://127.0.0.1:{fake.Port}/api/export/analysis", message);
        Assert.Contains("HTTP 502", message);
        Assert.Contains("<!DOCTYPE html>", message);
        Assert.DoesNotContain("</body>", message);   // only the first 200 characters of the body
    }

    [Fact]
    public async Task Export_of_an_unknown_kind_reports_the_servers_error()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "nope");

            Assert.Equal(1, r.Exit);
            Assert.Equal("not_found", r.Json["error"]!.GetValue<string>());
            Assert.Contains("nope", r.Json["message"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Export_report_before_any_completed_run_reports_export_unavailable()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "report");

            Assert.Equal(1, r.Exit);
            Assert.Equal("export_unavailable", r.Json["error"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Export_without_a_kind_is_a_usage_error()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "export");

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
        Assert.Contains("sqlpack", r.Json["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_without_a_project_fails()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis");

        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", r.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public void CopyToOut_without_an_out_option_keeps_the_saved_copy()
    {
        Assert.Equal(@"C:\ws\.dbmigrate\exports\analysis-v1.html",
            ExportCommand.CopyToOut(null, @"C:\ws\.dbmigrate\exports\analysis-v1.html", "analysis-v1.html"));
    }

    /// <summary>
    /// A loopback HTTP/1.1 responder standing in for "something else answers on the recorded port": /api/health says
    /// it is this process (so ServerControl accepts it), every other request gets a 502 with an HTML body.
    /// </summary>
    private sealed class HtmlAnsweringServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _html;
        private readonly Task _loop;

        private HtmlAnsweringServer(string html)
        {
            _html = html;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public static HtmlAnsweringServer Start(string html) => new(html);

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                var health = head.ToString().StartsWith("GET /api/health", StringComparison.Ordinal);
                var (status, type, body) = health
                    ? ("200 OK", "application/json", $$"""{"pid":{{Environment.ProcessId}}}""")
                    : ("502 Bad Gateway", "text/html", _html);
                var bytes = Encoding.UTF8.GetBytes(body);
                var response = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                await stream.WriteAsync(bytes);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // stopped
            }
            _cts.Dispose();
        }
    }
}
