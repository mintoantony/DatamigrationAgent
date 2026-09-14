using Dbm.Core;

namespace Dbm.Cli;

public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }

    /// <summary>From the global --workspace option.</summary>
    public string? WorkspaceOverride { get; init; }

    /// <summary>Test seam: how commands open services. Null = <see cref="DbmServices.Open"/> with the real registries.</summary>
    public Func<Dbm.Core.Workspace, DbmServices>? ServicesFactory { get; init; }

    public Dbm.Core.Workspace Workspace() => Dbm.Core.Workspace.Resolve(WorkspaceOverride);

    /// <summary>The resolved workspace, or CliFailure("no_project") when it has no project yet.</summary>
    public Dbm.Core.Workspace RequireProject()
    {
        var ws = Workspace();
        if (!ws.Exists) throw new CliFailure("no_project", $"No db-migrate project in {ws.Root}. Run: dbm init");
        return ws;
    }

    public DbmServices OpenProject() => OpenServices(RequireProject());

    public DbmServices OpenServices(Dbm.Core.Workspace ws) => ServicesFactory?.Invoke(ws) ?? DbmServices.Open(ws);
}
