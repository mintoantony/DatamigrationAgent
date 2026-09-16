using System.Net;
using Dbm.Core;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

public sealed class SqlEndpointsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Validate_without_a_version_is_a_conflict()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_version", (string?)body!["error"]);
    }

    [Fact]
    public async Task Validate_without_connections_is_not_ready()
    {
        SqlExportsTests.WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { task = "T01" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("not_ready", (string?)body!["error"]);
    }

    [Fact]
    public async Task Validate_with_a_malformed_body_is_a_bad_request()
    {
        SqlExportsTests.WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        using var content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        using var response = await server.Client.PostAsync("/api/sql/validate", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"error\":\"bad_request\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sql_screen_exports_as_standalone_html()
    {
        SqlExportsTests.WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        using var response = await server.Client.GetAsync("/api/export/sql");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("window.DBM_EXPORT", html);
        Assert.Contains("DBM.views.sql = ", html);
        Assert.Contains("DBM.highlight = ", html);
        Assert.Contains("FK_Customers_PrimaryAddress", html);
    }
}
