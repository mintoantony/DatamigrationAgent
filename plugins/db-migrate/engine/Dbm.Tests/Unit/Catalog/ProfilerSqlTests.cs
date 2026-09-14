using Dbm.Core.Catalog;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Unit.Catalog;

public sealed class ProfilerSqlTests
{
    [Fact]
    public void Batches_split_beyond_100_columns()
    {
        var columns = Enumerable.Range(1, 150).Select(i => Col($"C{i}", "int")).ToList();
        var batches = ProfilerSql.Batches(columns);
        Assert.Equal(new[] { 100, 50 }, batches.Select(b => b.Count));
    }

    [Fact]
    public void Aggregate_uses_the_sample_subquery_and_per_type_expressions()
    {
        var table = Table("dbo.T]x", 10, new[]
        {
            Col("Id", "int", nullable: false),
            Col("Flag", "bit"),
            Col("Blob", "varbinary", 16),
            Col("Name", "nvarchar", 50),
            Col("Notes", "text"),
            Col("Body", "nvarchar", -1),
            Col("Ver", "timestamp"),
            Col("Born", "datetime2", scale: 0),
        });
        var sql = ProfilerSql.Aggregate(table, table.Columns);

        Assert.StartsWith("SELECT COUNT_BIG(*) AS [rows]", sql);
        Assert.EndsWith("FROM (SELECT TOP (@sample) * FROM [dbo].[T]]x]) AS x;", sql);
        Assert.Contains("ISNULL(SUM(CAST(CASE WHEN x.[Id] IS NULL THEN 1 ELSE 0 END AS bigint)), 0) AS [n0]", sql);
        Assert.Contains("COUNT_BIG(DISTINCT x.[Id]) AS [d0]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MIN(x.[Id]), 126) AS [mn0]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MIN(CAST(x.[Flag] AS tinyint)), 126) AS [mn1]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MAX(x.[Blob]), 1) AS [mx2]", sql);
        Assert.Contains("CAST(MAX(LEN(x.[Name])) AS int) AS [ml3]", sql);
        Assert.Contains("CAST(MAX(DATALENGTH(x.[Notes])) AS int) AS [ml4]", sql);
        Assert.DoesNotContain("[d4]", sql);   // text: no DISTINCT/MIN/MAX
        Assert.DoesNotContain("[d5]", sql);   // nvarchar(max)
        Assert.Contains("CAST(MAX(LEN(x.[Body])) AS int) AS [ml5]", sql);
        Assert.DoesNotContain("[d6]", sql);   // timestamp
        Assert.Contains("[n6]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MAX(x.[Born]), 126) AS [mx7]", sql);
    }

    [Fact]
    public void PatternFetch_casts_string_columns_to_nvarchar_200()
    {
        var table = Table("dbo.CUST", 10, new[] { Col("CUST_NM", "varchar", 100), Col("NOTES", "text") });
        Assert.Equal(
            "SELECT TOP (@patternRows) CAST(t.[CUST_NM] AS nvarchar(200)) AS [p0], CAST(t.[NOTES] AS nvarchar(200)) AS [p1] FROM [dbo].[CUST] AS t;",
            ProfilerSql.PatternFetch(table, table.Columns));
    }

    [Fact]
    public void Truncate_limits_samples_to_40_characters()
    {
        Assert.Equal("short", Profiler.Truncate("short"));
        var truncated = Profiler.Truncate(new string('x', 60))!;
        Assert.Equal(40, truncated.Length);
        Assert.EndsWith("…", truncated);
    }
}
