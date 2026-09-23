using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>
/// A theory that needs to create a real SQL login (sweep item 52): skipped, with the reason, when the test server is
/// Windows-auth-only (<c>SERVERPROPERTY('IsIntegratedSecurityOnly')</c>) or the test login lacks ALTER ANY LOGIN -
/// rather than failing on a permission or a mode that says nothing about the behaviour under test. On the default
/// LocalDB the login owns the instance and mixed mode is on, so it always runs there. The probe runs once per process.
/// </summary>
public sealed class SqlAuthTheoryAttribute : TheoryAttribute
{
    private static readonly Lazy<string?> Reason = new(Probe);

    public SqlAuthTheoryAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }

    private static string? Probe()
    {
        try
        {
            using var conn = new SqlConnection(SqlTestServer.MasterConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT ISNULL(SERVERPROPERTY('IsIntegratedSecurityOnly'), 1), " +
                "ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY LOGIN'), 0)", conn);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return "The test SQL Server answered nothing for the SQL-auth capability probe.";
            var integratedOnly = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) == 1;
            var canAlterLogin = Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (integratedOnly)
                return "The test SQL Server is Windows-auth-only (IsIntegratedSecurityOnly = 1), so this test cannot create a SQL login.";
            if (!canAlterLogin)
                return "The test login lacks ALTER ANY LOGIN, so this test cannot create a SQL login.";
            return null;
        }
        catch (SqlException ex)
        {
            return "The test SQL Server could not be asked whether this login may create a SQL login: " + ex.Message;
        }
    }
}
