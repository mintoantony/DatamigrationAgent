namespace Dbm.Tests.Support;

public static class TempDatabaseExtensions
{
    /// <summary>COUNT_BIG(*) of a table given as it appears in SQL ("app.Parent", "[app].[Orders]").</summary>
    public static Task<long> CountAsync(this TempDatabase db, string table) => db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM {table}");
}
