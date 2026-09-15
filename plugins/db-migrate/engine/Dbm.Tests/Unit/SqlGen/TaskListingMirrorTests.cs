using System.Text.Json;
using Dbm.Core;
using Dbm.Core.SqlGen;

namespace Dbm.Tests.Unit.SqlGen;

/// <summary>The C# half of the listing mirror. <c>Dbm.Tests/js/fixtures/task-listing.json</c> holds tasks with the exact numbering
/// and anchors with the exact parse results; this class asserts them against <see cref="TaskListing.Build"/> and
/// <see cref="SqlModule.ParseAnchor"/>, and <c>js/sql-view.test.cjs</c> asserts the SAME file against <c>DBM.sqlView.listing</c> and
/// <c>DBM.sqlView.parseAnchor</c>. A comment anchor <c>sql:&lt;taskId&gt;:&lt;line&gt;</c> is created by the browser and resolved by
/// the engine, so either implementation drifting would land a reviewer's comment on the wrong line without any error.</summary>
public sealed class TaskListingMirrorTests
{
    private static JsonElement Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "plugins/db-migrate/engine/Dbm.Tests/js/fixtures/task-listing.json";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, relative)));
        return doc.RootElement.Clone();
    }

    public static IEnumerable<object[]> ListingCases() =>
        Fixture().GetProperty("listing").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

    public static IEnumerable<object[]> AnchorCases() =>
        Fixture().GetProperty("anchors").EnumerateArray().Select((_, i) => new object[] { i });

    [Theory]
    [MemberData(nameof(ListingCases))]
    public void Listing_matches_the_shared_fixture(string name)
    {
        var item = Fixture().GetProperty("listing").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var task = Json.Deserialize<TaskPlan>(item.GetProperty("task").GetRawText());
        var expected = item.GetProperty("expected").EnumerateArray()
            .Select(l => new ListingLine(l.GetProperty("no").GetInt32(), l.GetProperty("section").GetString()!, l.GetProperty("text").GetString()!))
            .ToList();

        Assert.Equal(expected, TaskListing.Build(task));
    }

    [Theory]
    [MemberData(nameof(AnchorCases))]
    public void ParseAnchor_matches_the_shared_fixture(int index)
    {
        var item = Fixture().GetProperty("anchors")[index];
        string? Str(string p) => item.GetProperty(p).ValueKind == JsonValueKind.Null ? null : item.GetProperty(p).GetString();
        var line = item.GetProperty("line");

        var (task, parsedLine) = SqlModule.ParseAnchor(Str("anchor"));

        Assert.Equal(Str("task"), task);
        Assert.Equal(line.ValueKind == JsonValueKind.Null ? null : line.GetInt32(), parsedLine);
    }

    /// <summary>The theories above iterate a file read from disk: an empty or unfound fixture would give them nothing to assert.
    /// The minimums are literals on purpose, never derived from the file; raise them when a case is added, never lower them to
    /// fit a trimmed fixture. This fixture is the only test pinning TaskListing's and ParseAnchor's rules against mutation.
    /// js/sql-view.test.cjs asserts the same minimums.</summary>
    [Fact]
    public void Fixture_holds_at_least_the_expected_number_of_cases()
    {
        const int minListingCases = 11;
        const int minAnchorCases = 31;
        var fixture = Fixture();

        Assert.True(fixture.GetProperty("listing").GetArrayLength() >= minListingCases, $"listing cases: {fixture.GetProperty("listing").GetArrayLength()}");
        Assert.True(fixture.GetProperty("anchors").GetArrayLength() >= minAnchorCases, $"anchor cases: {fixture.GetProperty("anchors").GetArrayLength()}");
        Assert.True(ListingCases().Count() >= minListingCases);
        Assert.True(AnchorCases().Count() >= minAnchorCases);
    }

    [Fact]
    public void Fixture_covers_rejected_line_forms()
    {
        var anchors = Fixture().GetProperty("anchors").EnumerateArray().ToList();
        Assert.True(anchors.Count(a => a.GetProperty("task").ValueKind == JsonValueKind.Null) >= 15);
        Assert.Contains(anchors, a => a.GetProperty("anchor").GetString() == "sql:T04:2147483648");
    }
}
