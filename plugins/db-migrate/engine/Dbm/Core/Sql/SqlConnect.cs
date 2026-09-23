using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Sql;

public static class SqlConnect
{
    private static readonly string DriverDefaultApplicationName = new SqlConnectionStringBuilder().ApplicationName;

    /// <summary>Sets Application Name=dbm when the user left the driver default; everything else is kept verbatim.</summary>
    public static string Normalize(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(b.ApplicationName) || b.ApplicationName == DriverDefaultApplicationName) b.ApplicationName = "dbm";
        return b.ConnectionString;
    }

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(Normalize(connectionString));
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private const string LocalDbPrefix = @"(localdb)\";

    /// <summary>
    /// Open item 49: <c>(localdb)\&lt;InstanceName&gt;</c> when the connection string's Data Source names a LocalDB instance, else null.
    /// It is what stays the same across instance restarts, where <c>@@SERVERNAME</c> (<c>&lt;machine&gt;\LOCALDB#&lt;hex&gt;</c>) does
    /// not. Only the Data Source is read, so nothing else in the string - a password above all - can reach the result.
    /// </summary>
    public static string? LocalDbDataSource(string connectionString)
    {
        string source;
        try
        {
            source = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return null;
        }
        if (!source.StartsWith(LocalDbPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        string instance = source[LocalDbPrefix.Length..].Trim();
        return instance.Length == 0 ? null : LocalDbPrefix + instance;
    }

    public static async Task<ServerMeta> ProbeAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionString, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(@@SERVERNAME, CAST(SERVERPROPERTY('ServerName') AS nvarchar(256))),
                   DB_NAME(),
                   @@VERSION,
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Collation') AS nvarchar(128)),
                   CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
                   (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME())
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("server metadata query returned no row");

        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        var productVersion = Text(3);
        var versionLine = Text(2).Split('\n')[0].Trim();
        var major = int.TryParse(productVersion.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var compat = r.IsDBNull(7) ? 0 : Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture);

        return new ServerMeta(Text(0), Text(1), versionLine, productVersion, major, Text(4), Text(5), Text(6), compat,
            Redactor.AuthMode(connectionString), LocalDbDataSource(connectionString));
    }
}
