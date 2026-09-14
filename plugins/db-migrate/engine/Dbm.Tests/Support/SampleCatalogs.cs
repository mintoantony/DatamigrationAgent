using Dbm.Core.Catalog;
using Dbm.Core.Sql;

namespace Dbm.Tests.Support;

/// <summary>In-code catalogs identical in structure to the C15 LegacyShop (source) and ShopV2 (target) sample databases.
/// Integration test SampleCatalogFidelityTests keeps them faithful to what CatalogExtractor reads from the real schema.</summary>
public static class SampleCatalogs
{
    public const string Collation = "SQL_Latin1_General_CP1_CI_AS";
    public static readonly DateTimeOffset ExtractedAt = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    public static CatalogSnapshot Source() => new(
        Meta("LegacyShop"),
        [Addr(), AuditLog(), Cust(), OrdHdr(), OrdLine(), OrdStatus(), Prod(), TmpImport()],
        new ObjectCounts(Views: 0, Procedures: 0, Functions: 0, Triggers: 0, Synonyms: 0),
        ExtractedAt);

    public static CatalogSnapshot Target() => new(
        Meta("ShopV2"),
        [Addresses(), AuditEvents(), Customers(), OrderLines(), Orders(), Products()],
        new ObjectCounts(Views: 0, Procedures: 0, Functions: 0, Triggers: 1, Synonyms: 0),
        ExtractedAt);

    // ---------------- LegacyShop (dbo) ----------------

    private static TableInfo Cust() => Table("dbo", "CUST", 1000, 0.3,
        [
            Int("CUST_ID", 1, identity: true) with { Profile = Prof(1000, 0, 1000, "1", "1000", null, "integer") },
            Str("CUST_NM", 2, "varchar", 100, false) with { Profile = Prof(1000, 0, 1000, null, null, 19, "text", "First1 Last1", "First2 Last2", "First3 Last3") },
            Str("EMAIL_ADDR", 3, "varchar", 120, true) with { Profile = Prof(1000, 100, 900, null, null, 34, "email", "first1.last1@example.com", "first2.last2@example.com", "first3.last3@example.com") },
            Str("PHONE_NO", 4, "varchar", 30, true),
            DateTimeCol("DOB", 5, true),
            DateTimeCol("CRT_DT", 6, false, "(getdate())"),
            Str("FAX_NO", 7, "varchar", 30, true),
            new ColumnInfo("NOTES", 8, "text", 0, 0, 0, true, false, false, false, null, Collation, null)
        ],
        [Pk("PK_CUST", "CUST_ID")],
        []);

    private static TableInfo Addr() => Table("dbo", "ADDR", 1500, 0.2,
        [
            Int("ADDR_ID", 1, identity: true) with { Profile = Prof(1500, 0, 1500, "1", "1500", null, "integer") },
            Int("CUST_ID", 2) with { Profile = Prof(1500, 0, 1000, "1", "1000", null, "integer") },
            Str("LINE1", 3, "varchar", 200, false),
            Str("CITY", 4, "varchar", 80, false),
            Str("ZIP", 5, "varchar", 12, true) with { Profile = Prof(1500, 0, 1500, null, null, 5, "postal_code", "10001", "10002", "10003") },
            Str("CTRY_CD", 6, "char", 2, false) with { Profile = Prof(1500, 0, 4, null, null, 2, "country_code", "US", "GB", "DE") }
        ],
        [Pk("PK_ADDR", "ADDR_ID")],
        [Fk("FK_ADDR_CUST", "CUST_ID", "dbo", "CUST", "CUST_ID")]);

    private static TableInfo Prod() => Table("dbo", "PROD", 200, 0.1,
        [
            Int("PROD_ID", 1, identity: true) with { Profile = Prof(200, 0, 200, "1", "200", null, "integer") },
            Str("PROD_NM", 2, "varchar", 150, false),
            Str("PROD_DESC", 3, "varchar", 500, true),
            Money("UNIT_PRC", 4) with { Profile = Prof(200, 0, 200, "1.0000", "200.0000", null, "decimal") },
            Str("ACTIVE_FLG", 5, "char", 1, false) with { Profile = Prof(200, 0, 2, "N", "Y", 1, "flag", "Y", "N") }
        ],
        [Pk("PK_PROD", "PROD_ID")],
        []);

