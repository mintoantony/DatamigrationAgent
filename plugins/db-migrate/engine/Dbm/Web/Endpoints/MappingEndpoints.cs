using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;

namespace Dbm.Web.Endpoints;

/// <summary>GET /api/mapping/context[?version=n] — catalog column lists plus validator results for the mapping screen.
/// Human edits go through the core POST /api/edit/mapping endpoint.</summary>
public static class MappingEndpoints
{
    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        app.MapGet("/api/mapping/context", (int? version) =>
            Results.Content(BuildContext(state.Services, version).ToJsonString(), "application/json"));
    }

    public static JsonObject BuildContext(DbmServices services, int? version = null)
    {
        var src = services.Catalog.Get(Side.Src);
        var tgt = services.Catalog.Get(Side.Tgt);
        var settings = services.Project.GetSettings();
        var options = new MatchOptions(settings.AutoAcceptScore, settings.CandidateScore);
        var v = version ?? services.Phases.Get(PhaseName.Mapping).CurrentVersion;
        var artifact = v is int n ? services.Artifacts.Get(PhaseName.Mapping, n) : null;

        var result = new JsonObject
        {
            ["version"] = artifact?.Version,
            ["autoAccept"] = options.AutoAccept,
            ["candidate"] = options.Candidate,
            ["source"] = new JsonArray((src?.Tables ?? []).Select(t => (JsonNode)new JsonObject
            {
                ["key"] = t.Key,
                ["rows"] = t.Rows,
                ["columns"] = new JsonArray(t.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name,
                    ["type"] = c.TypeDisplay,
                    ["nullable"] = c.IsNullable
                }).ToArray())
            }).ToArray()),
            ["target"] = new JsonArray((tgt?.Tables ?? []).Select(t => (JsonNode)new JsonObject
            {
                ["key"] = t.Key,
                ["columns"] = new JsonArray(t.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name,
                    ["type"] = c.TypeDisplay,
                    ["nullable"] = c.IsNullable,
                    ["identity"] = c.IsIdentity,
                    ["computed"] = c.IsComputed,
                    ["rowversion"] = c.IsRowVersion,
                    ["hasDefault"] = c.DefaultDefinition is not null
                }).ToArray())
            }).ToArray()),
            ["blockers"] = new JsonArray(),
            ["attention"] = new JsonArray(),
            ["uncovered"] = new JsonArray()
        };
        if (artifact is null || src is null || tgt is null) return result;

        var m = Json.Deserialize<MappingPayload>(artifact.PayloadJson);
        result["blockers"] = Strings(MappingValidator.Blockers(m, src, tgt));
        result["attention"] = Strings(MappingValidator.Attention(m, options));
        result["uncovered"] = Strings(MappingValidator.UncoveredSourceColumns(m, src));
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
}
