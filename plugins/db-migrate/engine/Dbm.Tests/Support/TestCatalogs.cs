using Dbm.Core.Catalog;
using Dbm.Core.Sql;

namespace Dbm.Tests.Support;

/// <summary>In-code catalogs for unit tests: builders plus structural copies of the C15 LegacyShop / ShopV2 pair (no server needed).</summary>
public static class TestCatalogs
{
    public const string Collation = "SQL_Latin1_General_CP1_CI_AS";

    public static ServerMeta Meta(string database, int majorVersion = 17, int compatLevel = 170, string collation = Collation) =>
        new("localhost", database, "Microsoft SQL Server 2025 (RTM)", "17.0.1000.7", majorVersion, "Developer Edition (64-bit)",
            collation, collation, compatLevel, "integrated");

    public static ColumnInfo Col(string name, string type, int maxLength = 0, bool nullable = true, bool identity = false,
        bool computed = false, int precision = 0, int scale = 0, string? defaultDefinition = null, ColumnProfile? profile = null,
        string? collation = null, string? description = null) =>
        new(name, 0, type, maxLength, precision, scale, nullable, identity, computed, type == "timestamp", defaultDefinition,
            collation ?? (TypeTraits.IsString(type) ? Collation : null), description) { Profile = profile };

    public static TableInfo Table(string key, long rows, IEnumerable<ColumnInfo> columns, string[]? pk = null,
        IEnumerable<ForeignKeyInfo>? fks = null, string[]? triggers = null, IEnumerable<IndexInfo>? indexes = null,
        string? temporal = null)
    {
        var dot = key.IndexOf('.');
        var name = key[(dot + 1)..];
        var cols = columns.Select((c, i) => c with { Ordinal = i + 1 }).ToList();
        var ix = new List<IndexInfo>();
        if (pk is not null) ix.Add(new IndexInfo($"PK_{name}", true, true, true, pk.ToList()));
        if (indexes is not null) ix.AddRange(indexes);
        var triggerNames = triggers?.ToList() ?? new List<string>();
        return new TableInfo(key[..dot], name, rows, Math.Round(rows / 5000.0, 3), cols, ix, fks?.ToList() ?? new List<ForeignKeyInfo>(),
            triggerNames.Count, temporal, null) { TriggerNames = triggerNames };
    }

    public static ForeignKeyInfo Fk(string name, string column, string refKey, string refColumn, bool notTrusted = false, bool disabled = false)
    {
        var dot = refKey.IndexOf('.');
        return new ForeignKeyInfo(name, new List<string> { column }, refKey[..dot], refKey[(dot + 1)..], new List<string> { refColumn },
            disabled, notTrusted);
    }

    public static ColumnProfile Profile(long sampled, long nulls, long? distinct = null, string? semanticClass = null, params string[] samples) =>
        new(sampled, nulls, distinct, null, null, null, null, semanticClass, new List<string>(), samples.ToList());

