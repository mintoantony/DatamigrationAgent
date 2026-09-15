using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Dbm.Core.SqlGen;

namespace Dbm.Core.Transfer;

/// <summary>Rejected-row JSON for error_row: bound columns by target name, each value cut to 200 characters.</summary>
public static class RowSnapshot
{
    public const int MaxValueLength = 200;

    public static string Json(DataRow row, IReadOnlyList<ColumnBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(bindings);
        var o = new JsonObject();
        foreach (var b in bindings)
        {
            if (!row.Table.Columns.Contains(b.Source)) continue;
            object v = row[b.Source];
            o[b.Target] = v is DBNull ? null : Text(v);
        }
        return o.ToJsonString();
    }

    public static string Text(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string s = value switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, MaxValueLength / 2)),
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        if (s.Length <= MaxValueLength) return s;
        int cut = char.IsHighSurrogate(s[MaxValueLength - 1]) ? MaxValueLength - 1 : MaxValueLength;   // never split a surrogate pair
        return s[..cut] + "…";
    }
}
