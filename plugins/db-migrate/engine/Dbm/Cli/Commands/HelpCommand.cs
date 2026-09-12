using System.Text;

namespace Dbm.Cli.Commands;

public sealed class HelpCommand : ICommand
{
    public string Name => "help";
    public string Help => "List commands";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var commands = CommandRegistry.All().OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("dbm — SQL Server migration engine. Global option: --workspace <dir>");
        foreach (var c in commands) sb.Append('\n').Append(c.Name).Append(" — ").Append(c.Help);
        return Task.FromResult(Output.Text(ctx, sb.ToString()));
    }
}
