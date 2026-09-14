using Dbm.Core.Patching;

namespace Dbm.Tests.Unit.Patching;

public class PatchParseTests
{
    [Fact]
    public void Parses_the_documented_patch_file_format()
    {
        var patch = Patch.Parse("""
            {"phase":"mapping","baseVersion":3,
             "ops":[{"op":"replace","path":"/tables/dbo.Customers/columns/Email/expr","value":"LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))"}],
             "responses":[{"feedbackId":12,"status":"addressed","note":"Email is now trimmed and lower-cased."}],
             "summary":"Normalised email; mapped FAX to drop list."}
            """);

        Assert.Equal("mapping", patch.Phase);
        Assert.Equal(3, patch.BaseVersion);
        Assert.Equal("LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))", patch.Ops.Single().Value!.GetValue<string>());
        Assert.Equal(12, patch.Responses.Single().FeedbackId);
        Assert.Equal("Normalised email; mapped FAX to drop list.", patch.Summary);
    }

    [Fact]
    public void Missing_lists_become_empty()
    {
        var patch = Patch.Parse("""{"phase":"analysis","baseVersion":0}""");

        Assert.Empty(patch.Ops);
        Assert.Empty(patch.Responses);
        Assert.Null(patch.Summary);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("null", "patch is empty")]
    [InlineData("""{"baseVersion":1}""", "patch.phase is required")]
    [InlineData("""{"phase":"sql","baseVersion":1,"ops":[{"op":"add"}]}""", "op 0: 'op' and 'path' are required")]
    public void Structural_problems_throw_PatchException(string json, string message)
    {
        var ex = Assert.Throws<PatchException>(() => Patch.Parse(json));

        Assert.Contains(message, ex.Message);
    }
}
