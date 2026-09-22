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

    /// <summary>Rulings 210/211 (review M1): a version stored before validation evidence existed - here reopened after approval - is
    /// blocked as not validated. Validate live, while it awaits review, re-validates it and stores the result as a new version with no
    /// SQL change, which is then approvable. While drafting or reworking the same call stores nothing (the agent's baseVersion must not
    /// move), and a stale seen version is refused.</summary>
    [Fact]
    public async Task Validate_live_stores_the_evidence_only_while_the_version_awaits_review()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        SqlGenJobTests.Prepare(s);
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.Null(plan.Validation);   // an old record: no evidence
        var v = s.Artifacts.NextVersion(PhaseName.Sql);
        s.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "old draft");
        s.Phases.SetCurrentVersion(PhaseName.Sql, v);
        await using var server = await WebTestServer.StartAsync(project.Ws, ws => DbmServices.Open(ws));

        foreach (var busy in new[] { PhaseStatus.Drafting, PhaseStatus.Reworking })
        {
            s.Phases.SetStatus(PhaseName.Sql, busy);
            var (st, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { version = v });
            Assert.Equal(HttpStatusCode.OK, st);
            Assert.True((bool)body!["ok"]!, body.ToJsonString());
            Assert.True(body["stored"] is not null && !(bool)body["stored"]!, $"{busy}: validation stored a version under the agent: " + body.ToJsonString());
            Assert.Equal(v, s.Phases.Get(PhaseName.Sql).CurrentVersion);
        }

        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);
        Assert.Throws<Dbm.Core.Workflow.WorkflowException>(() => s.Workflow.Approve(PhaseName.Sql, v));   // not validated

        var (stale, staleBody) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { version = v - 1 });
        Assert.Equal((HttpStatusCode.Conflict, "stale_version"), (stale, (string?)staleBody!["error"]));
        Assert.Equal(v, s.Phases.Get(PhaseName.Sql).CurrentVersion);

        var (status, result) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { version = v });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["stored"] is not null && (bool)result["stored"]!, "Validate live on a not-validated version awaiting review stored nothing: " + result.ToJsonString());
        var next = (int)result["storedVersion"]!;
        Assert.Equal(next, s.Phases.Get(PhaseName.Sql).CurrentVersion);
        var row = s.Artifacts.Get(PhaseName.Sql, next)!;
        Assert.Equal(("script", Dbm.Core.Workflow.WorkflowEngine.RevalidatedSummary), (row.Author, row.Summary));
        var after = Json.Deserialize<SqlPlanPayload>(row.PayloadJson);
        Assert.True(after.Validation is { Ok: true }, "the stored version carries no evidence");
        Assert.Equal(plan.Tasks["T05"].SourceQuery, after.Tasks["T05"].SourceQuery);   // no SQL change
        s.Workflow.Approve(PhaseName.Sql, next);   // approvable now
        Assert.Equal(PhaseStatus.Approved, s.Phases.Get(PhaseName.Sql).Status);
    }
}
