using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

/// <summary>Hosts the real server in-process (WebTestServer, real registries) on a workspace with a stored catalog and analysis v0.</summary>
public sealed class CatalogHttpTests : IAsyncLifetime
{
    private readonly TempProject _project = TempProject.Create("http");
    private WebTestServer _server = null!;

    public async Task InitializeAsync()
    {
        var s = _project.Services;
        s.Catalog.Save(Side.Src, TestCatalogs.LegacyShop(), "src-fp");
        s.Catalog.Save(Side.Tgt, TestCatalogs.ShopV2(), "tgt-fp");
        var (rows, _) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Synonyms.Default());
        s.Catalog.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Src));
        s.Catalog.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));
        var payload = Analyzer.Analyze(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2());
        s.Artifacts.Add(PhaseName.Analysis, 0, Json.Serialize(payload), "script", Analyzer.Summary(payload));
        s.Phases.SetCurrentVersion(PhaseName.Analysis, 0);
        _server = await WebTestServer.StartAsync(_project.Ws, ws => DbmServices.Open(ws));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _project.Dispose();
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> GetJsonAsync(string path) => _server.SendAsync(HttpMethod.Get, path);

    [Fact]
    public async Task Catalog_list_and_table_detail()
    {
        var (listStatus, list) = await GetJsonAsync("/api/catalog/tgt");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var tables = list!.AsArray();
        Assert.Equal(6, tables.Count);
        var customers = tables.Single(t => (string?)t!["key"] == "app.Customers")!;
        Assert.True((bool)customers["hasPk"]!);
        Assert.Equal("app.Addresses", (string?)customers["refs"]![0]);

        var (detailStatus, detail) = await GetJsonAsync("/api/catalog/src/table/dbo.CUST");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal("dbo.CUST", (string?)detail!["key"]);
        var email = detail["table"]!["columns"]!.AsArray().Single(c => (string?)c!["name"] == "EMAIL_ADDR")!;
        Assert.Equal(100, (int)email["profile"]!["nulls"]!);
        Assert.Equal(2, detail["referencedBy"]!.AsArray().Count);
    }

    [Fact]
    public async Task Search_returns_ranked_hits()
    {
        var (status, hits) = await GetJsonAsync("/api/search?q=customer%20email&side=tgt&k=3");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("app.Customers.Email", (string?)hits!.AsArray()[0]!["key"]);
    }

    [Fact]
    public async Task Bad_side_unknown_table_and_unknown_export_are_errors()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await GetJsonAsync("/api/catalog/xyz")).Status);
        var (status, body) = await GetJsonAsync("/api/catalog/src/table/dbo.NOPE");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", (string?)body!["error"]);
        Assert.Equal(HttpStatusCode.NotFound, (await GetJsonAsync("/api/export/nope")).Status);
    }

    [Fact]
    public async Task Rediscover_needs_both_connections()
    {
        var (status, body) = await _server.SendAsync(HttpMethod.Post, "/api/rediscover");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_connections", (string?)body!["error"]);
    }

    [Fact]
    public async Task Export_analysis_downloads_and_saves_a_copy()
    {
        using var get = await _server.Client.GetAsync("/api/export/analysis");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("text/html", get.Content.Headers.ContentType!.MediaType);
        Assert.Contains("analysis-v0.html", get.Content.Headers.ContentDisposition!.ToString());
        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("window.DBM_EXPORT = {\"view\":\"analysis\"", html);
        Assert.Contains("DBM.views.analysis", html);
        Assert.Contains("DBM.graph = { layout: layout, fkSvg: fkSvg }", html);
        Assert.True(File.Exists(Path.Combine(_project.Ws.ExportsDir, "analysis-v0.html")));

        var (status, body) = await _server.SendAsync(HttpMethod.Post, "/api/export/analysis");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(".dbmigrate/exports/analysis-v0.html", (string?)body!["path"]);
        Assert.StartsWith("/api/export/analysis?t=", (string?)body["download"]);
    }

    [Fact]
    public async Task Requests_without_the_token_are_rejected()
    {
        using var anonymous = _server.Anonymous();
        using var response = await anonymous.GetAsync("/api/catalog/src");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
