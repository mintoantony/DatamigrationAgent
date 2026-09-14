using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Catalog;

[Trait("Category", "Integration")]
public sealed class CatalogExtractorTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    public static async Task<CatalogSnapshot> ExtractAsync(string connectionString)
    {
        var meta = await SqlConnect.ProbeAsync(connectionString, CancellationToken.None);
        await using var conn = await SqlConnect.OpenAsync(connectionString, CancellationToken.None);
        return await CatalogExtractor.ExtractAsync(conn, meta, CancellationToken.None);
    }

    [Fact]
    public async Task Source_tables_keys_and_rows()
    {
        var src = await ExtractAsync(fixture.Pair.SourceCs);
        Assert.Equal(
            new[] { "dbo.ADDR", "dbo.AUDIT_LOG", "dbo.CUST", "dbo.ORD_HDR", "dbo.ORD_LINE", "dbo.ORD_STATUS", "dbo.PROD", "dbo.TMP_IMPORT" },
            src.Tables.Select(t => t.Key));

        var cust = src.FindTable("dbo.CUST")!;
        Assert.Equal(1000, cust.Rows);
        Assert.True(cust.SizeMb > 0);
        Assert.Equal(new[] { "CUST_ID" }, cust.BestKey());
        Assert.True(cust.FindColumn("CUST_ID")!.IsIdentity);
        Assert.Equal("varchar(120)", cust.FindColumn("EMAIL_ADDR")!.TypeDisplay);
        Assert.Equal("text", cust.FindColumn("NOTES")!.DataType);
        Assert.Equal("(getdate())", cust.FindColumn("CRT_DT")!.DefaultDefinition);
        Assert.Equal(Enumerable.Range(1, 8), cust.Columns.Select(c => c.Ordinal));

        Assert.Equal(new[] { "ORD_ID", "LINE_NO" }, src.FindTable("dbo.ORD_LINE")!.BestKey());

        var audit = src.FindTable("dbo.AUDIT_LOG")!;
        Assert.True(audit.IsHeap);
        Assert.Null(audit.BestKey());
        Assert.Equal(5000, audit.Rows);
    }

    [Fact]
    public async Task Source_untrusted_foreign_key()
    {
        var hdr = (await ExtractAsync(fixture.Pair.SourceCs)).FindTable("dbo.ORD_HDR")!;
        var ordCust = hdr.ForeignKeys.Single(f => f.Name == "FK_ORD_CUST");
        Assert.True(ordCust.IsNotTrusted);
        Assert.False(ordCust.IsDisabled);
        Assert.Equal("dbo.CUST", ordCust.RefKey);
        Assert.Equal(new[] { "CUST_ID" }, ordCust.Columns);
        Assert.False(hdr.ForeignKeys.Single(f => f.Name == "FK_ORD_STATUS").IsNotTrusted);
    }

    [Fact]
    public async Task Target_types_computed_rowversion_triggers_and_cycle()
    {
        var tgt = await ExtractAsync(fixture.Pair.TargetCs);
        Assert.Equal(6, tgt.Tables.Count);

        var customers = tgt.FindTable("app.Customers")!;
        Assert.Equal(120, customers.FindColumn("Email")!.MaxLength);
        Assert.Equal("nvarchar(max)", customers.FindColumn("Notes")!.TypeDisplay);
        Assert.Equal("datetime2(0)", customers.FindColumn("CreatedAt")!.TypeDisplay);
        var display = customers.FindColumn("DisplayName")!;
        Assert.True(display.IsComputed);
        Assert.Contains("[FirstName]", display.ComputedDefinition);

        var products = tgt.FindTable("app.Products")!;
        Assert.True(products.FindColumn("RowVer")!.IsRowVersion);
        Assert.Equal("decimal(19,4)", products.FindColumn("UnitPrice")!.TypeDisplay);

        var orders = tgt.FindTable("app.Orders")!;
        Assert.Equal(1, orders.TriggerCount);
        Assert.Equal(new[] { "trg_Orders_Audit" }, orders.TriggerNames);
        Assert.Equal(1, tgt.Objects.Triggers);

        Assert.True(tgt.FindTable("app.AuditEvents")!.IsHeap);
        Assert.Contains(customers.ForeignKeys, f => f.RefKey == "app.Addresses");
        Assert.Contains(tgt.FindTable("app.Addresses")!.ForeignKeys, f => f.RefKey == "app.Customers");
        Assert.All(tgt.Tables, t => Assert.Equal(0, t.Rows));
    }

    [Fact]
    public async Task Dbm_control_tables_are_not_part_of_the_catalog()
    {
        var before = await ExtractAsync(fixture.Pair.TargetCs);
        await SampleDatabases.RunAsync(fixture.Pair.TargetCs,
            "IF OBJECT_ID(N'dbo.__dbm_checkpoint') IS NULL CREATE TABLE dbo.__dbm_checkpoint (task_id nvarchar(20) NOT NULL PRIMARY KEY, last_key_json nvarchar(max) NULL);");
        var after = await ExtractAsync(fixture.Pair.TargetCs);
        Assert.DoesNotContain(after.Tables, t => t.Name.StartsWith("__dbm_", StringComparison.Ordinal));
        Assert.Equal(6, after.Tables.Count);
        Assert.Equal(Fingerprint.Compute(before), Fingerprint.Compute(after));
    }
}
