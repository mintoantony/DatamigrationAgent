using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

/// <summary>Null-aware column readers shared by every repository.</summary>
public static class ReaderExtensions
{
    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static int? IntN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static long? LongN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static bool Bool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;

    public static DateTimeOffset Ts(this SqliteDataReader r, int i) => ParseTs(r.GetString(i));

    public static DateTimeOffset? TsN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseTs(r.GetString(i));

    public static T Enum<T>(this SqliteDataReader r, int i) where T : struct, System.Enum => EnumText.Parse<T>(r.GetString(i));

    public static T? EnumN<T>(this SqliteDataReader r, int i) where T : struct, System.Enum =>
        r.IsDBNull(i) ? null : EnumText.Parse<T>(r.GetString(i));

    public static DateTimeOffset ParseTs(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
