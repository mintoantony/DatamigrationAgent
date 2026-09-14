using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Tests.Support;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Unit.Catalog;

public sealed class CatalogModelTests
{
    [Theory]
    [InlineData("nvarchar", 50, 0, 0, "nvarchar(50)")]
    [InlineData("varchar", -1, 0, 0, "varchar(max)")]
    [InlineData("varbinary", 16, 0, 0, "varbinary(16)")]
    [InlineData("decimal", 0, 19, 4, "decimal(19,4)")]
    [InlineData("datetime2", 0, 19, 0, "datetime2(0)")]
    [InlineData("timestamp", 0, 0, 0, "rowversion")]
    [InlineData("int", 0, 10, 0, "int")]
    public void TypeDisplay(string type, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, Col("C", type, maxLength, precision: precision, scale: scale).TypeDisplay);

    [Theory]
    [InlineData("nvarchar", 100, 50)]
    [InlineData("nchar", -1, -1)]
    [InlineData("varchar", 120, 120)]
    [InlineData("varbinary", -1, -1)]
    [InlineData("int", 4, 0)]
    [InlineData("text", 16, 0)]
    public void NormalizeMaxLength(string type, int raw, int expected) =>
        Assert.Equal(expected, CatalogExtractor.NormalizeMaxLength(type, raw));

    [Fact]
    public void BestKey_prefers_the_primary_key()
    {
        var t = Table("dbo.ORD_LINE", 1, new[] { Col("ORD_ID", "int", nullable: false), Col("LINE_NO", "smallint", nullable: false) },
            pk: new[] { "ORD_ID", "LINE_NO" });
        Assert.Equal(new[] { "ORD_ID", "LINE_NO" }, t.BestKey());
    }

    [Fact]
    public void BestKey_falls_back_to_a_unique_index_on_not_null_columns()
    {
        var t = Table("dbo.X", 1, new[] { Col("A", "int"), Col("B", "int", nullable: false) }, indexes: new[]
        {
            new IndexInfo("UX_A", false, true, false, new List<string> { "A" }),
            new IndexInfo("UX_B", false, true, false, new List<string> { "B" }),
        });
        Assert.Equal(new[] { "B" }, t.BestKey());
        Assert.True(t.IsHeap);
    }

    [Fact]
    public void BestKey_is_null_for_a_heap_without_unique_keys()
    {
        var t = LegacyShop().FindTable("dbo.audit_log")!;
        Assert.Null(t.BestKey());
        Assert.True(t.IsHeap);
        Assert.Null(t.PrimaryKey);
    }

    [Fact]
    public void Find_methods_are_case_insensitive()
    {
        var snapshot = LegacyShop();
        Assert.Equal("CUST", snapshot.FindTable("DBO.cust")!.Name);
        Assert.Equal("EMAIL_ADDR", snapshot.FindTable("dbo.CUST")!.FindColumn("email_addr")!.Name);
        Assert.Null(snapshot.FindTable("dbo.NOPE"));
    }

    [Fact]
    public void Snapshot_round_trips_through_json_without_computed_members()
    {
        var snapshot = ShopV2();
        var json = Json.Serialize(snapshot);
        Assert.DoesNotContain("\"key\"", json);
        Assert.DoesNotContain("\"isHeap\"", json);
        Assert.DoesNotContain("\"typeDisplay\"", json);
        Assert.Contains("\"triggerNames\":[\"trg_Orders_Audit\"]", json);

        var back = Json.Deserialize<CatalogSnapshot>(json);
        Assert.Equal(6, back.Tables.Count);
        Assert.Equal("app.Orders", back.FindTable("app.Orders")!.Key);
        Assert.Equal(new[] { "trg_Orders_Audit" }, back.FindTable("app.Orders")!.TriggerNames);
        Assert.Equal("(([FirstName]+N' ')+[LastName])", back.FindTable("app.Customers")!.FindColumn("DisplayName")!.ComputedDefinition);
        var email = Json.Deserialize<CatalogSnapshot>(Json.Serialize(LegacyShop())).FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!;
        Assert.Equal(0.1, email.Profile!.NullRatio, 3);
    }
}
