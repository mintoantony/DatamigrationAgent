using System.Text;

namespace Dbm.Cli;

public static class CliApp
{
    public static async Task<int> RunAsync(string[] argv, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        if (stdout is null) UseUtf8Console();
        var (rest, workspace) = ExtractWorkspace(argv);
        var ctx = new CliContext
        {
            Out = stdout ?? Console.Out,
            Err = stderr ?? Console.Error,
            WorkspaceOverride = workspace,
        };

        try
        {
            if (workspace == "") throw new CliFailure("usage", "--workspace expects a directory");
            var (command, words) = Match(rest);
            if (rest.Count == 0) (command, words) = (CommandRegistry.All().First(c => c.Name == "help"), 0);
            if (command is null)
            {
                var typed = string.Join(' ', rest.TakeWhile(w => !w.StartsWith('-')).Take(2));
                return Output.Fail(ctx, "unknown_command", $"Unknown command '{typed}'. Run: dbm help");
            }
            return await command.RunAsync(Args.Parse(rest.Skip(words)), ctx);
        }
        catch (CliFailure f)
        {
            return Output.Fail(ctx, f.Code, f.Message);
        }
        catch (Exception ex)
        {
            return Output.Fail(ctx, "internal", ex.Message);
        }
    }

    /// <summary>Removes the global "--workspace dir" / "--workspace=dir" from anywhere in argv.</summary>
    internal static (List<string> Remaining, string? Workspace) ExtractWorkspace(IReadOnlyList<string> argv)
    {
        var rest = new List<string>();
        string? workspace = null;
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            if (a == "--workspace")
            {
                workspace = i + 1 < argv.Count ? argv[++i] : "";
            }
            else if (a.StartsWith("--workspace=", StringComparison.Ordinal))
            {
                workspace = a["--workspace=".Length..];
            }
            else
            {
                rest.Add(a);
            }
        }
        return (rest, workspace);
    }

    /// <summary>Longest match first: a two-word command ("sql validate") wins over a one-word one ("sql").</summary>
    internal static (ICommand? Command, int Words) Match(IReadOnlyList<string> rest) => Match(rest, CommandRegistry.All());

    internal static (ICommand? Command, int Words) Match(IReadOnlyList<string> rest, IReadOnlyList<ICommand> commands)
    {
        var words = rest.TakeWhile(w => !w.StartsWith('-')).Take(2).ToList();
        for (var n = words.Count; n >= 1; n--)
        {
            var name = string.Join(' ', words.Take(n));
            var match = commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (match, n);
        }
        return (null, 0);
    }

    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // No console attached (detached server): nothing to configure.
        }
    }
}
