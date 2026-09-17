using System.Text;
using System.Text.Json;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Core.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>
/// The execute screen's API (contract C7, T5.5). Every route answers from the one <see cref="TransferService"/> the server owns.
/// <para>An expected refusal is 409 <c>{error, message, details}</c> with the service's own machine code - never a 500, and never a
/// silent 200: the execute screen's whole job is to say why a transfer will not start.</para>
/// </summary>
public static class TransferEndpoints
{
    public sealed record PreflightBody(TransferOptions? Options);

    public sealed record StartBody(TransferOptions? Options, string? ConfirmTarget);

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(state);

        // T5.6: GET/POST /api/export/report through the T2.8 registry - no route of its own.
        ExportEndpoints.Register("report", ReportExport.BuildAsync);

        app.MapGet("/api/transfer", () => Guard(() => Task.FromResult(ApiResults.Json(Service(state).View()))));

        app.MapPost("/api/transfer/preflight", (HttpRequest req, CancellationToken ct) => Guard(async () =>
        {
            var body = await ReadAsync<PreflightBody>(req, ct);
            return ApiResults.Json(await Service(state).PreflightAsync(body?.Options ?? new TransferOptions(), ct));
        }));

        app.MapPost("/api/transfer/start", (HttpRequest req, CancellationToken ct) => Guard(async () =>
        {
            var body = await ReadAsync<StartBody>(req, ct);
            long runId = await Service(state).StartAsync(body?.Options ?? new TransferOptions(), body?.ConfirmTarget ?? "", ct);
            return ApiResults.Json(new { ok = true, runId });
        }));

        app.MapPost("/api/transfer/pause", () => Guard(() =>
        {
            Service(state).Pause();
            return Task.FromResult(ApiResults.Json(new { ok = true }));
        }));

        app.MapPost("/api/transfer/resume", () => Guard(() =>
            Task.FromResult(ApiResults.Json(new { ok = true, runId = Service(state).Resume() }))));

        app.MapPost("/api/transfer/cancel", (CancellationToken ct) => Guard(async () =>
        {
            await Service(state).CancelAsync(ct);
            return ApiResults.Json(new { ok = true });
        }));

        app.MapGet("/api/transfer/errors", (string? task, int? limit) => Guard(() =>
        {
            TransferRunRow? run;
            try
            {
                run = state.Services.Transfers.Latest();
            }
            catch (JsonException ex)
            {
                // Ruling 135. TransferRepo.MapRun deserialises options_json, so a saved run that will not parse throws here - and
                // Guard would answer 400 "Invalid JSON body" to a GET that has no body, while GET /api/transfer beside it degrades
                // with a sentence (ruling 132). The drawer must not contradict the screen it opens over: an empty list, 200, and the
                // sentence saying which saved record is at fault, so the panel cannot be read as "no rejected rows".
                return Task.FromResult(ApiResults.Json(new
                {
                    rows = Array.Empty<ErrorRowEntry>(),
                    note = "The latest transfer run could not be read: the options this workspace saved for it are not readable JSON ("
                           + TransferFailure.Describe(ex, t => t) + "). No rejected rows can be listed for it.",
                }));
            }
            IReadOnlyList<ErrorRowEntry> rows = run is null
                ? []
                : state.Services.Transfers.ErrorRows(run.Id, string.IsNullOrWhiteSpace(task) ? null : task, limit ?? 100);
            return Task.FromResult(ApiResults.Json(rows));
        }));
    }

    private static TransferService Service(WebState state)
        => state.Transfer ?? throw new InvalidOperationException("The transfer service is not initialised (see WebHost).");

    /// <summary>Null for an absent body: <c>pause</c>/<c>resume</c>/<c>cancel</c> take none, and <c>preflight</c>/<c>start</c> may be
    /// sent without one. A body that is present but not JSON is a 400 from <see cref="Guard"/>, never a silent default.</summary>
    private static async Task<T?> ReadAsync<T>(HttpRequest req, CancellationToken ct) where T : class
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        string text = await reader.ReadToEndAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : Json.Deserialize<T>(text);
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TransferException ex)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, ex.Code, ex.Message, ex.Details);
        }
        catch (WorkflowException ex)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "workflow", ex.Message, ex.Details);
        }
        catch (JsonException ex)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "bad_request", "Invalid JSON body: " + ex.Message);
        }
    }
}
