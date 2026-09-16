using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>SQL type of one key column as reported by the source reader (V7: parameters must use this exact type).
/// Precision/Scale are 0 where the type has none (SqlClient reports 255 there).</summary>
public sealed record KeyType(string Name, int Size, byte Precision, byte Scale);

public sealed record KeyValue(IReadOnlyList<KeyType> Types, IReadOnlyList<object> Values);

/// <summary>
/// Type-preserving last-key serialisation: [{"t":"varchar","s":20,"p":0,"c":0,"v":"abc"}, …] (values as invariant text).
/// The decoded key becomes the next chunk's parameters, so name, size, precision and scale round-trip exactly, and every value
/// must be of the CLR type SqlClient returns for its SQL type. Any unreadable key is a <see cref="TransferException"/>:
/// "bad_key" (malformed), "key_type" (unsupported SQL type), "key_null" (NULL key value), "key_missing" (column absent).
/// </summary>
public static class KeyCodec
{
    public static IReadOnlyList<KeyType> TypesOf(SqlDataReader reader, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(keyColumns);
        var schema = reader.GetColumnSchema();
        var result = new List<KeyType>(keyColumns.Count);
        foreach (var key in keyColumns)
        {
            var col = schema.FirstOrDefault(c => string.Equals(c.ColumnName, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new TransferException("key_missing", $"Key column {key} is not in the source query result.");
            string name = (col.DataTypeName ?? "").ToLowerInvariant();
            ClrTypeOf(name);   // validates support early
            result.Add(new KeyType(name, col.ColumnSize ?? 0, Applicable(col.NumericPrecision), Applicable(col.NumericScale)));
        }
        return result;
    }

    public static KeyValue FromRow(DataRow row, IReadOnlyList<string> keyColumns, IReadOnlyList<KeyType> types)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(types);
        if (keyColumns.Count != types.Count)
            throw BadKey($"{keyColumns.Count} key columns but {types.Count} key types.");
        var values = new List<object>(keyColumns.Count);
        foreach (var key in keyColumns)
        {
            if (!row.Table.Columns.Contains(key)) throw new TransferException("key_missing", $"Key column {key} is not in the row.");
            object v = row[key];
            if (v is DBNull) throw new TransferException("key_null", $"Key column {key} is NULL; keyset chunking needs NOT NULL keys.");
            values.Add(v);
        }
        return new KeyValue(types, values);
    }

    public static string Encode(KeyValue key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckShape(key, null);
        var arr = new JsonArray();
        for (int i = 0; i < key.Types.Count; i++)
        {
            var t = key.Types[i];
            arr.Add(new JsonObject { ["t"] = t.Name, ["s"] = t.Size, ["p"] = t.Precision, ["c"] = t.Scale, ["v"] = ToText(key.Values[i]) });
        }
        return arr.ToJsonString();
    }

    public static KeyValue Decode(string json)
    {
        if (json is null) throw BadKey("Checkpoint key is null.");
        try
        {
            var arr = JsonNode.Parse(json) as JsonArray ?? throw BadKey("Checkpoint key is not a JSON array.");
            if (arr.Count == 0) throw BadKey("Checkpoint key has no columns.");
            var types = new List<KeyType>(arr.Count);
            var values = new List<object>(arr.Count);
            foreach (var node in arr)
            {
                if (node is not JsonObject o) throw BadKey("Checkpoint key entry is not an object.");
                var t = new KeyType(Member(o, "t").GetValue<string>(), Member(o, "s").GetValue<int>(),
                    Member(o, "p").GetValue<byte>(), Member(o, "c").GetValue<byte>());
                types.Add(t);
                values.Add(FromText(t.Name, Member(o, "v").GetValue<string>()));
            }
            return new KeyValue(types, values);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw BadKey($"Checkpoint key is unreadable: {ex.Message}");
        }
    }

    public static SqlParameter Parameter(string name, KeyType type, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        CheckValue(type, value, name);
        var p = new SqlParameter(name, DbTypeOf(type.Name)) { Value = value };
        switch (type.Name)
        {
            case "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary":
                p.Size = type.Size is <= 0 or > 8000 ? -1 : type.Size;
                break;
            case "decimal" or "numeric":
                p.Precision = type.Precision;
                p.Scale = type.Scale;
                break;
            case "datetime2" or "datetimeoffset" or "time":
                p.Scale = type.Scale > 7 ? (byte)7 : type.Scale;
                break;
        }
        return p;
    }

    /// <summary>Human-readable key for error rows and the UI: {"__k0":42,"__k1":"abc"}.</summary>
    public static string Display(KeyValue key, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyColumns);
        CheckShape(key, keyColumns.Count);
        var o = new JsonObject();
        for (int i = 0; i < keyColumns.Count; i++)
        {
            object v = key.Values[i];
            JsonNode? node = v switch
            {
                int n => n, short n => n, long n => n, byte n => n, decimal n => n, bool n => n, double n => n, float n => n,
                _ => ToText(v),
            };
            o[keyColumns[i]] = node;
        }
        return o.ToJsonString();
    }

