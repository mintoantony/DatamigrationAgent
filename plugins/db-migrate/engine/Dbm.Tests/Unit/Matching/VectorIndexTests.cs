using Dbm.Core;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Matching;

public sealed class VectorIndexTests
{
    private static readonly Synonyms Syn = Synonyms.Default();

    [Fact]
    public void SparseVector_normalises_and_dots()
    {
        var a = SparseVector.Normalized(new Dictionary<string, double> { ["x"] = 3, ["y"] = 4 });
        Assert.Equal(0.6, a.W["x"], 6);
        Assert.Equal(1.0, a.Dot(a), 6);
        var b = SparseVector.Normalized(new Dictionary<string, double> { ["y"] = 1 });
        Assert.Equal(0.8, a.Dot(b), 6);
        Assert.True(SparseVector.Normalized(new Dictionary<string, double> { ["z"] = 0 }).IsEmpty);
        Assert.Equal("{\"w\":{\"y\":1}}", Json.Serialize(b));
    }

    [Fact]
    public void Vectorizer_cosine_sanity()
    {
        var v = new NgramTfidfVectorizer();
        var docs = new[] { new[] { "customer", "name" }, new[] { "customer", "email" }, new[] { "product", "price" }, new[] { "order", "date" } };
        v.Fit(docs);
        var same = v.Transform(docs[0]).Dot(v.Transform(new[] { "customer", "name" }));
        var related = v.Transform(docs[0]).Dot(v.Transform(docs[1]));
        var unrelated = v.Transform(docs[0]).Dot(v.Transform(docs[2]));
        Assert.Equal(1.0, same, 6);
        Assert.True(related > unrelated);
        Assert.True(v.Transform(Array.Empty<string>()).IsEmpty);
    }

    [Fact]
    public void Build_creates_one_row_per_table_and_column()
    {
        var src = TestCatalogs.LegacyShop();
        var tgt = TestCatalogs.ShopV2();
        var (rows, _) = VectorIndex.Build(src, tgt, Syn);
        var expected = src.Tables.Count + src.Tables.Sum(t => t.Columns.Count) + tgt.Tables.Count + tgt.Tables.Sum(t => t.Columns.Count);
        Assert.Equal(expected, rows.Count);
        Assert.Contains(rows, r => r is { Side: Side.Tgt, Kind: "column", Key: "app.Customers.Email", Text: "app.Customers.Email — email" });
        Assert.Contains(rows, r => r is { Side: Side.Src, Kind: "column", Key: "dbo.CUST.CUST_NM", Text: "dbo.CUST.CUST_NM — name" });
        Assert.Contains(rows, r => r is { Side: Side.Src, Kind: "table", Key: "dbo.ORD_HDR", Text: "dbo.ORD_HDR — order header" });
    }

    [Fact]
    public void Search_customer_email_finds_both_email_columns_in_the_top_3()
    {
        var (_, index) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Syn);
        var top = index.Search("customer email", k: 3).Select(h => h.Key).ToList();
        Assert.Contains("app.Customers.Email", top);
        Assert.Contains("dbo.CUST.EMAIL_ADDR", top);
    }

    [Fact]
    public void Search_filters_by_side_and_kind_and_from_rows_matches_build()
    {
        var (rows, built) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Syn);
        var hits = built.Search("cust email", Side.Tgt, "column", 5);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal((Side.Tgt, "column"), (h.Side, h.Kind)));
        Assert.Equal("app.Customers.Email", hits[0].Key);

        var loaded = VectorIndex.FromRows(rows, Syn).Search("cust email", Side.Tgt, "column", 5);
        Assert.Equal(hits.Select(h => (h.Key, Math.Round(h.Score, 9))), loaded.Select(h => (h.Key, Math.Round(h.Score, 9))));
        Assert.Empty(built.Search("   "));
    }
}