    public static CatalogSnapshot Snapshot(ServerMeta meta, IEnumerable<TableInfo> tables, int triggers = 0) =>
        new(meta, tables.ToList(), new ObjectCounts(0, 0, 0, triggers, 0), new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

    public static CatalogSnapshot LegacyShop() => Snapshot(Meta("LegacyShop"), new[]
    {
        Table("dbo.CUST", 1000, new[]
        {
            Col("CUST_ID", "int", nullable: false, identity: true),
            Col("CUST_NM", "varchar", 100, nullable: false),
            Col("EMAIL_ADDR", "varchar", 120, profile: Profile(1000, 100, 900, "email", "first1.last1@example.com")),
            Col("PHONE_NO", "varchar", 30),
            Col("DOB", "datetime", precision: 23, scale: 3),
            Col("CRT_DT", "datetime", nullable: false, precision: 23, scale: 3, defaultDefinition: "(getdate())"),
            Col("FAX_NO", "varchar", 30, profile: Profile(1000, 980, 20, "phone")),
            Col("NOTES", "text", profile: Profile(1000, 750)),
        }, pk: new[] { "CUST_ID" }),
        Table("dbo.ADDR", 1500, new[]
        {
            Col("ADDR_ID", "int", nullable: false, identity: true),
            Col("CUST_ID", "int", nullable: false),
            Col("LINE1", "varchar", 200, nullable: false),
            Col("CITY", "varchar", 80, nullable: false),
            Col("ZIP", "varchar", 12),
            Col("CTRY_CD", "char", 2, nullable: false),
        }, pk: new[] { "ADDR_ID" }, fks: new[] { Fk("FK_ADDR_CUST", "CUST_ID", "dbo.CUST", "CUST_ID") }),
        Table("dbo.PROD", 200, new[]
        {
            Col("PROD_ID", "int", nullable: false, identity: true),
            Col("PROD_NM", "varchar", 150, nullable: false),
            Col("PROD_DESC", "varchar", 500),
            Col("UNIT_PRC", "money", nullable: false, precision: 19, scale: 4),
            Col("ACTIVE_FLG", "char", 1, nullable: false),
        }, pk: new[] { "PROD_ID" }),
        Table("dbo.ORD_STATUS", 4, new[]
        {
            Col("STATUS_ID", "tinyint", nullable: false),
            Col("STATUS_CD", "varchar", 10, nullable: false),
            Col("STATUS_DESC", "varchar", 50, nullable: false),
        }, pk: new[] { "STATUS_ID" }),
        Table("dbo.ORD_HDR", 3005, new[]
        {
            Col("ORD_ID", "int", nullable: false, identity: true),
            Col("CUST_ID", "int", nullable: false),
            Col("ORD_DT", "datetime", nullable: false, precision: 23, scale: 3),
            Col("STATUS_ID", "tinyint", nullable: false),
            Col("SHIP_ADDR_ID", "int"),
            Col("TOTAL_AMT", "money", nullable: false, precision: 19, scale: 4),
            Col("CMNT", "varchar", 500),
        }, pk: new[] { "ORD_ID" }, fks: new[]
        {
            Fk("FK_ORD_CUST", "CUST_ID", "dbo.CUST", "CUST_ID", notTrusted: true),
            Fk("FK_ORD_STATUS", "STATUS_ID", "dbo.ORD_STATUS", "STATUS_ID"),
        }),
        Table("dbo.ORD_LINE", 9002, new[]
        {
            Col("ORD_ID", "int", nullable: false),
            Col("LINE_NO", "smallint", nullable: false),
            Col("PROD_ID", "int", nullable: false),
            Col("QTY", "int", nullable: false),
            Col("UNIT_PRC", "money", nullable: false, precision: 19, scale: 4),
        }, pk: new[] { "ORD_ID", "LINE_NO" }, fks: new[]
        {
            Fk("FK_LINE_ORD", "ORD_ID", "dbo.ORD_HDR", "ORD_ID"),
            Fk("FK_LINE_PROD", "PROD_ID", "dbo.PROD", "PROD_ID"),
        }),
        Table("dbo.AUDIT_LOG", 5000, new[]
        {
            Col("LOG_TS", "datetime", nullable: false, precision: 23, scale: 3),
            Col("USR", "varchar", 50, nullable: false),
            Col("ACTION_TXT", "varchar", 200, nullable: false),
        }),
        Table("dbo.TMP_IMPORT", 0, new[] { Col("X", "int") }),
    });

    public static CatalogSnapshot ShopV2() => Snapshot(Meta("ShopV2"), new[]
    {
        Table("app.Customers", 0, new[]
        {
            Col("CustomerId", "int", nullable: false, identity: true),
            Col("FirstName", "nvarchar", 50, nullable: false),
            Col("LastName", "nvarchar", 50, nullable: false),
            Col("Email", "nvarchar", 120),
            Col("Phone", "nvarchar", 30),
            Col("BirthDate", "date", precision: 10),
            Col("CreatedAt", "datetime2", nullable: false, precision: 19, defaultDefinition: "(sysutcdatetime())"),
            Col("PrimaryAddressId", "int"),
            Col("Notes", "nvarchar", -1),
            Col("DisplayName", "nvarchar", 101, nullable: false, computed: true) with { ComputedDefinition = "(([FirstName]+N' ')+[LastName])" },
        }, pk: new[] { "CustomerId" }, fks: new[] { Fk("FK_Customers_PrimaryAddress", "PrimaryAddressId", "app.Addresses", "AddressId") }),
        Table("app.Addresses", 0, new[]
        {
            Col("AddressId", "int", nullable: false, identity: true),
            Col("CustomerId", "int", nullable: false),
            Col("Line1", "nvarchar", 200, nullable: false),
            Col("City", "nvarchar", 80, nullable: false),
            Col("PostalCode", "nvarchar", 12),
            Col("CountryCode", "char", 2, nullable: false),
        }, pk: new[] { "AddressId" }, fks: new[] { Fk("FK_Addresses_Customers", "CustomerId", "app.Customers", "CustomerId") }),
        Table("app.Products", 0, new[]
        {
            Col("ProductId", "int", nullable: false, identity: true),
            Col("Name", "nvarchar", 150, nullable: false),
            Col("Description", "nvarchar", 500),
            Col("UnitPrice", "decimal", nullable: false, precision: 19, scale: 4),
            Col("IsActive", "bit", nullable: false),
            Col("RowVer", "timestamp", nullable: false),
        }, pk: new[] { "ProductId" }),
        Table("app.Orders", 0, new[]
        {
            Col("OrderId", "int", nullable: false, identity: true),
            Col("CustomerId", "int", nullable: false),
            Col("OrderDate", "datetime2", nullable: false, precision: 19),
            Col("StatusCode", "varchar", 10, nullable: false),
            Col("ShippingAddressId", "int"),
            Col("TotalAmount", "decimal", nullable: false, precision: 19, scale: 4),
            Col("Comment", "nvarchar", 200),
        }, pk: new[] { "OrderId" }, fks: new[]
        {
            Fk("FK_Orders_Customers", "CustomerId", "app.Customers", "CustomerId"),
            Fk("FK_Orders_Addresses", "ShippingAddressId", "app.Addresses", "AddressId"),
        }, triggers: new[] { "trg_Orders_Audit" }),
        Table("app.OrderLines", 0, new[]
        {
            Col("OrderId", "int", nullable: false),
            Col("LineNumber", "smallint", nullable: false),
            Col("ProductId", "int", nullable: false),
            Col("Quantity", "int", nullable: false),
            Col("UnitPrice", "decimal", nullable: false, precision: 19, scale: 4),
        }, pk: new[] { "OrderId", "LineNumber" }, fks: new[]
        {
            Fk("FK_OrderLines_Orders", "OrderId", "app.Orders", "OrderId"),
            Fk("FK_OrderLines_Products", "ProductId", "app.Products", "ProductId"),
        }),
        Table("app.AuditEvents", 0, new[]
        {
            Col("EventTime", "datetime2", nullable: false, precision: 23, scale: 3),
            Col("UserName", "nvarchar", 50, nullable: false),
            Col("Action", "nvarchar", 200, nullable: false),
        }),
    }, triggers: 1);
}
