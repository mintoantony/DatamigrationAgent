using System.Net;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

/// <summary>
/// Open item 37, Ruling 195: POST /api/phase/{phase}/take-over. Refused (409 agent_active) while an agent may be applying a patch -
/// "may be applying" is AgentPresence.Online: an open /api/agent/await, or a `dbm next`/`dbm await` within AgentPresence.SeenWindow.
/// </summary>
public class TakeOverEndpointTests
{
    /// <summary>Analysis drafting at v0 (the default fake module needs an agent), with no agent seen.</summary>
    private static async Task<WebTestServer> StartDraftingAsync(TestWorkspace tw)
    {
        var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory());
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.Drafting);
        return server;
    }

    [Fact]
    public async Task Take_over_of_a_drafting_phase_with_no_agent_connected_returns_it_to_review()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartDraftingAsync(tw);

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/take-over");

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.Equal(PhaseStatus.AwaitingReview, server.Services.Phases.Get(PhaseName.Analysis).Status);
        var next = (await server.GetJsonAsync("/api/state"))["next"]!;
        Assert.Equal(("await", "review"), (next["action"]!.GetValue<string>(), next["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Take_over_is_refused_while_the_agent_is_online()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartDraftingAsync(tw);
        server.Services.Project.TouchAgent();   // what `dbm next` does as it hands the packet to the subagent

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/take-over");

        Assert.True(status == HttpStatusCode.Conflict && body!["error"]!.GetValue<string>() == "agent_active",
            $"Take over while Claude may be applying a patch must be 409 agent_active, got {(int)status} {body}");
        Assert.Equal(PhaseStatus.Drafting, server.Services.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Take_over_of_a_phase_awaiting_review_is_a_conflict()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartDraftingAsync(tw);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/take-over")).Status);

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/take-over");

        Assert.True(status == HttpStatusCode.Conflict, $"{(int)status} {body}");
    }
}
