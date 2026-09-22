using System.Text;
using System.Text.Json;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>POST /api/sql/validate {task?, version?} → live ValidationReport for the current sql version, plus the offline bare-CR
/// scan (without connections: that scan alone, globalWarnings = the "live validation skipped" line):
/// <c>{version, ok, taskErrors, taskWarnings, globalErrors, globalWarnings, stored, storedVersion?, storeNote?}</c>. With
/// <c>version</c> (the one on screen; 409 <c>stale_version</c> unless current), a whole-plan pass over a version without validation
/// evidence stores the evidence as a new version (Rulings 210/211) - only while Sql awaits review. <c>globalWarnings</c> is always present (T4.3):
/// <c>[]</c> means the global statements were checked and are clean; a single-task run carries
/// <see cref="SqlValidator.SingleTaskGlobalWarning"/>. 409 <c>no_version</c> / <c>not_ready</c>; 400 <c>bad_request</c>.</summary>
public static class SqlEndpoints
{
    public sealed record ValidateRequest(string? Task, int? Version);

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(state);
        app.MapPost("/api/sql/validate", async (HttpContext http, CancellationToken ct) =>
        {
            ValidateRequest? body = null;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            {
                var text = await reader.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { body = Json.Deserialize<ValidateRequest>(text); }
                    catch (JsonException ex) { return ApiResults.Error(StatusCodes.Status400BadRequest, "bad_request", $"Invalid JSON body: {ex.Message}"); }
                }
            }

            var s = state.Services;
            var row = SqlPlanSource.CurrentRow(s);
            if (row is null) return ApiResults.Error(StatusCodes.Status409Conflict, "no_version", "The sql phase has no version yet.");
            // Ruling 211: the screen names the version it shows; a stale one is refused before anything runs.
            if (body?.Version is int seen)
            {
                try { s.Workflow.EnsureCurrentVersion(PhaseName.Sql, seen); }
                catch (WorkflowException ex) { return ApiResults.Error(StatusCodes.Status409Conflict, ex.Code ?? "stale_version", ex.Message); }
            }

            var task = string.IsNullOrWhiteSpace(body?.Task) ? null : body!.Task;
            var plan = Json.Deserialize<SqlPlanPayload>(row.PayloadJson);
            var skipped = SqlPlanSource.SkippedWarning(s);
            ValidationReport? live = null;
            if (skipped is null)
            {
                try
                {
                    live = await SqlPlanSource.ValidateLiveAsync(s, plan, task, ct);
                }
                catch (InvalidOperationException ex)   // a connection or the catalog disappeared after the check
                {
                    return NotReady(ex.Message);
                }
            }
            // Review L2 (item 17's rule on this door): the offline bare-CR scan joins the report, and answers without connections.
            var report = SqlValidator.WithBareCarriageReturns(live, plan, task, skipped ?? "");
            if (report is null) return NotReady("Both connections and the target catalog are needed to validate.");
            // Rulings 210/211: a whole-plan pass over a version that lacks evidence records it - only while Sql awaits review.
            var store = live is null || body?.Version is not int shown ? new SqlPlanSource.StoreOutcome(false, null, null)
                : SqlPlanSource.StoreEvidence(s, shown, report, task);
            return ApiResults.Json(new
            {
                version = row.Version,
                ok = report.Ok,
                taskErrors = report.TaskErrors,
                taskWarnings = report.TaskWarnings,
                globalErrors = report.GlobalErrors,
                globalWarnings = report.GlobalWarnings,   // never defaulted to []: [] claims "checked"; a null is omitted and the UI shows it as unreported
                stored = store.Stored,
                storedVersion = store.Version,
                storeNote = store.Note,
            });
        });
    }

    private static IResult NotReady(string message) => ApiResults.Error(StatusCodes.Status409Conflict, "not_ready", message);
}
