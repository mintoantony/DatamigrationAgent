using System.IO.Compression;
using System.Net;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public sealed class SqlExportsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Stores the generated sample plan as sql v0 (current version).</summary>
    internal static DbmServices WithPlan(TestWorkspace workspace)
    {
        var s = workspace.OpenServices();
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var v = s.Artifacts.NextVersion(PhaseName.Sql);
        s.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "draft");
        s.Phases.SetCurrentVersion(PhaseName.Sql, v);
        return s;
    }

    [Fact]
    public void Pack_is_named_after_the_project_and_version()
    {
        var file = SqlExports.Pack(WithPlan(_workspace));
        Assert.Equal("test-sql-v0.zip", file.FileName);
        Assert.Equal("application/zip", file.ContentType);
        using var zip = new ZipArchive(new MemoryStream(file.Content), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == "05_app_Orders.sql");
    }

    [Fact]
    public void Pack_without_a_plan_is_unavailable()
    {
        var ex = Assert.Throws<ExportException>(() => SqlExports.Pack(_workspace.OpenServices()));
        Assert.Equal("No SQL plan yet.", ex.Message);
    }

    [Fact]
    public async Task Sqlpack_is_served_by_the_export_endpoint()
    {
        WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        using var response = await server.Client.GetAsync("/api/export/sqlpack");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Equal(9, zip.Entries.Count);
        Assert.True(File.Exists(Path.Combine(_workspace.Ws.ExportsDir, "test-sql-v0.zip")));
    }

    [Fact]
    public async Task Sqlpack_without_a_plan_answers_404_export_unavailable()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/export/sqlpack");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("export_unavailable", (string?)body!["error"]);
        Assert.Equal("No SQL plan yet.", (string?)body["message"]);
    }

    /// <summary>BY DESIGN until T4.5: the "sql" kind is registered, but the standalone HTML needs wwwroot/js/views/sql.js, which T4.5
    /// creates. T4.5 replaces this test with one asserting the HTML export succeeds.</summary>
    [Fact]
    public async Task Sql_html_export_answers_404_until_the_sql_view_exists()
    {
        WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/export/sql");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("export_unavailable", (string?)body!["error"]);
        Assert.Equal("View script js/views/sql.js not found.", (string?)body["message"]);
    }
}
