using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>Reads source rows into a DataTable (needed for bisection). Safe with CommandBehavior.SequentialAccess.</summary>
public static class ChunkReader
{
    private static readonly DateTime SqlBaseDate = new(1900, 1, 1);

    public static DataTable NewTable(SqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        for (int i = 0; i < reader.FieldCount; i++) table.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
        return table;
    }

    /// <summary>Appends up to max rows; returns the number read (0 = reader exhausted). max &lt; 1 is refused: it would return 0, the
    /// exhausted signal, while rows remain.</summary>
    public static async Task<int> FillAsync(SqlDataReader reader, DataTable table, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        int n = reader.FieldCount;
        var exact = new bool[n];
        for (int i = 0; i < n; i++) exact[i] = string.Equals(reader.GetDataTypeName(i), "datetime", StringComparison.OrdinalIgnoreCase);
        int count = 0;
        table.BeginLoadData();
        try
        {
            while (count < max && await reader.ReadAsync(ct))
            {
                var values = new object[n];
                for (int i = 0; i < n; i++)
                {
                    if (await reader.IsDBNullAsync(i, ct)) { values[i] = DBNull.Value; continue; }
                    values[i] = exact[i] ? ExactDateTime(reader.GetSqlDateTime(i)) : reader.GetValue(i);
                }
                table.Rows.Add(values);
                count++;
            }
        }
        finally
        {
            table.EndLoadData();
        }
        return count;
    }

    /// <summary>V8: datetime stores 1/300 s ticks; SqlClient rounds them to ms, CAST(... AS datetime2(7)) does not.</summary>
    public static DateTime ExactDateTime(SqlDateTime value)
        => SqlBaseDate.AddDays(value.DayTicks).AddTicks((value.TimeTicks * 100_000L + 1) / 3);
}
