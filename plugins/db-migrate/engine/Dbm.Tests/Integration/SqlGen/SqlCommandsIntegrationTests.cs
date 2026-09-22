using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public sealed class SqlCommandsIntegrationTests
{
    /// <summary>End to end through the REAL registries (null factory → ModuleRegistry, so the real SqlModule decides NeedsAgent when
    /// WorkflowEngine.OnJobDone stores the sqlgen draft). No stand-in module: T4.3 used a FakeModule until SqlModule existed (T4.4).</summary>
    [Fact]
    public async Task Gen_inline_then_validate_one_task()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        Assert.IsType<SqlModule>(project.Services.Modules[PhaseName.Sql]);
        SqlGenJobTests.Prepare(project.Services);
        project.Services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Running);

        var gen = await CliRunner.RunAsync(project.Ws, null, "sql", "gen", "--inline");
        Assert.True(gen.Exit == 0, gen.Out);
        Assert.Equal("awaiting_review", (string?)gen.Json["status"]);
        Assert.StartsWith("6 tasks, 0 errors,", (string?)gen.Json["summary"]);

        var check = await CliRunner.RunAsync(project.Ws, null, "sql", "validate", "--task", "T05");
        Assert.True(check.Exit == 0, check.Out);
        Assert.True((bool)check.Json["ok"]!);
        Assert.NotNull(check.Json["taskErrors"]!["T05"]);
        Assert.Equal([SqlValidator.SingleTaskGlobalWarning], check.Json["globalWarnings"]!.AsArray().Select(n => (string?)n));
        Assert.DoesNotContain("Integrated Security", check.Out, StringComparison.OrdinalIgnoreCase);

        // Open item 17: a patch whose only defect is a bare carriage return compiles on the server (SQL Server reads the CR as a line
        // break), so live validation alone says ok. Step 1 of the playbook must still refuse it, as step 2 (apply --dry-run) does.
        var version = project.Services.Phases.Get(PhaseName.Sql).CurrentVersion!.Value;
        var patchPath = Path.Combine(project.Root, "cr-patch.json");
        File.WriteAllText(patchPath, "{\"phase\":\"sql\",\"baseVersion\":" + version
            + ",\"ops\":[{\"op\":\"add\",\"path\":\"/tasks/T05/preSql/-\",\"value\":\"-- note\\rUPDATE STATISTICS app.Orders\"}]}");
        var cr = await CliRunner.RunAsync(project.Ws, null, "sql", "validate", "--patch", patchPath);
        Assert.True(cr.Exit == 1, "sql validate --patch green-lit a bare carriage return: " + cr.Out);
        Assert.False((bool)cr.Json["ok"]!);
        Assert.Contains(cr.Json["taskErrors"]!["T05"]!.AsArray(), e => ((string?)e)!.Contains(SqlValidator.BareCarriageReturnMarker, StringComparison.Ordinal));
    }

    /// <summary>Rulings 210/211: `dbm sql validate` without --patch records the evidence exactly as Validate live does - a new version
    /// while Sql awaits review, nothing while the agent is drafting (its self-check must not move its baseVersion).</summary>
    [Fact]
    public async Task Validate_stores_evidence_only_while_awaiting_review()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        SqlGenJobTests.Prepare(s);
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        s.Artifacts.Add(PhaseName.Sql, 0, Json.Serialize(plan), "script", "old draft");   // no evidence
        s.Phases.SetCurrentVersion(PhaseName.Sql, 0);

        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Drafting);
        var drafting = await CliRunner.RunAsync(project.Ws, null, "sql", "validate");
        Assert.True(drafting.Exit == 0, drafting.Out);
        Assert.True(drafting.Json["stored"] is not null && !(bool)drafting.Json["stored"]!, "the agent's self-check stored a version: " + drafting.Out);
        Assert.Equal(0, s.Phases.Get(PhaseName.Sql).CurrentVersion);

        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);
        var review = await CliRunner.RunAsync(project.Ws, null, "sql", "validate");
        Assert.True(review.Exit == 0, review.Out);
        Assert.True(review.Json["stored"] is not null && (bool)review.Json["stored"]!, "sql validate on a not-validated version awaiting review stored nothing: " + review.Out);
        Assert.Equal(1, s.Phases.Get(PhaseName.Sql).CurrentVersion);
        Assert.True(Json.Deserialize<SqlPlanPayload>(s.Artifacts.Get(PhaseName.Sql, 1)!.PayloadJson).Validation is { Ok: true });
    }

    /// <summary>Re-review R3: a version saved while `dbm sql validate` ran makes the store stale; the command still prints its report,
    /// with stored false and a note, instead of a generic internal error.</summary>
    [Fact]
    public async Task A_version_saved_during_sql_validate_is_reported_not_overwritten()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        SqlGenJobTests.Prepare(s);
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        s.Artifacts.Add(PhaseName.Sql, 0, Json.Serialize(plan), "script", "old draft");
        s.Phases.SetCurrentVersion(PhaseName.Sql, 0);
        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);
        SqlPlanSource.BeforeStoreOverrides[project.Ws.Root] = other =>
        {
            other.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(plan), "human", "edited in another tab");
            other.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        };
        try
        {
            var r = await CliRunner.RunAsync(project.Ws, null, "sql", "validate");

            Assert.True(r.Exit == 0 && r.Json["error"] is null, "a version saved during sql validate lost the report: " + r.Out[..Math.Min(300, r.Out.Length)]);
            Assert.False((bool)r.Json["stored"]!);
            Assert.Contains("current version", (string?)r.Json["storeNote"], StringComparison.Ordinal);
            Assert.Equal(1, s.Phases.Get(PhaseName.Sql).CurrentVersion);
        }
        finally { SqlPlanSource.BeforeStoreOverrides.TryRemove(project.Ws.Root, out _); }
    }
}
