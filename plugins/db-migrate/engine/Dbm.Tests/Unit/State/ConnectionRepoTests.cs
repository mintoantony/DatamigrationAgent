using Dbm.Core.Crypto;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class ConnectionRepoTests
{
    private static ServerMeta Meta(string db) =>
        new("SRV1", db, "Microsoft SQL Server 2022", "16.0.1000.6", 16, "Developer Edition (64-bit)",
            "SQL_Latin1_General_CP1_CI_AS", "Latin1_General_CI_AS", 160, "sql");

    [Fact]
    public void Stores_only_ciphertext_and_round_trips()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var repo = new ConnectionRepo(db, new AesGcmFileKeyProtector(Path.Combine(tw.Root, "key")));
        const string cs = "Server=SRV1;Database=Legacy;User ID=u;Password=S3cr3t!pw";

        Assert.False(repo.Has(Side.Src));
        repo.Save(Side.Src, cs, Meta("Legacy"));

        Assert.True(repo.Has(Side.Src));
        Assert.False(repo.Has(Side.Tgt));
        var stored = db.Scalar<string>("SELECT encrypted FROM connection WHERE side = 'src'")!;
        Assert.StartsWith("aesgcm:", stored);
        Assert.DoesNotContain("S3cr3t", stored);
        Assert.Equal(cs, repo.GetConnectionString(Side.Src));
        Assert.Equal(Meta("Legacy"), repo.GetMeta(Side.Src));
        Assert.Null(repo.GetConnectionString(Side.Tgt));
        Assert.Null(repo.GetMeta(Side.Tgt));
    }

    [Fact]
    public void Saving_again_replaces_the_side()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var repo = new ConnectionRepo(db, new AesGcmFileKeyProtector(Path.Combine(tw.Root, "key")));

        repo.Save(Side.Tgt, "Server=a;Database=One;Integrated Security=true", Meta("One"));
        repo.Save(Side.Tgt, "Server=a;Database=Two;Integrated Security=true", Meta("Two"));

        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM connection"));
        Assert.Equal("Two", repo.GetMeta(Side.Tgt)!.Database);
    }
}
