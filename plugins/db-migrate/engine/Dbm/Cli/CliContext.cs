namespace Dbm.Cli;

public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }

    /// <summary>From the global --workspace option.</summary>
    public string? WorkspaceOverride { get; init; }
}
