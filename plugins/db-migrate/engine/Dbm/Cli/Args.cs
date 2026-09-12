using System.Globalization;

namespace Dbm.Cli;

/// <summary>
/// Minimal argument parser: "--name value", "--name=value", "--flag", "-k 5"; everything else is positional.
/// Names listed in <see cref="BooleanFlags"/> never consume the following token.
/// </summary>
public sealed class Args
{
    /// <summary>Options that never take a value. Later milestones add their flags here.</summary>
    public static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "quiet", "rebuild", "dry-run", "no-browser", "json", "human", "inline", "help", "force", "detached",
    };

    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Positionals => _positionals;

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var items = argv.ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var token = items[i];
            string? name = null;
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2) name = token[2..];
            else if (token.Length > 1 && token[0] == '-' && !char.IsDigit(token[1])) name = token[1..];

            if (name is null)
            {
                a._positionals.Add(token);
                continue;
            }

            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                a._options[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            var hasValue = !BooleanFlags.Contains(name) && i + 1 < items.Count && !LooksLikeOption(items[i + 1]);
            a._options[name] = hasValue ? items[++i] : null;
        }
        return a;
    }

    private static bool LooksLikeOption(string token) =>
        token.StartsWith("--", StringComparison.Ordinal) || (token.Length > 1 && token[0] == '-' && !char.IsDigit(token[1]));

    public string? Opt(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public bool Flag(string name) => _options.ContainsKey(name);

    public int Int(string name, int defaultValue)
    {
        var raw = Opt(name);
        if (raw is null) return defaultValue;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new CliFailure("usage", $"--{name} expects an integer, got '{raw}'");
    }
}
