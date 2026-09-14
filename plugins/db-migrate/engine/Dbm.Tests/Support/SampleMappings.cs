using Dbm.Core.Mapping;

namespace Dbm.Tests.Support;

/// <summary>The C15 ground-truth mapping LegacyShop → ShopV2 (Method human, Confidence 1). Used by M3–M5 tests.</summary>
public static class SampleMappings
{
    public static MappingPayload Approved()
    {
        var m = new MappingPayload();

        m.Tables["app.Customers"] = Direct("dbo.CUST", new()
        {
            ["CustomerId"] = C("s.[CUST_ID]", "dbo.CUST.CUST_ID"),
            ["FirstName"] = C("LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", "dbo.CUST.CUST_NM"),
            ["LastName"] = C("LTRIM(SUBSTRING(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') + 1, 100))", "dbo.CUST.CUST_NM"),
            ["Email"] = C("s.[EMAIL_ADDR]", "dbo.CUST.EMAIL_ADDR"),
            ["Phone"] = C("s.[PHONE_NO]", "dbo.CUST.PHONE_NO"),
            ["BirthDate"] = C("CAST(s.[DOB] AS date)", "dbo.CUST.DOB"),
            ["CreatedAt"] = C("s.[CRT_DT]", "dbo.CUST.CRT_DT"),
            ["PrimaryAddressId"] = C("(SELECT MIN(a.[ADDR_ID]) FROM [dbo].[ADDR] AS a WHERE a.[CUST_ID] = s.[CUST_ID])",
                "dbo.ADDR.ADDR_ID", "dbo.ADDR.CUST_ID", "dbo.CUST.CUST_ID"),
            ["Notes"] = C("CAST(s.[NOTES] AS nvarchar(max))", "dbo.CUST.NOTES"),
        });

        m.Tables["app.Addresses"] = Direct("dbo.ADDR", new()
        {
            ["AddressId"] = C("s.[ADDR_ID]", "dbo.ADDR.ADDR_ID"),
            ["CustomerId"] = C("s.[CUST_ID]", "dbo.ADDR.CUST_ID"),
            ["Line1"] = C("s.[LINE1]", "dbo.ADDR.LINE1"),
            ["City"] = C("s.[CITY]", "dbo.ADDR.CITY"),
            ["PostalCode"] = C("s.[ZIP]", "dbo.ADDR.ZIP"),
            ["CountryCode"] = C("s.[CTRY_CD]", "dbo.ADDR.CTRY_CD"),
        });

        m.Tables["app.Products"] = Direct("dbo.PROD", new()
        {
            ["ProductId"] = C("s.[PROD_ID]", "dbo.PROD.PROD_ID"),
            ["Name"] = C("s.[PROD_NM]", "dbo.PROD.PROD_NM"),
            ["Description"] = C("s.[PROD_DESC]", "dbo.PROD.PROD_DESC"),
            ["UnitPrice"] = C("s.[UNIT_PRC]", "dbo.PROD.UNIT_PRC"),
            ["IsActive"] = C("CASE WHEN s.[ACTIVE_FLG] = 'Y' THEN 1 ELSE 0 END", "dbo.PROD.ACTIVE_FLG"),
        });

        m.Tables["app.Orders"] = new TableMap
        {
            Kind = "merge",
            Sources = ["dbo.ORD_HDR", "dbo.ORD_STATUS"],
            From = "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]",
            Confidence = 1,
            Method = MapMethod.Human,
            Rationale = "Order header joined to the status lookup to obtain the status code.",
            Columns = new()
            {
                ["OrderId"] = C("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"),
                ["CustomerId"] = C("s.[CUST_ID]", "dbo.ORD_HDR.CUST_ID"),
                ["OrderDate"] = C("s.[ORD_DT]", "dbo.ORD_HDR.ORD_DT"),
                ["StatusCode"] = C("st.[STATUS_CD]", "dbo.ORD_STATUS.STATUS_CD", "dbo.ORD_HDR.STATUS_ID"),
                ["ShippingAddressId"] = C("s.[SHIP_ADDR_ID]", "dbo.ORD_HDR.SHIP_ADDR_ID"),
                ["TotalAmount"] = C("s.[TOTAL_AMT]", "dbo.ORD_HDR.TOTAL_AMT"),
                ["Comment"] = C("s.[CMNT]", "dbo.ORD_HDR.CMNT"),
            }
        };

        m.Tables["app.OrderLines"] = Direct("dbo.ORD_LINE", new()
        {
            ["OrderId"] = C("s.[ORD_ID]", "dbo.ORD_LINE.ORD_ID"),
            ["LineNumber"] = C("s.[LINE_NO]", "dbo.ORD_LINE.LINE_NO"),
            ["ProductId"] = C("s.[PROD_ID]", "dbo.ORD_LINE.PROD_ID"),
            ["Quantity"] = C("s.[QTY]", "dbo.ORD_LINE.QTY"),
            ["UnitPrice"] = C("s.[UNIT_PRC]", "dbo.ORD_LINE.UNIT_PRC"),
        });

        m.Tables["app.AuditEvents"] = Direct("dbo.AUDIT_LOG", new()
        {
            ["EventTime"] = C("s.[LOG_TS]", "dbo.AUDIT_LOG.LOG_TS"),
            ["UserName"] = C("s.[USR]", "dbo.AUDIT_LOG.USR"),
            ["Action"] = C("s.[ACTION_TXT]", "dbo.AUDIT_LOG.ACTION_TXT"),
        });

        m.Drops["dbo.TMP_IMPORT"] = new DropDecision("Empty import staging table; not part of ShopV2.", MapMethod.Human);
        m.Drops["dbo.CUST.FAX_NO"] = new DropDecision("ShopV2 no longer stores fax numbers.", MapMethod.Human);
        m.Drops["dbo.ORD_STATUS.STATUS_ID"] = new DropDecision("Surrogate lookup key; Orders stores the status code instead.", MapMethod.Human);
        m.Drops["dbo.ORD_STATUS.STATUS_DESC"] = new DropDecision("Status descriptions are not stored in ShopV2.", MapMethod.Human);
        return m;
    }

    private static TableMap Direct(string source, Dictionary<string, ColumnMap> columns) => new()
    {
        Kind = "direct",
        Sources = [source],
        Confidence = 1,
        Method = MapMethod.Human,
        Columns = columns
    };

    private static ColumnMap C(string expr, params string[] sourceColumns) => new()
    {
        Expr = expr,
        SourceColumns = sourceColumns.ToList(),
        Confidence = 1,
        Method = MapMethod.Human
    };
}
