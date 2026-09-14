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
        var existing = ServerControl.ReadInfo(ws);
        if (existing is not null && await ServerControl.IsAliveAsync(existing))
            return Output.Ok(ctx, new { ok = true, alreadyRunning = true, url = existing.UiUrl });

        var detached = args.Flag("detached");
        if (detached)
        {
            // Nobody reads our stdout/stderr once the spawning command has exited.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
        var output = detached ? TextWriter.Null : ctx.Out;
        var started = false;
        await WebHost.RunAsync(ws, args.Int("port", 0), CancellationToken.None, ctx.ServicesFactory, info =>
        {
            started = true;
            output.WriteLine(Json.Serialize(new { ok = true, url = info.UiUrl, pid = info.Pid }));
            output.Flush();
        });
        if (started) return 0;

        // WebHost.RunAsync returned early without starting: another process already owns this workspace
        // (a live server answered health, or it holds the exclusive server.lock). Never silently exit as if we served.
        var owner = ServerControl.ReadInfo(ws);
        return Output.Ok(ctx, new { ok = true, alreadyRunning = true, url = owner?.UiUrl ?? "" });
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
        var wasRunning = ServerControl.ReadInfo(ws) is not null;
        await ServerControl.StopAsync(ws);
        return Output.Ok(ctx, new { ok = true, stopped = wasRunning });
    }
}
