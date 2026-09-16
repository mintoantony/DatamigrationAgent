using System.Net;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

/// <summary>POST /api/sql/validate against real databases: the response carries the T4.3 globalWarnings contract, which the SQL
/// screen relies on to tell "checked, nothing to report" ([]) from "not checked" (a line) — and it stores nothing.</summary>
[Trait("Category", "Integration")]
public sealed class SqlEndpointsIntegrationTests
{
    [Fact]
    public async Task Validate_reports_global_coverage_for_the_whole_plan_and_for_one_task()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        SqlGenJobTests.Prepare(project.Services);
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var v = project.Services.Artifacts.NextVersion(PhaseName.Sql);
        project.Services.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "draft");
        project.Services.Phases.SetCurrentVersion(PhaseName.Sql, v);
        var stored = project.Services.Artifacts.Get(PhaseName.Sql, v)!.PayloadJson;
        await using var server = await WebTestServer.StartAsync(project.Ws, ws => DbmServices.Open(ws));

        var (status, whole) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(v, (int)whole!["version"]!);
        Assert.True((bool)whole["ok"]!, whole.ToJsonString());
        Assert.Equal(plan.Order, whole["taskErrors"]!.AsObject().Select(p => p.Key));
        Assert.NotNull(whole["globalErrors"]!.AsArray());
        Assert.NotNull(whole["globalWarnings"]!.AsArray());
        Assert.DoesNotContain(SqlValidator.SingleTaskGlobalWarning, whole["globalWarnings"]!.AsArray().Select(n => (string?)n));

        var (oneStatus, one) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { task = "T05" });

        Assert.Equal(HttpStatusCode.OK, oneStatus);
        Assert.Equal(["T05"], one!["taskErrors"]!.AsObject().Select(p => p.Key));
        Assert.Equal([SqlValidator.SingleTaskGlobalWarning], one["globalWarnings"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(stored, project.Services.Artifacts.Get(PhaseName.Sql, v)!.PayloadJson);
        Assert.Equal(v, project.Services.Phases.Get(PhaseName.Sql).CurrentVersion);
    }
}
