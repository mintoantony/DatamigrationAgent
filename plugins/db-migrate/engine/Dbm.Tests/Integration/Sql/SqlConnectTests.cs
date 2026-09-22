using Dbm.Core.Sql;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Sql;

[Trait("Category", "Integration")]
public class SqlConnectTests
{
    [Fact]
    public async Task Probe_reads_server_and_database_metadata()
    {
        await using var db = await TempDatabase.CreateAsync();

        var meta = await SqlConnect.ProbeAsync(db.ConnectionString, CancellationToken.None);

        Assert.Equal(db.Name, meta.Database);
        Assert.False(string.IsNullOrEmpty(meta.Server));
        Assert.StartsWith("Microsoft SQL Server", meta.Version);
        Assert.DoesNotContain('\n', meta.Version);
        Assert.True(meta.MajorVersion >= 13, $"major {meta.MajorVersion}");
        Assert.StartsWith(meta.MajorVersion + ".", meta.ProductVersion);
        Assert.False(string.IsNullOrEmpty(meta.Edition));
        Assert.False(string.IsNullOrEmpty(meta.ServerCollation));
        Assert.False(string.IsNullOrEmpty(meta.DatabaseCollation));
        Assert.True(meta.CompatLevel >= 100);
        Assert.Equal("integrated", meta.AuthSummary);
    }

    /// <summary>Open item 49: a LocalDB connection's probe records the stable <c>(localdb)\Name</c> the connection string names - the
    /// server name it reports changes on every instance start - and a probe of any other server records none.</summary>
    [Fact]
    public async Task Probe_records_the_stable_LocalDB_data_source_and_no_other()
    {
        await using var db = await TempDatabase.CreateAsync();
        string named = new SqlConnectionStringBuilder(db.ConnectionString).DataSource.Trim();
        string? expected = named.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase) ? @"(localdb)\" + named[10..] : null;

        var meta = await SqlConnect.ProbeAsync(db.ConnectionString, CancellationToken.None);

        Assert.True(meta.DataSource == expected,
            $"the probe recorded the data source '{meta.DataSource}', but the connection string names '{expected}'");
    }

    [Theory]
    [InlineData(@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Shop;Integrated Security=true", @"(localdb)\MSSQLLocalDB")]
    [InlineData(@"Server=(LocalDB)\Dev2 ;Database=Shop;User ID=sa;Password=Secr3t!x", @"(localdb)\Dev2")]
    [InlineData(@"Server= (localdb)\.\Shared ;Database=Shop", @"(localdb)\.\Shared")]
    [InlineData(@"Server=SQL01;Database=Shop;User ID=sa;Password=Secr3t!x", null)]
    [InlineData(@"Server=tcp:sql01.internal,1433;Database=Shop", null)]
    [InlineData(@"Server=np:\\.\pipe\LOCALDB#1A254D6D\tsql\query;Database=Shop", null)]
    [InlineData(@"Server=(localdb)\;Database=Shop", null)]
    public void LocalDbDataSource_is_the_instance_the_connection_string_names_and_never_a_credential(string cs, string? expected)
    {
        string? actual = SqlConnect.LocalDbDataSource(cs);

        Assert.True(actual == expected, $"the LocalDB data source of '{cs}' came out as '{actual}', not '{expected}'");
        Assert.True(actual is null || !actual.Contains("Secr3t", StringComparison.Ordinal), "the data source carries the password");
    }

    [Fact]
    public async Task Open_tags_the_session_with_application_name_dbm()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var connection = await SqlConnect.OpenAsync(db.ConnectionString, CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT APP_NAME()";

        Assert.Equal("dbm", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Probe_of_a_missing_database_throws_SqlException()
    {
        var cs = SqlTestServer.ForDatabase("dbm_does_not_exist_" + Guid.NewGuid().ToString("N")[..6]);

        await Assert.ThrowsAsync<SqlException>(() => SqlConnect.ProbeAsync(cs, CancellationToken.None));
    }

    [Fact]
    public async Task TempDatabase_runs_go_separated_batches()
    {
        await using var db = await TempDatabase.CreateAsync();

        await db.ExecAsync("CREATE TABLE dbo.T (X int)\nGO\nCREATE VIEW dbo.V AS SELECT X FROM dbo.T\n  go  \nINSERT dbo.T VALUES (1)");

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.V"));
    }
}
