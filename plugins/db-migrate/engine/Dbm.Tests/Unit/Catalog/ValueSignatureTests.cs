using Dbm.Core.Catalog;

namespace Dbm.Tests.Unit.Catalog;

public sealed class ValueSignatureTests
{
    [Theory]
    [InlineData("John.Smith@x.com", "Aa.Aa@a.a")]
    [InlineData("first12.last12@example.com", "a9.a9@a.a")]
    [InlineData("+1-555-0123", "+9-9-9")]
    [InlineData("AB12 3CD", "A9 9A")]
    [InlineData("2020-01-02", "9-9-9")]
    [InlineData("", "")]
    public void Pattern(string value, string expected) => Assert.Equal(expected, ValueSignature.Pattern(value));

    [Theory]
    [InlineData("email", new[] { "a@b.com", "first1.last1@example.com", "x.y@z.org" })]
    [InlineData("phone", new[] { "+1-555-0123", "(555) 123-4567", "555 123 4567", "+44 20 7946 0958" })]
    [InlineData("date", new[] { "2020-01-02", "1999-12-31", "31/12/1999" })]
    [InlineData("datetime", new[] { "2020-01-02T10:00:00", "2020-01-02 10:00", "2021-06-30T23:59:59.123Z" })]
    [InlineData("guid", new[] { "0f8fad5b-d9cb-469f-a165-70867728950e", "{7c9e6679-7425-40de-944b-e07fc1f90ae7}" })]
    [InlineData("postal_code", new[] { "12345", "02134-1234", "SW1A 1AA", "K1A 0B1" })]
    [InlineData("flag", new[] { "Y", "N", "Y", "y" })]
    [InlineData("flag", new[] { "true", "false" })]
    [InlineData("country_code", new[] { "GB", "FR", "DE", "IT" })]
    [InlineData("integer", new[] { "1", "22", "-333" })]
    [InlineData("decimal", new[] { "1.5", "2,25", "-10.125" })]
    [InlineData("url", new[] { "http://x.com/a", "https://example.org", "www.example.com" })]
    [InlineData("text", new[] { "hello world", "Leave at reception" })]
    public void Classify(string expected, string[] values) => Assert.Equal(expected, ValueSignature.Classify(values));

    [Fact]
    public void Classify_needs_80_percent()
    {
        Assert.Equal("email", ValueSignature.Classify(new[] { "a@b.com", "c@d.com", "e@f.com", "g@h.com", "junk" }));
        Assert.Equal("text", ValueSignature.Classify(new[] { "a@b.com", "c@d.com", "e@f.com", "junk", "more junk" }));
    }

    [Fact]
    public void Classify_returns_null_without_values()
    {
        Assert.Null(ValueSignature.Classify(Array.Empty<string>()));
        Assert.Null(ValueSignature.Classify(new[] { "", "   " }));
    }
}
