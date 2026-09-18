using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

/// <summary>
/// Ruling 185 (open item 36) end to end, on the C15 sample pair with the real modules and jobs: a run completes, Mapping is reopened,
/// one mapping changes, the plan is re-approved through SQL, and Execute starts a <b>new</b> run with Truncate target first. Before 185
/// the reopen was refused and the only way out was a new project folder.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReopenAfterRunIntegrationTests
{
    private static readonly TransferOptions Options = new() { ChunkSize = 2000, Parallelism = 2, ErrorMode = "skip" };

    private static async Task SaveCatalogAsync(DbmServices s, Side side, string cs)
    {
        var snapshot = await SampleExtract.CatalogAsync(cs, profile: false);
        s.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
    }

    /// <summary>Approve Mapping as it stands, let the real sqlgen job draft the plan, and approve SQL (Ready then awaits Execute).</summary>
    private static async Task ApproveThroughSqlAsync(DbmServices s)
    {
        s.ApproveCurrent(PhaseName.Mapping);
        await new JobRunner(s).RunPendingAsync(CancellationToken.None);
        var sql = s.Phases.Get(PhaseName.Sql);
        Assert.True(sql.Status is PhaseStatus.Drafting or PhaseStatus.AwaitingReview,
            $"the sqlgen job left SQL {EnumText.ToText(sql.Status)}: {s.Jobs.LatestFor(PhaseName.Sql)?.Error}");
        if (sql.Status == PhaseStatus.Drafting)
        {
            // What the sql-engineer does when it has nothing to change: an empty patch against the draft.
            var applied = s.Workflow.ApplyPatch(new Patch("sql", sql.CurrentVersion!.Value, [], [], "reviewed, no change"));
            Assert.True(applied.Ok, string.Join("; ", applied.Errors));
        }
        s.ApproveCurrent(PhaseName.Sql);
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Ready).Status);
    }

    [Fact]
    public async Task Complete_reopen_mapping_change_one_mapping_and_run_again_with_truncate_gives_the_new_numbers()
    {
        await using var pair = await SampleDatabases.CreateAsync(scale: 1);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        await SaveCatalogAsync(s, Side.Src, pair.SourceCs);
        await SaveCatalogAsync(s, Side.Tgt, pair.TargetCs);
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis }) s.Phases.SetApproved(p, 1, null);
        s.Artifacts.Add(PhaseName.Mapping, 1, Json.Serialize(SampleMappings.Approved()), "human", "approved mapping");
        s.Phases.SetCurrentVersion(PhaseName.Mapping, 1);
        s.Phases.SetStatus(PhaseName.Mapping, PhaseStatus.AwaitingReview);
        await ApproveThroughSqlAsync(s);

        var service = new TransferService(s);
        long first = await service.StartAsync(Options, pair.Target.Name, CancellationToken.None);
        await service.Current;
        Assert.Equal(RunStatus.Completed, s.Transfers.GetRun(first)!.Status);
        Assert.Equal(5000, await pair.Target.CountAsync("app.AuditEvents"));
        Assert.Equal("complete", s.Workflow.Next().Reason);

        // The loop the ruling reopens: Mapping, after a completed run.
        s.Workflow.Reopen(PhaseName.Mapping);
        Assert.Equal(PhaseStatus.Pending, s.Phases.Get(PhaseName.Transfer).Status);
        var edit = s.Workflow.HumanEdit(new Patch("mapping", s.Phases.Get(PhaseName.Mapping).CurrentVersion!.Value,
            [new PatchOp("add", "/tables/app.AuditEvents/filter", JsonValue.Create("s.[USR] <> 'svc_import'"))], [],
            "leave the import service's audit rows behind"));
        Assert.True(edit.Ok, string.Join("; ", edit.Errors));
        await ApproveThroughSqlAsync(s);
        Assert.Contains("svc_import", Preflight.LoadApprovedPlan(s)!.Plan.Tasks.Values.Single(t => t.Target == "app.AuditEvents").SourceQuery);

        long second = await service.StartAsync(Options with { TruncateTarget = true }, pair.Target.Name, CancellationToken.None);
        await service.Current;

        Assert.NotEqual(first, second);
        var run = s.Transfers.GetRun(second)!;
        Assert.True(run.Status == RunStatus.Completed, $"the new run is {EnumText.ToText(run.Status)}: {run.SummaryJson}");
        // The new numbers: 5 000 audit rows less the 1 000 written by svc_import (i % 5 = 3), every other table exactly as before.
        Assert.Equal(4000, await pair.Target.CountAsync("app.AuditEvents"));
        Assert.Equal(0, await pair.Target.ScalarAsync<int>("SELECT COUNT(*) FROM app.AuditEvents WHERE UserName = 'svc_import'"));
        Assert.Equal(1000, await pair.Target.CountAsync("app.Customers"));
        Assert.Equal(3000, await pair.Target.CountAsync("app.Orders"));
        var report = Json.Deserialize<FinalReport>(run.SummaryJson!);
        Assert.Equal(18_699, report.RowsLoaded);
        Assert.Equal(8, report.RowsError);
        Assert.StartsWith("Transferred 18,699 of 18,707 rows into 6 tables", FinalReportBuilder.Summary(report));
        // Both reports stay: v1 is the first run's, v2 the new one's, and Complete now points at v2.
        var complete = s.Phases.Get(PhaseName.Complete);
        Assert.Equal(PhaseStatus.Approved, complete.Status);
        Assert.Equal(2, s.Artifacts.List(PhaseName.Complete).Count);
        Assert.Contains($"\"runId\":{first}", s.Artifacts.Get(PhaseName.Complete, 1)!.PayloadJson);
        Assert.Contains($"\"runId\":{second}", s.Artifacts.Get(PhaseName.Complete, complete.ApprovedVersion!.Value)!.PayloadJson);
        Assert.Equal("complete", s.Workflow.Next().Reason);
    }
}
