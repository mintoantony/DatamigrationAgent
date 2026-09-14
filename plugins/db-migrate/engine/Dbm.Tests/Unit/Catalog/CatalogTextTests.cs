using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Catalog;

public sealed class CatalogTextTests
{
    private static readonly CatalogSnapshot Src = TestCatalogs.LegacyShop();

    [Fact]
    public void TableHeader()
    {
        Assert.Equal("src table dbo.CUST rows=1000 size=0.2MB pk=CUST_ID triggers=0", CatalogText.TableHeader(Side.Src, Src.FindTable("dbo.CUST")!));
        Assert.Equal("src table dbo.AUDIT_LOG rows=5000 size=1MB pk=none heap triggers=0", CatalogText.TableHeader(Side.Src, Src.FindTable("dbo.AUDIT_LOG")!));
        Assert.Equal("src table dbo.ORD_HDR rows=3005 size=0.6MB pk=ORD_ID triggers=0 fk→dbo.CUST(untrusted) fk→dbo.ORD_STATUS",
            CatalogText.TableHeader(Side.Src, Src.FindTable("dbo.ORD_HDR")!));
    }

    [Fact]
    public void ColumnLine()
    {
        var cust = Src.FindTable("dbo.CUST")!;
        Assert.Equal("CUST_ID int NN id pk", CatalogText.ColumnLine(cust, cust.FindColumn("CUST_ID")!));
        Assert.Equal("EMAIL_ADDR varchar(120) null=10% distinct=0.90 class=email samples=[first1.last1@example.com]",
            CatalogText.ColumnLine(cust, cust.FindColumn("EMAIL_ADDR")!));
        Assert.Equal("CRT_DT datetime NN default=(getdate())", CatalogText.ColumnLine(cust, cust.FindColumn("CRT_DT")!));
        var addr = Src.FindTable("dbo.ADDR")!;
        Assert.Equal("CUST_ID int NN fk→dbo.CUST.CUST_ID", CatalogText.ColumnLine(addr, addr.FindColumn("CUST_ID")!));
    }

    [Fact]
    public void Table_caps_columns()
    {
        var text = CatalogText.Table(Side.Src, Src.FindTable("dbo.CUST")!, maxColumns: 2);
        Assert.Equal(4, text.Split('\n').Length);
        Assert.EndsWith("  … 6 more columns", text);
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.004, "<1%")]
    [InlineData(0.1, "10%")]
    [InlineData(0.985, "99%")]
    public void Percent(double ratio, string expected) => Assert.Equal(expected, CatalogText.Percent(ratio));

    [Fact]
    public void SearchLine() =>
        Assert.Equal("0.82 tgt column app.Customers.Email — email",
            CatalogText.SearchLine(new SearchHit(Side.Tgt, "column", "app.Customers.Email", 0.8219, "app.Customers.Email — email")));
}
