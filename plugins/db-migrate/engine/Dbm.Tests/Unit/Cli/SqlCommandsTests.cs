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

    /// <summary>Open item 8: `not_found` (SqlCommands.cs) had no test - the --patch file itself is missing.</summary>
    [Fact]
    public async Task Validate_reports_a_missing_patch_file()
    {
        WithSqlVersion();
        var missing = Path.Combine(_workspace.Root, "does-not-exist.json");
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--patch", missing);
        Assert.Equal(1, r.Exit);
        Assert.Equal("not_found", (string?)r.Json["error"]);
        Assert.Contains(missing, (string?)r.Json["message"]);
    }

    /// <summary>Open item 8: the OTHER `invalid_patch` emitter (Patch.Parse's PatchException on malformed JSON), distinct from
    /// the phase-mismatch path above, which is the only one previously covered.</summary>
    [Fact]
    public async Task Validate_refuses_a_patch_file_that_is_not_valid_json()
    {
        WithSqlVersion();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--patch", PatchFile("not json"));
        Assert.Equal(1, r.Exit);
        Assert.Equal("invalid_patch", (string?)r.Json["error"]);
        Assert.Contains("not valid JSON", (string?)r.Json["message"]);
    }

    /// <summary>Open item 8: `bad_payload` (SqlCommands.cs) had no test - a structurally valid patch produces a payload that
    /// does not deserialize into SqlPlanPayload (chunkSize expects a number).</summary>
    [Fact]
    public async Task Validate_reports_a_patch_that_produces_an_undeserialisable_payload()
    {
        WithSqlVersion();
        var patch = PatchFile("{\"phase\":\"sql\",\"baseVersion\":0,\"ops\":[{\"op\":\"replace\",\"path\":\"/tasks/T01/identityInsert\",\"value\":\"not-a-bool\"}]}");
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--patch", patch);
        Assert.Equal(1, r.Exit);
        Assert.Equal("bad_payload", (string?)r.Json["error"]);
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

    const string CrPatch = "{\"phase\":\"sql\",\"baseVersion\":0,\"ops\":[{\"op\":\"add\",\"path\":\"/tasks/T05/preSql/-\",\"value\":\"-- note\\rDELETE FROM app.Orders\"}]}";
    const string CrLine = "preSql[0]: bare carriage return at line 1 (SQL Server treats it as a line break; use CRLF or LF)";

    /// <summary>Open item 17: step 1 of sql-engineer.md's self-check must agree with step 2 (<c>dbm apply --dry-run</c>) about the bare
    /// carriage return the playbook just taught. The scan is offline, so it answers without any connection: not "not_ready", but the
    /// error itself, and a globalWarnings line saying live validation did not run (never [], which claims "checked").</summary>
    [Fact]
    public async Task Validate_patch_reports_a_bare_carriage_return_without_any_connection()
    {
        WithSqlVersion();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--patch", PatchFile(CrPatch));
        Assert.True(r.Exit == 1, "sql validate --patch green-lit a bare carriage return offline: " + r.Out);
        Assert.True(r.Json["error"] is null, "sql validate --patch needed live connections to report an offline bare carriage return: " + r.Out);
        Assert.False((bool)r.Json["ok"]!);
        Assert.True((bool)r.Json["patched"]!);
        Assert.Equal([CrLine], r.Json["taskErrors"]!["T05"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["live validation skipped: source connection, target connection, target catalog missing"],
            r.Json["globalWarnings"]!.AsArray().Select(n => (string?)n));
    }

    /// <summary>Open item 17, live path: the bare-CR lines join the live report, so ok is false even when the server has nothing to
    /// say. A global statement's carriage return is plan-level (globalErrors); the unknown task keeps this free of any SQL Server.</summary>
    [Fact]
    public async Task Validate_patch_adds_bare_carriage_returns_to_the_live_report()
    {
        var s = WithSqlVersion();
        s.Connections.Save(Side.Src, FakeServices.SrcConnection, FakeServices.Meta("src-host", "Legacy"));
        s.Connections.Save(Side.Tgt, FakeServices.TgtConnection, FakeServices.Meta("tgt-host", "ShopV2"));
        s.Catalog.Save(Side.Tgt, SampleCatalogs.Target(), "tgt-fp");
        var patch = PatchFile("{\"phase\":\"sql\",\"baseVersion\":0,\"ops\":[{\"op\":\"add\",\"path\":\"/preSql/-\",\"value\":\"-- x\\rDELETE FROM app.Orders\"}]}");

        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--task", "T99", "--patch", patch);

        Assert.Equal(1, r.Exit);
        var global = r.Json["globalErrors"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("unknown task T99", global);
        Assert.True(global.Any(g => g!.Contains(SqlValidator.BareCarriageReturnMarker, StringComparison.Ordinal)),
            "the live report left out the global statement's bare carriage return: " + r.Out);
    }

    /// <summary>Review L4: `dbm artifact sql` states the validation fact in so many words - computed beside the payload, never stored
    /// in it - instead of leaving it to the absence of a field.</summary>
    [Fact]
    public async Task Artifact_sql_says_whether_the_version_is_validated()
    {
        var s = WithSqlVersion();   // no evidence
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "artifact", "sql");
        Assert.Equal(0, r.Exit);
        Assert.True(r.Json["validated"] is not null && !(bool)r.Json["validated"]!, "dbm artifact sql does not say the version is not validated: " + r.Out[..Math.Min(300, r.Out.Length)]);
        Assert.Equal([SqlModule.NoEvidence], r.Json["notValidatedReasons"]!.AsArray().Select(n => (string?)n));
        Assert.Null(r.Json["payload"]!["validated"]);   // computed, not in the payload

        var plan = Json.Deserialize<SqlPlanPayload>(s.Artifacts.Get(PhaseName.Sql, 0)!.PayloadJson);
        plan.Validation = SqlValidation.From(DateTimeOffset.UtcNow, true);
        s.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(plan), "script", "validated");
        s.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        var v = await CliRunner.RunAsync(_workspace.Ws, null, "artifact", "sql");
        Assert.True((bool)v.Json["validated"]!);
        Assert.Empty(v.Json["notValidatedReasons"]!.AsArray());
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
