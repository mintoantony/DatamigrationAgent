using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Patching;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

/// <summary>SqlModule.Validate's LIVE branch, through WorkflowEngine.ApplyPatch — the path `dbm apply --dry-run` takes.</summary>
[Trait("Category", "Integration")]
public sealed class SqlModuleIntegrationTests
{
    /// <summary>Stores <paramref name="plan"/> as the current sql version, phase drafting (agent patches accepted).</summary>
    private static int Store(DbmServices s, SqlPlanPayload plan)
    {
        var v = s.Artifacts.NextVersion(PhaseName.Sql);
        s.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "draft");
        s.Phases.SetCurrentVersion(PhaseName.Sql, v);
        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Drafting);
        return v;
    }

    private static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    /// <summary>Ruling 44-Q1 (M3): the global "not checked" lines of THIS validation reach the dry-run. Ruling 44fix M4: so do the
    /// task-level "not checked" lines about SQL the agent has just written — the stored plan the agent holds predates them.</summary>
    [Fact]
    public async Task Dry_run_surfaces_global_and_task_not_checked_lines()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        SqlGenJobTests.Prepare(s);
        var plan = Plan();
        var v = Store(s, plan);
        var customers = plan.Tasks.Single(kv => kv.Value.Target == "app.Customers").Key;
        var ops = new List<PatchOp>
        {
            new("add", "/preSql/-", JsonValue.Create("CREATE TABLE [dbo].[agent_stage] ([id] int);")),
            new("add", "/postSql/-", JsonValue.Create("INSERT INTO [app].[AuditEvents] ([EventTime], [UserName], [Action]) SELECT SYSUTCDATETIME(), N'x', N'y' FROM [dbo].[agent_stage];")),
            new("add", $"/tasks/{customers}/preSql/-", JsonValue.Create("CREATE TABLE #work ([x] int); SELECT [x] FROM #work;")),   // sp_describe cannot analyse it: not checked, no error
        };

        var result = s.Workflow.ApplyPatch(new Patch("sql", v, ops, []), dryRun: true);

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var globalField = $"postSql[{plan.PostSql.Count}]";
        Assert.Contains(result.Warnings, w => w.StartsWith(globalField + SqlValidator.NotCheckedMarker, StringComparison.Ordinal));
        var taskField = $"{customers}: preSql[{plan.Tasks[customers].PreSql.Count}]";
        Assert.Contains(result.Warnings, w => w.StartsWith(taskField + SqlValidator.NotCheckedMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith(SqlPlanSource.SkippedPrefix, StringComparison.Ordinal));
        // Only the "not checked" task lines: an ordinary task warning (the Orders narrowing risk) stays in the stored plan.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("varchar(500) -> nvarchar(200)", StringComparison.Ordinal));
    }

    /// <summary>Ruling 57, live branch: sp_describe accepts a statement a lone CR splits, so the offline check must still reach the
    /// dry-run, for a global statement and a task statement alike.</summary>
    [Fact]
    public async Task Live_dry_run_reports_a_bare_carriage_return()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        SqlGenJobTests.Prepare(s);
        var plan = Plan();
        var v = Store(s, plan);
        var customers = plan.Tasks.Single(kv => kv.Value.Target == "app.Customers").Key;
        const string hidden = "SELECT 1 AS a -- hidden\rDELETE FROM [app].[Customers];";
        var ops = new List<PatchOp>
        {
            new("add", "/preSql/-", JsonValue.Create(hidden)),
            new("add", $"/tasks/{customers}/postSql/-", JsonValue.Create(hidden)),
        };

        var result = s.Workflow.ApplyPatch(new Patch("sql", v, ops, []), dryRun: true);

        Assert.False(result.Ok);
        var patched = Json.Deserialize<SqlPlanPayload>(Json.Serialize(plan));
        patched.PreSql.Add(hidden);
        patched.Tasks[customers].PostSql.Add(hidden);
        var expected = SqlValidator.FindBareCarriageReturns(patched).Select(f => $"{f.Scope}: {f.Line}").ToList();
        Assert.Equal(2, expected.Count);
        Assert.All(expected, e => Assert.Contains(e, result.Errors));
        Assert.StartsWith("plan: preSql[", expected[0], StringComparison.Ordinal);
        Assert.StartsWith($"{customers}: postSql[", expected[1], StringComparison.Ordinal);
    }

    /// <summary>H1: the "not validated" marker is re-derived on every run — once live validation does run, it goes, and the plan
    /// is approvable on its merits.</summary>
    [Fact]
    public async Task Live_validation_clears_a_stale_not_validated_marker()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        SqlGenJobTests.Prepare(s);
        var plan = Plan();
        plan.Warnings.Add(SqlPlanSource.SkippedPrefix + "source connection, target connection missing");   // as an offline sqlgen stored it
        var v = Store(s, plan);

        var result = s.Workflow.ApplyPatch(new Patch("sql", v, [], []));

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var stored = s.Artifacts.Get(PhaseName.Sql, result.Version!.Value)!;
        Assert.DoesNotContain(Json.Deserialize<SqlPlanPayload>(stored.PayloadJson).Warnings,
            w => w.StartsWith(SqlPlanSource.SkippedPrefix, StringComparison.Ordinal));
        var ctx = new Dbm.Core.Workflow.ModuleContext { Services = s, Current = stored, OpenFeedback = [] };
        Assert.Empty(s.Modules[PhaseName.Sql].ApprovalBlockers(ctx, JsonNode.Parse(stored.PayloadJson)!));
    }
}
