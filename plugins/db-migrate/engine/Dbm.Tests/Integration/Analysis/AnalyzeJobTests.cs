using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Tests.Integration.Catalog;
using Dbm.Tests.Support;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Integration.Analysis;

[Trait("Category", "Integration")]
public sealed class AnalyzeJobTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Analyze_job_reports_the_sample_anomalies()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        await DiscoverJobTests.RunDiscoverAsync(project);
        var services = project.Services;
        var log = new List<string>();
        var job = services.Jobs.Get(services.Jobs.Enqueue("analyze", PhaseName.Analysis))!;

        var result = await new AnalyzeJob().RunAsync(new JobContext { Services = services, Job = job, Log = log.Add }, CancellationToken.None);

        Assert.Contains("orphan check dbo.ORD_HDR FK_ORD_CUST: 2", log);
        var payload = Json.FromNode<AnalysisPayload>(result.DraftPayload!);
        var findings = payload.Findings.Values.ToList();
        Assert.Contains(findings, f => f is { Rule: "R01", Object: "dbo.AUDIT_LOG", Severity: Severity.High });
        Assert.Contains(findings, f => f is { Rule: "R02", Object: "dbo.CUST.NOTES" });
        Assert.Contains(findings, f => f.Rule == "R06" && f.Object == "app.Orders" && f.Message.Contains("trg_Orders_Audit"));
        Assert.Contains(findings, f => f is { Rule: "R09", Object: "dbo.ORD_HDR", Severity: Severity.High, Count: 2 });
        Assert.Contains(findings, f => f is { Rule: "R10", Object: "dbo.CUST.FAX_NO" });
        Assert.Contains(findings, f => f is { Rule: "R11", Object: "dbo.TMP_IMPORT" });
        Assert.Contains(findings, f => f is { Rule: "R14", Object: "app.Addresses ↔ app.Customers" });
        Assert.DoesNotContain(findings, f => f.Rule == "R15");
        Assert.Equal(Analyzer.Summary(payload), result.Summary);
        Assert.Equal(19_711, payload.Estimates.TotalRows);
        Assert.Null(result.DraftPayload!["narrative"]);
    }

    [Fact]
    public async Task CountOrphansAsync_leaves_the_key_absent_when_the_query_fails()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        var connectionString = project.Services.Connections.GetConnectionString(Side.Src);
        var log = new List<string>();

        // A real, open connection, but an FK pointed at a table that doesn't exist on this database: the query
        // itself fails ("Invalid object name"), which is the per-candidate catch, not the connection-open one.
        var brokenFk = Fk("FK_ORD_CUST", "CUST_ID", "dbo.NOPE_TABLE", "CUST_ID", notTrusted: true);
        var src = LegacyShop() with
        {
            Tables = LegacyShop().Tables.Select(t => t.Key == "dbo.ORD_HDR" ? t with { ForeignKeys = new List<ForeignKeyInfo> { brokenFk } } : t).ToList(),
        };

        var counts = await AnalyzeJob.CountOrphansAsync(connectionString, src, log.Add, CancellationToken.None);

        // Key absent (not 0): the query never returned a count, so R09 must read this as "not counted" (medium).
        Assert.False(counts.ContainsKey(RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")));
        Assert.Empty(counts);
        Assert.Contains(log, l => l.StartsWith("orphan check skipped for dbo.ORD_HDR FK_ORD_CUST:", StringComparison.Ordinal));
    }
}
