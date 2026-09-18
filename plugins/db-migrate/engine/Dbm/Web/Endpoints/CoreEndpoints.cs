using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Patching;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Dbm.Web.Endpoints;

/// <summary>M1 API: health, state, SSE, agent long-poll, pause/resume, connections, artifacts, feedback, review actions.</summary>
public static class CoreEndpoints
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    private sealed record ConnectionBody(string? ConnectionString);
    private sealed record FeedbackBody(string? Anchor, string? Text);
    private sealed record ApproveBody(int? Version);

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        var api = app.MapGroup("/api");
        var s = state.Services;

        api.MapGet("/health", () => ApiResults.Json(new { ok = true, pid = Environment.ProcessId }));
        api.MapGet("/state", () => ApiResults.Json(StateView.Build(state)));
        api.MapGet("/events", (HttpContext http, IHostApplicationLifetime life) => StreamEventsAsync(http, life, state));
        api.MapGet("/agent/await", (HttpContext http, IHostApplicationLifetime life, int? timeout) => AwaitAsync(http, life, state, timeout));

        api.MapPost("/pause", () =>
        {
            s.Workflow.SetPaused(true);
            return ApiResults.Ok();
        });
        api.MapPost("/resume", () =>
        {
            s.Workflow.SetPaused(false);
            return ApiResults.Ok();
        });

        api.MapPost("/connections/{side}/test", async (string side, HttpContext http) =>
        {
            ParseSide(side);
            var cs = await ReadConnectionStringAsync(http);
            try
            {
                var meta = await ProbeAsync(cs, http.RequestAborted);
                return ApiResults.Json(new { ok = true, meta });
            }
            catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
            {
                return ApiResults.Json(new { ok = false, error = Redactor.Scrub(ex.Message, Redactor.SecretsOf(cs)) });
            }
        });

        api.MapPost("/connections/{side}", async (string side, HttpContext http) =>
        {
            var which = ParseSide(side);
            // Ruling 185: the same rule as reopen - before the first run, or after a completed, cancelled or failed one.
            if (s.Workflow.UpstreamLock(failedRunWillBeCancelled: true) is { } locked)
                return ApiResults.Error(StatusCodes.Status409Conflict, "locked", "The connections cannot change: " + locked);
            var cs = await ReadConnectionStringAsync(http);
            ServerMeta meta;
            try
            {
                meta = await ProbeAsync(cs, http.RequestAborted);
            }
            catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "connection_failed", Redactor.Scrub(ex.Message, Redactor.SecretsOf(cs)));
            }
            // Before the save: a failed run's PostSql has to run against the target it loaded into, not the new one.
            await CancelFailedRunAsync(state, $"the {(which == Side.Src ? "source" : "target")} connection was changed", http.RequestAborted);
            s.Connections.Save(which, cs, meta);
            s.Sink.Publish("state_changed", new { phase = PhaseName.Setup, status = s.Phases.Get(PhaseName.Setup).Status });
            s.Workflow.OnConnectionsSaved();
            return ApiResults.Json(new { ok = true, meta });
        });

        api.MapGet("/artifact/{phase}", (string phase) =>
        {
            var p = ParsePhase(phase);
            var row = s.Phases.Get(p);
            var current = row.CurrentVersion is int v ? s.Artifacts.Get(p, v) : null;
            return ApiResults.Json(new { phase = p, versions = s.Artifacts.List(p), current = current is null ? null : View(current) });
        });

        api.MapGet("/artifact/{phase}/{version:int}", (string phase, int version) =>
        {
            var a = s.Artifacts.Get(ParsePhase(phase), version)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", $"{phase} v{version} does not exist.");
            return ApiResults.Json(View(a));
        });

        api.MapGet("/feedback/{phase}", (string phase) => ApiResults.Json(s.Feedback.List(ParsePhase(phase))));

        api.MapPost("/feedback/{phase}", async (string phase, HttpContext http) =>
        {
            var p = ParsePhase(phase);
            var body = await ApiResults.ReadAsync<FeedbackBody>(http.Request);
            var text = body.Text?.Trim();
            if (string.IsNullOrEmpty(text)) throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "Feedback text is required.");
            if (text.Length > 4000) throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "Feedback text is limited to 4000 characters.");
            var version = s.Phases.Get(p).CurrentVersion
                          ?? throw new ApiException(StatusCodes.Status409Conflict, "no_artifact", $"{phase} has no version to comment on yet.");
            var anchor = string.IsNullOrWhiteSpace(body.Anchor) ? null : body.Anchor.Trim();
            var row = s.Feedback.Add(p, version, anchor, text);
            s.Sink.Publish("feedback_changed", new { phase = p });
            return ApiResults.Json(row);
        });

        api.MapDelete("/feedback/{id:long}", (long id) =>
        {
            var row = s.Feedback.Get(id) ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", $"Feedback {id} does not exist.");
            if (!s.Feedback.DeleteDraft(id))
                return ApiResults.Error(StatusCodes.Status409Conflict, "not_draft", "Only draft feedback can be deleted.");
            s.Sink.Publish("feedback_changed", new { phase = row.Phase });
            return ApiResults.Ok();
        });

        api.MapPost("/phase/{phase}/request-changes", (string phase) =>
        {
            s.Workflow.RequestChanges(ParsePhase(phase));
            return ApiResults.Ok();
        });

        // Ruling 194 (open item 1): the body names the version the reviewer saw, {"version": n}. None is 400 version_required; one
        // that is no longer current is 409 stale_version (before the guards, so a stale screen is told to reload, not about drift).
        api.MapPost("/phase/{phase}/approve", async (string phase, HttpContext http) =>
        {
            var p = ParsePhase(phase);
            var seen = await ReadApproveVersionAsync(http);
            try
            {
                if (s.Phases.Get(p).Status == PhaseStatus.AwaitingReview) s.Workflow.EnsureCurrentVersion(p, seen);
                foreach (var guard in ApprovalGuards.All)
                {
                    var message = await guard(state, p, http.RequestAborted);
                    if (message is not null) return ApiResults.Error(StatusCodes.Status409Conflict, "guard", message);
                }
                s.Workflow.Approve(p, seen);
                return ApiResults.Ok();
            }
            catch (WorkflowException ex)
            {
                return ApiResults.Error(StatusCodes.Status409Conflict, ex.Code ?? "blocked", ex.Message, ex.Details);
            }
        });

        // Ruling 185: allowed before the first run and after a completed, cancelled or failed one. A failed run is cancelled first -
        // through Cancel's own door, so the plan's PostSql restores what its PreSql disabled - and only once the reopen itself is known
        // to be allowed, so a refused reopen never costs the operator their failed run.
        api.MapPost("/phase/{phase}/reopen", async (string phase, CancellationToken ct) =>
        {
            var p = ParsePhase(phase);
            if (s.Workflow.WhyNotReopen(p, failedRunWillBeCancelled: true) is { } why) throw new WorkflowException(why);
            await CancelFailedRunAsync(state, $"{p.Text()} was reopened", ct);
            s.Workflow.Reopen(p);
            return ApiResults.Ok();
        });

        api.MapPost("/phase/{phase}/retry", (string phase) =>
        {
            s.Workflow.RetryJob(ParsePhase(phase));
            return ApiResults.Ok();
        });

        api.MapPost("/edit/{phase}", async (string phase, HttpContext http) =>
        {
            var p = ParsePhase(phase);
            Patch patch;
            try
            {
                patch = Patch.Parse(await ApiResults.ReadTextAsync(http.Request));
            }
            catch (PatchException ex)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_patch", ex.Message);
            }
            if (!Phases.TryParse(patch.Phase, out var patchPhase) || patchPhase != p)
                return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_patch", $"patch.phase must be '{p.Text()}'.");
            return ApiResults.Json(s.Workflow.HumanEdit(patch));
        });

        api.MapPost("/shutdown", (HttpContext http, IHostApplicationLifetime life) =>
        {
            http.Response.OnCompleted(() =>
            {
                life.StopApplication();
                return Task.CompletedTask;
            });
            return ApiResults.Ok();
        });
    }

    // ------------------------------------------------------------------ SSE

    private static async Task StreamEventsAsync(HttpContext http, IHostApplicationLifetime life, WebState state)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, life.ApplicationStopping);
        var ct = linked.Token;
        using var subscription = state.Broadcaster.Subscribe();
        long sent = 0;
        try
        {
            await WriteAsync(response, "retry: 2000\n\n", ct);

            var lastEventId = http.Request.Headers["Last-Event-ID"].ToString();
            if (string.IsNullOrEmpty(lastEventId)) lastEventId = http.Request.Query["lastEventId"].ToString();
            if (long.TryParse(lastEventId, out var last))
            {
                sent = last;
                foreach (var e in state.Services.Events.Since(last, 1000))
                {
                    await WriteEventAsync(response, new SseMessage(e.Id, e.Type, e.PayloadJson), ct);
                    sent = e.Id;
                }
            }

            while (!ct.IsCancellationRequested)
            {
                using var ping = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ping.CancelAfter(PingInterval);
                bool more;
                try
                {
                    more = await subscription.Reader.WaitToReadAsync(ping.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, ": ping\n\n", ct);
                    continue;
                }
                if (!more) break;
                while (subscription.Reader.TryRead(out var message))
                {
                    if (message.Id is long id)
                    {
                        if (id <= sent) continue;   // already sent as backlog
                        sent = id;
                    }
                    await WriteEventAsync(response, message, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client went away or server stopping
        }
        catch (IOException)
        {
            // connection reset
        }
    }

    private static Task WriteEventAsync(HttpResponse response, SseMessage m, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (m.Id is long id) sb.Append("id: ").Append(id).Append('\n');
        sb.Append("event: ").Append(m.Type).Append('\n');
        sb.Append("data: ").Append(m.Data.Replace("\n", " ")).Append("\n\n");
        return WriteAsync(response, sb.ToString(), ct);
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.WriteAsync(text, ct);
        await response.Body.FlushAsync(ct);
    }

    // ------------------------------------------------------------------ agent long-poll

    private static async Task<IResult> AwaitAsync(HttpContext http, IHostApplicationLifetime life, WebState state, int? timeout)
    {
        using var presence = state.Presence.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, life.ApplicationStopping);
        DateTimeOffset? deadline = timeout is > 0 ? DateTimeOffset.UtcNow.AddSeconds(timeout.Value) : null;
        while (true)
        {
            var signal = state.Broadcaster.NextSignal();   // taken before Next() so no change can be missed
            var action = state.Services.Workflow.Next();
            if (action.Action != "await") return ApiResults.Json(action);
            if (deadline is { } d && DateTimeOffset.UtcNow >= d) return ApiResults.Json(action);
            if (life.ApplicationStopping.IsCancellationRequested)
                return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "stopping", "The server is stopping.");
            if (http.RequestAborted.IsCancellationRequested) return Results.Empty;
            await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(1), linked.Token));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static object View(ArtifactRow a) =>
        new { version = a.Version, author = a.Author, summary = a.Summary, createdAt = a.CreatedAt, payload = JsonNode.Parse(a.PayloadJson) };

    /// <summary>Ruling 185: a failed latest run is cancelled (PostSql and all) before an upstream change; a no-op otherwise. A refusal
    /// keeps its own code as a 409.</summary>
    internal static async Task CancelFailedRunAsync(WebState state, string why, CancellationToken ct)
    {
        if (state.Transfer is not { } transfer) return;
        try
        {
            await transfer.CancelFailedRunAsync(why, ct);
        }
        catch (Dbm.Core.Transfer.TransferException ex)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }
    }

    /// <summary>Ruling 194: the version an approve names; a missing body, an unreadable one or one without a version is 400
    /// version_required.</summary>
    private static async Task<int> ReadApproveVersionAsync(HttpContext http)
    {
        const string need = "Approve names the version you reviewed: send {\"version\": n}. Reload the page and approve again.";
        var text = await ApiResults.ReadTextAsync(http.Request);
        ApproveBody? body = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                body = Json.Deserialize<ApproveBody>(text);
            }
            catch (System.Text.Json.JsonException)
            {
                body = null;
            }
        }
        return body?.Version ?? throw new ApiException(StatusCodes.Status400BadRequest, "version_required", need);
    }

    private static PhaseName ParsePhase(string text) =>
        Phases.TryParse(text, out var p) ? p : throw new ApiException(StatusCodes.Status404NotFound, "unknown_phase", $"Unknown phase '{text}'.");

    private static Side ParseSide(string text) =>
        EnumText.TryParse<Side>(text, out var side) ? side : throw new ApiException(StatusCodes.Status404NotFound, "unknown_side", "Side must be 'src' or 'tgt'.");

    private static async Task<string> ReadConnectionStringAsync(HttpContext http)
    {
        var body = await ApiResults.ReadAsync<ConnectionBody>(http.Request);
        var cs = body.ConnectionString?.Trim();
        return string.IsNullOrEmpty(cs)
            ? throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "connectionString is required.")
            : cs;
    }

    private static async Task<ServerMeta> ProbeAsync(string connectionString, CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            return await SqlConnect.ProbeAsync(connectionString, cts.Token);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            throw new TimeoutException($"The connection test timed out after {ProbeTimeout.TotalSeconds:0} s.");
        }
    }
}
