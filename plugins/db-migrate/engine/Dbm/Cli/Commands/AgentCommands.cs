using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm next` — the orchestrator's next action (writes the work packet for agent actions).</summary>
public sealed class NextCommand : ICommand
{
    public string Name => "next";
    public string Help => "Print the next action for the orchestrator (agent | await | stop)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        await ServerControl.EnsureRunningAsync(ws);
        using var services = ctx.OpenServices(ws);
        services.Project.TouchAgent();
        return Output.Ok(ctx, services.Workflow.Next());
    }
}

/// <summary>`dbm await [--timeout s]` — long-polls the server until the next action is not "await".</summary>
public sealed class AwaitCommand : ICommand
{
    public const int MaxReconnects = 3;

    public static readonly NextAction ServerStopped = new("stop", Reason: "server_stopped",
        Summary: "The db-migrate server was stopped. Run /db-migrate resume to continue.");

    public string Name => "await";
    public string Help => "Block until a human action changes the next step (--timeout s, 0 = forever); prints the next action";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var timeout = args.Int("timeout", 0);
        var ws = ctx.RequireProject();
        using (var services = ctx.OpenServices(ws)) services.Project.TouchAgent();

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reconnects = 0;
        while (true)
        {
            var info = await ServerControl.EnsureRunningAsync(ws);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.BaseUrl}/api/agent/await?timeout={timeout}");
                request.Headers.Add(TokenGuard.Header, info.Token);
                using var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return Output.Ok(ctx, Json.Deserialize<NextAction>(await response.Content.ReadAsStringAsync()));
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    return Output.Ok(ctx, ServerStopped);   // clean shutdown (dbm stop / UI): never respawn
                return Output.Fail(ctx, "server_error", $"await failed with HTTP {(int)response.StatusCode}");
            }
            catch (HttpRequestException)
            {
                // server died or restarted: handled below
            }
            catch (IOException)
            {
                // connection reset: handled below
            }

            // Connection lost. Wait (≤ 3 s) for the old server to remove server.json: a clean stop removes it, a crash does not.
            for (var i = 0; i < 15 && ServerControl.ReadInfo(ws) is { } still && still.Pid == info.Pid; i++) await Task.Delay(200);
            if (ServerControl.ReadInfo(ws) is null) return Output.Ok(ctx, ServerStopped);
            if (++reconnects > MaxReconnects)
                return Output.Fail(ctx, "server_unreachable", $"Lost the connection to the dbm server {MaxReconnects} times; see {ws.ServerLogPath}");
        }
    }
}

/// <summary>`dbm apply &lt;patch.json&gt; [--dry-run]` — validate an agent patch and create the next version.</summary>
public sealed class ApplyCommand : ICommand
{
    public string Name => "apply";
    public string Help => "Apply an agent patch file (--dry-run: validate only); exit 1 when rejected";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var path = args.Positionals.FirstOrDefault() ?? throw new CliFailure("usage", "usage: dbm apply <patch.json> [--dry-run]");
        if (!File.Exists(path)) throw new CliFailure("not_found", $"Patch file not found: {path}");
        Patch patch;
        try
        {
            patch = Patch.Parse(File.ReadAllText(path));
        }
        catch (PatchException ex)
        {
            throw new CliFailure("invalid_patch", ex.Message);
        }

        using var services = ctx.OpenProject();
        var result = services.Workflow.ApplyPatch(patch, args.Flag("dry-run"));
        if (result.Ok) return Task.FromResult(Output.Ok(ctx, result));
        return Task.FromResult(Output.Write(ctx, new
        {
            ok = false,
            error = "patch_rejected",
            message = string.Join("; ", result.Errors),
            errors = result.Errors,
            warnings = result.Warnings,
        }, 1));
    }
}

/// <summary>`dbm artifact &lt;phase&gt; [--version n] [--path /json/pointer]` — print an artifact or one slice of it.</summary>
public sealed class ArtifactCommand : ICommand
{
    public string Name => "artifact";
    public string Help => "Print a phase artifact (--version n, --path /json/pointer for one slice)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var phase = ParsePhaseArg(args, "usage: dbm artifact <phase> [--version n] [--path /pointer]");
        using var services = ctx.OpenProject();
        var version = args.Opt("version") is null
            ? services.Phases.Get(phase).CurrentVersion ?? throw new CliFailure("not_found", $"{phase.Text()} has no artifact yet")
            : args.Int("version", 0);
        var artifact = services.Artifacts.Get(phase, version)
                       ?? throw new CliFailure("not_found", $"{phase.Text()} v{version} does not exist");
        var payload = JsonNode.Parse(artifact.PayloadJson)!;

        var pointer = args.Opt("path");
        if (pointer is null)
            return Task.FromResult(Output.Ok(ctx, new { phase, version, author = artifact.Author, summary = artifact.Summary, payload }));

        JsonNode? value;
        try
        {
            value = JsonPatch.Resolve(payload, pointer);
        }
        catch (PatchException ex)
        {
            throw new CliFailure("usage", ex.Message);
        }
        if (value is null) throw new CliFailure("not_found", $"{pointer} does not exist in {phase.Text()} v{version}");
        return Task.FromResult(Output.Ok(ctx, new { phase, version, path = pointer, value }));
    }

    internal static PhaseName ParsePhaseArg(Args args, string usage)
    {
        var text = args.Positionals.FirstOrDefault() ?? throw new CliFailure("usage", usage);
        return Phases.TryParse(text, out var phase) ? phase : throw new CliFailure("usage", $"unknown phase '{text}'");
    }
}

/// <summary>`dbm feedback &lt;phase&gt; [--status open]` — list feedback items with responses.</summary>
public sealed class FeedbackCommand : ICommand
{
    public string Name => "feedback";
    public string Help => "List feedback items of a phase (--status draft|open|addressed|declined)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var phase = ArtifactCommand.ParsePhaseArg(args, "usage: dbm feedback <phase> [--status s]");
        FeedbackStatus? status = null;
        if (args.Opt("status") is { } s)
            status = EnumText.TryParse<FeedbackStatus>(s, out var fs) ? fs : throw new CliFailure("usage", $"unknown status '{s}'");
        using var services = ctx.OpenProject();
        return Task.FromResult(Output.Ok(ctx, services.Feedback.List(phase, status)));
    }
}

/// <summary>`dbm run-jobs` — run queued jobs in this process (no server needed).</summary>
public sealed class RunJobsCommand : ICommand
{
    public string Name => "run-jobs";
    public string Help => "Run all queued jobs in this process and exit";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
        return Output.Ok(ctx, new { ok = true, ran });
    }
}
