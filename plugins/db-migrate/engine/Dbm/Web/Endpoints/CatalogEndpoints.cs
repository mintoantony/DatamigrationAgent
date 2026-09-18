using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>Row of GET /api/catalog/{side}. Refs = distinct referenced table keys (FK targets), used for the FK graph.</summary>
public sealed record TableListItem(string Key, long Rows, double SizeMb, int Columns, bool HasPk, bool IsHeap, int FkOut, int FkIn,
    int Triggers, List<string> Refs);

public sealed record ReferencedBy(string Table, string ForeignKey, List<string> Columns);

/// <summary>Body of GET /api/catalog/{side}/table/{key}: the TableInfo (with profiles) plus derived facts the UI needs.</summary>
public sealed record TableDetail(string Side, string Key, TableInfo Table, IReadOnlyList<string>? BestKey, bool IsHeap,
    List<ReferencedBy> ReferencedBy);

public static class CatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        app.MapGet("/api/catalog/{side}", (string side) =>
        {
            if (!TryParseSide(side, out var s)) return BadSide();
            var snapshot = state.Services.Catalog.Get(s);
            return snapshot is null ? NoCatalog() : Results.Json(ListTables(snapshot), Json.Options);
        });

        app.MapGet("/api/catalog/{side}/table/{key}", (string side, string key) =>
        {
            if (!TryParseSide(side, out var s)) return BadSide();
            var snapshot = state.Services.Catalog.Get(s);
            if (snapshot is null) return NoCatalog();
            var detail = Detail(s, snapshot, key);
            return detail is null
                ? Error(404, "not_found", $"No table '{key}' in the {side} catalog.")
                : Results.Json(detail, Json.Options);
        });

        app.MapPost("/api/rediscover", async (CancellationToken ct) =>
        {
            if (!state.Services.Connections.Has(Side.Src) || !state.Services.Connections.Has(Side.Tgt))
                return Error(409, "no_connections", "Save both connection strings first.");
            try
            {
                // Ruling 185: the same rule as reopen; a failed run is cancelled (its PostSql runs) before the catalogs change.
                if (state.Services.Workflow.UpstreamLock(failedRunWillBeCancelled: true) is { } locked)
                    return Error(409, "workflow", "Discovery cannot run again: " + locked);
                await CoreEndpoints.CancelFailedRunAsync(state, "discovery was run again", ct);
                state.Services.Workflow.Rediscover();
                return Results.Json(new { ok = true }, Json.Options);
            }
            catch (WorkflowException ex)
            {
                return Error(409, "workflow", ex.Message);
            }
        });

        app.MapGet("/api/search", (string? q, string? side, string? kind, int? k) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Error(400, "bad_request", "q is required");
            Side? only = null;
            if (!string.IsNullOrEmpty(side))
            {
                if (!TryParseSide(side, out var s)) return BadSide();
                only = s;
            }
            var rows = state.Services.Catalog.Vectors();
            if (rows.Count == 0) return NoCatalog();
            var hits = VectorIndex.FromRows(rows, Synonyms.ForProject(state.Services.Ws))
                .Search(q, only, kind, Math.Clamp(k ?? 8, 1, 50))
                .Select(h => new { side = EnumText.ToText(h.Side), kind = h.Kind, key = h.Key, score = Math.Round(h.Score, 3), text = h.Text })
                .ToList();
            return Results.Json(hits, Json.Options);
        });
    }

    public static List<TableListItem> ListTables(CatalogSnapshot snapshot)
    {
        var fkIn = snapshot.Tables
            .SelectMany(t => t.ForeignKeys.Select(f => f.RefKey))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return snapshot.Tables.Select(t => new TableListItem(
            t.Key, t.Rows, t.SizeMb, t.Columns.Count, t.PrimaryKey is not null, t.IsHeap, t.ForeignKeys.Count,
            fkIn.GetValueOrDefault(t.Key), t.TriggerCount,
            t.ForeignKeys.Select(f => f.RefKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList();
    }

    public static TableDetail? Detail(Side side, CatalogSnapshot snapshot, string key)
    {
        var table = snapshot.FindTable(key);
        if (table is null) return null;
        var referencedBy = snapshot.Tables
            .SelectMany(t => t.ForeignKeys
                .Where(f => string.Equals(f.RefKey, table.Key, StringComparison.OrdinalIgnoreCase))
                .Select(f => new ReferencedBy(t.Key, f.Name, f.Columns)))
            .ToList();
        return new TableDetail(EnumText.ToText(side), table.Key, table, table.BestKey(), table.IsHeap, referencedBy);
    }

    public static bool TryParseSide(string text, out Side side)
    {
        side = Side.Src;
        if (string.Equals(text, "src", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(text, "tgt", StringComparison.OrdinalIgnoreCase)) return false;
        side = Side.Tgt;
        return true;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = code, message }, Json.Options, statusCode: status);

    private static IResult BadSide() => Error(400, "bad_request", "side must be src or tgt");

    private static IResult NoCatalog() => Error(404, "no_catalog", "No catalog yet; discovery has not completed.");
}
