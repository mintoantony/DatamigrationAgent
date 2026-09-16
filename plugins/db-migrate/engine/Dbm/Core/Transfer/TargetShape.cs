using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>MaxLength in bytes as in sys.columns (-1 = max).</summary>
public sealed record TargetColumn(string Name, string DataType, int MaxLength, byte Precision, byte Scale, bool IsIdentity, bool IsComputed)
{
    public string TypeText => TargetShape.TypeText(DataType, MaxLength, Precision, Scale);
}

/// <summary>Target table metadata read live from sys.columns, plus the V8 temporal rounding applied before bulk copy.</summary>
public sealed class TargetShape(string target, IReadOnlyList<TargetColumn> columns)
{
    private static readonly long[] Units = [10_000_000, 1_000_000, 100_000, 10_000, 1_000, 100, 10];

    public string Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
    public IReadOnlyList<TargetColumn> Columns { get; } = columns ?? throw new ArgumentNullException(nameof(columns));

    public TargetColumn? Find(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static async Task<TargetShape> LoadAsync(SqlConnection conn, string targetKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(targetKey);
        await using var cmd = new SqlCommand("""
            SELECT c.name, COALESCE(CASE WHEN t.is_user_defined = 1 THEN bt.name END, t.name), c.max_length, c.precision, c.scale, c.is_identity, c.is_computed
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.types AS bt ON bt.user_type_id = c.system_type_id
            WHERE c.object_id = OBJECT_ID(@t)
            ORDER BY c.column_id
            """, conn);
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(targetKey) });
        var cols = new List<TargetColumn>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                cols.Add(new TargetColumn(r.GetString(0), r.GetString(1).ToLowerInvariant(), r.GetInt16(2), r.GetByte(3), r.GetByte(4),
                    r.GetBoolean(5), r.GetBoolean(6)));
        if (cols.Count == 0) throw new TransferException("target_missing", $"Target table {targetKey} does not exist.");
        return new TargetShape(targetKey, cols);
    }

    /// <summary>The namespace <see cref="Normalize"/> writes its own columns in. <c>BulkLoader</c> refuses a task that binds a column
    /// here, because <see cref="Normalize"/> would overwrite it before the load.</summary>
    public const string NormalizedPrefix = "__dbm_n_";

    /// <summary>The column <see cref="Normalize"/> writes one binding's rounded values into, and the column <c>BulkLoader</c> maps that
    /// binding from when the table has it. One per binding, because one source column can be bound to two targets of different scale and
    /// rounding it in place would give the finer target the coarser target's value.</summary>
    /// <remarks>The source's length goes in front of it, so the name is unique for each binding: without it ("A", "X-&gt;Y") and
    /// ("A-&gt;X", "Y") produce one column, and the two bindings overwrite each other's rounded values.</remarks>
    public static string NormalizedColumn(ColumnBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return string.Create(CultureInfo.InvariantCulture,
            $"{NormalizedPrefix}{binding.Source.Length}_{binding.Source}->{binding.Target}");
    }

    /// <summary>Rounds bound datetime2/datetimeoffset/time values to the target scale, and smalldatetime values to the minute, exactly like
    /// CAST (V8). Without it SqlBulkCopy truncates the fraction (datetime2(0) gets 10:00:00 for 10:00:00.997) and rounds smalldatetime on the
    /// fractional second (10:01 for 10:00:29.999, where CAST gives 10:00). Each binding's rounded values go into a column of its own
    /// (<see cref="NormalizedColumn"/>); the source column is never written to, so a second binding on the same source — or a key column
    /// the caller still has to read — keeps the value it needs.</summary>
    public void Normalize(DataTable table, IReadOnlyList<ColumnBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var b in bindings)
        {
            var col = Find(b.Target);
            if (col is null || !Rounds(col.DataType, col.Scale)) continue;
            int src = table.Columns.IndexOf(b.Source);
            if (src < 0) continue;
            string name = NormalizedColumn(b);
            var dest = table.Columns[name] ?? table.Columns.Add(name, table.Columns[src]!.DataType);
            foreach (DataRow row in table.Rows)
            {
                object v = row[src];
                row[dest] = v is DBNull ? v : RoundValue(v, col.DataType, col.Scale);
            }
        }
    }

    public static string TypeText(string dataType, int maxLength, byte precision, byte scale) => dataType switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{dataType}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
        "nchar" or "nvarchar" => $"{dataType}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{dataType}({precision},{scale})"),
        "datetime2" or "datetimeoffset" or "time" => string.Create(CultureInfo.InvariantCulture, $"{dataType}({scale})"),
        _ => dataType,
    };

    /// <summary>Half-up rounding to 10^-scale seconds; when rounding up would pass maxTicks the value is truncated instead (as CAST does).</summary>
    public static long RoundTicks(long ticks, int scale, long maxTicks)
    {
        if (scale >= 7) return ticks;
        long unit = Units[Math.Max(scale, 0)];
        long rem = ticks % unit;
        if (rem * 2 < unit) return ticks - rem;
        long up = ticks - rem + unit;
        return up > maxTicks ? ticks - rem : up;
    }

    private static bool Rounds(string dataType, int scale)
        => dataType == "smalldatetime" || (scale < 7 && dataType is ("datetime2" or "datetimeoffset" or "time"));

    /// <summary>CAST to smalldatetime drops the fractional second, then rounds half a minute up: 10:00:29.9999999 -> 10:00, 10:00:30 -> 10:01.
    /// A value past the type's range is left for the server to reject, as CAST does.</summary>
    private static DateTime RoundSmallDateTime(DateTime value)
    {
        long ticks = value.Ticks - value.Ticks % TimeSpan.TicksPerSecond;
        long rem = ticks % TimeSpan.TicksPerMinute;
        ticks -= rem;
        if (rem >= 30 * TimeSpan.TicksPerSecond && ticks + TimeSpan.TicksPerMinute <= DateTime.MaxValue.Ticks) ticks += TimeSpan.TicksPerMinute;
        return new DateTime(ticks, value.Kind);
    }

    public static object RoundValue(object value, string targetType, int scale)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (targetType == "smalldatetime") return value is DateTime sd ? RoundSmallDateTime(sd) : value;
        if (scale >= 7 || targetType is not ("datetime2" or "datetimeoffset" or "time")) return value;
        return value switch
        {
            DateTime d => new DateTime(RoundTicks(d.Ticks, scale, DateTime.MaxValue.Ticks), d.Kind),
            DateTimeOffset o => new DateTimeOffset(RoundTicks(o.Ticks, scale, DateTime.MaxValue.Ticks), o.Offset),
            TimeSpan t => new TimeSpan(RoundTicks(t.Ticks, scale, TimeSpan.TicksPerDay - 1)),
            _ => value,
        };
    }
}
