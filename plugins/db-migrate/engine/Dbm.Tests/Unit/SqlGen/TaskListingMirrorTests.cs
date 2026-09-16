using System.Text.Json;
using Dbm.Core;
using Dbm.Core.SqlGen;

namespace Dbm.Tests.Unit.SqlGen;

/// <summary>The C# half of the listing mirror. <c>Dbm.Tests/js/fixtures/task-listing.json</c> holds tasks with the exact numbering
/// and anchors with the exact parse results; this class asserts them against <see cref="TaskListing.Build"/> and
/// <see cref="SqlModule.ParseAnchor"/>, and <c>js/sql-view.test.cjs</c> asserts the SAME file against <c>DBM.sqlView.listing</c> and
/// <c>DBM.sqlView.parseAnchor</c>. A comment anchor <c>sql:&lt;taskId&gt;:&lt;line&gt;</c> is created by the browser and resolved by
/// the engine, so either implementation drifting would land a reviewer's comment on the wrong line without any error.
/// <para>The same file's "globalCard" array pins the OTHER numbering rule — the global pre/post card, which is one code block over
/// the statements joined by a GO line — against the engine's bare-carriage-return line numbers. See
/// <see cref="Global_card_numbering_matches_the_shared_fixture"/>.</para></summary>
public sealed class TaskListingMirrorTests
{
    /// <summary>Exactly one location: the build copies <c>js/fixtures/task-listing.json</c> of THIS project to
    /// <c>fixtures/task-listing.json</c> under the output directory (see Dbm.Tests.csproj). No upward search, so another checkout's
    /// copy can never be read in its place.</summary>
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "task-listing.json");

    private static JsonElement Fixture()
    {
        if (!File.Exists(FixturePath))
            throw new FileNotFoundException($"Shared listing fixture not found at {FixturePath} (it is copied there from Dbm.Tests/js/fixtures by the build).", FixturePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        return doc.RootElement.Clone();
    }

    /// <summary>The file this class asserts is byte-for-byte the fixture node reads (js/fixtures/task-listing.json of this project,
    /// whose path the build records in assembly metadata). Fails if the copy is missing, stale, or another file.</summary>
    [Fact]
    public void Fixture_is_a_byte_identical_output_copy_of_this_projects_source_fixture()
    {
        var source = typeof(TaskListingMirrorTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(a => string.Equals(a.Key, "SharedListingFixtureSource", StringComparison.Ordinal))?.Value;

        Assert.False(string.IsNullOrEmpty(source), "the build did not record SharedListingFixtureSource");
        Assert.True(File.Exists(source), $"source fixture missing: {source}");
        Assert.True(File.Exists(FixturePath), $"output copy missing: {FixturePath}");
        Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(FixturePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(File.ReadAllBytes(source!), File.ReadAllBytes(FixturePath));
    }

    /// <summary>Ruling 94. Both parsers skip leading whitespace, so editing damage at the head of this file — 46 blank lines were
    /// prepended once, by a shell, and neither suite noticed — changes the authority two implementations read while every test
    /// stays green. Its first byte is <c>{</c>: no BOM, no blank lines, nothing before the object.</summary>
    [Fact]
    public void Fixture_starts_with_its_opening_brace()
    {
        var bytes = File.ReadAllBytes(FixturePath);
        Assert.Equal((byte)'{', Assert.Single(bytes.Take(1)));
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
        const int minGlobalCardCases = 6;
        var fixture = Fixture();

        Assert.True(fixture.GetProperty("listing").GetArrayLength() >= minListingCases, $"listing cases: {fixture.GetProperty("listing").GetArrayLength()}");
        Assert.True(fixture.GetProperty("anchors").GetArrayLength() >= minAnchorCases, $"anchor cases: {fixture.GetProperty("anchors").GetArrayLength()}");
        Assert.True(ListingCases().Count() >= minListingCases);
        Assert.True(AnchorCases().Count() >= minAnchorCases);
        Assert.True(fixture.GetProperty("globalCard").GetArrayLength() >= minGlobalCardCases, $"global card cases: {fixture.GetProperty("globalCard").GetArrayLength()}");
        Assert.True(GlobalCardCases().Count() >= minGlobalCardCases);
    }

    [Fact]
    public void Fixture_covers_rejected_line_forms()
    {
        var anchors = Fixture().GetProperty("anchors").EnumerateArray().ToList();
        Assert.True(anchors.Count(a => a.GetProperty("task").ValueKind == JsonValueKind.Null) >= 15);
        Assert.Contains(anchors, a => a.GetProperty("anchor").GetString() == "sql:T04:2147483648");
    }

    public static IEnumerable<object[]> GlobalCardCases() =>
        Fixture().GetProperty("globalCard").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

    /// <summary>Ruling 80. The GLOBAL pre/post card is numbered by a different path than a task listing: the screen renders
    /// <c>codeBlock(joinStatements(list))</c>, so the card is ONE block whose line N is the Nth line of the statements joined by a
    /// GO line. <see cref="SqlValidator.FindBareCarriageReturns"/> reports a bare carriage return at a line of THAT card and a
    /// reviewer reads the number off the screen, so the two must agree. Here the card is modelled by <see cref="TaskListing.Build"/>
    /// of the joined text (same rule: CRLF normalised, split on \n, a lone CR never a break); js/sql-view.test.cjs asserts the SAME
    /// fixture over the shipped <c>joinStatements</c> and <c>cardLines</c>. "crLines" are the card lines whose text holds a lone CR
    /// and "reported" the lines the engine reports; they differ only in the swallowed-CR case the fixture names.</summary>
    [Theory]
    [MemberData(nameof(GlobalCardCases))]
    public void Global_card_numbering_matches_the_shared_fixture(string name)
    {
        var item = Fixture().GetProperty("globalCard").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var statements = item.GetProperty("statements").EnumerateArray().Select(s => s.GetString()!).ToList();
        var crLines = item.GetProperty("crLines").EnumerateArray().Select(n => n.GetInt32()).ToList();
        var reported = item.GetProperty("reported").EnumerateArray().Select(n => n.GetInt32()).ToList();

        var card = TaskListing.Build(new TaskPlan { SourceQuery = string.Join("\nGO\n", statements) });

        Assert.Equal(item.GetProperty("lineCount").GetInt32(), card.Count);
        Assert.Equal(crLines, card.Where(l => l.Text.Contains('\r', StringComparison.Ordinal)).Select(l => l.No).ToList());
        // The pre card and the post card are numbered independently, each from its own first line.
        Assert.Equal(reported, ReportedLines(new SqlPlanPayload { PreSql = statements }, "preSql"));
        Assert.Equal(reported, ReportedLines(new SqlPlanPayload { PostSql = statements }, "postSql"));
    }

    /// <summary>The card line number of every bare carriage return the engine reports for one global card, in report order.</summary>
    private static List<int> ReportedLines(SqlPlanPayload plan, string field) =>
        SqlValidator.FindBareCarriageReturns(plan).Select(f =>
        {
            Assert.Equal("plan", f.Scope);
            Assert.StartsWith(field + "[", f.Line, StringComparison.Ordinal);
            var at = f.Line.IndexOf(SqlValidator.BareCarriageReturnMarker, StringComparison.Ordinal) + SqlValidator.BareCarriageReturnMarker.Length;
            return int.Parse(f.Line[at..f.Line.IndexOf(' ', at)], System.Globalization.CultureInfo.InvariantCulture);
        }).ToList();
}
