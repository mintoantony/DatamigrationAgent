using Dbm.Core.State;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm init [name] [--no-browser]` — create the project (if new), start the server, open the browser.</summary>
public sealed class InitCommand : ICommand
{
    public string Name => "init";
    public string Help => "Create (or reuse) the project in this folder, start the UI server and open the browser";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.Workspace();
        ws.EnsureCreated();
        bool created;
        using (var services = ctx.OpenServices(ws))
        {
            created = !services.Project.Exists();
            if (created) services.Project.Init(args.Positionals.FirstOrDefault() ?? new DirectoryInfo(ws.Root).Name);
        }
        var info = await ServerControl.EnsureRunningAsync(ws);
        if (!args.Flag("no-browser")) ServerControl.OpenBrowser(info.UiUrl);
        return Output.Ok(ctx, new { ok = true, created, url = info.UiUrl, workspace = ws.Root });
    }
}

/// <summary>`dbm status` — one-line project/phase status.</summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Help => "Project name, current phase and status, next action, UI URL";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);
        Dbm.Core.Transfer.RunFinisher.Reconcile(services);   // ruling 183, as in `dbm next`
        var project = services.Project.Get();
        var phases = services.Phases.All();
        var current = phases.FirstOrDefault(p => p.Status != PhaseStatus.Approved) ?? phases[^1];
        var info = ServerControl.ReadInfo(ws);
        var url = info is not null && await ServerControl.IsAliveAsync(info) ? info.UiUrl : "";
        return Output.Ok(ctx, new
        {
            name = project.Name,
            paused = project.Paused,
            phase = current.Name,
            status = current.Status,
            version = current.CurrentVersion,
            next = services.Workflow.Peek(),
            url,
        });
    }
}

/// <summary>`dbm pause` — the agent stops taking new work; the UI shows a Resume banner.</summary>
public sealed class PauseCommand : ICommand
{
    public string Name => "pause";
    public string Help => "Pause the project (agent work stops after the current step)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        services.Workflow.SetPaused(true);
        return Task.FromResult(Output.Ok(ctx, new { ok = true, paused = true }));
    }
}

/// <summary>`dbm resume`.</summary>
public sealed class ResumeCommand : ICommand
{
    public string Name => "resume";
    public string Help => "Resume a paused project";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        services.Workflow.SetPaused(false);
        return Task.FromResult(Output.Ok(ctx, new { ok = true, paused = false }));
    }
}
