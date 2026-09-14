using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Mapping;

[Trait("Category", "Integration")]
public class AutoMapperBaselineIntegrationTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Baseline_on_the_profiled_sample_pair()
    {
        var src = await SampleExtract.CatalogAsync(fixture.Pair.SourceCs, profile: true);
        var tgt = await SampleExtract.CatalogAsync(fixture.Pair.TargetCs, profile: true);

        var m = AutoMapper.Map(src, tgt, Synonyms.Default(), new MatchOptions());

        var misses = AutoMapperBaseline.Misses(m);
        Assert.True(misses.Count == 0, "baseline misses:\n" + string.Join("\n", misses));
        Assert.Equal(MapMethod.Vector, m.Drops["dbo.TMP_IMPORT"].Method);
        Assert.Empty(MappingValidator.Errors(m, src, tgt));
        Assert.Empty(MappingValidator.Errors(SampleMappings.Approved(), src, tgt));
        Assert.Empty(MappingValidator.Blockers(SampleMappings.Approved(), src, tgt));
    }
}
