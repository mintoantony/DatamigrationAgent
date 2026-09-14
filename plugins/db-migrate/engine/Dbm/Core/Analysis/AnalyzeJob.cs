using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Analysis;

/// <summary>Job "analyze": counts orphans behind untrusted/disabled source FKs, runs the rules, returns the v0 draft.</summary>
public sealed class AnalyzeJob : IJobHandler
{
    public const int OrphanQueryTimeoutSeconds = 60;

    public string Kind => "analyze";

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var services = ctx.Services;
        var src = services.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("Source catalog missing; run discovery first.");
        var tgt = services.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("Target catalog missing; run discovery first.");
        var orphans = await CountOrphansAsync(services.Connections.GetConnectionString(Side.Src), src, ctx.Log, ct);
        var payload = Analyzer.Analyze(src, tgt, orphans);
        var summary = Analyzer.Summary(payload);
        ctx.Log($"analysis: {summary}");
        return new JobResult(Json.ToNode(payload), summary);
    }

    /// <summary>Counts child rows without a parent for every untrusted/disabled FK; a failing query is logged and skipped.</summary>
    public static async Task<Dictionary<string, long>> CountOrphansAsync(string? sourceConnectionString, CatalogSnapshot src,
        Action<string> log, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var candidates = src.Tables
            .SelectMany(t => t.ForeignKeys.Where(f => f.IsNotTrusted || f.IsDisabled).Select(f => (Table: t, Fk: f)))
            .ToList();
        if (candidates.Count == 0 || sourceConnectionString is null) return counts;

        SqlConnection conn;
        try
        {
            conn = await SqlConnect.OpenAsync(sourceConnectionString, ct);
        }
        catch (SqlException ex)
        {
            log($"orphan checks skipped: {Redactor.Scrub(ex.Message, Redactor.SecretsOf(sourceConnectionString))}");
            return counts;
        }

        await using (conn)
        {
            foreach (var (table, fk) in candidates)
            {
                try
                {
                    await using var cmd = new SqlCommand(OrphanSql(table, fk), conn) { CommandTimeout = OrphanQueryTimeoutSeconds };
                    var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                    counts[RuleContext.OrphanKey(table.Key, fk.Name)] = count;
                    log($"orphan check {table.Key} {fk.Name}: {count}");
                }
                catch (SqlException ex)
                {
                    log($"orphan check skipped for {table.Key} {fk.Name}: {ex.Message}");
                }
            }
        }
        return counts;
    }

    /// <summary>SELECT COUNT_BIG(*) of child rows whose FK columns are all non-NULL and have no matching parent row.</summary>
    public static string OrphanSql(TableInfo child, ForeignKeyInfo fk)
    {
        var notNull = string.Join(" AND ", fk.Columns.Select(c => $"c.{ProfilerSql.Quote(c)} IS NOT NULL"));
        var join = string.Join(" AND ", fk.Columns.Select((c, i) => $"p.{ProfilerSql.Quote(fk.RefColumns[i])} = c.{ProfilerSql.Quote(c)}"));
        return $"SELECT COUNT_BIG(*) FROM {ProfilerSql.Quote(child.Schema)}.{ProfilerSql.Quote(child.Name)} AS c " +
               $"WHERE {notNull} AND NOT EXISTS (SELECT 1 FROM {ProfilerSql.Quote(fk.RefSchema)}.{ProfilerSql.Quote(fk.RefTable)} AS p WHERE {join});";
    }
}
