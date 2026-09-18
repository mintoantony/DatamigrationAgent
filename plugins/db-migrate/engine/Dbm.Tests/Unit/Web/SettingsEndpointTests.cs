using System.Net;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

/// <summary>Open item 33, Ruling 196: the Setup screen's sample-values switch - POST /api/settings and state.project.sampleValues.</summary>
public class SettingsEndpointTests
{
    [Fact]
    public async Task The_switch_round_trips_through_the_state_and_off_clears_the_stored_samples()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());
        server.Services.WithSampleCatalogs();
        Assert.True((await server.GetJsonAsync("/api/state"))["project"]!["sampleValues"]!.GetValue<bool>());

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/settings", new { sampleValues = false });

        Assert.True(status == HttpStatusCode.OK && body!["sampleValues"]!.GetValue<bool>() == false, $"{(int)status} {body}");
        Assert.False((await server.GetJsonAsync("/api/state"))["project"]!["sampleValues"]!.GetValue<bool>(),
            "the Setup screen reads the switch from state.project.sampleValues; it still says on after switching off");
        var email = server.Services.Catalog.Get(Side.Src)!.FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!.Profile!;
        Assert.True(email.Samples.Count == 0, "switched off from the Setup screen, the catalog still holds " + string.Join(", ", email.Samples));

        (status, body) = await server.SendAsync(HttpMethod.Post, "/api/settings", new { sampleValues = true });
        Assert.True(status == HttpStatusCode.OK && body!["sampleValues"]!.GetValue<bool>(), $"{(int)status} {body}");
        Assert.True(server.Services.Project.GetSettings().SampleValues);
    }

    [Fact]
    public async Task A_body_without_sampleValues_is_400()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());

        var (status, _) = await server.SendAsync(HttpMethod.Post, "/api/settings", new { other = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(server.Services.Project.GetSettings().SampleValues);
    }
}
