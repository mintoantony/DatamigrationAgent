using Dbm.Cli.Commands;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Cli;

public sealed class SqlCommandsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Commands_use_the_contract_names()
    {
        Assert.Equal("sql gen", new SqlGenCommand().Name);
        Assert.Equal("sql validate", new SqlValidateCommand().Name);
    }

    [Fact]
    public async Task Gen_is_refused_outside_the_sql_phase()
    {
        _workspace.OpenServices();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "gen");
        Assert.Equal(1, r.Exit);
        Assert.Equal("wrong_phase", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Gen_while_drafting_requeues_the_job_and_reruns_the_phase()
    {
        var s = _workspace.OpenServices();
        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Drafting);
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "gen");
        Assert.Equal(0, r.Exit);
        Assert.Equal("sqlgen", (string?)r.Json["queued"]);
        Assert.Equal(PhaseStatus.Running, s.Phases.Get(PhaseName.Sql).Status);
        Assert.Contains(s.Jobs.Active(), j => j.Kind == "sqlgen" && j.Phase == PhaseName.Sql);
    }

    [Fact]
    public async Task Validate_needs_a_version()
    {
        _workspace.OpenServices();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--task", "T01");
        Assert.Equal(1, r.Exit);
        Assert.Equal("no_version", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Missing_project_is_reported()
    {
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate");
        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", (string?)r.Json["error"]);
    }
}
