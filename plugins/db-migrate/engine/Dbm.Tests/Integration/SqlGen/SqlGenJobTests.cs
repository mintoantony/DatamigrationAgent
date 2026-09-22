using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public sealed class SqlGenJobTests
{
    /// <summary>Both sample catalogs saved; the C15 ground-truth mapping stored and approved (when <paramref name="approveMapping"/>).</summary>
    internal static void Prepare(DbmServices s, bool approveMapping = true)
    {
        s.Catalog.Save(Side.Src, SampleCatalogs.Source(), "src-fp");
        s.Catalog.Save(Side.Tgt, SampleCatalogs.Target(), "tgt-fp");
        if (!approveMapping) return;
        var v = s.Artifacts.NextVersion(PhaseName.Mapping);
        s.Artifacts.Add(PhaseName.Mapping, v, Json.Serialize(SampleMappings.Approved()), "human", "approved mapping");
        s.Phases.SetApproved(PhaseName.Mapping, v, null);
    }

    private static JobContext Ctx(DbmServices s, List<string> log) =>
        new() { Services = s, Job = s.Jobs.Get(s.Jobs.Enqueue("sqlgen", PhaseName.Sql))!, Log = log.Add };

    [Fact]
    public async Task Job_generates_and_validates_the_sample_plan()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        Prepare(project.Services);
        var log = new List<string>();

        var result = await new SqlGenJob().RunAsync(Ctx(project.Services, log), CancellationToken.None);

        var plan = Json.FromNode<SqlPlanPayload>(result.DraftPayload!);
        Assert.Equal(6, plan.Tasks.Count);
        Assert.True(plan.ErrorCount() == 0, string.Join("\n", plan.Tasks.SelectMany(t => t.Value.Errors.Select(e => $"{t.Key}: {e}"))));
        Assert.Equal($"6 tasks, 0 errors, {plan.WarningCount()} warnings", result.Summary);
        Assert.Contains(plan.Tasks.Values.Single(t => t.Target == "app.Orders").Warnings,
            w => w.StartsWith("validate: Comment: varchar(500) -> nvarchar(200)", StringComparison.Ordinal));
        Assert.DoesNotContain("Integrated Security", Json.Serialize(plan), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, l => l.StartsWith("sqlgen: 6 task(s) generated", StringComparison.Ordinal));
        // Ruling 204: the job validated live, so the draft carries positive evidence of it.
        Assert.True(plan.Validation is { Ok: true }, "a live sqlgen draft stored no validation evidence");
        var at = DateTimeOffset.ParseExact(plan.Validation!.At, "O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(5), "evidence time " + plan.Validation.At);
    }

    /// <summary>H1: an offline sqlgen draft records, in the stored plan, that nothing validated it — and that blocks approval.</summary>
    [Fact]
    public async Task Offline_job_stores_the_not_validated_marker()
    {
        using var project = TempProject.Create();
        Prepare(project.Services);

        var result = await new SqlGenJob().RunAsync(Ctx(project.Services, new List<string>()), CancellationToken.None);

        const string marker = "live validation skipped: source connection, target connection missing";
        Assert.Contains(marker, Json.FromNode<SqlPlanPayload>(result.DraftPayload!).Warnings);
        Assert.Null(Json.FromNode<SqlPlanPayload>(result.DraftPayload!).Validation);   // Ruling 204: offline, no evidence
        var ctx = new Dbm.Core.Workflow.ModuleContext
        {
            Services = project.Services, OpenFeedback = [],
            Current = project.Services.Artifacts.Add(PhaseName.Sql, 0, result.DraftPayload!.ToJsonString(), "script", "draft"),
        };
        Assert.Contains(SqlModule.NotValidatedBlocker + marker, new SqlModule().ApprovalBlockers(ctx, result.DraftPayload!));
    }

    /// <summary>Ruling 57: generated output does not pass through SqlModule.Validate before it is stored, so the job records the
    /// bare-CR check itself. Here the CR arrives through carried-over custom SQL.</summary>
    [Fact]
    public async Task Job_records_a_bare_carriage_return_in_carried_over_sql()
    {
        using var project = TempProject.Create();
        var s = project.Services;
        Prepare(s);
        var previous = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var orders = previous.Tasks.Single(kv => kv.Value.Target == "app.Orders").Key;
        previous.Tasks[orders].Custom = true;
        previous.Tasks[orders].PreSql.Insert(0, "-- note\rDELETE FROM app.Customers");
        s.Artifacts.Add(PhaseName.Sql, s.Artifacts.NextVersion(PhaseName.Sql), Json.Serialize(previous), "agent", "custom");

        var result = await new SqlGenJob().RunAsync(Ctx(s, new List<string>()), CancellationToken.None);

        var plan = Json.FromNode<SqlPlanPayload>(result.DraftPayload!);
        var task = plan.Tasks.Single(kv => kv.Value.Target == "app.Orders");
        Assert.Equal("-- note\rDELETE FROM app.Customers", task.Value.PreSql[0]);
        const string line = "preSql[0]: bare carriage return at line 1 (SQL Server treats it as a line break; use CRLF or LF)";
        Assert.Equal([line], task.Value.Errors);
        Assert.Contains(", 1 errors,", result.Summary);
    }

    /// <summary>Review L5: the evidence's outcome is pinned - a live sqlgen whose validation finds an error stores ok false.</summary>
    [Fact]
    public async Task A_live_job_whose_validation_finds_errors_stores_ok_false()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        var s = project.Services;
        Prepare(s);
        var previous = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var orders = previous.Tasks.Single(kv => kv.Value.Target == "app.Orders").Key;
        previous.Tasks[orders].Custom = true;
        previous.Tasks[orders].SourceQuery = previous.Tasks[orders].SourceQuery.Replace("SELECT", "SELECT s.[NO_SUCH_COLUMN] AS [x_dbm_missing],", StringComparison.Ordinal);
        s.Artifacts.Add(PhaseName.Sql, s.Artifacts.NextVersion(PhaseName.Sql), Json.Serialize(previous), "agent", "custom");

        var result = await new SqlGenJob().RunAsync(Ctx(s, new List<string>()), CancellationToken.None);

        var plan = Json.FromNode<SqlPlanPayload>(result.DraftPayload!);
        Assert.True(plan.ErrorCount() > 0, "fixture guard: the carried-over SQL must fail live validation");
        Assert.True(plan.Validation is { Ok: false }, "a live sqlgen with errors did not store ok:false evidence: " + Json.Serialize(plan.Validation));
    }

    /// <summary>Review L3: a sqlgen whose connections fail compiled nothing. It stores no evidence, and the reason it is not validated
    /// names the failed connection - never "Validated live · errors found".</summary>
    [Fact]
    public async Task A_job_that_could_not_connect_stores_no_evidence()
    {
        using var project = TempProject.Create();
        var s = project.Services;
        Prepare(s);
        const string dead = "Server=tcp:127.0.0.1,1;Database=x;Integrated Security=true;Connect Timeout=2;TrustServerCertificate=True";
        s.Connections.Save(Side.Src, dead, FakeServices.Meta("127.0.0.1", "x"));
        s.Connections.Save(Side.Tgt, dead, FakeServices.Meta("127.0.0.1", "x"));

        var result = await new SqlGenJob().RunAsync(Ctx(s, new List<string>()), CancellationToken.None);

        var plan = Json.FromNode<SqlPlanPayload>(result.DraftPayload!);
        Assert.Contains(plan.Errors, e => e.StartsWith("source connection failed: ", StringComparison.Ordinal));   // fixture guard
        Assert.True(plan.Validation is null, "a sqlgen that could not connect stored validation evidence: " + Json.Serialize(plan.Validation));
        Assert.Contains(plan.NotValidatedReasons(), r => r.StartsWith(SqlPlanPayload.CouldNotConnect, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_requires_an_approved_mapping()
    {
        using var project = TempProject.Create();
        Prepare(project.Services, approveMapping: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqlGenJob().RunAsync(Ctx(project.Services, new List<string>()), CancellationToken.None));
        Assert.Contains("no approved version", ex.Message);
    }
}
