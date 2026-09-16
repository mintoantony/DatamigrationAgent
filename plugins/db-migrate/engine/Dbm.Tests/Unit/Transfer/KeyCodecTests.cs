using System.Data;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class KeyCodecTests
{
    [Fact]
    public void Encode_decode_preserves_types_and_values()
    {
        var types = new List<KeyType>
        {
            new("int", 4, 10, 0), new("smallint", 2, 5, 0), new("varchar", 20, 0, 0), new("datetime2", 8, 27, 7),
            new("decimal", 17, 19, 4), new("uniqueidentifier", 16, 0, 0), new("varbinary", 16, 0, 0),
            new("datetimeoffset", 10, 34, 7), new("bigint", 8, 19, 0), new("time", 5, 16, 7), new("bit", 1, 1, 0),
        };
        var dt = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Unspecified).AddTicks(33333);
        var values = new List<object>
        {
            42, (short)7, "a-b", dt, 12.3456m, Guid.Parse("7d9f7a4c-3b8e-4a53-9a57-1b2d4c6e8f00"), new byte[] { 1, 2, 255 },
            new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)), 9_000_000_000L, new TimeSpan(0, 23, 59, 59, 999), true,
        };
        string json = KeyCodec.Encode(new KeyValue(types, values));
        Assert.StartsWith("[{\"t\":\"int\"", json);

        var back = KeyCodec.Decode(json);
        Assert.Equal(types, back.Types);
        Assert.Equal(42, back.Values[0]);
        Assert.Equal((short)7, back.Values[1]);
        Assert.Equal("a-b", back.Values[2]);
        Assert.Equal(dt, back.Values[3]);
        Assert.Equal(12.3456m, back.Values[4]);
        Assert.Equal(values[5], back.Values[5]);
        Assert.Equal(new byte[] { 1, 2, 255 }, (byte[])back.Values[6]);
        Assert.Equal(values[7], back.Values[7]);
        Assert.Equal(9_000_000_000L, back.Values[8]);
        Assert.Equal(values[9], back.Values[9]);
        Assert.Equal(true, back.Values[10]);
    }

    [Fact]
    public void Parameter_uses_the_exact_sql_type()
    {
        var p = KeyCodec.Parameter("@k0", new KeyType("varchar", 20, 0, 0), "abc");
        Assert.Equal(SqlDbType.VarChar, p.SqlDbType);
        Assert.Equal(20, p.Size);
        var d = KeyCodec.Parameter("@k1", new KeyType("decimal", 17, 19, 4), 1.5m);
        Assert.Equal(SqlDbType.Decimal, d.SqlDbType);
        Assert.Equal(19, d.Precision);
        Assert.Equal(4, d.Scale);
        var m = KeyCodec.Parameter("@k2", new KeyType("nvarchar", int.MaxValue, 0, 0), "x");
        Assert.Equal(-1, m.Size);
        Assert.Throws<TransferException>(() => KeyCodec.Parameter("@k3", new KeyType("xml", 0, 0, 0), "<a/>"));
    }

    [Fact]
    public void FromRow_and_Display_use_key_aliases()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("__k0", typeof(int));
        table.Columns.Add("__k1", typeof(short));
        table.Rows.Add("x", 5, (short)2);
        var types = new List<KeyType> { new("int", 4, 10, 0), new("smallint", 2, 5, 0) };
        var key = KeyCodec.FromRow(table.Rows[0], ["__k0", "__k1"], types);
        Assert.Equal(5, key.Values[0]);
        Assert.Equal((short)2, key.Values[1]);
        Assert.Equal("{\"__k0\":5,\"__k1\":2}", KeyCodec.Display(key, ["__k0", "__k1"]));
    }

    // ---- additions beyond the brief: the awkward types, and exact fidelity rather than value equality ----

    [Fact]
    public void Awkward_values_round_trip_exactly_including_scale_offset_kind_and_ticks()
    {
        var types = new List<KeyType>
        {
            new("decimal", 17, 18, 4), new("numeric", 17, 38, 10), new("datetime2", 8, 27, 7), new("datetime2", 7, 23, 3),
            new("datetimeoffset", 10, 34, 7), new("time", 5, 16, 7), new("nchar", 6, 0, 0), new("nvarchar", 50, 0, 0),
            new("varbinary", 8000, 0, 0), new("float", 8, 15, 0), new("real", 4, 7, 0), new("money", 8, 19, 4),
            new("date", 3, 10, 0), new("datetime", 8, 23, 3), new("tinyint", 1, 3, 0), new("bit", 1, 1, 0),
            new("int", 4, 10, 0), new("bigint", 8, 19, 0),
        };
        var values = new List<object>
        {
            1.5000m,                                                                   // trailing zeros carry the scale
            -123456789012345678.0123456789m,                                         // 28 significant digits, scale 10
            new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).AddTicks(9_999_999),
            new DateTime(2001, 2, 3, 4, 5, 6, 789, DateTimeKind.Unspecified),
            new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.FromMinutes(-570)).AddTicks(1),
            new TimeSpan(863_999_999_999L),                                            // 23:59:59.9999999
            "ab  é ",
            "O'Brien \"quoted\" \\ 中文 😀",
            Array.Empty<byte>(),
            0.1 + 0.2,
            1.1f,
            -922337203685477.5808m,
            new DateTime(1, 1, 1),
            new DateTime(1753, 1, 1, 0, 0, 0, 3),
            (byte)255,
            false,
            int.MinValue,
            long.MaxValue,
        };
        var back = KeyCodec.Decode(KeyCodec.Encode(new KeyValue(types, values)));

        Assert.Equal(types, back.Types);                        // name, size, precision AND scale survive
        for (int i = 0; i < values.Count; i++)
        {
            Assert.Equal(values[i].GetType(), back.Values[i].GetType());
            if (values[i] is byte[] bytes) Assert.Equal(bytes, (byte[])back.Values[i]);
            else Assert.Equal(values[i], back.Values[i]);
        }
        Assert.Equal(4, ((decimal)back.Values[0]).Scale);
        Assert.Equal("1.5000", ((decimal)back.Values[0]).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(((DateTime)values[2]).Ticks, ((DateTime)back.Values[2]).Ticks);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)back.Values[2]).Kind);
        Assert.Equal(TimeSpan.FromMinutes(-570), ((DateTimeOffset)back.Values[4]).Offset);
        Assert.Equal(((DateTimeOffset)values[4]).UtcTicks, ((DateTimeOffset)back.Values[4]).UtcTicks);
        Assert.Equal(0.30000000000000004, (double)back.Values[9]);
    }

    [Fact]
    public void Decoded_key_becomes_a_parameter_with_the_original_precision_and_scale()
    {
        var types = new List<KeyType> { new("decimal", 17, 18, 4), new("datetime2", 7, 23, 3), new("time", 5, 16, 7), new("nvarchar", 30, 0, 0) };
        var values = new List<object> { 12.5m, new DateTime(2020, 1, 1, 0, 0, 0, 5), TimeSpan.FromTicks(1), "x" };
        var back = KeyCodec.Decode(KeyCodec.Encode(new KeyValue(types, values)));

        var dec = KeyCodec.Parameter("@k0", back.Types[0], back.Values[0]);
        Assert.Equal((18, 4), (dec.Precision, dec.Scale));
        var dt2 = KeyCodec.Parameter("@k1", back.Types[1], back.Values[1]);
        Assert.Equal(SqlDbType.DateTime2, dt2.SqlDbType);
        Assert.Equal(3, dt2.Scale);
        var time = KeyCodec.Parameter("@k2", back.Types[2], back.Values[2]);
        Assert.Equal(SqlDbType.Time, time.SqlDbType);
        Assert.Equal(7, time.Scale);
        var nv = KeyCodec.Parameter("@k3", back.Types[3], back.Values[3]);
        Assert.Equal(SqlDbType.NVarChar, nv.SqlDbType);
        Assert.Equal(30, nv.Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[{\"t\":\"int\",\"s\":4,\"p\":10,\"c\":0}]")]
    [InlineData("[{\"t\":\"int\",\"s\":4,\"p\":10,\"c\":0,\"v\":\"4x\"}]")]
    [InlineData("[{\"t\":\"int\",\"s\":4,\"p\":10,\"c\":0,\"v\":42}]")]
    [InlineData("[{\"t\":\"int\",\"s\":4,\"p\":300,\"c\":0,\"v\":\"42\"}]")]
    [InlineData("[{\"t\":\"xml\",\"s\":0,\"p\":0,\"c\":0,\"v\":\"<a/>\"}]")]
    [InlineData("[{\"t\":\"bit\",\"s\":1,\"p\":1,\"c\":0,\"v\":\"yes\"}]")]
    [InlineData("[]")]
    public void Decode_reports_an_unreadable_checkpoint_as_bad_key(string json)
    {
        var ex = Assert.Throws<TransferException>(() => KeyCodec.Decode(json));
        Assert.True(ex.Code is "bad_key" or "key_type", ex.Code);
    }

    [Fact]
    public void Null_and_mismatched_inputs_are_rejected()
    {
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => KeyCodec.Decode(null!)).Code);
        Assert.Throws<ArgumentNullException>(() => KeyCodec.Encode(null!));
        var one = new List<KeyType> { new("int", 4, 10, 0) };
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => KeyCodec.Encode(new KeyValue(one, [1, 2]))).Code);
        Assert.Equal("key_null", Assert.Throws<TransferException>(() => KeyCodec.Encode(new KeyValue(one, [null!]))).Code);
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => KeyCodec.Encode(new KeyValue(one, ["42"]))).Code);   // value type must match the SQL type
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => KeyCodec.Display(new KeyValue(one, [1]), ["__k0", "__k1"])).Code);

        var table = new DataTable();
        table.Columns.Add("__k0", typeof(int));
        table.Rows.Add(DBNull.Value);
        Assert.Equal("key_null", Assert.Throws<TransferException>(() => KeyCodec.FromRow(table.Rows[0], ["__k0"], one)).Code);
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => KeyCodec.FromRow(table.Rows[0], ["__k0", "__k1"], one)).Code);
    }
}
