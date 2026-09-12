namespace Dbm.Cli;

public interface ICommand
{
    /// <summary>One or two words, e.g. "next", "sql validate".</summary>
    string Name { get; }

    /// <summary>One line shown by `dbm help`.</summary>
    string Help { get; }

    /// <summary><paramref name="args"/>.Positionals exclude the command words.</summary>
    Task<int> RunAsync(Args args, CliContext ctx);
}
