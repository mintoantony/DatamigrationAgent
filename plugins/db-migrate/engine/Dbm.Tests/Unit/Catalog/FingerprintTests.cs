using Dbm.Core.Catalog;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Catalog;

public sealed class FingerprintTests
{
    private static CatalogSnapshot WithTable(CatalogSnapshot s, string key, Func<TableInfo, TableInfo> change) =>
        s with { Tables = s.Tables.Select(t => t.Key == key ? change(t) : t).ToList() };

    [Fact]
    public void Is_lower_hex_sha256_and_deterministic()
    {
        var fp = Fingerprint.Compute(TestCatalogs.LegacyShop());
        Assert.Matches("^[0-9a-f]{64}$", fp);
        Assert.Equal(fp, Fingerprint.Compute(TestCatalogs.LegacyShop()));
        Assert.NotEqual(fp, Fingerprint.Compute(TestCatalogs.ShopV2()));
    }

    [Fact]
    public void Ignores_rows_sizes_profiles_descriptions_time_order_and_fk_trust()
    {
        var baseline = TestCatalogs.LegacyShop();
        var changed = WithTable(baseline, "dbo.CUST", t => t with
        {
            Rows = 99, SizeMb = 42, Description = "customers",
            Columns = t.Columns.Select(c => c with { Profile = null }).ToList(),
        });
        changed = WithTable(changed, "dbo.ORD_HDR", t => t with
        {
            ForeignKeys = t.ForeignKeys.Select(f => f with { IsNotTrusted = false, IsDisabled = true }).ToList(),
        });
        changed = changed with { ExtractedAt = DateTimeOffset.UnixEpoch, Tables = Enumerable.Reverse(changed.Tables).ToList() };
        Assert.Equal(Fingerprint.Compute(baseline), Fingerprint.Compute(changed));
    }

    [Fact]
    public void Changes_with_structure()
    {
        var baseline = TestCatalogs.ShopV2();
        var fp = Fingerprint.Compute(baseline);
        var widened = WithTable(baseline, "app.Customers", t => t with
        {
            Columns = t.Columns.Select(c => c.Name == "Email" ? c with { MaxLength = 200 } : c).ToList(),
        });
        var added = WithTable(baseline, "app.Orders", t => t with
        {
            Columns = t.Columns.Append(TestCatalogs.Col("Channel", "varchar", 10) with { Ordinal = 8 }).ToList(),
        });
        var trigger = WithTable(baseline, "app.Products", t => t with { TriggerCount = 1, TriggerNames = new List<string> { "trg_x" } });
        Assert.NotEqual(fp, Fingerprint.Compute(widened));
        Assert.NotEqual(fp, Fingerprint.Compute(added));
        Assert.NotEqual(fp, Fingerprint.Compute(trigger));
    }

    [Fact]
    public void Collation_change_alters_hash()
    {
        var baseline = TestCatalogs.LegacyShop();
        var recollated = WithTable(baseline, "dbo.CUST", t => t with
        {
            Columns = t.Columns.Select(c => c.Name == "CUST_NM" ? c with { Collation = "Latin1_General_BIN2" } : c).ToList(),
        });
        Assert.NotEqual(Fingerprint.Compute(baseline), Fingerprint.Compute(recollated));
    }

    [Fact]
    public void Index_reordering_does_not_alter_hash()
    {
        var baseline = WithTable(TestCatalogs.LegacyShop(), "dbo.CUST", t => t with
        {
            Indexes = t.Indexes.Append(new IndexInfo("IX_CUST_NM", false, false, false, new List<string> { "CUST_NM" })).ToList(),
        });
        var reordered = WithTable(baseline, "dbo.CUST", t => t with { Indexes = Enumerable.Reverse(t.Indexes).ToList() });
        Assert.Equal(Fingerprint.Compute(baseline), Fingerprint.Compute(reordered));
    }
}
