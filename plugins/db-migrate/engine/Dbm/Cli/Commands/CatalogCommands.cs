using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm discover [--inline]`: re-run discovery (the server runs the job; --inline runs pending jobs in this process).</summary>
public sealed class DiscoverCommand : ICommand
{
    public string Name => "discover";
    public string Help => "Extract + profile both catalogs and rebuild the vector index (--inline: run the jobs here instead of in the server)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);
        if (!services.Connections.Has(Side.Src) || !services.Connections.Has(Side.Tgt))
            return Output.Fail(ctx, "no_connections", "Save both connection strings in the UI (Setup) first.");

        if (!services.Jobs.Active().Any(j => j.Kind == "discover"))
        {
            try
            {
                services.Workflow.Rediscover();
            }
            catch (WorkflowException ex)
            {
                return Output.Fail(ctx, "workflow", ex.Message);
            }
        }

        if (args.Flag("inline"))
        {
            var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
            var job = services.Jobs.LatestFor(PhaseName.Discovery);
            if (job is { Status: JobStatus.Failed }) return Output.Fail(ctx, "job_failed", job.Error ?? "discover failed");
            return Output.Ok(ctx, new
            {
                ok = true,
                ran,
                src = services.Catalog.Get(Side.Src)?.Tables.Count ?? 0,
                tgt = services.Catalog.Get(Side.Tgt)?.Tables.Count ?? 0,
            });
        }

        var info = await ServerControl.EnsureRunningAsync(ws);
        return Output.Ok(ctx, new { ok = true, queued = true, url = info.UiUrl });
    }
}

/// <summary>`dbm search &lt;query&gt; [--side src|tgt] [--kind table|column] [-k n] [--json]`.</summary>
public sealed class SearchCommand : ICommand
{
    public string Name => "search";
    public string Help => "Vector search over both catalogs: search <query> [--side src|tgt] [--kind table|column] [-k n] [--json]";

    public Task<int> RunAsync(Args args, CliContext ctx) => Task.FromResult(Run(args, ctx));

    private static int Run(Args args, CliContext ctx)
    {
        var query = string.Join(' ', args.Positionals).Trim();
        if (query.Length == 0) return Output.Fail(ctx, "usage", "usage: dbm search <query> [--side src|tgt] [--kind table|column] [-k n] [--json]");
        if (!CatalogCli.TryParseSide(args.Opt("side"), out var side)) return Output.Fail(ctx, "usage", "--side must be src or tgt");
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);
        var rows = services.Catalog.Vectors();
        if (rows.Count == 0) return Output.Fail(ctx, "no_catalog", CatalogCli.NoCatalog);

        var hits = VectorIndex.FromRows(rows, Synonyms.ForProject(ws))
            .Search(query, side, args.Opt("kind"), Math.Clamp(args.Int("k", 8), 1, 50));
        if (args.Flag("json"))
            return Output.Ok(ctx, hits.Select(h => new
            {
                side = EnumText.ToText(h.Side), kind = h.Kind, key = h.Key, score = Math.Round(h.Score, 3), text = h.Text,
            }).ToList());
        return Output.Text(ctx, hits.Count == 0 ? "no matches" : string.Join('\n', hits.Select(CatalogText.SearchLine)));
    }
}

/// <summary>`dbm show &lt;schema.table[.column] | table | F001&gt; [--side src|tgt] [--json]`.</summary>
public sealed class ShowCommand : ICommand
{
    private static readonly Regex FindingId = new(@"^F\d{3,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Name => "show";
    public string Help => "Compact view of a table, column or finding: show <schema.table[.column]|F001> [--side src|tgt] [--json]";

    public Task<int> RunAsync(Args args, CliContext ctx) => Task.FromResult(Run(args, ctx));

    private static int Run(Args args, CliContext ctx)
    {
        if (args.Positionals.Count == 0) return Output.Fail(ctx, "usage", "usage: dbm show <schema.table[.column]|F001> [--side src|tgt] [--json]");
        var target = args.Positionals[0];
        var json = args.Flag("json");
        if (!CatalogCli.TryParseSide(args.Opt("side"), out var only)) return Output.Fail(ctx, "usage", "--side must be src or tgt");
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);

        if (FindingId.IsMatch(target)) return ShowFinding(services, target.ToUpperInvariant(), json, ctx);

        var anyCatalog = false;
        foreach (var side in only is { } s ? new[] { s } : new[] { Side.Src, Side.Tgt })
        {
            var snapshot = services.Catalog.Get(side);
            if (snapshot is null) continue;
            anyCatalog = true;
            var (table, column) = Resolve(snapshot, target);
            if (table is null) continue;
            var sideText = EnumText.ToText(side);
            if (column is null)
                return json ? Output.Ok(ctx, new { side = sideText, table }) : Output.Text(ctx, CatalogText.Table(side, table));
            if (json) return Output.Ok(ctx, new { side = sideText, table = table.Key, column });
            var lines = new List<string> { $"{sideText} column {table.Key}.{column.Name}", CatalogText.ColumnLine(table, column) };
            if (column.Profile is { } profile) lines.Add(CatalogText.ProfileLine(profile));
            return Output.Text(ctx, string.Join('\n', lines));
        }
        return anyCatalog
            ? Output.Fail(ctx, "not_found", $"No table or column '{target}' in the catalog (try `dbm search {target}`).")
            : Output.Fail(ctx, "no_catalog", CatalogCli.NoCatalog);
    }

    /// <summary>"schema.table" → table; "schema.table.column" → column; a bare name → the only table with that name.</summary>
    public static (TableInfo? Table, ColumnInfo? Column) Resolve(CatalogSnapshot snapshot, string target)
    {
        if (snapshot.FindTable(target) is { } table) return (table, null);
        var dot = target.LastIndexOf('.');
        if (dot > 0 && snapshot.FindTable(target[..dot]) is { } owner && owner.FindColumn(target[(dot + 1)..]) is { } column)
            return (owner, column);
        if (dot < 0)
        {
            var byName = snapshot.Tables.Where(t => string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return (byName[0], null);
        }
        return (null, null);
    }

    private static int ShowFinding(DbmServices services, string id, bool json, CliContext ctx)
    {
        var version = services.Phases.Get(PhaseName.Analysis).CurrentVersion;
        var artifact = (version is { } v ? services.Artifacts.Get(PhaseName.Analysis, v) : null) ?? services.Artifacts.Latest(PhaseName.Analysis);
        if (artifact is null) return Output.Fail(ctx, "no_analysis", "No analysis artifact yet.");
        var finding = JsonNode.Parse(artifact.PayloadJson)?["findings"]?[id];
        if (finding is null) return Output.Fail(ctx, "not_found", $"No finding {id} in analysis v{artifact.Version}.");
        if (json) return Output.Ok(ctx, new { id, version = artifact.Version, finding });
        var line = $"{id} {finding["severity"]} {finding["rule"]} {finding["side"]} {finding["object"]} — {finding["message"]}";
        if (finding["count"] is { } count) line += $" (n={count})";
        if (finding["commentary"] is { } commentary) line += $"\ncommentary: {commentary}";
        return Output.Text(ctx, line);
    }
}

internal static class CatalogCli
{
    public const string NoCatalog = "No catalog yet; run discovery first (`dbm discover`).";

    public static bool TryParseSide(string? text, out Side? side)
    {
        side = null;
        if (string.IsNullOrEmpty(text)) return true;
        if (string.Equals(text, "src", StringComparison.OrdinalIgnoreCase)) side = Side.Src;
        else if (string.Equals(text, "tgt", StringComparison.OrdinalIgnoreCase)) side = Side.Tgt;
        else return false;
        return true;
    }
}
