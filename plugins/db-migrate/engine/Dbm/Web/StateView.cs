using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>GET /api/state body (see C7 StateView). Later milestones fill "drift"/"transfer" through <see cref="Extenders"/>.</summary>
public static class StateView
{
    /// <summary>Each extender may replace or add top-level keys (T2.5: "drift", T5.5: "transfer").</summary>
    public static readonly List<Action<WebState, JsonObject>> Extenders = new();

    public static JsonObject Build(WebState state)
    {
        var s = state.Services;
        var project = s.Project.Get();
        var view = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["name"] = project.Name,
                ["paused"] = project.Paused,
                ["agentOnline"] = state.Presence.Online,
            },
            ["phases"] = Json.ToNode(s.Phases.All().Select(p => new
            {
                name = p.Name,
                status = p.Status,
                currentVersion = p.CurrentVersion,
                approvedVersion = p.ApprovedVersion,
            })),
            ["next"] = Json.ToNode(s.Workflow.Peek()),
            ["jobs"] = Json.ToNode(s.Jobs.Recent(20).Select(j => new
            {
                id = j.Id,
                kind = j.Kind,
                phase = j.Phase,
                status = j.Status,
                error = j.Error,
                startedAt = j.StartedAt,
                endedAt = j.EndedAt,
            })),
            ["connections"] = new JsonObject
            {
                ["src"] = Connection(s, Side.Src),
                ["tgt"] = Connection(s, Side.Tgt),
            },
            ["drift"] = new JsonObject { ["src"] = false, ["tgt"] = false },
            ["transfer"] = null,
        };
        foreach (var extend in Extenders) extend(state, view);
        return view;
    }

    private static JsonObject Connection(DbmServices s, Side side)
    {
        if (!s.Connections.Has(side)) return new JsonObject { ["saved"] = false };
        string describe;
        try
        {
            describe = Redactor.Describe(s.Connections.GetConnectionString(side)!);
        }
        catch (Exception)
        {
            describe = "saved (cannot be decrypted by this OS user — enter it again)";
        }
        var meta = s.Connections.GetMeta(side);
        return new JsonObject
        {
            ["saved"] = true,
            ["describe"] = describe,
            ["meta"] = meta is null ? null : Json.ToNode(meta),
        };
    }
}
