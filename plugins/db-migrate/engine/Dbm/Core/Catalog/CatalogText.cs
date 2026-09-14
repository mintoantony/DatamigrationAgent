using System.Globalization;
using Dbm.Core.Matching;
using Dbm.Core.State;

namespace Dbm.Core.Catalog;

/// <summary>Compact one-line renderings used by `dbm show`, `dbm search` and work packets.</summary>
public static class CatalogText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"src table dbo.CUST rows=1000 size=0.14MB pk=CUST_ID triggers=0" (+ " heap", " temporal=…", " fk→dbo.X").</summary>
    public static string TableHeader(Side side, TableInfo t)
    {
        var parts = new List<string>
        {
            $"{EnumText.ToText(side)} table {t.Key}",
            $"rows={t.Rows.ToString(Inv)}",
            $"size={t.SizeMb.ToString("0.##", Inv)}MB",
            t.PrimaryKey is { } pk ? $"pk={string.Join(",", pk.Columns)}" : t.BestKey() is { } key ? $"key={string.Join(",", key)}" : "pk=none",
        };
        if (t.IsHeap) parts.Add("heap");
        parts.Add($"triggers={t.TriggerCount.ToString(Inv)}");
        if (t.TemporalType is not null) parts.Add($"temporal={t.TemporalType}");
        foreach (var fk in t.ForeignKeys)
            parts.Add($"fk→{fk.RefKey}{(fk.IsNotTrusted ? "(untrusted)" : "")}{(fk.IsDisabled ? "(disabled)" : "")}");
        return string.Join(' ', parts);
    }

    /// <summary>"EMAIL_ADDR varchar(120) null=10% distinct=1.00 class=email samples=[a, b, c]".</summary>
    public static string ColumnLine(TableInfo t, ColumnInfo c)
    {
        var parts = new List<string> { c.Name, c.TypeDisplay };
        if (!c.IsNullable) parts.Add("NN");
        if (c.IsIdentity) parts.Add("id");
        if (t.PrimaryKey?.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true) parts.Add("pk");
        foreach (var fk in t.ForeignKeys)
        {
            var i = fk.Columns.FindIndex(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            parts.Add(fk.Columns.Count == 1 ? $"fk→{fk.RefKey}.{fk.RefColumns[i]}" : $"fk→{fk.RefKey}({string.Join(",", fk.RefColumns)})");
        }
        if (c.IsComputed) parts.Add("computed");
        if (c.IsRowVersion) parts.Add("rowversion");
        if (c.DefaultDefinition is not null) parts.Add($"default={c.DefaultDefinition}");
        if (c.Profile is { } p)
        {
            if (p.SampledRows == 0) parts.Add("empty");
            else
            {
                parts.Add($"null={Percent(p.NullRatio)}");
                if (p.DistinctRatio is { } d) parts.Add($"distinct={d.ToString("0.00", Inv)}");
                if (p.MaxLen is { } ml) parts.Add($"maxlen={ml.ToString(Inv)}");
                if (p.SemanticClass is not null) parts.Add($"class={p.SemanticClass}");
                if (p.Samples.Count > 0) parts.Add($"samples=[{string.Join(", ", p.Samples)}]");
            }
        }
        return string.Join(' ', parts);
    }

    /// <summary>"profile sampled=1000 nulls=100 distinct=900 min=… max=… maxlen=28 avglen=25.9 patterns=[a9.a9@a.a]".</summary>
    public static string ProfileLine(ColumnProfile p)
    {
        var parts = new List<string> { "profile", $"sampled={p.SampledRows.ToString(Inv)}", $"nulls={p.Nulls.ToString(Inv)}" };
        if (p.Distinct is { } d) parts.Add($"distinct={d.ToString(Inv)}");
        if (p.Min is not null) parts.Add($"min={p.Min}");
        if (p.Max is not null) parts.Add($"max={p.Max}");
        if (p.MaxLen is { } ml) parts.Add($"maxlen={ml.ToString(Inv)}");
        if (p.AvgLen is { } al) parts.Add($"avglen={al.ToString("0.#", Inv)}");
        if (p.SemanticClass is not null) parts.Add($"class={p.SemanticClass}");
        if (p.TopPatterns.Count > 0) parts.Add($"patterns=[{string.Join(", ", p.TopPatterns)}]");
        return string.Join(' ', parts);
    }

    /// <summary>Header line + one indented line per column (at most <paramref name="maxColumns"/>, then "… n more columns").</summary>
    public static string Table(Side side, TableInfo t, int maxColumns = int.MaxValue)
    {
        var lines = new List<string> { TableHeader(side, t) };
        lines.AddRange(t.Columns.Take(maxColumns).Select(c => "  " + ColumnLine(t, c)));
        if (t.Columns.Count > maxColumns) lines.Add($"  … {t.Columns.Count - maxColumns} more columns");
        return string.Join('\n', lines);
    }

    /// <summary>"0.82 tgt column app.Customers.Email — email".</summary>
    public static string SearchLine(SearchHit hit) =>
        $"{hit.Score.ToString("0.00", Inv)} {EnumText.ToText(hit.Side)} {hit.Kind} {hit.Text}";

    public static string Percent(double ratio) =>
        ratio == 0 ? "0%" : ratio < 0.01 ? "<1%" : (ratio * 100).ToString("0", Inv) + "%";
}
