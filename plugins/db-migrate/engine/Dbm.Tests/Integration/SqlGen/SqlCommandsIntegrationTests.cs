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
}
