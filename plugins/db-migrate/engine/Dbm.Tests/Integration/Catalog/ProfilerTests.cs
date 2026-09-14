using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Catalog;

[Trait("Category", "Integration")]
public sealed class ProfilerTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    private static async Task<(CatalogSnapshot Snapshot, List<string> Log)> ProfileAsync(string connectionString, bool sampleValues = true)
    {
        var log = new List<string>();
        var meta = await SqlConnect.ProbeAsync(connectionString, CancellationToken.None);
        await using var conn = await SqlConnect.OpenAsync(connectionString, CancellationToken.None);
        var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, CancellationToken.None);
        var profiled = await Profiler.ProfileAsync(conn, snapshot, new ProfileOptions(100_000, sampleValues), log.Add, CancellationToken.None);
        return (profiled, log);
    }

    [Fact]
    public async Task Source_profiles()
    {
        var (src, log) = await ProfileAsync(fixture.Pair.SourceCs);
        Assert.Equal(8, log.Count(l => l.StartsWith("profiled ", StringComparison.Ordinal)));
        Assert.Contains("profiled dbo.CUST: 1000 rows sampled, 8 columns", log);

        var cust = src.FindTable("dbo.CUST")!;
        var email = cust.FindColumn("EMAIL_ADDR")!.Profile!;
        Assert.Equal(1000, email.SampledRows);
        Assert.Equal(0.1, email.NullRatio, 3);
        Assert.Equal("email", email.SemanticClass);
        Assert.Equal(new[] { "a9.a9@a.a" }, email.TopPatterns);
        Assert.Equal(3, email.Samples.Count);
        Assert.All(email.Samples, s => Assert.EndsWith("@example.com", s));
        Assert.Equal(900, email.Distinct);

        var id = cust.FindColumn("CUST_ID")!.Profile!;
        Assert.Equal(("1", "1000", 1000L), (id.Min, id.Max, id.Distinct!.Value));
        Assert.Equal("integer", id.SemanticClass);

        var notes = cust.FindColumn("NOTES")!.Profile!;
        Assert.Null(notes.Distinct);
        Assert.Null(notes.Min);
        Assert.NotNull(notes.MaxLen);
        Assert.Equal(0.75, notes.NullRatio, 3);

        Assert.Equal("phone", cust.FindColumn("PHONE_NO")!.Profile!.SemanticClass);
        Assert.Equal("datetime", cust.FindColumn("DOB")!.Profile!.SemanticClass);
        Assert.Equal(0.98, cust.FindColumn("FAX_NO")!.Profile!.NullRatio, 3);
        Assert.Equal("flag", src.FindTable("dbo.PROD")!.FindColumn("ACTIVE_FLG")!.Profile!.SemanticClass);
        var addr = src.FindTable("dbo.ADDR")!;
        Assert.Equal("country_code", addr.FindColumn("CTRY_CD")!.Profile!.SemanticClass);
        Assert.Equal("postal_code", addr.FindColumn("ZIP")!.Profile!.SemanticClass);
        Assert.Equal(300, src.FindTable("dbo.ORD_HDR")!.FindColumn("CMNT")!.Profile!.MaxLen);
        Assert.Equal(0, src.FindTable("dbo.TMP_IMPORT")!.FindColumn("X")!.Profile!.SampledRows);
    }

    [Fact]
    public async Task Target_profiles_of_empty_tables()
    {
        var (tgt, _) = await ProfileAsync(fixture.Pair.TargetCs);
        var email = tgt.FindTable("app.Customers")!.FindColumn("Email")!.Profile!;
        Assert.Equal(0, email.SampledRows);
        Assert.Null(email.SemanticClass);
        Assert.Empty(email.Samples);
        Assert.NotNull(tgt.FindTable("app.Products")!.FindColumn("RowVer")!.Profile);
    }

    [Fact]
    public async Task Sample_values_can_be_disabled()
    {
        var (src, _) = await ProfileAsync(fixture.Pair.SourceCs, sampleValues: false);
        var email = src.FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!.Profile!;
        Assert.Empty(email.Samples);
        Assert.Null(email.Min);
        Assert.Equal("email", email.SemanticClass);
    }
}
