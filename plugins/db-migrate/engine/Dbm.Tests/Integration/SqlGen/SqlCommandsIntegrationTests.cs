using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public sealed class SqlCommandsIntegrationTests
{
    /// <summary>Real registries plus a stand-in Sql module. WorkflowEngine.OnJobDone needs *a* module for the phase to store the
    /// sqlgen draft; SqlModule arrives in T4.4.
    /// TODO(T4.4): drop this FakeModule factory (pass null to CliRunner) so the test runs against the real SqlModule.</summary>
    private static DbmServices WithFakeSqlModule(Workspace ws) =>
        DbmServices.Open(ws, null, s => ModuleRegistry.Create(s)
            .Append(new FakeModule(PhaseName.Sql, "sql-engineer", "sqlgen") { NeedsAgentResult = false }));

    [Fact]
    public async Task Gen_inline_then_validate_one_task()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        SqlGenJobTests.Prepare(project.Services);
        project.Services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Running);

        var gen = await CliRunner.RunAsync(project.Ws, WithFakeSqlModule, "sql", "gen", "--inline");
        Assert.True(gen.Exit == 0, gen.Out);
        Assert.Equal("awaiting_review", (string?)gen.Json["status"]);
        Assert.StartsWith("6 tasks, 0 errors,", (string?)gen.Json["summary"]);

        var check = await CliRunner.RunAsync(project.Ws, WithFakeSqlModule, "sql", "validate", "--task", "T05");
        Assert.True(check.Exit == 0, check.Out);
        Assert.True((bool)check.Json["ok"]!);
        Assert.NotNull(check.Json["taskErrors"]!["T05"]);
        Assert.DoesNotContain("Integrated Security", check.Out, StringComparison.OrdinalIgnoreCase);
    }
}
