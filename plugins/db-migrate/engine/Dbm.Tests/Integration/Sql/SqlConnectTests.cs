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
