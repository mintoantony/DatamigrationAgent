using Dbm.Core.Matching;

namespace Dbm.Tests.Unit.Matching;

public sealed class NameNormalizerTests
{
    private static readonly Synonyms Syn = Synonyms.Default();

    [Theory]
    [InlineData("CUST_NM", new[] { "cust", "nm" })]
    [InlineData("EmailAddress", new[] { "email", "address" })]
    [InlineData("Line1", new[] { "line", "1" })]
    [InlineData("HTTPServer", new[] { "http", "server" })]
    [InlineData("tbl_Customer", new[] { "tbl", "customer" })]
    [InlineData("order-line.item id", new[] { "order", "line", "item", "id" })]
    public void Split(string identifier, string[] expected) => Assert.Equal(expected, NameNormalizer.Split(identifier));

    [Theory]
    [InlineData("EmailAddress", new[] { "email", "address" })]
    [InlineData("tbl_Customer", new[] { "customer" })]
    [InlineData("DOB", new[] { "birth", "date" })]
    [InlineData("CRT_DT", new[] { "created", "date" })]
    [InlineData("CreatedAt", new[] { "created", "date" })]
    [InlineData("Clients", new[] { "customer" })]
    [InlineData("ZIP", new[] { "postal", "code" })]
    [InlineData("PHONE_NO", new[] { "phone", "number" })]
    [InlineData("LastModified", new[] { "last", "updated" })]
    [InlineData("Addresses", new[] { "address" })]
    [InlineData("ORD_HDR", new[] { "order", "header" })]
    public void Tokens(string identifier, string[] expected) => Assert.Equal(expected, NameNormalizer.Tokens(identifier, Syn));

    [Fact]
    public void Tokens_drop_context_tokens_when_others_remain()
    {
        var context = NameNormalizer.Tokens("CUST", Syn);
        Assert.Equal(new[] { "customer" }, context);
        Assert.Equal(new[] { "name" }, NameNormalizer.Tokens("CUST_NM", Syn, context));
        Assert.Equal(new[] { "customer" }, NameNormalizer.Tokens("CUST", Syn, context));   // nothing else would remain
        Assert.Equal(new[] { "id" }, NameNormalizer.Tokens("CustomerId", Syn, NameNormalizer.Tokens("Customers", Syn)));
    }

    [Theory]
    [InlineData("addresses", "address")]
    [InlineData("categories", "category")]
    [InlineData("statuses", "status")]
    [InlineData("orders", "order")]
    [InlineData("address", "address")]
    [InlineData("status", "status")]
    [InlineData("analysis", "analysis")]
    [InlineData("boxes", "box")]
    [InlineData("ids", "id")]
    [InlineData("is", "is")]
    [InlineData("1", "1")]
    public void Singular(string token, string expected) => Assert.Equal(expected, NameNormalizer.Singular(token));

    [Fact]
    public void Synonyms_expand_abbreviations_then_canonicalise_groups()
    {
        Assert.Equal(new[] { "customer" }, Syn.Expand("CUST"));
        Assert.Equal(new[] { "customer" }, Syn.Expand("client"));
        Assert.Equal(new[] { "birth", "date" }, Syn.Expand("dob"));
        Assert.Equal(new[] { "time" }, Syn.Expand("ts"));
        Assert.Equal(new[] { "updated" }, Syn.Expand("modified"));
        Assert.Equal(new[] { "zebra" }, Syn.Expand("zebra"));
    }

    [Fact]
    public void Synonyms_load_merges_extra_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbm-syn-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"abbreviations":{"kd":["kunde"],"cust":["patron"]},"groups":[["kunde","customer"]]}""");
        try
        {
            var syn = Synonyms.Load(path, Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
            Assert.Equal(new[] { "kunde" }, syn.Expand("KD"));
            Assert.Equal(new[] { "patron" }, syn.Expand("cust"));
            Assert.Equal(new[] { "kunde" }, syn.Expand("client"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synonyms_load_reports_invalid_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbm-syn-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => Synonyms.Load(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
