using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>
/// A fact that needs to <c>KILL</c> another session (review NIT-4): skipped, with the reason, when the test login lacks ALTER ANY
/// CONNECTION - a least-privilege <c>DBM_TEST_SQL</c> - rather than failing on a permission that says nothing about the behaviour under
/// test. On the default LocalDB the login owns the instance, so it always runs there. The permission is asked for once per process.
/// </summary>
public sealed class KillSessionFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(Probe);

    public KillSessionFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }

    private static string? Probe()
    {
        try
        {
            using var conn = new SqlConnection(SqlTestServer.MasterConnectionString);
            conn.Open();
            using var cmd = new SqlCommand("SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY CONNECTION'), 0)", conn);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1
                ? null
                : "The test login lacks ALTER ANY CONNECTION, so this test cannot KILL the lock session it needs to kill.";
        }
        catch (SqlException ex)
        {
            return "The test SQL Server could not be asked whether this login may KILL a session: " + ex.Message;
        }
    }
}
