using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Catalog;

[Trait("Category", "Integration")]
public sealed class DiscoverJobTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    public static async Task<List<string>> RunDiscoverAsync(TempProject project)
    {
        var log = new List<string>();
        var services = project.Services;
        var job = services.Jobs.Get(services.Jobs.Enqueue("discover", PhaseName.Discovery))!;
        var result = await new DiscoverJob().RunAsync(new JobContext { Services = services, Job = job, Log = log.Add }, CancellationToken.None);
        Assert.Null(result.DraftPayload);
        Assert.Equal("src: 8 tables, tgt: 6 tables", result.Summary);
        return log;
    }

    [Fact]
    public async Task Discover_saves_catalogs_fingerprints_and_vectors_then_search_and_show_work()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        var log = await RunDiscoverAsync(project);
        var catalog = project.Services.Catalog;

        Assert.Contains(log, l => l.StartsWith("src: profiled dbo.CUST", StringComparison.Ordinal));
        Assert.Equal(8, catalog.Get(Side.Src)!.Tables.Count);
        Assert.Equal(Fingerprint.Compute(catalog.Get(Side.Tgt)!), catalog.Fingerprint(Side.Tgt));
        Assert.Equal(8 + 6, catalog.Vectors(kind: "table").Count);

        var r = await CliRunner.RunAsync(project.Ws, null, "search", "customer", "email", "--side", "tgt", "-k", "3");
        Assert.Equal(0, r.Exit);
        Assert.Matches(@"^\d\.\d\d tgt column app\.Customers\.Email — email$", r.Out.Split('\n')[0].TrimEnd('\r'));

        r = await CliRunner.RunAsync(project.Ws, null, "search", "customer email", "--json");
        Assert.Equal(0, r.Exit);
        Assert.Contains(r.Json.AsArray(), h => (string?)h!["key"] == "dbo.CUST.EMAIL_ADDR");

        r = await CliRunner.RunAsync(project.Ws, null, "show", "dbo.CUST");
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("src table dbo.CUST rows=1000 ", r.Out);
        Assert.Contains("  EMAIL_ADDR varchar(120) null=10% distinct=0.90 maxlen=", r.Out);
        Assert.Contains("class=email samples=[", r.Out);

        r = await CliRunner.RunAsync(project.Ws, null, "show", "app.Customers.Email");
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("tgt column app.Customers.Email", r.Out);
        Assert.Contains("Email nvarchar(120) empty", r.Out);

        r = await CliRunner.RunAsync(project.Ws, null, "show", "CUST", "--json");
        Assert.Equal(0, r.Exit);
        Assert.Equal("src", (string?)r.Json["side"]);

        r = await CliRunner.RunAsync(project.Ws, null, "show", "dbo.NOPE");
        Assert.Equal(1, r.Exit);
        Assert.Equal("not_found", (string?)r.Json["error"]);
    }

    /// <summary>Ruling 199 (fix round 1, F4): the job read "on" when it started; the user switched sample values off while it ran.
    /// Each side is saved as it finishes, so each save re-reads the setting and stores no sample value when it is off.</summary>
    [Fact]
    public async Task A_discovery_that_runs_while_sample_values_are_switched_off_stores_none()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        var services = project.Services;
        var job = services.Jobs.Get(services.Jobs.Enqueue("discover", PhaseName.Discovery))!;
        var switched = false;
        void Log(string line)
        {
            if (switched || !line.StartsWith("src: profiled", StringComparison.Ordinal)) return;
            switched = true;
            SampleValuesSetting.Set(services, on: false);   // mid-job, after profiling began with the setting on
        }

        await new DiscoverJob().RunAsync(new JobContext { Services = services, Job = job, Log = Log }, CancellationToken.None);

        Assert.True(switched, "the job never logged a profiled table, so the switch was never flipped mid-job");
        var leaks = new[] { Side.Src, Side.Tgt }.SelectMany(side => services.Catalog.Get(side)!.Tables.SelectMany(t => t.Columns
                .Where(c => c.Profile is { } p && (p.Samples.Count > 0 || (TypeTraits.IsString(c.DataType) && (p.Min ?? p.Max) is not null)))
                .Select(c => $"{EnumText.ToText(side)} {t.Key}.{c.Name}: [{string.Join(", ", c.Profile!.Samples)}] {c.Profile.Min}..{c.Profile.Max}")))
            .ToList();
        Assert.True(leaks.Count == 0, "sample values were switched off during discovery, yet it stored: " + string.Join("; ", leaks.Take(5)));
    }

    [Fact]
    public async Task Drift_checker_detects_structural_changes_only()
    {
        await using var pair = await SampleDatabases.CreateAsync(seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        await RunDiscoverAsync(project);
        Assert.False((await DriftChecker.CheckAsync(project.Services, CancellationToken.None)).Any);

        await using (var conn = new SqlConnection(pair.TargetCs))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("ALTER TABLE app.Orders ADD Channel varchar(10) NULL;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var drift = await DriftChecker.CheckAsync(project.Services, CancellationToken.None);
        Assert.False(drift.SrcChanged);
        Assert.True(drift.TgtChanged);
    }
}
