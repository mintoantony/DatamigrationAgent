using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.SqlGen;
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

    /// <summary>A stored sql v0 (the generated sample plan) as the current version.</summary>
    private DbmServices WithSqlVersion()
    {
        var s = _workspace.OpenServices();
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        s.Artifacts.Add(PhaseName.Sql, 0, Json.Serialize(plan), "script", "sample plan");
        s.Phases.SetCurrentVersion(PhaseName.Sql, 0);
        return s;
    }

    private string PatchFile(string json)
    {
        var path = Path.Combine(_workspace.Root, "patch-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Validate_refuses_a_patch_for_another_phase()
    {
        WithSqlVersion();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--patch", PatchFile("{\"phase\":\"mapping\",\"baseVersion\":0,\"ops\":[]}"));
        Assert.Equal(1, r.Exit);
        Assert.Equal("invalid_patch", (string?)r.Json["error"]);
        Assert.Equal("patch.phase must be 'sql'.", (string?)r.Json["message"]);
    }

    [Fact]
    public async Task Validate_refuses_a_stale_patch()
    {
        WithSqlVersion();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--patch", PatchFile("{\"phase\":\"sql\",\"baseVersion\":3,\"ops\":[]}"));
        Assert.Equal(1, r.Exit);
        Assert.Equal("stale_patch", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Validate_needs_connections_and_the_target_catalog()
    {
        WithSqlVersion();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate");
        Assert.Equal(1, r.Exit);
        Assert.Equal("not_ready", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Validate_exits_1_when_the_report_is_not_ok()
    {
        var s = WithSqlVersion();
        s.Connections.Save(Side.Src, FakeServices.SrcConnection, FakeServices.Meta("src-host", "Legacy"));
        s.Connections.Save(Side.Tgt, FakeServices.TgtConnection, FakeServices.Meta("tgt-host", "ShopV2"));
        s.Catalog.Save(Side.Tgt, SampleCatalogs.Target(), "tgt-fp");
        // An unknown task id is reported before any connection is opened, so this needs no SQL Server.
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--task", "T99");
        Assert.Equal(1, r.Exit);
        Assert.False((bool)r.Json["ok"]!);
        Assert.Equal("unknown task T99", (string?)r.Json["globalErrors"]![0]);
        Assert.Equal(SqlValidator.SingleTaskGlobalWarning, (string?)r.Json["globalWarnings"]![0]);
    }

    [Fact]
    public async Task Gen_inline_reports_a_failed_job()
    {
        var s = _workspace.OpenServices();
        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Running);   // no approved mapping: the sqlgen job throws
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "gen", "--inline");
        Assert.Equal(1, r.Exit);
        Assert.Equal("job_failed", (string?)r.Json["error"]);
        Assert.Contains("no approved version", (string?)r.Json["message"]);
    }

    [Fact]
    public async Task Missing_project_is_reported()
    {
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate");
        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", (string?)r.Json["error"]);
    }
}
