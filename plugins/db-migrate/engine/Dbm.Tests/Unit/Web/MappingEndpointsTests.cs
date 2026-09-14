using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public class MappingEndpointsTests
{
    private static IEnumerable<string?> Texts(JsonNode? array) => array!.AsArray().Select(n => (string?)n);

    [Fact]
    public void Context_lists_catalog_columns_and_validation_of_the_current_version()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.AddMapping(AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions()),
            PhaseStatus.AwaitingReview);

        var ctx = MappingEndpoints.BuildContext(services);

        Assert.Equal(0, (int?)ctx["version"]);
        Assert.Equal(0.85, (double?)ctx["autoAccept"]);
        var customers = ctx["target"]!.AsArray().Single(t => (string?)t!["key"] == "app.Customers")!["columns"]!.AsArray();
        Assert.True((bool?)customers.Single(c => (string?)c!["name"] == "DisplayName")!["computed"]);
        Assert.True((bool?)customers.Single(c => (string?)c!["name"] == "CustomerId")!["identity"]);
        Assert.True((bool?)customers.Single(c => (string?)c!["name"] == "CreatedAt")!["hasDefault"]);
        var cust = ctx["source"]!.AsArray().Single(t => (string?)t!["key"] == "dbo.CUST")!;
        Assert.Equal(8, cust["columns"]!.AsArray().Count);
        Assert.Equal("varchar(100)", (string?)cust["columns"]![1]!["type"]);
        Assert.Contains("source table dbo.ORD_STATUS is not mapped or dropped (3 columns)", Texts(ctx["blockers"]));
        Assert.NotEmpty(ctx["attention"]!.AsArray());
        Assert.Contains("dbo.CUST.FAX_NO", Texts(ctx["uncovered"]));
    }

    [Fact]
    public void Context_follows_the_requested_version()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.AddMapping(AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions()),
            PhaseStatus.AwaitingReview);
        services.AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");

        Assert.Empty(MappingEndpoints.BuildContext(services)["blockers"]!.AsArray());
        Assert.NotEmpty(MappingEndpoints.BuildContext(services, 0)["blockers"]!.AsArray());
        Assert.Equal(1, (int?)MappingEndpoints.BuildContext(services)["version"]);
    }

    [Fact]
    public void Context_without_a_mapping_has_catalogs_but_no_validation()
    {
        using var project = TempProject.Create();
        var ctx = MappingEndpoints.BuildContext(project.Services.WithSampleCatalogs());
        Assert.Null(ctx["version"]);
        Assert.Equal(6, ctx["target"]!.AsArray().Count);
        Assert.Empty(ctx["blockers"]!.AsArray());
    }

    [Fact]
    public async Task Http_endpoint_requires_the_token_and_serves_the_context()
    {
        using var project = TempProject.Create();
        project.Services.WithSampleCatalogs().AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");
        await using var server = await WebTestServer.StartAsync(project.Ws, ws => DbmServices.Open(ws));

        using var anonymous = server.Anonymous();
        var denied = await anonymous.GetAsync("/api/mapping/context");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var json = await server.GetJsonAsync("/api/mapping/context?version=0");
        Assert.Equal(0, (int?)json["version"]);
        Assert.Empty(json["blockers"]!.AsArray());
        Assert.Equal(6, json["target"]!.AsArray().Count);
    }
}
