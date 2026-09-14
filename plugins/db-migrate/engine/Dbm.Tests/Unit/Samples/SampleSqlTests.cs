using Dbm.Core.Samples;

namespace Dbm.Tests.Unit.Samples;

public sealed class SampleSqlTests
{
    [Fact]
    public void SplitBatches_splits_on_lines_that_contain_only_GO()
    {
        var batches = SampleSql.SplitBatches("CREATE SCHEMA app;\r\nGO\nSELECT 'GO' AS x; -- GO\n  go  -- trailing comment\n\nGO\n");
        Assert.Equal(new[] { "CREATE SCHEMA app;", "SELECT 'GO' AS x; -- GO" }, batches);
    }

    [Fact]
    public void Embedded_scripts_are_present()
    {
        Assert.Contains("CREATE TABLE dbo.CUST", SampleSql.LegacyShopSchema);
        Assert.Contains("CREATE TABLE dbo.TMP_IMPORT", SampleSql.LegacyShopSchema);
        Assert.Contains("CREATE TRIGGER app.trg_Orders_Audit", SampleSql.ShopV2Schema);
        Assert.Equal(3, SampleSql.SplitBatches(SampleSql.ShopV2Schema).Count);
    }

    [Fact]
    public void Seed_replaces_the_scale_token_and_ends_with_the_untrusted_fk()
    {
        var seed = SampleSql.Seed(3);
        Assert.DoesNotContain("$(scale)", seed);
        Assert.Contains("DECLARE @scale int = 3;", seed);
        Assert.EndsWith(SampleSql.UntrustedForeignKey, seed.TrimEnd());
    }

    [Fact]
    public void Seed_rejects_scale_below_one() => Assert.Throws<ArgumentOutOfRangeException>(() => SampleSql.Seed(0));
}
