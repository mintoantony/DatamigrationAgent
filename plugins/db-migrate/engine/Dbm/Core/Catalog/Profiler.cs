using System.Text;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Catalog;

public sealed record ProfileOptions(int SampleRows, bool SampleValues, int PatternRows = 1000);

/// <summary>Samples each table (TOP n, no ORDER BY) and attaches a <see cref="ColumnProfile"/> to every column.</summary>
public static class Profiler
{
    public const int MaxColumnsPerQuery = 100;
    public const int SampleMaxChars = 40;
    private const int PatternInputChars = 64;
    private const int CommandTimeoutSeconds = 300;

    public static async Task<CatalogSnapshot> ProfileAsync(SqlConnection conn, CatalogSnapshot snapshot, ProfileOptions options,
        Action<string>? log, CancellationToken ct)
    {
        var tables = new List<TableInfo>(snapshot.Tables.Count);
        foreach (var table in snapshot.Tables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (profiled, sampled) = await ProfileTableAsync(conn, table, options, ct);
                tables.Add(profiled);
                log?.Invoke($"profiled {table.Key}: {sampled} rows sampled, {table.Columns.Count} columns");
            }
            catch (SqlException ex)
            {
                tables.Add(table);
                log?.Invoke($"profile skipped for {table.Key}: {ex.Message}");
            }
        }
        return snapshot with { Tables = tables };
    }

    private static async Task<(TableInfo Table, long Sampled)> ProfileTableAsync(SqlConnection conn, TableInfo table,
        ProfileOptions options, CancellationToken ct)
    {
        var stats = new Dictionary<string, RawStats>(StringComparer.Ordinal);
        long sampled = 0;
        foreach (var batch in ProfilerSql.Batches(table.Columns))
        {
            await using var cmd = new SqlCommand(ProfilerSql.Aggregate(table, batch), conn) { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@sample", Math.Max(1, options.SampleRows));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;
            sampled = r.GetInt64(0);
            for (var j = 0; j < batch.Count; j++)
            {
                var column = batch[j];
                var s = new RawStats { Nulls = r.GetInt64(r.GetOrdinal($"n{j}")) };
                if (TypeTraits.IsComparable(column))
                {
                    s.Distinct = r.GetInt64(r.GetOrdinal($"d{j}"));
                    s.Min = Str(r, $"mn{j}");
                    s.Max = Str(r, $"mx{j}");
                }
                if (TypeTraits.IsString(column.DataType))
                {
                    var ml = r.GetOrdinal($"ml{j}");
                    var al = r.GetOrdinal($"al{j}");
                    s.MaxLen = r.IsDBNull(ml) ? null : r.GetInt32(ml);
                    s.AvgLen = r.IsDBNull(al) ? null : r.GetDouble(al);
                }
                stats[column.Name] = s;
            }
        }

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var stringColumns = table.Columns.Where(c => TypeTraits.IsString(c.DataType)).ToList();
        if (stringColumns.Count > 0 && sampled > 0 && options.PatternRows > 0)
        {
            foreach (var batch in ProfilerSql.Batches(stringColumns))
            {
                await using var cmd = new SqlCommand(ProfilerSql.PatternFetch(table, batch), conn) { CommandTimeout = CommandTimeoutSeconds };
                cmd.Parameters.AddWithValue("@patternRows", options.PatternRows);
                var lists = new List<string>[batch.Count];
                for (var j = 0; j < batch.Count; j++) values[batch[j].Name] = lists[j] = new List<string>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    for (var j = 0; j < batch.Count; j++)
                    {
                        if (!r.IsDBNull(j)) lists[j].Add(r.GetString(j).TrimEnd());
                    }
                }
            }
        }

        var columns = table.Columns
            .Select(c => stats.TryGetValue(c.Name, out var s) ? c with { Profile = Build(c, s, sampled, values.GetValueOrDefault(c.Name), options) } : c)
            .ToList();
        return (table with { Columns = columns }, sampled);
    }

    private static ColumnProfile Build(ColumnInfo column, RawStats s, long sampled, List<string>? values, ProfileOptions options)
    {
        var isString = TypeTraits.IsString(column.DataType);
        var patterns = new List<string>();
        var samples = new List<string>();
        string? semanticClass;
        if (isString)
        {
            var list = values ?? new List<string>();
            patterns = list
                .GroupBy(v => ValueSignature.Pattern(v.Length > PatternInputChars ? v[..PatternInputChars] : v), StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(3).Select(g => g.Key).ToList();
            semanticClass = ValueSignature.Classify(list);
            if (options.SampleValues)
                samples = list.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Take(3).Select(v => Truncate(v)!).ToList();
        }
        else
        {
            semanticClass = sampled > s.Nulls ? TypeTraits.TypeClass(column.DataType) : null;
        }

        // String MIN/MAX are real values: hide them when the project disables sample values.
        var hide = isString && !options.SampleValues;
        return new ColumnProfile(sampled, s.Nulls, s.Distinct, hide ? null : Truncate(s.Min), hide ? null : Truncate(s.Max),
            s.MaxLen, s.AvgLen is null ? null : Math.Round(s.AvgLen.Value, 2), semanticClass, patterns, samples);
    }

    public static string? Truncate(string? value) =>
        value is null ? null : value.Length <= SampleMaxChars ? value : value[..(SampleMaxChars - 1)] + "…";

    private static string? Str(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private sealed class RawStats
    {
        public long Nulls;
        public long? Distinct;
        public string? Min;
        public string? Max;
        public int? MaxLen;
        public double? AvgLen;
    }
}

/// <summary>SQL text used by <see cref="Profiler"/>; public so it can be unit-tested without a server.</summary>
public static class ProfilerSql
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string From(TableInfo table) => $"{Quote(table.Schema)}.{Quote(table.Name)}";

    public static IReadOnlyList<IReadOnlyList<ColumnInfo>> Batches(IReadOnlyList<ColumnInfo> columns, int size = Profiler.MaxColumnsPerQuery)
    {
        var batches = new List<IReadOnlyList<ColumnInfo>>();
        for (var i = 0; i < columns.Count; i += size) batches.Add(columns.Skip(i).Take(size).ToList());
        return batches;
    }

    /// <summary>One row: [rows], then per column j: [n{j}] nulls; [d{j}] [mn{j}] [mx{j}] for comparable types; [ml{j}] [al{j}] for strings.</summary>
    public static string Aggregate(TableInfo table, IReadOnlyList<ColumnInfo> columns)
    {
        var sb = new StringBuilder("SELECT COUNT_BIG(*) AS [rows]");
        for (var j = 0; j < columns.Count; j++)
        {
            var c = columns[j];
            var q = "x." + Quote(c.Name);
            sb.Append($",\n  ISNULL(SUM(CAST(CASE WHEN {q} IS NULL THEN 1 ELSE 0 END AS bigint)), 0) AS [n{j}]");
            if (TypeTraits.IsComparable(c))
            {
                var v = c.DataType == "bit" ? $"CAST({q} AS tinyint)" : q;
                var style = c.DataType is "binary" or "varbinary" ? 1 : 126;
                sb.Append($",\n  COUNT_BIG(DISTINCT {q}) AS [d{j}]");
                sb.Append($",\n  CONVERT(nvarchar(4000), MIN({v}), {style}) AS [mn{j}]");
                sb.Append($",\n  CONVERT(nvarchar(4000), MAX({v}), {style}) AS [mx{j}]");
            }
            if (TypeTraits.IsString(c.DataType))
            {
                var len = c.DataType switch
                {
                    "text" => $"DATALENGTH({q})",
                    "ntext" => $"DATALENGTH({q}) / 2",
                    _ => $"LEN({q})",
                };
                sb.Append($",\n  CAST(MAX({len}) AS int) AS [ml{j}]");
                sb.Append($",\n  AVG(CAST({len} AS float)) AS [al{j}]");
            }
        }
        sb.Append($"\nFROM (SELECT TOP (@sample) * FROM {From(table)}) AS x;");
        return sb.ToString();
    }

    /// <summary>First @patternRows rows of the given string columns, each cast to nvarchar(200) as [p{j}].</summary>
    public static string PatternFetch(TableInfo table, IReadOnlyList<ColumnInfo> stringColumns)
    {
        var columns = string.Join(", ", stringColumns.Select((c, j) => $"CAST(t.{Quote(c.Name)} AS nvarchar(200)) AS [p{j}]"));
        return $"SELECT TOP (@patternRows) {columns} FROM {From(table)} AS t;";
    }
}
