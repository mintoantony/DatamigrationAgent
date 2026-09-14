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
        if (saved is null) return false;   // nothing discovered yet: nothing to drift from
        // A fingerprint WAS saved, so there is something to compare against; a missing connection string means the
        // comparison cannot be made, not that it came out clean. Throw so the caller's existing "couldn't verify"
        // policy applies (DriftGuardAsync warns and allows) instead of silently reporting "no drift".
        var connectionString = services.Connections.GetConnectionString(side)
            ?? throw new InvalidOperationException($"{EnumText.ToText(side)}: no connection string saved; cannot verify against the saved fingerprint.");
        var meta = services.Connections.GetMeta(side) ?? await SqlConnect.ProbeAsync(connectionString, ct);
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        var current = await CatalogExtractor.ExtractAsync(conn, meta, ct);
        return !string.Equals(Fingerprint.Compute(current), saved, StringComparison.Ordinal);
    }
}
