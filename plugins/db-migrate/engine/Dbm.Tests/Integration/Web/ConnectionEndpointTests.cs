using System.Net;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Web;

[Trait("Category", "Integration")]
public class ConnectionEndpointTests
{
    [Fact]
    public async Task Test_and_save_probe_the_real_server_and_start_discovery()
    {
        await using var src = await TempDatabase.CreateAsync();
        await using var tgt = await TempDatabase.CreateAsync();
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(handlers: []));

        var (testStatus, test) = await server.SendAsync(HttpMethod.Post, "/api/connections/src/test", new { connectionString = src.ConnectionString });
        Assert.Equal(HttpStatusCode.OK, testStatus);
        Assert.True(test!["ok"]!.GetValue<bool>());
        Assert.Equal(src.Name, test["meta"]!["database"]!.GetValue<string>());
        Assert.False(server.Services.Connections.Has(Side.Src));

        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/connections/src", new { connectionString = src.ConnectionString })).Status);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/connections/tgt", new { connectionString = tgt.ConnectionString })).Status);

        var state = await server.GetJsonAsync("/api/state");
        Assert.Contains($"database={src.Name}; auth=integrated", state["connections"]!["src"]!["describe"]!.GetValue<string>());
        Assert.Equal(tgt.Name, state["connections"]!["tgt"]!["meta"]!["database"]!.GetValue<string>());
        Assert.Equal(PhaseStatus.Approved, server.Services.Phases.Get(PhaseName.Setup).Status);
        Assert.Equal(PhaseStatus.Running, server.Services.Phases.Get(PhaseName.Discovery).Status);
        Assert.Equal("discover", server.Services.Jobs.LatestFor(PhaseName.Discovery)!.Kind);
    }

    [Fact]
    public async Task Failures_are_reported_without_the_password()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        var cs = new SqlConnectionStringBuilder(SqlTestServer.ConnectionString)
        {
            InitialCatalog = "dbm_missing_" + Guid.NewGuid().ToString("N")[..6],
            IntegratedSecurity = false,
            UserID = "dbm_nobody",
            Password = "Sup3rS3cretPw!",
            ConnectTimeout = 5,
        }.ConnectionString;

        var (testStatus, test) = await server.SendAsync(HttpMethod.Post, "/api/connections/src/test", new { connectionString = cs });
        var (saveStatus, save) = await server.SendAsync(HttpMethod.Post, "/api/connections/src", new { connectionString = cs });

        Assert.Equal(HttpStatusCode.OK, testStatus);
        Assert.False(test!["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(test["error"]!.GetValue<string>()));
        Assert.DoesNotContain("Sup3rS3cretPw!", test.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, saveStatus);
        Assert.Equal("connection_failed", save!["error"]!.GetValue<string>());
        Assert.DoesNotContain("Sup3rS3cretPw!", save.ToJsonString());
        Assert.False(server.Services.Connections.Has(Side.Src));
    }
}
