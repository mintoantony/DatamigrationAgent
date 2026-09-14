using Dbm.Core;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm serve [--port n] [--detached]` — run the web server (ServerControl spawns it with --detached).</summary>
public sealed class ServeCommand : ICommand
{
    public string Name => "serve";
    public string Help => "Run the UI/API server in the foreground (normally started detached by other commands)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();

        // Set up --detached suppression, and the `output` writer every JSON line below goes through, before the
        // first possible early return — not just before the ones that happen to follow it in source order.
        var detached = args.Flag("detached");
        if (detached)
        {
            // Nobody reads our stdout/stderr once the spawning command has exited.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
        var output = detached ? TextWriter.Null : ctx.Out;

        var existing = ServerControl.ReadInfo(ws);
        if (existing is not null && await ServerControl.IsAliveAsync(existing))
        {
            output.WriteLine(Json.Serialize(new { ok = true, alreadyRunning = true, url = existing.UiUrl }));
            output.Flush();
            return 0;
        }

        var result = await WebHost.RunAsync(ws, args.Int("port", 0), CancellationToken.None, ctx.ServicesFactory, info =>
        {
            output.WriteLine(Json.Serialize(new { ok = true, url = info.UiUrl, pid = info.Pid }));
            output.Flush();
        });
        if (result == WebHostStartResult.Started) return 0;

        // WebHost.RunAsync returned early without starting: another process already owns this workspace (a live
        // server answered health, or it holds the exclusive server.lock). Only report alreadyRunning when a live
        // server's URL can actually be confirmed — the lock holder may not have written server.json yet (still
        // starting) or may have already deleted it (draining), and claiming "already running" with an empty or
        // stale URL would tell the caller a server is there to talk to when none is reachable. Waits (bounded by
        // StartTimeout) rather than checking once, so a lock holder that is legitimately still mid-start (DI build
        // and Kestrel bind happen before it writes server.json) is not mistaken for a stuck lock.
        var owner = await ServerControl.WaitForHealthyOwnerAsync(ws);
        if (owner is not null)
        {
            output.WriteLine(Json.Serialize(new { ok = true, alreadyRunning = true, url = owner.UiUrl }));
            output.Flush();
            return 0;
        }

        output.WriteLine(Json.Serialize(new
        {
            error = "workspace_locked",
            message = $"another process holds this workspace's server.lock but is not a reachable server; see {ws.ServerLogPath}.",
        }));
        output.Flush();
        return 1;
    }
}

/// <summary>`dbm ui [--no-browser]` — make sure the server runs and open the browser.</summary>
public sealed class UiCommand : ICommand
{
    public string Name => "ui";
    public string Help => "Start the UI server if needed and open the browser";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        var info = await ServerControl.EnsureRunningAsync(ws);
        if (!args.Flag("no-browser")) ServerControl.OpenBrowser(info.UiUrl);
        return Output.Ok(ctx, new { ok = true, url = info.UiUrl });
    }
}

/// <summary>`dbm stop` — stop the detached server of this workspace.</summary>
public sealed class StopCommand : ICommand
{
    public string Name => "stop";
    public string Help => "Stop the UI server of this workspace";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.Workspace();
        var stopped = await ServerControl.StopAsync(ws);
        return Output.Ok(ctx, new { ok = true, stopped });
    }
}
