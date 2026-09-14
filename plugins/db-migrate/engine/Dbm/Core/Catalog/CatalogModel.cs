using System.Globalization;
using System.Text.Json.Serialization;
using Dbm.Core.Sql;

namespace Dbm.Core.Catalog;

public sealed record CatalogSnapshot(ServerMeta Server, List<TableInfo> Tables, ObjectCounts Objects, DateTimeOffset ExtractedAt)
{
    /// <summary>Finds a table by "schema.name" (case-insensitive).</summary>
    public TableInfo? FindTable(string key) =>
        Tables.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}

public sealed record ObjectCounts(int Views, int Procedures, int Functions, int Triggers, int Synonyms);

public sealed record TableInfo(string Schema, string Name, long Rows, double SizeMb, List<ColumnInfo> Columns,
    List<IndexInfo> Indexes, List<ForeignKeyInfo> ForeignKeys, int TriggerCount, string? TemporalType, string? Description)
{
    /// <summary>Names of the table's DML triggers (M2 addition to C10; TriggerCount == TriggerNames.Count after extraction).</summary>
    public List<string> TriggerNames { get; init; } = new();

    [JsonIgnore] public string Key => $"{Schema}.{Name}";
    [JsonIgnore] public IndexInfo? PrimaryKey => Indexes.FirstOrDefault(i => i.IsPrimaryKey);
    [JsonIgnore] public bool IsHeap => !Indexes.Any(i => i.IsClustered);

    public ColumnInfo? FindColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>PK columns, else the first unique index whose columns are all NOT NULL, else null.</summary>
    public IReadOnlyList<string>? BestKey()
    {
        if (PrimaryKey is { Columns.Count: > 0 } pk) return pk.Columns;
        foreach (var index in Indexes)
        {
            if (!index.IsUnique || index.Columns.Count == 0) continue;
            if (index.Columns.All(name => FindColumn(name) is { IsNullable: false })) return index.Columns;
        }
        return null;
    }
}

// DataType = sys.types name, lower-case. MaxLength = characters for char/varchar/nchar/nvarchar, bytes for binary/varbinary,
// -1 = max, 0 for other types.
public sealed record ColumnInfo(string Name, int Ordinal, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable,
    bool IsIdentity, bool IsComputed, bool IsRowVersion, string? DefaultDefinition, string? Collation, string? Description)
{
    public ColumnProfile? Profile { get; init; }

    /// <summary>sys.computed_columns.definition for computed columns (M2 addition to C10).</summary>
    public string? ComputedDefinition { get; init; }

    [JsonIgnore]
    public string TypeDisplay => DataType switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
            $"{DataType}({(MaxLength == -1 ? "max" : MaxLength.ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{DataType}({Precision.ToString(CultureInfo.InvariantCulture)},{Scale.ToString(CultureInfo.InvariantCulture)})",
        "datetime2" or "datetimeoffset" or "time" => $"{DataType}({Scale.ToString(CultureInfo.InvariantCulture)})",
        "timestamp" => "rowversion",
        _ => DataType,
    };
}

public sealed record IndexInfo(string Name, bool IsPrimaryKey, bool IsUnique, bool IsClustered, List<string> Columns);

public sealed record ForeignKeyInfo(string Name, List<string> Columns, string RefSchema, string RefTable, List<string> RefColumns,
    bool IsDisabled, bool IsNotTrusted)
{
    [JsonIgnore] public string RefKey => $"{RefSchema}.{RefTable}";
}

public sealed record ColumnProfile(long SampledRows, long Nulls, long? Distinct, string? Min, string? Max, int? MaxLen, double? AvgLen,
    string? SemanticClass, List<string> TopPatterns, List<string> Samples)
{
    [JsonIgnore] public double NullRatio => SampledRows == 0 ? 0 : (double)Nulls / SampledRows;
    [JsonIgnore] public double? DistinctRatio => Distinct is null || SampledRows == 0 ? null : (double)Distinct / SampledRows;
}

/// <summary>Data-type predicates shared by the profiler, rules and text renderers.</summary>
public static class TypeTraits
{
    public static bool IsString(string dataType) => dataType is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

    public static bool IsDeprecated(string dataType) => dataType is "text" or "ntext" or "image";

    /// <summary>(max) types and xml; the deprecated text/ntext/image types are reported separately (R02).</summary>
    public static bool IsModernLob(ColumnInfo c) => c.MaxLength == -1 || c.DataType == "xml";

    public static bool IsLob(ColumnInfo c) => IsModernLob(c) || IsDeprecated(c.DataType);

    /// <summary>Types that support COUNT(DISTINCT), MIN and MAX in the profiler.</summary>
    public static bool IsComparable(ColumnInfo c) =>
        c.MaxLength != -1 &&
        c.DataType is not ("text" or "ntext" or "image" or "xml" or "geography" or "geometry" or "hierarchyid" or "sql_variant" or "timestamp");

    /// <summary>Semantic class implied by a non-string type (strings are classified from their values instead).</summary>
    public static string? TypeClass(string dataType) => dataType switch
    {
        "date" => "date",
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "datetime",
        "tinyint" or "smallint" or "int" or "bigint" => "integer",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "decimal",
        "uniqueidentifier" => "guid",
        "bit" => "flag",
        _ => null,
    };
}
