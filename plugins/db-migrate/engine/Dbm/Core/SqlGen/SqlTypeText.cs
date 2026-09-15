using System.Globalization;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.SqlGen;

/// <summary>Formats catalog column types as T-SQL and parses <c>sp_describe_first_result_set</c> type names back into <see cref="ColumnType"/>.</summary>
public static class SqlTypeText
{
    static readonly HashSet<string> Lengthed = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" };
    static readonly HashSet<string> PrecisionScale = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };
    static readonly HashSet<string> ScaleOnly = new(StringComparer.OrdinalIgnoreCase) { "datetime2", "time", "datetimeoffset" };
    static readonly HashSet<string> LegacyLob = new(StringComparer.OrdinalIgnoreCase) { "text", "ntext", "image", "xml" };

    /// <summary>"nvarchar(50)", "varchar(max)", "decimal(19,4)", "datetime2(0)", "int".</summary>
    public static string Format(ColumnType t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var dt = t.DataType.ToLowerInvariant();
        if (Lengthed.Contains(dt)) return $"{dt}({(t.MaxLength == -1 ? "max" : Inv(t.MaxLength))})";
        if (PrecisionScale.Contains(dt)) return $"{dt}({Inv(t.Precision)},{Inv(t.Scale)})";
        if (ScaleOnly.Contains(dt)) return $"{dt}({Inv(t.Scale)})";
        return dt;
    }

    public static string Format(ColumnInfo c) => Format(ColumnType.From(c ?? throw new ArgumentNullException(nameof(c))));

    static string Inv(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>(max) types and the legacy LOB types text / ntext / image / xml.</summary>
    public static bool IsLob(ColumnType t) => (t ?? throw new ArgumentNullException(nameof(t))).MaxLength == -1 || LegacyLob.Contains(t.DataType);

    public static bool IsLob(ColumnInfo c) => IsLob(ColumnType.From(c ?? throw new ArgumentNullException(nameof(c))));

    /// <summary>Parses a <c>system_type_name</c> such as "varchar(100)", "nvarchar(max)", "decimal(19,4)" or "int".
    /// Length is taken from the name (characters for n/char types, bytes for binary types, -1 for max);
    /// precision and scale come from the result-set columns of the same name (same meaning as sys.columns).</summary>
    public static ColumnType Parse(string systemTypeName, int precision, int scale)
    {
        ArgumentNullException.ThrowIfNull(systemTypeName);
        var name = systemTypeName.Trim().ToLowerInvariant();
        var paren = name.IndexOf('(');
        var dataType = paren < 0 ? name : name[..paren].Trim();
        var args = paren < 0 ? "" : name[(paren + 1)..].TrimEnd(')').Trim();
        var maxLength = 0;
        if (Lengthed.Contains(dataType))
            maxLength = args == "max" ? -1 : int.TryParse(args, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        else if (string.Equals(dataType, "xml", StringComparison.Ordinal))
            maxLength = -1;
        return new ColumnType(dataType, maxLength, Lengthed.Contains(dataType) ? 0 : precision, Lengthed.Contains(dataType) ? 0 : scale);
    }
}
