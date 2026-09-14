using Dbm.Core.Jobs;
using Dbm.Core.Matching;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Catalog;

/// <summary>Job "discover": extract + profile + fingerprint both catalogs, then build the vector index.</summary>
public sealed class DiscoverJob : IJobHandler
{
    public string Kind => "discover";

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var services = ctx.Services;
        var settings = services.Project.GetSettings();
        var options = new ProfileOptions(settings.ProfileSampleRows, settings.SampleValues);
        var snapshots = new Dictionary<Side, CatalogSnapshot>();

        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            var label = EnumText.ToText(side);
            var connectionString = services.Connections.GetConnectionString(side)
                ?? throw new InvalidOperationException($"No {label} connection saved; complete Setup first.");
            try
            {
                ctx.Log($"{label}: connecting to {Redactor.Describe(connectionString)}");
                var meta = await SqlConnect.ProbeAsync(connectionString, ct);
                await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
                var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, ct);
                ctx.Log($"{label}: {snapshot.Tables.Count} tables, {snapshot.Tables.Sum(t => t.Columns.Count)} columns extracted");
                snapshot = await Profiler.ProfileAsync(conn, snapshot, options, message => ctx.Log($"{label}: {message}"), ct);
                services.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
                snapshots[side] = snapshot;
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException($"{label}: {Redactor.Scrub(ex.Message, Redactor.SecretsOf(connectionString))}");
            }
        }

        var (rows, _) = VectorIndex.Build(snapshots[Side.Src], snapshots[Side.Tgt], Synonyms.ForProject(services.Ws));
        services.Catalog.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Src));
        services.Catalog.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));
        ctx.Log($"vector index: {rows.Count} entries");

        return new JobResult(null, $"src: {snapshots[Side.Src].Tables.Count} tables, tgt: {snapshots[Side.Tgt].Tables.Count} tables");
    }
}