    private static TableInfo OrdStatus() => Table("dbo", "ORD_STATUS", 4, 0.01,
        [
            new ColumnInfo("STATUS_ID", 1, "tinyint", 0, 3, 0, false, false, false, false, null, null, null) with { Profile = Prof(4, 0, 4, "1", "4", null, "integer") },
            Str("STATUS_CD", 2, "varchar", 10, false) with { Profile = Prof(4, 0, 4, null, null, 9, "text", "NEW", "PAID", "SHIPPED") },
            Str("STATUS_DESC", 3, "varchar", 50, false)
        ],
        [Pk("PK_ORD_STATUS", "STATUS_ID")],
        []);

    private static TableInfo OrdHdr() => Table("dbo", "ORD_HDR", 3005, 0.4,
        [
            Int("ORD_ID", 1, identity: true) with { Profile = Prof(3005, 0, 3005, "1", "3005", null, "integer") },
            Int("CUST_ID", 2) with { Profile = Prof(3005, 0, 1002, "-2", "1000", null, "integer") },
            DateTimeCol("ORD_DT", 3, false),
            new ColumnInfo("STATUS_ID", 4, "tinyint", 0, 3, 0, false, false, false, false, null, null, null) with { Profile = Prof(3005, 0, 4, "1", "4", null, "integer") },
            Int("SHIP_ADDR_ID", 5, nullable: true),
            Money("TOTAL_AMT", 6) with { Profile = Prof(3005, 0, 2900, "1.0000", "9999.0000", null, "decimal") },
            Str("CMNT", 7, "varchar", 500, true) with { Profile = Prof(3005, 2500, 400, null, null, 300, "text", "Leave at the door") }
        ],
        [Pk("PK_ORD_HDR", "ORD_ID")],
        [
            Fk("FK_ORD_CUST", "CUST_ID", "dbo", "CUST", "CUST_ID", notTrusted: true),
            Fk("FK_ORD_STATUS", "STATUS_ID", "dbo", "ORD_STATUS", "STATUS_ID")
        ]);

    private static TableInfo OrdLine() => Table("dbo", "ORD_LINE", 9002, 0.6,
        [
            Int("ORD_ID", 1) with { Profile = Prof(9002, 0, 3002, "1", "3002", null, "integer") },
            new ColumnInfo("LINE_NO", 2, "smallint", 0, 5, 0, false, false, false, false, null, null, null) with { Profile = Prof(9002, 0, 3, "1", "3", null, "integer") },
            Int("PROD_ID", 3) with { Profile = Prof(9002, 0, 200, "1", "200", null, "integer") },
            Int("QTY", 4) with { Profile = Prof(9002, 0, 10, "0", "10", null, "integer") },
            Money("UNIT_PRC", 5) with { Profile = Prof(9002, 0, 200, "1.0000", "200.0000", null, "decimal") }
        ],
        [new IndexInfo("PK_ORD_LINE", true, true, true, ["ORD_ID", "LINE_NO"])],
        [
            Fk("FK_LINE_ORD", "ORD_ID", "dbo", "ORD_HDR", "ORD_ID"),
            Fk("FK_LINE_PROD", "PROD_ID", "dbo", "PROD", "PROD_ID")
        ]);

    private static TableInfo AuditLog() => Table("dbo", "AUDIT_LOG", 5000, 0.5,
        [
            DateTimeCol("LOG_TS", 1, false),
            Str("USR", 2, "varchar", 50, false),
            Str("ACTION_TXT", 3, "varchar", 200, false)
        ],
        [],
        []);

    private static TableInfo TmpImport() => Table("dbo", "TMP_IMPORT", 0, 0,
        [Int("X", 1, nullable: true)],
        [],
        []);

    // ---------------- ShopV2 (app) ----------------

    private static TableInfo Customers() => Table("app", "Customers", 0, 0,
        [
            Int("CustomerId", 1, identity: true),
            Str("FirstName", 2, "nvarchar", 50, false),
            Str("LastName", 3, "nvarchar", 50, false),
            Str("Email", 4, "nvarchar", 120, true),
            Str("Phone", 5, "nvarchar", 30, true),
            new ColumnInfo("BirthDate", 6, "date", 0, 10, 0, true, false, false, false, null, null, null),
            new ColumnInfo("CreatedAt", 7, "datetime2", 0, 19, 0, false, false, false, false, "(sysutcdatetime())", null, null),
            Int("PrimaryAddressId", 8, nullable: true),
            Str("Notes", 9, "nvarchar", -1, true),
            new ColumnInfo("DisplayName", 10, "nvarchar", 101, 0, 0, false, false, true, false, null, Collation, null)
                { ComputedDefinition = "(([FirstName]+N' ')+[LastName])" }
        ],
        [Pk("PK_Customers", "CustomerId")],
        [Fk("FK_Customers_PrimaryAddress", "PrimaryAddressId", "app", "Addresses", "AddressId")]);

