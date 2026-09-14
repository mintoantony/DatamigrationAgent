using Dbm.Core.Mapping;

namespace Dbm.Tests.Support;

/// <summary>C15 auto-mapper baseline: the six target tables pair with the right primary source and these column pairs are accepted.</summary>
public static class AutoMapperBaseline
{
    public static readonly IReadOnlyDictionary<string, string> TablePairs = new Dictionary<string, string>
    {
        ["app.Customers"] = "dbo.CUST",
        ["app.Addresses"] = "dbo.ADDR",
        ["app.Products"] = "dbo.PROD",
        ["app.Orders"] = "dbo.ORD_HDR",
        ["app.OrderLines"] = "dbo.ORD_LINE",
        ["app.AuditEvents"] = "dbo.AUDIT_LOG",
    };

    public static readonly IReadOnlyList<(string Target, string Column, string Source)> ColumnPairs =
    [
        ("app.Customers", "CustomerId", "dbo.CUST.CUST_ID"),
        ("app.Customers", "Email", "dbo.CUST.EMAIL_ADDR"),
        ("app.Customers", "Phone", "dbo.CUST.PHONE_NO"),
        ("app.Customers", "BirthDate", "dbo.CUST.DOB"),
        ("app.Addresses", "AddressId", "dbo.ADDR.ADDR_ID"),
        ("app.Addresses", "CustomerId", "dbo.ADDR.CUST_ID"),
        ("app.Addresses", "Line1", "dbo.ADDR.LINE1"),
        ("app.Addresses", "City", "dbo.ADDR.CITY"),
        ("app.Addresses", "PostalCode", "dbo.ADDR.ZIP"),
        ("app.Addresses", "CountryCode", "dbo.ADDR.CTRY_CD"),
        ("app.Products", "ProductId", "dbo.PROD.PROD_ID"),
        ("app.Products", "UnitPrice", "dbo.PROD.UNIT_PRC"),
        ("app.Orders", "OrderId", "dbo.ORD_HDR.ORD_ID"),
        ("app.Orders", "CustomerId", "dbo.ORD_HDR.CUST_ID"),
        ("app.Orders", "OrderDate", "dbo.ORD_HDR.ORD_DT"),
        ("app.Orders", "TotalAmount", "dbo.ORD_HDR.TOTAL_AMT"),
        ("app.Orders", "Comment", "dbo.ORD_HDR.CMNT"),
        ("app.OrderLines", "OrderId", "dbo.ORD_LINE.ORD_ID"),
        ("app.OrderLines", "LineNumber", "dbo.ORD_LINE.LINE_NO"),
        ("app.OrderLines", "ProductId", "dbo.ORD_LINE.PROD_ID"),
        ("app.OrderLines", "Quantity", "dbo.ORD_LINE.QTY"),
        ("app.OrderLines", "UnitPrice", "dbo.ORD_LINE.UNIT_PRC"),
    ];

    /// <summary>Returns every deviation from the baseline (empty = pass), so a failing test lists all misses at once.</summary>
    public static List<string> Misses(MappingPayload m)
    {
        var misses = new List<string>();
        foreach (var (target, source) in TablePairs)
        {
            if (!m.Tables.TryGetValue(target, out var map)) { misses.Add($"{target}: no table map"); continue; }
            var actual = map.Sources.FirstOrDefault() ?? "(none)";
            if (!string.Equals(actual, source, StringComparison.OrdinalIgnoreCase))
                misses.Add($"{target}: paired with {actual} (confidence {map.Confidence}), expected {source}");
        }
        foreach (var (target, column, source) in ColumnPairs)
        {
            if (!m.Tables.TryGetValue(target, out var map) || !map.Columns.TryGetValue(column, out var cm))
            {
                misses.Add($"{target}.{column}: no column map");
                continue;
            }
            var actual = cm.SourceColumns.FirstOrDefault() ?? "(unmapped)";
            if (!string.Equals(actual, source, StringComparison.OrdinalIgnoreCase))
                misses.Add($"{target}.{column}: got {actual} (confidence {cm.Confidence}; candidates {string.Join(", ", (cm.Candidates ?? []).Select(c => $"{c.Source} {c.Score} [{c.Why}]"))}), expected {source}");
        }
        return misses;
    }
}
