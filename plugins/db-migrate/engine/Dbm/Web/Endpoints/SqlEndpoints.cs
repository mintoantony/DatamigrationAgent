using System.Text;
using System.Text.Json;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>POST /api/sql/validate {task?} → live ValidationReport for the current sql version (nothing is stored):
/// <c>{version, ok, taskErrors, taskWarnings, globalErrors, globalWarnings}</c>. <c>globalWarnings</c> is always present (T4.3):
/// <c>[]</c> means the global statements were checked and are clean; a single-task run carries
/// <see cref="SqlValidator.SingleTaskGlobalWarning"/>. 409 <c>no_version</c> / <c>not_ready</c>; 400 <c>bad_request</c>.</summary>
public static class SqlEndpoints
{
    public sealed record ValidateRequest(string? Task);

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
            if (!SqlPlanSource.CanValidate(s))
                return NotReady("Both connections and the target catalog are needed to validate.");

            var plan = Json.Deserialize<SqlPlanPayload>(row.PayloadJson);
            ValidationReport report;
            try
            {
                report = await SqlPlanSource.ValidateLiveAsync(s, plan, string.IsNullOrWhiteSpace(body?.Task) ? null : body!.Task, ct);
            }
            catch (InvalidOperationException ex)   // a connection or the catalog disappeared after CanValidate
            {
                return NotReady(ex.Message);
            }
            return ApiResults.Json(new
            {
                version = row.Version,
                ok = report.Ok,
                taskErrors = report.TaskErrors,
                taskWarnings = report.TaskWarnings,
                globalErrors = report.GlobalErrors,
                globalWarnings = report.GlobalWarnings,   // never defaulted to []: [] claims "checked"; a null is omitted and the UI shows it as unreported
            });
        });
    }

    private static IResult NotReady(string message) => ApiResults.Error(StatusCodes.Status409Conflict, "not_ready", message);
}
