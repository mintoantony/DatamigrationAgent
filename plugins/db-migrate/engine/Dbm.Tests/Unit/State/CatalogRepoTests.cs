using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public sealed class CatalogRepoTests
{
    [Fact]
    public void Save_get_and_replace_snapshots()
    {
        using var project = TempProject.Create();
        var repo = project.Services.Catalog;
        Assert.Null(repo.Get(Side.Src));
        Assert.Null(repo.Fingerprint(Side.Src));

        repo.Save(Side.Src, TestCatalogs.LegacyShop(), "fp1");
        var back = repo.Get(Side.Src)!;
        Assert.Equal(8, back.Tables.Count);
        Assert.Equal(0.1, back.FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!.Profile!.NullRatio, 3);
        Assert.Equal("fp1", repo.Fingerprint(Side.Src));
        Assert.Null(repo.Get(Side.Tgt));

        repo.Save(Side.Src, TestCatalogs.ShopV2(), "fp2");
        Assert.Equal(6, repo.Get(Side.Src)!.Tables.Count);
        Assert.Equal("fp2", repo.Fingerprint(Side.Src));
    }

    [Fact]
    public void SaveVectors_replaces_rows_per_side_and_filters()
    {
        using var project = TempProject.Create();
        var repo = project.Services.Catalog;
        var (rows, _) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Synonyms.Default());
        repo.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Src));
        repo.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));
        repo.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));   // idempotent replace

        Assert.Equal(rows.Count, repo.Vectors().Count);
        Assert.Equal(6, repo.Vectors(Side.Tgt, "table").Count);
        var email = repo.Vectors(Side.Tgt, "column").Single(r => r.Key == "app.Customers.Email");
        Assert.Equal("app.Customers.Email — email", email.Text);
        Assert.Equal(1.0, email.Vec.Dot(email.Vec), 6);

        Assert.Throws<ArgumentException>(() => repo.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Tgt)));
    }
}
