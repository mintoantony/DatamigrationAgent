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

    /// <summary>Review M1: the version the screen shows is named, and a stale one is refused before anything runs.</summary>
    [Fact]
    public async Task Validate_naming_a_stale_version_is_refused()
    {
        SqlExportsTests.WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var current = server.Services.Phases.Get(Dbm.Core.State.PhaseName.Sql).CurrentVersion!.Value;
        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { version = current + 3 });
        Assert.True(status == HttpStatusCode.Conflict && (string?)body!["error"] == "stale_version",
            "Validate live on a version that is not current was not refused as stale: " + (int)status + " " + body?.ToJsonString());
    }

    /// <summary>Review L2 (item 17's rule, on the screen's door): the offline bare-CR scan runs here too, so Validate live never says
    /// "passed" over a bare carriage return, and says so without connections.</summary>
    [Fact]
    public async Task Validate_reports_a_bare_carriage_return_offline()
    {
        var s = SqlExportsTests.WithPlan(_workspace);
        var row = Dbm.Core.SqlGen.SqlPlanSource.CurrentRow(s)!;
        var plan = Json.Deserialize<Dbm.Core.SqlGen.SqlPlanPayload>(row.PayloadJson);
        plan.PreSql.Add("-- x\rDELETE FROM app.Orders");
        s.Artifacts.Add(Dbm.Core.State.PhaseName.Sql, row.Version + 1, Json.Serialize(plan), "agent", "cr");
        s.Phases.SetCurrentVersion(Dbm.Core.State.PhaseName.Sql, row.Version + 1);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { });

        Assert.True(status == HttpStatusCode.OK, "Validate live refused to report a bare carriage return offline: " + (int)status + " " + body?.ToJsonString());
        Assert.False((bool)body!["ok"]!);
        Assert.Contains(body["globalErrors"]!.AsArray(), e => ((string?)e)!.Contains(Dbm.Core.SqlGen.SqlValidator.BareCarriageReturnMarker, StringComparison.Ordinal));
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
