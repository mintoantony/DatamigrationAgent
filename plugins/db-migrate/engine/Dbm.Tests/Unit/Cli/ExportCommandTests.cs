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

    [Fact]
    public async Task Export_analysis_writes_a_standalone_html_into_the_exports_folder()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis");

            Assert.Equal(0, r.Exit);
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

        await WithServerAsync(tw, async () =>
        {
            var toDirectory = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", directory);
            var toFile = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", file);

            Assert.Equal(Path.Combine(directory, "analysis-v1.html"), toDirectory.Json["path"]!.GetValue<string>());
            Assert.True(File.Exists(Path.Combine(directory, "analysis-v1.html")));
            Assert.Equal(file, toFile.Json["path"]!.GetValue<string>());
            Assert.True(File.Exists(file));
            // the copy under .dbmigrate/exports is kept as well
            Assert.True(File.Exists(Path.Combine(tw.Ws.ExportsDir, "analysis-v1.html")));
        });
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
}
