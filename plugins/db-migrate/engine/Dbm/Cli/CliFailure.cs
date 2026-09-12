namespace Dbm.Cli;

/// <summary>Thrown by commands (or helpers) to end the command with {"error":Code,"message":Message} and exit 1.</summary>
public sealed class CliFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
