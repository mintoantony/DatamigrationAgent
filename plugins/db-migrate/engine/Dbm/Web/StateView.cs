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
        var projectNode = new JsonObject
        {
            ["name"] = project.Name,
            ["paused"] = project.Paused,
            ["agentOnline"] = state.Presence.Online,
            ["sampleValues"] = s.Project.GetSettings().SampleValues,   // Ruling 196: the Setup screen's switch
        };
        // Ruling 197: when the CLI-touch window lapses nothing is published; the UI refreshes once at this instant instead.
        if (state.Presence.SeenUntil is { } seenUntil) projectNode["agentSeenUntil"] = seenUntil.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var view = new JsonObject
        {
            ["project"] = projectNode,
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
            ["transfer"] = Transfer(s),
        };
        foreach (var extend in Extenders) extend(state, view);
        return view;
    }

    /// <summary>
    /// Ruling 185: what the review and setup screens need to offer Reopen, Re-run discovery and Edit after a run - the latest run's id
    /// and status, and <c>changesLocked</c>, the sentence that says why the plan cannot change now (absent when it can). The screens
    /// route a failed run through the server, which cancels it first, so a failed run does not lock them.
    /// </summary>
    private static JsonObject Transfer(DbmServices s)
    {
        var run = s.Db.Query("SELECT id, status FROM transfer_run ORDER BY id DESC LIMIT 1",
            r => (Id: r.GetInt64(0), Status: r.GetString(1))).Select(r => ((long, string)?)r).FirstOrDefault();
        var o = new JsonObject();
        if (run is { } latest)
        {
            o["runId"] = latest.Item1;
            o["runStatus"] = latest.Item2;
        }
        if (s.Workflow.UpstreamLock(failedRunWillBeCancelled: true) is { } locked) o["changesLocked"] = locked;
        return o;
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
