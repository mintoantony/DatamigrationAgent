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
    }
}
