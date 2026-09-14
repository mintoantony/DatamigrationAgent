using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public sealed class CatalogEndpointsTests
{
    [Fact]
    public void ListTables_reports_keys_counts_and_fk_degrees()
    {
        var list = CatalogEndpoints.ListTables(TestCatalogs.ShopV2());
        Assert.Equal(6, list.Count);
        var customers = list.Single(t => t.Key == "app.Customers");
        Assert.Equal((10, true, false, 1, 2, 0), (customers.Columns, customers.HasPk, customers.IsHeap, customers.FkOut, customers.FkIn, customers.Triggers));
        Assert.Equal(new[] { "app.Addresses" }, customers.Refs);
        var orders = list.Single(t => t.Key == "app.Orders");
        Assert.Equal((2, 1, 1), (orders.FkOut, orders.FkIn, orders.Triggers));
        Assert.True(list.Single(t => t.Key == "app.AuditEvents").IsHeap);
        Assert.Contains("\"hasPk\":true", Json.Serialize(customers));
    }

    [Fact]
    public void Detail_includes_profiles_best_key_and_referencing_tables()
    {
        var detail = CatalogEndpoints.Detail(Side.Src, TestCatalogs.LegacyShop(), "DBO.cust")!;
        Assert.Equal(("src", "dbo.CUST", false), (detail.Side, detail.Key, detail.IsHeap));
        Assert.Equal(new[] { "CUST_ID" }, detail.BestKey);
        Assert.Equal(new[] { "dbo.ADDR", "dbo.ORD_HDR" }, detail.ReferencedBy.Select(r => r.Table).OrderBy(t => t, StringComparer.Ordinal));
        var json = Json.Serialize(detail);
        Assert.Contains("\"profile\":{\"sampledRows\":1000,\"nulls\":100", json);
        Assert.Null(CatalogEndpoints.Detail(Side.Src, TestCatalogs.LegacyShop(), "dbo.NOPE"));
    }

    [Theory]
    [InlineData("src", true, Side.Src)]
    [InlineData("TGT", true, Side.Tgt)]
    [InlineData("both", false, Side.Src)]
    public void TryParseSide(string text, bool ok, Side expected)
    {
        Assert.Equal(ok, CatalogEndpoints.TryParseSide(text, out var side));
        if (ok) Assert.Equal(expected, side);
    }
}
