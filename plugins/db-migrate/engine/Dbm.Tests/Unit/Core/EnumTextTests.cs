using Dbm.Core;

namespace Dbm.Tests.Unit.Core;

public class EnumTextTests
{
    public enum Status { Pending, AwaitingReview, Approved }
    public enum Where { Src, Tgt }

    [Theory]
    [InlineData(Status.AwaitingReview, "awaiting_review")]
    [InlineData(Status.Pending, "pending")]
    [InlineData(Where.Tgt, "tgt")]
    public void ToText_is_snake_case_lower(Enum value, string expected)
    {
        Assert.Equal(expected, EnumText.ToText(value));
    }

    [Fact]
    public void Generic_round_trip_matches_the_json_converter()
    {
        foreach (var status in Enum.GetValues<Status>())
        {
            var text = EnumText.ToText(status);
            Assert.Equal(status, EnumText.Parse<Status>(text));
            Assert.Equal($"\"{text}\"", Json.Serialize(status));
        }
    }

    [Fact]
    public void Parse_accepts_pascal_case_and_any_case()
    {
        Assert.Equal(Status.AwaitingReview, EnumText.Parse<Status>("AwaitingReview"));
        Assert.Equal(Status.AwaitingReview, EnumText.Parse<Status>("AWAITING_REVIEW"));
    }

    [Fact]
    public void Parse_rejects_unknown_text_listing_valid_values()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnumText.Parse<Where>("source"));
        Assert.Contains("src, tgt", ex.Message);
        Assert.False(EnumText.TryParse<Where>(null, out _));
    }
}
