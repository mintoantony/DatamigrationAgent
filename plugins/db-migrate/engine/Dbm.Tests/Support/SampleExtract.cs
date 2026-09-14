using Dbm.Core.Catalog;
using Dbm.Core.Sql;

namespace Dbm.Tests.Support;

/// <summary>Reads catalogs from the real sample databases created by SampleDatabases (integration tests only).</summary>
public static class SampleExtract
{
    public static async Task<CatalogSnapshot> CatalogAsync(string connectionString, bool profile)
    {
        var meta = await SqlConnect.ProbeAsync(connectionString, CancellationToken.None);
        await using var conn = await SqlConnect.OpenAsync(connectionString, CancellationToken.None);
        var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, CancellationToken.None);
        return profile
            ? await Profiler.ProfileAsync(conn, snapshot, new ProfileOptions(100_000, SampleValues: true), null, CancellationToken.None)
            : snapshot;
    }
}