    private static TableInfo Addresses() => Table("app", "Addresses", 0, 0,
        [
            Int("AddressId", 1, identity: true),
            Int("CustomerId", 2),
            Str("Line1", 3, "nvarchar", 200, false),
            Str("City", 4, "nvarchar", 80, false),
            Str("PostalCode", 5, "nvarchar", 12, true),
            Str("CountryCode", 6, "char", 2, false)
        ],
        [Pk("PK_Addresses", "AddressId")],
        [Fk("FK_Addresses_Customers", "CustomerId", "app", "Customers", "CustomerId")]);

    private static TableInfo Products() => Table("app", "Products", 0, 0,
        [
            Int("ProductId", 1, identity: true),
            Str("Name", 2, "nvarchar", 150, false),
            Str("Description", 3, "nvarchar", 500, true),
            Dec194("UnitPrice", 4),
            new ColumnInfo("IsActive", 5, "bit", 0, 1, 0, false, false, false, false, null, null, null),
            new ColumnInfo("RowVer", 6, "timestamp", 0, 0, 0, false, false, false, true, null, null, null)
        ],
        [Pk("PK_Products", "ProductId")],
        []);

    private static TableInfo Orders() => Table("app", "Orders", 0, 0,
        [
            Int("OrderId", 1, identity: true),
            Int("CustomerId", 2),
            new ColumnInfo("OrderDate", 3, "datetime2", 0, 19, 0, false, false, false, false, null, null, null),
            Str("StatusCode", 4, "varchar", 10, false),
            Int("ShippingAddressId", 5, nullable: true),
            Dec194("TotalAmount", 6),
            Str("Comment", 7, "nvarchar", 200, true)
        ],
        [Pk("PK_Orders", "OrderId")],
        [
            Fk("FK_Orders_Addresses", "ShippingAddressId", "app", "Addresses", "AddressId"),
            Fk("FK_Orders_Customers", "CustomerId", "app", "Customers", "CustomerId")
        ],
        triggers: ["trg_Orders_Audit"]);

    private static TableInfo OrderLines() => Table("app", "OrderLines", 0, 0,
        [
            Int("OrderId", 1),
            new ColumnInfo("LineNumber", 2, "smallint", 0, 5, 0, false, false, false, false, null, null, null),
            Int("ProductId", 3),
            Int("Quantity", 4),
            Dec194("UnitPrice", 5)
        ],
        [new IndexInfo("PK_OrderLines", true, true, true, ["OrderId", "LineNumber"])],
        [
            Fk("FK_OrderLines_Orders", "OrderId", "app", "Orders", "OrderId"),
            Fk("FK_OrderLines_Products", "ProductId", "app", "Products", "ProductId")
        ]);

    private static TableInfo AuditEvents() => Table("app", "AuditEvents", 0, 0,
        [
            new ColumnInfo("EventTime", 1, "datetime2", 0, 23, 3, false, false, false, false, null, null, null),
            Str("UserName", 2, "nvarchar", 50, false),
            Str("Action", 3, "nvarchar", 200, false)
        ],
        [],
        []);

    // ---------------- helpers ----------------

    private static ServerMeta Meta(string database) => new(
        Server: @"(localdb)\MSSQLLocalDB", Database: database, Version: "Microsoft SQL Server 2022", ProductVersion: "16.0.1000.6",
        MajorVersion: 16, Edition: "Express Edition (64-bit)", ServerCollation: Collation, DatabaseCollation: Collation,
        CompatLevel: 160, AuthSummary: "integrated");

    private static TableInfo Table(string schema, string name, long rows, double sizeMb, List<ColumnInfo> columns,
        List<IndexInfo> indexes, List<ForeignKeyInfo> fks, string[]? triggers = null) =>
        new(schema, name, rows, sizeMb, columns, indexes, fks, triggers?.Length ?? 0, null, null) { TriggerNames = (triggers ?? []).ToList() };

