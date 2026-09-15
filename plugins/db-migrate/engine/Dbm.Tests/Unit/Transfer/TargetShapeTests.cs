using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class TargetShapeTests
{
    private static DateTime At(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Theory]
    [InlineData("2020-01-01T10:00:00.9970000", 0, "2020-01-01T10:00:01.0000000")]   // V8: CAST rounds up
    [InlineData("2020-01-01T10:00:00.4970000", 0, "2020-01-01T10:00:00.0000000")]
    [InlineData("2020-01-01T10:00:00.5000000", 0, "2020-01-01T10:00:01.0000000")]   // half rounds up
    [InlineData("2020-01-01T00:00:00.0500000", 1, "2020-01-01T00:00:00.1000000")]
    [InlineData("2020-01-01T00:00:00.4999999", 1, "2020-01-01T00:00:00.5000000")]
    [InlineData("9999-12-31T23:59:59.9999999", 1, "9999-12-31T23:59:59.9000000")]   // clamped at the maximum
    [InlineData("2020-01-01T10:00:00.0033333", 7, "2020-01-01T10:00:00.0033333")]
    public void Datetime2_values_round_like_CAST(string input, int scale, string expected)
        => Assert.Equal(At(expected), TargetShape.RoundValue(At(input), "datetime2", scale));

    [Theory]   // expected values probed on SQL Server: CAST(CAST(v AS datetime2(7)) AS smalldatetime)
    [InlineData("2020-01-01T10:00:29.9999999", "2020-01-01T10:00:00")]   // SqlBulkCopy alone gives 10:01
    [InlineData("2020-01-01T10:00:29.5000000", "2020-01-01T10:00:00")]
    [InlineData("2020-01-01T10:00:30.0000000", "2020-01-01T10:01:00")]
    [InlineData("2020-01-01T10:00:59.9999999", "2020-01-01T10:01:00")]
    [InlineData("2020-01-01T23:59:30.0000000", "2020-01-02T00:00:00")]
    [InlineData("2020-01-01T10:00:29.9966667", "2020-01-01T10:00:00")]   // datetime 10:00:29.997, read exactly
    public void Smalldatetime_values_round_like_CAST(string input, string expected)
    {
        Assert.Equal(At(expected), TargetShape.RoundValue(At(input), "smalldatetime", 0));
        var shape = new TargetShape("app.T", [new TargetColumn("S", "smalldatetime", 4, 16, 0, false, false)]);
        var table = new DataTable();
        table.Columns.Add("S", typeof(DateTime));
        table.Rows.Add(At(input));
        shape.Normalize(table, [new ColumnBinding("S", "S")]);
        Assert.Equal(At(expected), table.Rows[0]["S"]);
    }

    [Fact]
    public void Time_and_datetimeoffset_round_like_CAST()
    {
        Assert.Equal(TimeSpan.Parse("23:59:59.9", CultureInfo.InvariantCulture), TargetShape.RoundValue(TimeSpan.Parse("23:59:59.9999999", CultureInfo.InvariantCulture), "time", 1));
        Assert.Equal(TimeSpan.Parse("00:00:00.1", CultureInfo.InvariantCulture), TargetShape.RoundValue(TimeSpan.Parse("00:00:00.05", CultureInfo.InvariantCulture), "time", 1));
        var dto = DateTimeOffset.Parse("2020-01-01T10:00:00.9999996+02:00", CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse("2020-01-01T10:00:01+02:00", CultureInfo.InvariantCulture), TargetShape.RoundValue(dto, "datetimeoffset", 0));
        Assert.Equal("abc", TargetShape.RoundValue("abc", "datetime2", 0));
        Assert.Equal(At("2020-01-01T10:00:00.9970000"), TargetShape.RoundValue(At("2020-01-01T10:00:00.9970000"), "datetime", 0));
    }

    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 12, 0, 0, "varchar(12)")]
    [InlineData("char", 2, 0, 0, "char(2)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 19, 4, "decimal(19,4)")]
    [InlineData("datetime2", 6, 19, 0, "datetime2(0)")]
    [InlineData("time", 5, 16, 7, "time(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("bit", 1, 1, 0, "bit")]
    public void TypeText_matches_TSQL_declarations(string type, int maxLength, byte precision, byte scale, string expected)
        => Assert.Equal(expected, TargetShape.TypeText(type, maxLength, precision, scale));

    [Fact]
    public void Normalize_rounds_only_bound_temporal_columns_with_lower_scale()
    {
        var shape = new TargetShape("app.T", [
            new TargetColumn("CreatedAt", "datetime2", 6, 19, 0, false, false),
            new TargetColumn("Raw", "datetime2", 8, 27, 7, false, false),
        ]);
        var table = new DataTable();
        table.Columns.Add("CreatedAtSrc", typeof(DateTime));
        table.Columns.Add("Raw", typeof(DateTime));
        table.Rows.Add(At("2020-01-01T10:00:00.997"), At("2020-01-01T10:00:00.997"));
        table.Rows.Add(DBNull.Value, DBNull.Value);
        shape.Normalize(table, [new ColumnBinding("CreatedAtSrc", "CreatedAt"), new ColumnBinding("Raw", "Raw")]);
        Assert.Equal(At("2020-01-01T10:00:01"), table.Rows[0]["CreatedAtSrc"]);
        Assert.Equal(At("2020-01-01T10:00:00.997"), table.Rows[0]["Raw"]);
        Assert.Equal(DBNull.Value, table.Rows[1]["CreatedAtSrc"]);
    }

    [Fact]
    public void RowSnapshot_truncates_long_values_and_keeps_nulls()
    {
        var table = new DataTable();
        table.Columns.Add("Cmnt", typeof(string));
        table.Columns.Add("Qty", typeof(int));
        table.Columns.Add("Blob", typeof(byte[]));
        table.Rows.Add(new string('x', 300), DBNull.Value, new byte[] { 0xAB, 0x01 });
        string json = RowSnapshot.Json(table.Rows[0],
            [new ColumnBinding("Cmnt", "Comment"), new ColumnBinding("Qty", "Quantity"), new ColumnBinding("Blob", "Data")]);
        var o = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(RowSnapshot.MaxValueLength + 1, o["Comment"]!.GetValue<string>().Length);   // 200 chars + "…"
        Assert.True(o.ContainsKey("Quantity"));
        Assert.Null(o["Quantity"]);
        Assert.Equal("0xAB01", o["Data"]!.GetValue<string>());
    }

    [Fact]
    public void RowSnapshot_never_cuts_a_surrogate_pair_in_half()
    {
        // Harm: a lone high surrogate is not valid text; the snapshot would carry a broken character into error_row.
        string value = new string('x', RowSnapshot.MaxValueLength - 1) + "\U0001F600" + "tail";
        string text = RowSnapshot.Text(value);
        Assert.DoesNotContain(text, c => char.IsSurrogate(c));
        Assert.Equal(new string('x', RowSnapshot.MaxValueLength - 1) + "…", text);
    }
}
