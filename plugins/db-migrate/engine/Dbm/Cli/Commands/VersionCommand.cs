using System.Reflection;
using System.Runtime.InteropServices;

namespace Dbm.Cli.Commands;

public sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Help => "Print engine and runtime versions";

    public static string EngineVersion =>
        typeof(VersionCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VersionCommand).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public Task<int> RunAsync(Args args, CliContext ctx) =>
        Task.FromResult(Output.Ok(ctx, new { version = EngineVersion, runtime = RuntimeInformation.FrameworkDescription }));
}