    private static byte Applicable(int? reported) => reported is null or < 0 or >= 255 ? (byte)0 : (byte)reported.Value;

    private static JsonNode Member(JsonObject o, string name)
        => o[name] ?? throw BadKey($"Checkpoint key entry has no \"{name}\".");

    private static void CheckShape(KeyValue key, int? expectedCount)
    {
        if (key.Types is null || key.Values is null) throw BadKey("Key has no types or no values.");
        if (key.Types.Count != key.Values.Count) throw BadKey($"Key has {key.Types.Count} types but {key.Values.Count} values.");
        if (key.Types.Count == 0) throw BadKey("Key has no columns.");
        if (expectedCount is { } n && n != key.Types.Count) throw BadKey($"Key has {key.Types.Count} columns but {n} were expected.");
        for (int i = 0; i < key.Types.Count; i++)
        {
            if (key.Types[i] is null) throw BadKey($"Key type {i} is null.");
            CheckValue(key.Types[i], key.Values[i], $"key {i}");
        }
    }

    private static void CheckValue(KeyType type, object? value, string what)
    {
        var clr = ClrTypeOf(type.Name);
        if (value is null or DBNull) throw new TransferException("key_null", $"The value of {what} is NULL; keyset chunking needs NOT NULL keys.");
        if (value.GetType() != clr)
            throw BadKey($"The value of {what} is a {value.GetType().Name} but SQL type {type.Name} needs a {clr.Name}.");
    }

    /// <summary>The CLR type SqlDataReader.GetValue returns for each supported key type; anything else is "key_type".</summary>
    private static Type ClrTypeOf(string type) => type switch
    {
        "bigint" => typeof(long), "int" => typeof(int), "smallint" => typeof(short), "tinyint" => typeof(byte), "bit" => typeof(bool),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal), "float" => typeof(double), "real" => typeof(float),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime), "datetimeoffset" => typeof(DateTimeOffset),
        "time" => typeof(TimeSpan), "char" or "varchar" or "nchar" or "nvarchar" => typeof(string),
        "uniqueidentifier" => typeof(Guid), "binary" or "varbinary" => typeof(byte[]),
        _ => throw Unsupported(type),
    };

    private static SqlDbType DbTypeOf(string type) => type switch
    {
        "bigint" => SqlDbType.BigInt, "int" => SqlDbType.Int, "smallint" => SqlDbType.SmallInt, "tinyint" => SqlDbType.TinyInt,
        "bit" => SqlDbType.Bit, "decimal" or "numeric" => SqlDbType.Decimal, "money" => SqlDbType.Money, "smallmoney" => SqlDbType.SmallMoney,
        "float" => SqlDbType.Float, "real" => SqlDbType.Real, "date" => SqlDbType.Date, "datetime" => SqlDbType.DateTime,
        "datetime2" => SqlDbType.DateTime2, "smalldatetime" => SqlDbType.SmallDateTime, "datetimeoffset" => SqlDbType.DateTimeOffset,
        "time" => SqlDbType.Time, "char" => SqlDbType.Char, "varchar" => SqlDbType.VarChar, "nchar" => SqlDbType.NChar,
        "nvarchar" => SqlDbType.NVarChar, "uniqueidentifier" => SqlDbType.UniqueIdentifier, "binary" => SqlDbType.Binary,
        "varbinary" => SqlDbType.VarBinary,
        _ => throw Unsupported(type),
    };

    private static string ToText(object v) => v switch
    {
        string s => s,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid g => g.ToString("D"),
        bool flag => flag ? "true" : "false",
        double dbl => dbl.ToString("R", CultureInfo.InvariantCulture),
        float flt => flt.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static object FromText(string type, string s) => type switch
    {
        "bigint" => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture),
        "int" => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture),
        "smallint" => short.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture),
        "tinyint" => byte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture),
        "bit" => s switch { "true" => true, "false" => false, _ => throw new FormatException($"'{s}' is not a bit value.") },
        "decimal" or "numeric" or "money" or "smallmoney" => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture),
        "float" => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        "real" => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        "date" or "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "datetimeoffset" => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "time" => TimeSpan.ParseExact(s, "c", CultureInfo.InvariantCulture),
        "char" or "varchar" or "nchar" or "nvarchar" => s,
        "uniqueidentifier" => Guid.ParseExact(s, "D"),
        "binary" or "varbinary" => Convert.FromBase64String(s),
        _ => throw Unsupported(type),
    };

    private static TransferException BadKey(string message) => new("bad_key", message);

    private static TransferException Unsupported(string type)
        => new("key_type", $"Key column type '{type}' is not supported for keyset chunking.");
}