    private static ColumnInfo Int(string name, int ordinal, bool nullable = false, bool identity = false) =>
        new(name, ordinal, "int", 0, 10, 0, nullable, identity, false, false, null, null, null);

    private static ColumnInfo Str(string name, int ordinal, string type, int length, bool nullable) =>
        new(name, ordinal, type, length, 0, 0, nullable, false, false, false, null, Collation, null);

    private static ColumnInfo DateTimeCol(string name, int ordinal, bool nullable, string? dflt = null) =>
        new(name, ordinal, "datetime", 0, 23, 3, nullable, false, false, false, dflt, null, null);

    private static ColumnInfo Money(string name, int ordinal) =>
        new(name, ordinal, "money", 0, 19, 4, false, false, false, false, null, null, null);

    private static ColumnInfo Dec194(string name, int ordinal) =>
        new(name, ordinal, "decimal", 0, 19, 4, false, false, false, false, null, null, null);

    private static IndexInfo Pk(string name, string column) => new(name, true, true, true, [column]);

    private static ForeignKeyInfo Fk(string name, string column, string refSchema, string refTable, string refColumn, bool notTrusted = false) =>
        new(name, [column], refSchema, refTable, [refColumn], false, notTrusted);

    private static ColumnProfile Prof(long rows, long nulls, long? distinct, string? min, string? max, int? maxLen, string? semanticClass,
        params string[] samples) =>
        new(rows, nulls, distinct, min, max, maxLen, maxLen is null ? null : maxLen * 0.8, semanticClass, [], samples.ToList());

    /// <summary>Human-readable structural differences (profiles, rows, sizes and ExtractedAt ignored). Empty = identical structure.</summary>
    public static List<string> Diff(CatalogSnapshot expected, CatalogSnapshot actual)
    {
        var diffs = new List<string>();
        if (expected.Objects != actual.Objects) diffs.Add($"objects: expected {expected.Objects} actual {actual.Objects}");
        foreach (var e in expected.Tables)
        {
            var a = actual.FindTable(e.Key);
            if (a is null) { diffs.Add($"missing table {e.Key}"); continue; }
            if (e.TriggerCount != a.TriggerCount || !e.TriggerNames.Order().SequenceEqual(a.TriggerNames.Order()))
                diffs.Add($"{e.Key}: triggers [{string.Join(",", e.TriggerNames)}] vs [{string.Join(",", a.TriggerNames)}]");
            if (e.TemporalType != a.TemporalType) diffs.Add($"{e.Key}: temporal '{e.TemporalType}' vs '{a.TemporalType}'");
            foreach (var ec in e.Columns)
            {
                var ac = a.FindColumn(ec.Name);
                if (ac is null) { diffs.Add($"missing column {e.Key}.{ec.Name}"); continue; }
                if (ec with { Profile = null } != ac with { Profile = null }) diffs.Add($"{e.Key}.{ec.Name}: expected {ec with { Profile = null }} actual {ac with { Profile = null }}");
            }
            foreach (var ac in a.Columns.Where(c => e.FindColumn(c.Name) is null)) diffs.Add($"unexpected column {e.Key}.{ac.Name}");
            string Ix(IndexInfo i) => $"{i.Name}|{i.IsPrimaryKey}|{i.IsUnique}|{i.IsClustered}|{string.Join(",", i.Columns)}";
            string F(ForeignKeyInfo f) => $"{f.Name}|{string.Join(",", f.Columns)}|{f.RefKey}|{string.Join(",", f.RefColumns)}|{f.IsDisabled}|{f.IsNotTrusted}";
            var ei = e.Indexes.Select(Ix).Order().ToList(); var ai = a.Indexes.Select(Ix).Order().ToList();
            if (!ei.SequenceEqual(ai)) diffs.Add($"{e.Key}: indexes [{string.Join("; ", ei)}] vs [{string.Join("; ", ai)}]");
            var ef = e.ForeignKeys.Select(F).Order().ToList(); var af = a.ForeignKeys.Select(F).Order().ToList();
            if (!ef.SequenceEqual(af)) diffs.Add($"{e.Key}: foreign keys [{string.Join("; ", ef)}] vs [{string.Join("; ", af)}]");
        }
        foreach (var a in actual.Tables.Where(t => expected.FindTable(t.Key) is null)) diffs.Add($"unexpected table {a.Key}");
        return diffs;
    }
}
