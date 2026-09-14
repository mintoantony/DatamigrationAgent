using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Samples;

[Trait("Category", "Integration")]
public sealed class SampleDatabasesTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("dbo.CUST", 1000)]
    [InlineData("dbo.ADDR", 1500)]
    [InlineData("dbo.PROD", 200)]
    [InlineData("dbo.ORD_STATUS", 4)]
    [InlineData("dbo.ORD_HDR", 3005)]
    [InlineData("dbo.ORD_LINE", 9002)]
    [InlineData("dbo.AUDIT_LOG", 5000)]
    [InlineData("dbo.TMP_IMPORT", 0)]
    public async Task Source_row_counts_at_scale_1(string table, long expected) =>
        Assert.Equal(expected, await ScalarAsync(fixture.Pair.SourceCs, $"SELECT COUNT_BIG(*) FROM {table}"));

    [Fact]
    public async Task Two_orphan_orders_each_with_one_line()
    {
        const string orphans = "FROM dbo.ORD_HDR AS h WHERE NOT EXISTS (SELECT 1 FROM dbo.CUST AS c WHERE c.CUST_ID = h.CUST_ID)";
        Assert.Equal(2, await ScalarAsync(fixture.Pair.SourceCs, $"SELECT COUNT_BIG(*) {orphans}"));
        Assert.Equal(2, await ScalarAsync(fixture.Pair.SourceCs,
            $"SELECT COUNT_BIG(*) FROM dbo.ORD_LINE AS l WHERE l.ORD_ID IN (SELECT h.ORD_ID {orphans})"));
    }

    [Fact]
    public async Task Three_orders_have_a_300_character_comment_and_no_lines()
    {
        Assert.Equal(3, await ScalarAsync(fixture.Pair.SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_HDR WHERE LEN(CMNT) = 300"));
        Assert.Equal(0, await ScalarAsync(fixture.Pair.SourceCs,
            "SELECT COUNT_BIG(*) FROM dbo.ORD_LINE AS l JOIN dbo.ORD_HDR AS h ON h.ORD_ID = l.ORD_ID WHERE LEN(h.CMNT) = 300"));
    }

    [Fact]
    public async Task Exactly_one_line_has_zero_quantity() =>
        Assert.Equal(1, await ScalarAsync(fixture.Pair.SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_LINE WHERE QTY = 0"));

    [Fact]
    public async Task Fk_ord_cust_is_not_trusted() =>
        Assert.Equal(1, await ScalarAsync(fixture.Pair.SourceCs,
            "SELECT CAST(is_not_trusted AS int) FROM sys.foreign_keys WHERE name = 'FK_ORD_CUST'"));

    [Fact]
    public async Task Ten_percent_of_emails_are_null() =>
        Assert.Equal(100, await ScalarAsync(fixture.Pair.SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST WHERE EMAIL_ADDR IS NULL"));

    [Fact]
    public async Task Target_is_empty_and_has_the_audit_trigger()
    {
        Assert.Equal(6, await ScalarAsync(fixture.Pair.TargetCs,
            "SELECT COUNT_BIG(*) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = 'app'"));
        Assert.Equal(0, await ScalarAsync(fixture.Pair.TargetCs, "SELECT COUNT_BIG(*) FROM app.Customers"));
        Assert.Equal(1, await ScalarAsync(fixture.Pair.TargetCs, "SELECT COUNT_BIG(*) FROM sys.triggers WHERE name = 'trg_Orders_Audit'"));
    }
}
