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
