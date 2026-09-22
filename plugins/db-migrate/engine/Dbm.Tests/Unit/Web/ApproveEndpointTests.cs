using System.Net;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

/// <summary>
/// Open item 1, Ruling 194: POST /api/phase/{phase}/approve carries {"version": n}, the version on the reviewer's screen. A missing
/// version is 400 version_required; a version that is no longer current is 409 stale_version and approves nothing.
/// </summary>
public class ApproveEndpointTests
{
    public static TheoryData<PhaseName> Reviewable => new() { PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql };

    /// <summary>Every module stores its script draft for review (no agent), so each review phase is reachable over HTTP alone.</summary>
    private static FakeModules NoAgentModules()
    {
        var modules = new FakeModules();
        foreach (var m in modules.All.Cast<FakeModule>()) m.NeedsAgentResult = false;
        return modules;
    }

    /// <summary>Drives <paramref name="phase"/> to awaiting_review at v0 by approving each earlier review phase over HTTP.</summary>
    private static async Task<WebTestServer> StartAtAsync(TestWorkspace tw, PhaseName phase)
    {
        var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(NoAgentModules()));
        FakeServices.SaveConnections(server.Services);
        foreach (var p in new[] { PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql })
        {
            await Wait.UntilAsync(() => server.Services.Phases.Get(p).Status == PhaseStatus.AwaitingReview);
            if (p == phase) break;
            var (status, body) = await server.SendAsync(HttpMethod.Post, $"/api/phase/{p.Text()}/approve", new { version = 0 });
            Assert.True(status == HttpStatusCode.OK, $"approve {p.Text()} v0: {status} {body}");
        }
        return server;
    }

    [Theory]
    [MemberData(nameof(Reviewable))]
    public async Task Approve_with_the_version_on_screen_approves_it(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        await using var server = await StartAtAsync(tw, phase);

        var (status, body) = await server.SendAsync(HttpMethod.Post, $"/api/phase/{phase.Text()}/approve", new { version = 0 });

        Assert.True(status == HttpStatusCode.OK, $"{status} {body}");
        Assert.Equal(PhaseStatus.Approved, server.Services.Phases.Get(phase).Status);
    }

    [Theory]
    [MemberData(nameof(Reviewable))]
    public async Task Approve_of_a_version_that_is_no_longer_current_is_409_stale_version(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        await using var server = await StartAtAsync(tw, phase);
        // Another tab stores v1 by a direct edit; this tab still shows v0.
        var (edited, editBody) = await server.SendAsync(HttpMethod.Post, $"/api/edit/{phase.Text()}", new
        {
            phase = phase.Text(),
            baseVersion = 0,
            ops = new[] { new { op = "replace", path = "/summary", value = "v1" } },
        });
        Assert.True(edited == HttpStatusCode.OK && editBody!["ok"]!.GetValue<bool>(), $"edit: {edited} {editBody}");

        var (status, body) = await server.SendAsync(HttpMethod.Post, $"/api/phase/{phase.Text()}/approve", new { version = 0 });

        Assert.True(status == HttpStatusCode.Conflict && body!["error"]!.GetValue<string>() == "stale_version",
            $"a stale approve must be 409 stale_version, got {(int)status} {body}");
        var row = server.Services.Phases.Get(phase);
        Assert.True(row.Status == PhaseStatus.AwaitingReview && row.ApprovedVersion is null,
            $"the stale approve signed off {phase.Text()}: {row.Status} approved v{row.ApprovedVersion}");
    }

    [Theory]
    [MemberData(nameof(Reviewable))]
    public async Task Approve_without_a_version_is_400_version_required(PhaseName phase)
    {
        using var tw = new TestWorkspace();
        await using var server = await StartAtAsync(tw, phase);

        foreach (var request in new object?[] { null, new { }, new { version = (int?)null } })
        {
            var (status, body) = await server.SendAsync(HttpMethod.Post, $"/api/phase/{phase.Text()}/approve", request);

            Assert.True(status == HttpStatusCode.BadRequest && body!["error"]!.GetValue<string>() == "version_required",
                $"an approve naming no version must be 400 version_required, got {(int)status} {body} for {Json.Serialize(request)}");
        }
        Assert.Equal(PhaseStatus.AwaitingReview, server.Services.Phases.Get(phase).Status);
    }
}
