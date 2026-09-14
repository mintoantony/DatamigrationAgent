using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Core.Catalog;

public sealed record DriftResult(bool SrcChanged, bool TgtChanged)
{
    public bool Any => SrcChanged || TgtChanged;
}

/// <summary>Re-extracts structure (no profiling) and compares fingerprints with the stored catalog.</summary>
public static class DriftChecker
{
    public static async Task<DriftResult> CheckAsync(DbmServices services, CancellationToken ct)
    {
        var src = await ChangedAsync(services, Side.Src, ct);
        var tgt = await ChangedAsync(services, Side.Tgt, ct);
        return new DriftResult(src, tgt);
    }

    private static async Task<bool> ChangedAsync(DbmServices services, Side side, CancellationToken ct)
    {
        var saved = services.Catalog.Fingerprint(side);
        var connectionString = services.Connections.GetConnectionString(side);
        if (saved is null || connectionString is null) return false;   // nothing discovered yet: nothing to drift from
        var meta = services.Connections.GetMeta(side) ?? await SqlConnect.ProbeAsync(connectionString, ct);
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        var current = await CatalogExtractor.ExtractAsync(conn, meta, ct);
        return !string.Equals(Fingerprint.Compute(current), saved, StringComparison.Ordinal);
    }
}
