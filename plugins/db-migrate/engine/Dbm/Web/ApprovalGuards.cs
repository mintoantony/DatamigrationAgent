using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>
/// Run by POST /api/phase/{phase}/approve before WorkflowEngine.Approve; the first non-null message aborts with
/// 409 {"error":"guard","message":…}. Empty in M1; T2.5 appends the drift guard.
/// </summary>
public static class ApprovalGuards
{
    public static readonly List<Func<WebState, PhaseName, CancellationToken, Task<string?>>> All = new()
    {
        DriftGuardAsync,   // T2.5
    };

    // T2.5: Analysis/Mapping/Sql approval is refused while either database's structure differs from the discovered catalog.
    // A failing check (database unreachable) does not block approval; it is logged as a warning instead.
    public static async Task<string?> DriftGuardAsync(WebState state, PhaseName phase, CancellationToken ct)
    {
        if (phase is not (PhaseName.Analysis or PhaseName.Mapping or PhaseName.Sql)) return null;
        var services = state.Services;
        DriftResult drift;
        try
        {
            drift = await DriftChecker.CheckAsync(services, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var secrets = new[] { Side.Src, Side.Tgt }
                .Select(side => services.Connections.GetConnectionString(side))
                .Where(cs => cs is not null)
                .SelectMany(cs => Redactor.SecretsOf(cs!));
            services.Sink.Publish("log", new { level = "warn", message = $"Drift check skipped: {Redactor.Scrub(ex.Message, secrets)}" });
            return null;
        }
        if (!drift.Any) return null;
        var sides = new List<string>();
        if (drift.SrcChanged) sides.Add("src");
        if (drift.TgtChanged) sides.Add("tgt");
        foreach (var side in sides) services.Sink.Publish("drift_detected", new { side });
        return $"Schema changed since discovery on {string.Join(" and ", sides)}; re-run discovery";
    }
}
