using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Transfer;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>
/// All transfer commands go through the running server, because the transfer lives in the server process: it is one background run
/// guarded by one <see cref="TransferService"/>, and a second process reaching past it would be a second runner.
/// </summary>
internal static class TransferApi
{
    public sealed record Reply(bool Ok, JsonNode? Body, string Code, string Message);

    public static async Task<Reply> SendAsync(CliContext ctx, HttpMethod method, string path, object? body)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var ws = ctx.RequireProject();   // CliFailure("no_project") outside a project
        var info = await ServerControl.EnsureRunningAsync(ws);
        using var http = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TokenGuard.Header, info.Token);
        if (body is not null) req.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch (JsonException) { node = null; }
        if (res.IsSuccessStatusCode) return new Reply(true, node, "", "");
        // A failure that carries no machine code still says what happened: "http_409" and the reason phrase beat an empty string,
        // which would end as {"error":"","message":""} - a failure the operator cannot act on or report.
        string code = Text(node, "error") ?? $"http_{(int)res.StatusCode}";
        string message = Text(node, "message") ?? (res.ReasonPhrase ?? "Request failed.");
        var details = node?["details"]?.AsArray().Select(d => Value(d)).OfType<string>().ToList() ?? [];
        return new Reply(false, node, code, details.Count == 0 ? message : message + " " + string.Join(" | ", details));
    }

    public static int Print(CliContext ctx, Reply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply.Ok ? Output.Ok(ctx, reply.Body ?? new JsonObject { ["ok"] = true }) : Output.Fail(ctx, reply.Code, reply.Message);
    }

    private static string? Text(JsonNode? node, string name) => node is JsonObject o && o[name] is JsonValue v ? Value(v) : null;

    private static string? Value(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch (InvalidOperationException) { return node?.ToJsonString(); }
    }
}

public sealed class TransferStartCommand : ICommand
{
    public string Name => "transfer start";

    public string Help => "Start the transfer (normally from the UI): --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]";

    /// <summary>The flags, mapped onto the contract's defaults. <c>--skip-errors</c> and <c>--truncate</c> are in
    /// <see cref="Args.BooleanFlags"/>, so neither can swallow the target name that follows it.</summary>
    public static (TransferOptions Options, string? Confirm) Parse(Args args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return (new TransferOptions
        {
            ChunkSize = args.Int("chunk", 100_000),
            Parallelism = args.Int("parallel", 4),
            ErrorMode = args.Flag("skip-errors") ? "skip" : "stop",
            TruncateTarget = args.Flag("truncate"),
        }.Normalized(), args.Opt("yes-target"));
    }

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var (options, confirm) = Parse(args);
        if (string.IsNullOrWhiteSpace(confirm))
            return Output.Fail(ctx, "confirm_required", "Pass --yes-target <target database name> to confirm the transfer.");
        return TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/start",
            new { options, confirmTarget = confirm }));
    }
}

public sealed class TransferPauseCommand : ICommand
{
    public string Name => "transfer pause";

    public string Help => "Pause the running transfer after the current chunks commit";

    public async Task<int> RunAsync(Args args, CliContext ctx)
        => TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/pause", null));
}

public sealed class TransferResumeCommand : ICommand
{
    public string Name => "transfer resume";

    public string Help => "Resume a paused or failed transfer run from its checkpoints";

    public async Task<int> RunAsync(Args args, CliContext ctx)
        => TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/resume", null));
}

public sealed class TransferCancelCommand : ICommand
{
    public string Name => "transfer cancel";

    public string Help => "Cancel the transfer (committed rows stay in the target)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
        => TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/cancel", null));
}

public sealed class TransferStatusCommand : ICommand
{
    public string Name => "transfer status";

    public string Help => "One-line transfer status: run, rows done/total, errors, active tasks";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var reply = await TransferApi.SendAsync(ctx, HttpMethod.Get, "/api/transfer", null);
        return reply.Ok && reply.Body is not null ? Output.Ok(ctx, Compact(reply.Body)) : TransferApi.Print(ctx, reply);
    }

    /// <summary>
    /// Compact projection of GET /api/transfer for the orchestrator: no per-row data, no connection details, and only the tasks that
    /// are running, paused or failed - the ones an operator can be told something about.
    /// <para>The two error fields are copied only when they are there. An <c>"error": null</c> would read as a run that failed with
    /// nothing to say, which is the one thing the orchestrator must never report.</para>
    /// </summary>
    public static JsonObject Compact(JsonNode view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var run = view["run"];
        if (run is null) return new JsonObject { ["status"] = "none" };
        var totals = view["totals"];
        var o = new JsonObject
        {
            ["runId"] = run["id"]?.DeepClone(),
            ["status"] = run["status"]?.DeepClone(),
            ["done"] = totals?["rowsDone"]?.DeepClone(),
            ["total"] = totals?["rowsSource"]?.DeepClone(),
            ["errors"] = totals?["rowsError"]?.DeepClone(),
            ["tasksDone"] = totals?["tasksDone"]?.DeepClone(),
            ["tasksTotal"] = totals?["tasksTotal"]?.DeepClone(),
        };
        if (run["error"] is JsonValue error) o["error"] = error.DeepClone();
        var active = new JsonArray();
        foreach (var t in view["tasks"]?.AsArray() ?? [])
        {
            string status = t?["status"]?.GetValue<string>() ?? "";
            if (status is not ("running" or "paused" or "failed")) continue;
            var item = new JsonObject
            {
                ["id"] = t!["taskId"]?.DeepClone(),
                ["target"] = t["target"]?.DeepClone(),
                ["status"] = status,
                ["done"] = t["rowsDone"]?.DeepClone(),
                ["source"] = t["rowsSource"]?.DeepClone(),
                ["errors"] = t["rowsError"]?.DeepClone(),
            };
            if (t["error"] is JsonValue taskError) item["error"] = taskError.DeepClone();
            active.Add(item);
        }
        o["tasks"] = active;
        return o;
    }
}
