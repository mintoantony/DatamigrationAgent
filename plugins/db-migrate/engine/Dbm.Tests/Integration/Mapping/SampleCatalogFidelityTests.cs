using Dbm.Core.Catalog;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Mapping;

[Trait("Category", "Integration")]
public class SampleCatalogFidelityTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Sample_catalogs_match_the_extracted_sample_databases()
    {
        AssertSameStructure(SampleCatalogs.Source(), await SampleExtract.CatalogAsync(fixture.Pair.SourceCs, profile: false));
        AssertSameStructure(SampleCatalogs.Target(), await SampleExtract.CatalogAsync(fixture.Pair.TargetCs, profile: false));
    }

    private static void AssertSameStructure(CatalogSnapshot expected, CatalogSnapshot actual)
    {
        // The sample databases use the server's default collation; compare modulo that one value.
        var normalized = actual with
        {
            Tables = actual.Tables.Select(t => t with
            {
                Columns = t.Columns.Select(c => c.Collation is not null && c.Collation == actual.Server.DatabaseCollation
                    ? c with { Collation = SampleCatalogs.Collation } : c).ToList()
            }).ToList()
        };
        var diffs = SampleCatalogs.Diff(expected, normalized);
        Assert.True(diffs.Count == 0, "SampleCatalogs differ from the real schema; fix SampleCatalogs.cs:\n" + string.Join("\n", diffs));
        Assert.Equal(Fingerprint.Compute(normalized), Fingerprint.Compute(expected));
    }
}
