using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Transfer;

namespace Dbm.Web.Endpoints;

/// <summary>The "report" export (T2.8 registry; registered by TransferEndpoints.Map): the Final report screen as one offline HTML file.</summary>
public static class ReportExport
{
    public const string FileName = "final-report.html";

    /// <summary>
    /// Payload of the latest Complete artifact (the FinalReport JSON), or null before the first completed run.
    /// <para>Ruling 117: only a <b>stored</b> Complete artifact counts. Nothing here builds a report on demand, so a run that failed,
    /// was cancelled or is still paused has no payload - and the screen that asks for one is told so rather than shown an empty report.</para>
    /// </summary>
    public static JsonNode? LatestPayload(DbmServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Artifacts.Latest(PhaseName.Complete) is { } artifact ? JsonNode.Parse(artifact.PayloadJson) : null;
    }

    /// <summary>
    /// ExportEndpoints.Builder. Payload = the envelope app.js export mode reads:
    /// {project, exportedAt, artifact:{version, author, summary, createdAt, payload}}. ExportException (→ 404 export_unavailable)
    /// before completion, and equally when the stored report itself will not parse - with the sentence saying which of the two it is,
    /// because "no report yet" and "the saved report cannot be read" are not the same news.
    /// </summary>
    /// <remarks>
    /// What is exported is the Complete artifact's payload: the error <b>samples</b> the engine chose, keyed, and never the rejected
    /// rows themselves. <c>GET /api/transfer/errors</c> hands out whole rows (customer data) behind the token guard for the drawer;
    /// this file leaves the server and must not carry them.
    /// </remarks>
    public static Task<ExportFile> BuildAsync(WebState state, IWebAssets assets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var s = state.Services;
        var row = s.Artifacts.Latest(PhaseName.Complete)
                  ?? throw new ExportException("No transfer run has completed yet, so there is no final report to export.");
        var project = s.Project.Get().Name;
        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(row.PayloadJson);
        }
        catch (JsonException ex)
        {
            throw new ExportException($"The saved final report (Complete v{row.Version}) could not be read: "
                                      + TransferFailure.Describe(ex, t => t));
        }
        var data = new JsonObject
        {
            ["project"] = project,
            ["exportedAt"] = Clock.NowText(),
            ["artifact"] = new JsonObject
            {
                ["version"] = row.Version,
                ["author"] = row.Author,
                ["summary"] = row.Summary,
                ["createdAt"] = row.CreatedAt.ToString("O"),
                ["payload"] = payload,
            },
        };
        var html = WebExport.BuildHtml(assets, "report", $"{project} — Final report", data);
        return Task.FromResult(new ExportFile(FileName, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html)));
    }
}
