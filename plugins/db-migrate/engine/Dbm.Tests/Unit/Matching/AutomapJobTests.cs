using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Matching;

public class AutomapJobTests
{
    private static JobContext Ctx(DbmServices services, List<string> log) => new()
    {
        Services = services,
        Job = new JobRow(1, "automap", PhaseName.Mapping, JobStatus.Running, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
        Log = log.Add
    };

    [Fact]
    public void Kind_is_automap_and_the_handler_is_registered()
    {
        Assert.Equal("automap", new AutomapJob().Kind);
        using var project = TempProject.Create();
        Assert.IsType<AutomapJob>(project.Services.JobHandlers["automap"]);
    }

    [Fact]
    public async Task Drafts_a_mapping_from_the_stored_catalogs()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var log = new List<string>();

        var result = await new AutomapJob().RunAsync(Ctx(services, log), CancellationToken.None);

        var m = Json.FromNode<MappingPayload>(result.DraftPayload!);
        Assert.Equal(6, m.Tables.Count);
        var misses = AutoMapperBaseline.Misses(m);
        Assert.True(misses.Count == 0, string.Join("\n", misses));
        Assert.Matches(@"^6 tables mapped, \d+ need attention, \d+ blockers$", result.Summary);
        Assert.Contains(log, l => l.Contains("carry-over none"));
    }

    [Fact]
    public async Task Carries_over_the_latest_mapping_artifact()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.AddMapping(SampleMappings.Approved(), PhaseStatus.Running, "human");
        var log = new List<string>();

        var result = await new AutomapJob().RunAsync(Ctx(services, log), CancellationToken.None);

        Assert.Equal("6 tables mapped, 0 need attention, 0 blockers", result.Summary);
        var m = Json.FromNode<MappingPayload>(result.DraftPayload!);
        Assert.Equal(MapMethod.Carried, m.Tables["app.Customers"].Columns["FirstName"].Method);
        Assert.Contains(log, l => l.Contains("carry-over v0"));
    }

    [Fact]
    public async Task Uses_the_project_auto_accept_setting()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var strict = await new AutomapJob().RunAsync(Ctx(services, []), CancellationToken.None);
        services.Project.SaveSettings(services.Project.GetSettings() with { AutoAcceptScore = 0.5 });

        var lenient = await new AutomapJob().RunAsync(Ctx(services, []), CancellationToken.None);

        static int Attention(string? summary) => int.Parse(summary!.Split(", ")[1].Split(' ')[0]);
        Assert.True(Attention(lenient.Summary) < Attention(strict.Summary), $"{lenient.Summary} vs {strict.Summary}");
    }

    [Fact]
    public async Task Fails_when_discovery_has_not_run()
    {
        using var project = TempProject.Create();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new AutomapJob().RunAsync(Ctx(project.Services, []), CancellationToken.None));
        Assert.Contains("run discovery first", ex.Message);
    }
}
