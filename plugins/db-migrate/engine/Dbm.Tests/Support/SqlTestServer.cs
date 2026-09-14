using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>The SQL Server used by integration tests: env DBM_TEST_SQL, else LocalDB.</summary>
public static class SqlTestServer
{
    public const string DefaultConnectionString = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBM_TEST_SQL") is { Length: > 0 } cs ? cs : DefaultConnectionString;

    public static string MasterConnectionString => ForDatabase("master");

    public static string ForDatabase(string database) =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;
}
