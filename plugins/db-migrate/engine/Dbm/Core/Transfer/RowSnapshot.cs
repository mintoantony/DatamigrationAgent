using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Dbm.Core.SqlGen;

namespace Dbm.Core.Transfer;

/// <summary>Rejected-row JSON for error_row: bound columns by target name, each value cut to 200 characters.</summary>
public static class RowSnapshot
{
    public const int MaxValueLength = 200;

    /// <summary>One entry per binding: no bound column may be missing from the artifact a human reads to diagnose a rejected row. Two
    /// bindings sharing a target would overwrite each other under the target's name, so both are keyed <c>"Target &lt;- Source"</c>
    /// instead; a binding whose source column is not in the row keeps its key and says so in the value.</summary>
    public static string Json(DataRow row, IReadOnlyList<ColumnBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(bindings);
        var shared = bindings.GroupBy(b => b.Target, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var o = new JsonObject();
        foreach (var b in bindings)
        {
            string key = shared.Contains(b.Target) ? $"{b.Target} <- {b.Source}" : b.Target;
            for (int n = 2; o.ContainsKey(key); n++) key = $"{b.Target} <- {b.Source} #{n}";
            if (!row.Table.Columns.Contains(b.Source))
            {
                o[key] = $"(the row has no column \"{b.Source}\")";
                continue;
            }
            object v = row[b.Source];
            o[key] = v is DBNull ? null : Text(v);
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
