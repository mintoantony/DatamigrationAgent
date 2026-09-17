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
            var run = state.Services.Transfers.Latest();
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
