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

/// <summary>`dbm config sample-values [off]` - Ruling 196: show whether the profiles Claude reads carry sample values, or switch
/// them off. Ruling 200: switching them back on is a human decision taken on the Setup screen's Privacy card; the CLI, which
/// Claude itself runs, refuses it.</summary>
public sealed class ConfigSampleValuesCommand : ICommand
{
    public const string OnRefused = "Sample values can be switched back on only by you, on the Setup screen: open the UI "
                                    + "(dbm ui) and tick \"Send sample values to Claude\" under Privacy.";

    public string Name => "config sample-values";
    public string Help => "Show whether column profiles sent to Claude include sample values, or switch them off: config sample-values [off]";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        const string usage = "usage: dbm config sample-values [off]";
        bool? on = args.Positionals.FirstOrDefault() switch
        {
            null => null,
            "on" => true,
            "off" => false,
            _ => throw new CliFailure("usage", usage),
        };
        if (args.Positionals.Count > 1) throw new CliFailure("usage", usage);
        if (on == true) throw new CliFailure("setup_screen_only", OnRefused);
        using var services = ctx.OpenProject();
        if (on is not bool value)
        {
            var current = services.Project.GetSettings().SampleValues;
            return Task.FromResult(Output.Ok(ctx, new
            {
                sampleValues = current,
                note = current ? SampleValuesSetting.OnNote : SampleValuesSetting.OffNote,
            }));
        }
        var change = SampleValuesSetting.Set(services, value);
        services.Sink.Publish("state_changed", new { phase = PhaseName.Setup, status = services.Phases.Get(PhaseName.Setup).Status });
        return Task.FromResult(Output.Ok(ctx, change));
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
