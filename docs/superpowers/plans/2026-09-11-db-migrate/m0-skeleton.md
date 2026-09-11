# Milestone 0 — Skeleton

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** a building .NET 8 solution with the `dbm` CLI core (argument parsing, command routing, JSON output), the Claude Code plugin scaffold (marketplace + plugin manifests, `bin/dbm` launchers, SessionStart hook) and a working `dbm doctor`. After M0, `plugins/db-migrate/bin/dbm version` works from PowerShell and Git Bash, builds the engine on first run, and `claude plugin validate` accepts the repository.

All commands below run from the repository root `D:\DatamigrationAgent` unless a step says otherwise. Git Bash and PowerShell are both fine for `dotnet`/`git` commands.

---

### Task 0.1: Solution scaffold + CLI core

**Files:**
- Create: `plugins/db-migrate/engine/Directory.Build.props`
- Create: `plugins/db-migrate/engine/Dbm.sln`
- Create: `plugins/db-migrate/engine/Dbm/Dbm.csproj`, `Dbm/Program.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Json.cs` (C1 — moved here from T1.1 because `Output` needs it)
- Create: `plugins/db-migrate/engine/Dbm/Cli/Args.cs`, `CliFailure.cs`, `CliContext.cs`, `ICommand.cs`, `Output.cs`, `CommandRegistry.cs`, `CliApp.cs`
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/HelpCommand.cs`, `VersionCommand.cs`
- Create: `plugins/db-migrate/engine/Dbm.Tests/Dbm.Tests.csproj`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ArgsTests.cs`, `Unit/Cli/CliAppTests.cs`, `Unit/Cli/OutputTests.cs`, `Unit/Core/JsonTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (C1/C8): `Dbm.Core.Json` (`Options`, `Pretty`, `Serialize`, `Deserialize`, `ToNode`, `FromNode`); `Dbm.Cli.Args` (`Parse`, `Positionals`, `Opt`, `Flag`, `Int`, plus `static HashSet<string> BooleanFlags`); `CliContext` (`Out`, `Err`, `WorkspaceOverride` — `Workspace()` is added in T1.1); `ICommand`; `Output` (`Ok`, `Fail`, `Text`, plus `Write(ctx, payload, exitCode)`); `CommandRegistry.All()`; `CliApp.RunAsync(string[] argv, TextWriter? stdout = null, TextWriter? stderr = null)`; `CliFailure(string code, string message)` (any command may throw it to end with `{"error":code,"message":message}` / exit 1).

- [ ] **Step 1: Create the feature branch**

```bash
git checkout -b feat/db-migrate
```

- [ ] **Step 2: Create `plugins/db-migrate/engine/Directory.Build.props`**

`IncludeSourceRevisionInInformationalVersion=false` keeps `dbm version` at exactly `0.1.0` (the SDK would otherwise append `+<commit sha>`).

`plugins/db-migrate/engine/Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <Deterministic>true</Deterministic>
    <Version>0.1.0</Version>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create `plugins/db-migrate/engine/Dbm/Dbm.csproj`**

`wwwroot` does not exist yet (T1.9 creates it); the `Content Update` item is harmless until then and makes the UI files land in both build and publish output. The `schema.sql` embedded resource is added by T1.2.

`plugins/db-migrate/engine/Dbm/Dbm.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Dbm</AssemblyName>
    <RootNamespace>Dbm</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.3" />
    <PackageReference Include="Microsoft.Data.SqlClient.Extensions.Azure" Version="7.0.3" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Dbm.Tests" />
  </ItemGroup>

  <ItemGroup>
    <Content Update="wwwroot\**" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create `plugins/db-migrate/engine/Dbm.Tests/Dbm.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>Dbm.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Dbm\Dbm.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Create `Dbm/Program.cs` and the solution**

`plugins/db-migrate/engine/Dbm/Program.cs`:

```csharp
return await Dbm.Cli.CliApp.RunAsync(args);
```

Then (the .NET 10 SDK defaults to `.slnx`; `--format sln` keeps the file name from the file map):

```bash
cd plugins/db-migrate/engine
dotnet new sln -n Dbm --format sln
dotnet sln Dbm.sln add Dbm/Dbm.csproj Dbm.Tests/Dbm.Tests.csproj
cd ../../..
```

Expected: `Project 'Dbm\Dbm.csproj' added to the solution.` and the same for `Dbm.Tests`.

- [ ] **Step 6: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ArgsTests.cs`:

```csharp
using Dbm.Cli;

namespace Dbm.Tests.Unit.Cli;

public class ArgsTests
{
    [Fact]
    public void Separates_positionals_options_and_flags()
    {
        var a = Args.Parse(["p.json", "--dry-run", "--version", "3", "--path=/a/b", "-k", "5"]);

        Assert.Equal(new[] { "p.json" }, a.Positionals);
        Assert.True(a.Flag("dry-run"));
        Assert.Null(a.Opt("dry-run"));
        Assert.Equal("3", a.Opt("version"));
        Assert.Equal("/a/b", a.Opt("path"));
        Assert.Equal(5, a.Int("k", 0));
    }

    [Fact]
    public void Known_boolean_flags_never_consume_the_next_token()
    {
        var a = Args.Parse(["--no-browser", "myproject"]);

        Assert.True(a.Flag("no-browser"));
        Assert.Equal(new[] { "myproject" }, a.Positionals);
    }

    [Fact]
    public void An_option_followed_by_another_option_is_a_flag()
    {
        var a = Args.Parse(["--verbose", "--port", "8080"]);

        Assert.True(a.Flag("verbose"));
        Assert.Null(a.Opt("verbose"));
        Assert.Equal(8080, a.Int("port", 0));
    }

    [Fact]
    public void Int_returns_the_default_when_missing_and_fails_on_garbage()
    {
        var a = Args.Parse(["--timeout", "soon"]);

        Assert.Equal(7, a.Int("k", 7));
        var ex = Assert.Throws<CliFailure>(() => a.Int("timeout", 0));
        Assert.Equal("usage", ex.Code);
    }

    [Fact]
    public void Negative_numbers_are_positionals_and_the_last_duplicate_wins()
    {
        var a = Args.Parse(["-5", "--k", "1", "--k", "2"]);

        Assert.Equal(new[] { "-5" }, a.Positionals);
        Assert.Equal(2, a.Int("k", 0));
        Assert.False(a.Flag("missing"));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/CliAppTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Cli;

namespace Dbm.Tests.Unit.Cli;

public class CliAppTests
{
    private sealed class NamedCommand(string name) : ICommand
    {
        public string Name => name;
        public string Help => "test";
        public Task<int> RunAsync(Args args, CliContext ctx) => Task.FromResult(0);
    }

    private static async Task<(int Exit, string Out)> Run(params string[] argv)
    {
        var stdout = new StringWriter();
        var exit = await CliApp.RunAsync(argv, stdout, new StringWriter());
        return (exit, stdout.ToString().Trim());
    }

    [Fact]
    public async Task Version_prints_one_compact_json_line()
    {
        var (exit, output) = await Run("version");

        Assert.Equal(0, exit);
        Assert.DoesNotContain('\n', output);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("0.1.0", json["version"]!.GetValue<string>());
        Assert.StartsWith(".NET", json["runtime"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_command_fails_with_unknown_command()
    {
        var (exit, output) = await Run("frobnicate", "--x", "1");

        Assert.Equal(1, exit);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("unknown_command", json["error"]!.GetValue<string>());
        Assert.Contains("frobnicate", json["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_arguments_prints_help_text()
    {
        var (exit, output) = await Run();

        Assert.Equal(0, exit);
        Assert.Contains("version — ", output);
        Assert.Contains("help — ", output);
    }

    [Fact]
    public async Task Workspace_without_a_value_is_a_usage_error()
    {
        var (exit, output) = await Run("version", "--workspace");

        Assert.Equal(1, exit);
        Assert.Equal("usage", JsonNode.Parse(output)!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(new[] { "next", "--workspace", "D:/p" }, "D:/p")]
    [InlineData(new[] { "--workspace=D:/q", "next" }, "D:/q")]
    [InlineData(new[] { "next" }, null)]
    public void Global_workspace_is_extracted_from_anywhere(string[] argv, string? expected)
    {
        var (rest, ws) = CliApp.ExtractWorkspace(argv);

        Assert.Equal(expected, ws);
        Assert.Equal(new[] { "next" }, rest);
    }

    [Fact]
    public void Workspace_is_extracted_after_two_word_commands()
    {
        var (rest, ws) = CliApp.ExtractWorkspace(["map", "auto", "--workspace", @"D:\x", "--dry-run"]);

        Assert.Equal(@"D:\x", ws);
        Assert.Equal(new[] { "map", "auto", "--dry-run" }, rest);
    }

    [Fact]
    public void Longest_command_name_wins()
    {
        ICommand[] commands = [new NamedCommand("sql"), new NamedCommand("sql validate")];

        var (two, twoWords) = CliApp.Match(["sql", "validate", "--task", "T01"], commands);
        var (one, oneWord) = CliApp.Match(["sql", "gen"], commands);
        var (none, _) = CliApp.Match(["--flag"], commands);

        Assert.Equal("sql validate", two!.Name);
        Assert.Equal(2, twoWords);
        Assert.Equal("sql", one!.Name);
        Assert.Equal(1, oneWord);
        Assert.Null(none);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/OutputTests.cs`:

```csharp
using Dbm.Cli;

namespace Dbm.Tests.Unit.Cli;

public class OutputTests
{
    private static CliContext Ctx(StringWriter sw) => new() { Out = sw, Err = new StringWriter() };

    [Fact]
    public void Ok_writes_compact_camel_case_json_and_returns_zero()
    {
        var sw = new StringWriter();

        var exit = Output.Ok(Ctx(sw), new { Ok = true, PatchPath = "a/b.json", Missing = (string?)null });

        Assert.Equal(0, exit);
        Assert.Equal("{\"ok\":true,\"patchPath\":\"a/b.json\"}" + Environment.NewLine, sw.ToString());
    }

    [Fact]
    public void Fail_writes_error_and_message_and_returns_one()
    {
        var sw = new StringWriter();

        var exit = Output.Fail(Ctx(sw), "no_project", "Run dbm init");

        Assert.Equal(1, exit);
        Assert.Equal("{\"error\":\"no_project\",\"message\":\"Run dbm init\"}" + Environment.NewLine, sw.ToString());
    }

    [Fact]
    public void Text_writes_the_text_verbatim()
    {
        var sw = new StringWriter();

        Assert.Equal(0, Output.Text(Ctx(sw), "a — b"));
        Assert.Equal("a — b" + Environment.NewLine, sw.ToString());
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Core/JsonTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;

namespace Dbm.Tests.Unit.Core;

public class JsonTests
{
    public enum SampleState { Pending, AwaitingReview }

    public sealed record Sample(string Name, SampleState State, int? Count, Dictionary<string, int> Tables);

    [Fact]
    public void Serialises_camel_case_snake_enums_and_omits_nulls()
    {
        var json = Json.Serialize(new Sample("x", SampleState.AwaitingReview, null, new() { ["dbo.Customer"] = 1 }));

        Assert.Equal("{\"name\":\"x\",\"state\":\"awaiting_review\",\"tables\":{\"dbo.Customer\":1}}", json);
    }

    [Fact]
    public void Deserialises_case_insensitively_and_parses_snake_enums()
    {
        var s = Json.Deserialize<Sample>("{\"NAME\":\"y\",\"state\":\"awaiting_review\",\"count\":3,\"tables\":{}}");

        Assert.Equal("y", s.Name);
        Assert.Equal(SampleState.AwaitingReview, s.State);
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void Dictionary_keys_are_never_renamed()
    {
        var json = Json.Serialize(new Dictionary<string, int> { ["dbo.CUST_NM"] = 1, ["FirstName"] = 2, ["T01"] = 3 });

        Assert.Equal("{\"dbo.CUST_NM\":1,\"FirstName\":2,\"T01\":3}", json);
        Assert.Equal(2, Json.Deserialize<Dictionary<string, int>>(json)["FirstName"]);
    }

    [Fact]
    public void Deserialising_null_throws()
    {
        Assert.Throws<JsonException>(() => Json.Deserialize<Sample>("null"));
    }

    [Fact]
    public void Keeps_quotes_and_angle_brackets_readable()
    {
        Assert.Equal("\"LOWER(s.[EMAIL]) <> 'x'\"", Json.Serialize("LOWER(s.[EMAIL]) <> 'x'"));
    }

    [Fact]
    public void Pretty_is_indented_and_nodes_round_trip()
    {
        var node = Json.ToNode(new Sample("z", SampleState.Pending, 1, new()));
        var back = Json.FromNode<Sample>(node);

        Assert.Contains("\n", Json.Serialize(new { a = 1 }, pretty: true));
        Assert.Equal("pending", node["state"]!.GetValue<string>());
        Assert.Equal("z", back.Name);
        Assert.IsType<JsonObject>(node);
    }
}
```

- [ ] **Step 7: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with errors such as `error CS0246: The type or namespace name 'Args' could not be found` and `CS0103: The name 'Program'…`/`'CliApp' does not exist`.

- [ ] **Step 8: Implement `Dbm/Core/Json.cs`**

`UnsafeRelaxedJsonEscaping` keeps `'`, `<`, `>` and non-ASCII readable in CLI output and work packets (agents read them). Anything that embeds JSON inside HTML must additionally replace `</` with `<\/`.

`plugins/db-migrate/engine/Dbm/Core/Json.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dbm.Core;

/// <summary>The one JSON configuration used everywhere (CLI output, SQLite payloads, HTTP API, packets).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // no DictionaryKeyPolicy: keys like "dbo.CUST_NM" round-trip exactly
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            // Keep quotes, <, >, & and non-ASCII readable for agents. Anything that embeds JSON inside HTML
            // must additionally replace "</" with "<\/".
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value, bool pretty = false) =>
        JsonSerializer.Serialize(value, pretty ? Pretty : Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}");

    public static JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Options) ?? throw new JsonException("value serialised to null");

    public static T FromNode<T>(JsonNode node) =>
        node.Deserialize<T>(Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}");
}
```

- [ ] **Step 9: Implement the CLI core**

`plugins/db-migrate/engine/Dbm/Cli/CliFailure.cs`:

```csharp
namespace Dbm.Cli;

/// <summary>Thrown by commands (or helpers) to end the command with {"error":Code,"message":Message} and exit 1.</summary>
public sealed class CliFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
```

`plugins/db-migrate/engine/Dbm/Cli/Args.cs` (`BooleanFlags` lists every flag used anywhere in the plan so that `--dry-run patch.json` never swallows the positional; later milestones add their own flags to this set):

```csharp
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
```

`plugins/db-migrate/engine/Dbm/Cli/ICommand.cs`:

```csharp
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
```

`plugins/db-migrate/engine/Dbm/Cli/Output.cs`:

```csharp
using Dbm.Core;

namespace Dbm.Cli;

/// <summary>Every command ends through one of these so stdout is always one compact JSON line (or text).</summary>
public static class Output
{
    public static int Ok(CliContext ctx, object payload) => Write(ctx, payload, 0);

    public static int Fail(CliContext ctx, string code, string message) =>
        Write(ctx, new { error = code, message }, 1);

    public static int Text(CliContext ctx, string text)
    {
        ctx.Out.WriteLine(text);
        return 0;
    }

    /// <summary>Writes <paramref name="payload"/> as compact JSON and returns <paramref name="exitCode"/>.</summary>
    public static int Write(CliContext ctx, object payload, int exitCode)
    {
        ctx.Out.WriteLine(Json.Serialize(payload));
        return exitCode;
    }
}
```

`plugins/db-migrate/engine/Dbm/Cli/CliContext.cs` (T1.1 adds `Workspace()`, T1.8 adds the services helpers):

```csharp
namespace Dbm.Cli;

public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }

    /// <summary>From the global --workspace option.</summary>
    public string? WorkspaceOverride { get; init; }
}
```

`plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs`:

```csharp
using Dbm.Cli.Commands;

namespace Dbm.Cli;

public static class CommandRegistry
{
    public static IReadOnlyList<ICommand> All() =>
    [
        // M0
        new HelpCommand(),
        new VersionCommand(),
        // Later tasks append their commands below this line.
    ];
}
```

`plugins/db-migrate/engine/Dbm/Cli/CliApp.cs` (T1.8 adds an internal overload with a services factory for tests):

```csharp
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
```

`plugins/db-migrate/engine/Dbm/Cli/Commands/HelpCommand.cs`:

```csharp
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
```

`plugins/db-migrate/engine/Dbm/Cli/Commands/VersionCommand.cs`:

```csharp
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
```

- [ ] **Step 10: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23` (ArgsTests 5, CliAppTests 9, OutputTests 3, JsonTests 6), no build warnings.

Also check the real entry point:

```bash
dotnet run --project plugins/db-migrate/engine/Dbm -- version
```

Expected: `{"version":"0.1.0","runtime":".NET 8.0.<patch>"}` (or `.NET 10…` if only a newer runtime is present — `RollForward=Major`).

- [ ] **Step 11: Commit**

```bash
git add plugins/db-migrate/engine/Directory.Build.props plugins/db-migrate/engine/Dbm.sln plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(engine): solution scaffold and CLI core (args, routing, JSON output)" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 0.2: Plugin scaffold, launchers, `dbm doctor`

**Files:**
- Create: `.claude-plugin/marketplace.json`, `.gitattributes`
- Create: `plugins/db-migrate/.claude-plugin/plugin.json`
- Create: `plugins/db-migrate/bin/dbm`, `plugins/db-migrate/bin/dbm.cmd`
- Create: `plugins/db-migrate/hooks/hooks.json`
- Create: `plugins/db-migrate/engine/Dbm/Core/UserHome.cs` (C1 — moved here from T1.1 because doctor checks it)
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorCommand.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs`
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/TestEnvironment.cs` (module initializer: `DBM_HOME` → a temp folder for the whole test run)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorCommandTests.cs`, `Unit/Core/UserHomeTests.cs`

**Interfaces:**
- Consumes: T0.1 CLI core.
- Produces: `Dbm.Core.UserHome.Dir` (C1); `DoctorCommand` (`doctor [--quiet] [--rebuild]`, JSON `{ok, checks:[{name, ok, detail}]}`), `DoctorCommand.RunChecks()` → `IReadOnlyList<DoctorCommand.Check>`; launcher exit codes 3 (runtime missing) and 4 (engine not built / build failed).

Why the runtime check lives in the launchers: `Dbm.dll` is a `Microsoft.NET.Sdk.Web` app, so the host refuses to start it at all when `Microsoft.AspNetCore.App` is missing — no C# code can print guidance in that case.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Support/TestEnvironment.cs` (runs once when the test assembly loads; `DBM_NO_BROWSER` is honoured by `ServerControl.OpenBrowser` from T1.7 on):

```csharp
using System.Runtime.CompilerServices;

namespace Dbm.Tests.Support;

/// <summary>Runs once when the test assembly loads: isolates every test run from the real user profile.</summary>
internal static class TestEnvironment
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), $"dbm-tests-home-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("DBM_HOME", Home);
        Environment.SetEnvironmentVariable("DBM_WORKSPACE", null);
        Environment.SetEnvironmentVariable("DBM_NO_BROWSER", "1");   // honoured by ServerControl.OpenBrowser (T1.7)
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Core/UserHomeTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Core;

public class UserHomeTests
{
    [Fact]
    public void Uses_DBM_HOME_and_creates_the_directory()
    {
        var dir = UserHome.Dir;

        Assert.Equal(Path.GetFullPath(TestEnvironment.Home), dir);
        Assert.True(Directory.Exists(dir));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorCommandTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Cli;
using Dbm.Cli.Commands;

namespace Dbm.Tests.Unit.Cli;

public class DoctorCommandTests
{
    [Fact]
    public void All_checks_pass_on_a_healthy_machine()
    {
        var checks = DoctorCommand.RunChecks();

        Assert.Equal(new[] { "runtime", "aspnetcore", "sqlclient", "sqlite", "home" }, checks.Select(c => c.Name));
        Assert.All(checks, c => Assert.True(c.Ok, $"{c.Name}: {c.Detail}"));
        Assert.StartsWith("3.", checks.Single(c => c.Name == "sqlite").Detail);
    }

    [Fact]
    public async Task Doctor_prints_json_report()
    {
        var sw = new StringWriter();

        var exit = await CliApp.RunAsync(["doctor"], sw, new StringWriter());

        Assert.Equal(0, exit);
        var json = JsonNode.Parse(sw.ToString())!;
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal(5, json["checks"]!.AsArray().Count);
        Assert.NotNull(json["checks"]![0]!["detail"]);
    }

    [Fact]
    public async Task Quiet_doctor_is_silent_when_healthy()
    {
        var sw = new StringWriter();

        var exit = await CliApp.RunAsync(["doctor", "--quiet"], sw, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal("", sw.ToString());
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'DoctorCommand' could not be found` and `CS0103: The name 'UserHome' does not exist`.

- [ ] **Step 3: Implement `UserHome` and `DoctorCommand`**

`plugins/db-migrate/engine/Dbm/Core/UserHome.cs` (reads `DBM_HOME` on every access so tests can isolate it):

```csharp
namespace Dbm.Core;

/// <summary>Per-user data directory (encryption key, team synonyms). Env DBM_HOME wins; read on every access.</summary>
public static class UserHome
{
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DBM_HOME");
            var dir = !string.IsNullOrWhiteSpace(env)
                ? Path.GetFullPath(env)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbmigrate");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
```

`plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorCommand.cs` — `--quiet` prints nothing when healthy because SessionStart hook stdout is injected into Claude's context:

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using Dbm.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Dbm.Cli.Commands;

public sealed class DoctorCommand : ICommand
{
    public sealed record Check(string Name, bool Ok, string Detail);

    public string Name => "doctor";
    public string Help => "Check runtime, drivers and user profile (--quiet: silent when healthy; --rebuild: launcher rebuilds first)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        // --rebuild is handled by bin/dbm(.cmd) before this process starts; accepted here so it is not an error.
        var checks = RunChecks();
        var ok = checks.All(c => c.Ok);
        if (args.Flag("quiet"))
        {
            if (ok) return Task.FromResult(0);
            var problems = string.Join("; ", checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}"));
            ctx.Out.WriteLine($"dbm doctor: {problems}");
            return Task.FromResult(1);
        }
        if (!ok) return Task.FromResult(Output.Write(ctx, new { ok, checks }, 1));
        return Task.FromResult(Output.Ok(ctx, new { ok, checks }));
    }

    public static IReadOnlyList<Check> RunChecks() =>
    [
        Probe("runtime", () => (Environment.Version.Major >= 8, RuntimeInformation.FrameworkDescription)),
        Probe("aspnetcore", () =>
        {
            var v = VersionOf(typeof(WebApplication).Assembly);
            return (Major(v) >= 8, v);
        }),
        Probe("sqlclient", () => (true, VersionOf(typeof(SqlConnection).Assembly))),
        Probe("sqlite", () =>
        {
            using var c = new SqliteConnection("Data Source=:memory:");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "select sqlite_version()";
            return (true, Convert.ToString(cmd.ExecuteScalar()) ?? "?");
        }),
        Probe("home", () =>
        {
            var dir = UserHome.Dir;
            var probe = Path.Combine(dir, $".doctor-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, dir);
        }),
    ];

    private static Check Probe(string name, Func<(bool Ok, string Detail)> check)
    {
        try
        {
            var (ok, detail) = check();
            return new Check(name, ok, detail);
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string VersionOf(Assembly assembly) =>
        (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? assembly.GetName().Version?.ToString()
         ?? "unknown").Split('+')[0];

    private static int Major(string version) =>
        int.TryParse(version.Split('.')[0], out var m) ? m : 0;
}
```

Register it in `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` — replace

```csharp
        new VersionCommand(),
        // Later tasks append their commands below this line.
```

with

```csharp
        new VersionCommand(),
        new DoctorCommand(),
        // Later tasks append their commands below this line.
```

- [ ] **Step 4: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:    27` (23 from T0.1 + DoctorCommandTests 3 + UserHomeTests 1).

- [ ] **Step 5: Create the plugin manifests**

`.claude-plugin/marketplace.json`:

```json
{
  "name": "db-migrate",
  "description": "Xanalys plugins for SQL Server data migration.",
  "owner": {
    "name": "Xanalys"
  },
  "plugins": [
    {
      "name": "db-migrate",
      "source": "./plugins/db-migrate",
      "description": "Migrate data between two SQL Server databases with different schemas: analysis, mapping and SQL reviewed in a local web UI, then a chunked, resumable transfer.",
      "version": "0.1.0"
    }
  ]
}
```

`plugins/db-migrate/.claude-plugin/plugin.json`:

```json
{
  "name": "db-migrate",
  "version": "0.1.0",
  "description": "SQL Server to SQL Server migration agent: schema analysis, source-to-target mapping and migration SQL, each reviewed and approved in a local web UI, then a chunked, checkpointed transfer with a final report.",
  "author": {
    "name": "Xanalys"
  },
  "keywords": ["sql-server", "database", "migration", "etl", "schema-mapping"]
}
```

`plugins/db-migrate/hooks/hooks.json` (SessionStart; the first session builds the engine, hence the 600 s timeout):

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "\"${CLAUDE_PLUGIN_ROOT}/bin/dbm\" doctor --quiet",
            "timeout": 600
          }
        ]
      }
    ]
  }
}
```

`.gitattributes`:

```gitattributes
# Launchers must keep their native line endings on every platform.
plugins/db-migrate/bin/dbm text eol=lf
*.sh text eol=lf
*.cmd text eol=crlf
```

- [ ] **Step 6: Create the launchers**

`plugins/db-migrate/bin/dbm` (POSIX sh; used on macOS/Linux and by Git Bash — which is also what Claude Code's Bash tool uses on Windows). The build output goes to stderr so stdout stays one JSON line:

```sh
#!/bin/sh
# db-migrate launcher (macOS, Linux, Git Bash on Windows).
# Runs engine/dist/Dbm.dll with the installed .NET runtime; builds it once when missing (needs the .NET SDK).
# Exit codes: 3 = runtime missing, 4 = engine not built / build failed; otherwise the engine's own exit code.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(dirname -- "$here")
engine="$root/engine"
dll="$engine/dist/Dbm.dll"

install_help() {
  echo "Install the ASP.NET Core Runtime 8 (or later), then open a new terminal:" >&2
  case "$(uname -s 2>/dev/null || echo unknown)" in
    Darwin) echo "  macOS:   brew install --cask dotnet" >&2 ;;
    Linux) echo "  Linux:   install package aspnetcore-runtime-8.0 (apt/dnf) - see https://learn.microsoft.com/dotnet/core/install/linux" >&2 ;;
    MINGW* | MSYS* | CYGWIN*) echo "  Windows: winget install Microsoft.DotNet.AspNetCore.8" >&2 ;;
    *) echo "  See https://dotnet.microsoft.com/download/dotnet/8.0" >&2 ;;
  esac
}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dbm: 'dotnet' was not found on PATH." >&2
  install_help
  exit 3
fi

aspnet_major=$(dotnet --list-runtimes 2>/dev/null |
  awk '$1 == "Microsoft.AspNetCore.App" { split($2, v, "."); if (v[1] + 0 > m) m = v[1] + 0 } END { print m + 0 }')
if [ "$aspnet_major" -lt 8 ]; then
  echo "dbm: the ASP.NET Core Runtime 8 or later is required (found: ${aspnet_major:-none})." >&2
  install_help
  exit 3
fi

if [ "${1:-}" = "doctor" ]; then
  for arg in "$@"; do
    if [ "$arg" = "--rebuild" ]; then DBM_REBUILD=1; fi
  done
fi

if [ ! -f "$dll" ] || [ "${DBM_REBUILD:-}" = "1" ]; then
  if dotnet --list-sdks 2>/dev/null | grep -q .; then
    echo "dbm: building the engine (first run, about a minute)..." >&2
    if ! dotnet publish "$engine/Dbm/Dbm.csproj" -c Release -o "$engine/dist" --nologo -v q >&2; then
      echo "dbm: engine build failed (see the messages above)." >&2
      exit 4
    fi
  else
    echo "dbm: engine not built ($dll is missing) and no .NET SDK is installed to build it." >&2
    echo "     Install the .NET 8 SDK or use a release of the plugin that ships engine/dist." >&2
    exit 4
  fi
fi

exec dotnet "$dll" "$@"
```

`plugins/db-migrate/bin/dbm.cmd` (cmd/PowerShell). Delayed expansion stays off so `!` in arguments survives; the version scan uses a `call :consider` subroutine instead:

```bat
@echo off
rem db-migrate launcher (Windows cmd / PowerShell).
rem Runs engine\dist\Dbm.dll with the installed .NET runtime; builds it once when missing (needs the .NET SDK).
rem Exit codes: 3 = runtime missing, 4 = engine not built / build failed; otherwise the engine's own exit code.
setlocal EnableExtensions DisableDelayedExpansion

set "DBM_ENGINE=%~dp0..\engine"
set "DBM_DLL=%DBM_ENGINE%\dist\Dbm.dll"

where dotnet >nul 2>nul
if errorlevel 1 (
  >&2 echo dbm: 'dotnet' was not found on PATH.
  >&2 echo Install the ASP.NET Core Runtime 8 or later, then open a new terminal:
  >&2 echo   winget install Microsoft.DotNet.AspNetCore.8
  exit /b 3
)

set "DBM_ASPNET_MAJOR=0"
for /f "tokens=2" %%v in ('dotnet --list-runtimes 2^>nul ^| findstr /b /c:"Microsoft.AspNetCore.App "') do call :consider %%v
if %DBM_ASPNET_MAJOR% LSS 8 (
  >&2 echo dbm: the ASP.NET Core Runtime 8 or later is required - found major version %DBM_ASPNET_MAJOR%.
  >&2 echo Install it, then open a new terminal:
  >&2 echo   Windows: winget install Microsoft.DotNet.AspNetCore.8
  >&2 echo   macOS:   brew install --cask dotnet
  >&2 echo   Linux:   https://learn.microsoft.com/dotnet/core/install/linux
  exit /b 3
)

if /i "%~1"=="doctor" for %%a in (%*) do if /i "%%~a"=="--rebuild" set "DBM_REBUILD=1"

set "DBM_NEED_BUILD="
if not exist "%DBM_DLL%" set "DBM_NEED_BUILD=1"
if "%DBM_REBUILD%"=="1" set "DBM_NEED_BUILD=1"
if not defined DBM_NEED_BUILD goto run

dotnet --list-sdks 2>nul | findstr /r "." >nul
if errorlevel 1 (
  >&2 echo dbm: engine not built - %DBM_DLL% is missing and no .NET SDK is installed to build it.
  >&2 echo      Install the .NET 8 SDK or use a release of the plugin that ships engine\dist.
  exit /b 4
)
>&2 echo dbm: building the engine - first run, about a minute...
dotnet publish "%DBM_ENGINE%\Dbm\Dbm.csproj" -c Release -o "%DBM_ENGINE%\dist" --nologo -v q 1>&2
if errorlevel 1 (
  >&2 echo dbm: engine build failed - see the messages above.
  exit /b 4
)

:run
dotnet "%DBM_DLL%" %*
exit /b %ERRORLEVEL%

:consider
for /f "tokens=1 delims=." %%m in ("%~1") do if %%m GTR %DBM_ASPNET_MAJOR% set "DBM_ASPNET_MAJOR=%%m"
exit /b 0
```

`cmd.exe` mis-parses labels (`:run`, `:consider`) in LF-only files, and `.gitattributes` only converts on checkout, so convert the new file now and make the sh launcher executable (Git Bash):

```bash
sed -i 's/\r*$/\r/' plugins/db-migrate/bin/dbm.cmd
chmod +x plugins/db-migrate/bin/dbm
file plugins/db-migrate/bin/dbm plugins/db-migrate/bin/dbm.cmd
```

Expected: `dbm: POSIX shell script, ASCII text executable` and `dbm.cmd: DOS batch file, ASCII text, with CRLF line terminators`.

- [ ] **Step 7: Manual verification of launchers, doctor and manifests**

`engine/dist/` is build output (T6.2 decides how it is committed); never `git add -A` it.

1. Git Bash, first run builds the engine (≈ 10–60 s):
   ```bash
   rm -rf plugins/db-migrate/engine/dist
   plugins/db-migrate/bin/dbm version
   ```
   Expected: stderr `dbm: building the engine (first run, about a minute)...`, then stdout `{"version":"0.1.0","runtime":".NET 8.0.<patch>"}`; `plugins/db-migrate/engine/dist/Dbm.dll` exists.
2. PowerShell:
   ```powershell
   plugins/db-migrate/bin/dbm.cmd version; $LASTEXITCODE
   plugins/db-migrate/bin/dbm.cmd doctor --quiet; $LASTEXITCODE
   plugins/db-migrate/bin/dbm.cmd doctor
   ```
   Expected: the version JSON and `0`; nothing and `0`; `{"ok":true,"checks":[{"name":"runtime",…},{"name":"aspnetcore","ok":true,"detail":"8.0.<patch>"},{"name":"sqlclient","ok":true,"detail":"7.0.3"},{"name":"sqlite","ok":true,"detail":"3.<x>"},{"name":"home","ok":true,"detail":"C:\\Users\\<you>\\.dbmigrate"}]}`.
3. Missing runtime guidance (Git Bash, then PowerShell):
   ```bash
   PATH=/usr/bin:/bin plugins/db-migrate/bin/dbm version; echo "exit $?"
   ```
   ```powershell
   $env:PATH = 'C:\Windows\System32'; plugins/db-migrate/bin/dbm.cmd version; "exit $LASTEXITCODE"
   ```
   Expected: `dbm: 'dotnet' was not found on PATH.` plus the `winget install Microsoft.DotNet.AspNetCore.8` line, `exit 3` (open a new PowerShell afterwards to restore PATH).
4. Rebuild path: `DBM_REBUILD=1 plugins/db-migrate/bin/dbm version` (Git Bash) prints the build message again.
5. Manifests (the `claude plugin validate` command exists in current Claude Code):
   ```bash
   claude plugin validate .
   claude plugin validate plugins/db-migrate
   ```
   Expected: `✔ Validation passed` for both.

- [ ] **Step 8: Commit**

```bash
git add .claude-plugin/marketplace.json .gitattributes plugins/db-migrate/.claude-plugin plugins/db-migrate/bin plugins/db-migrate/hooks plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git update-index --chmod=+x plugins/db-migrate/bin/dbm
git commit -m "feat(plugin): marketplace, plugin manifest, launchers, SessionStart doctor" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

## Contract notes

- **Moved files:** `Dbm/Core/Json.cs` is created in T0.1 (not T1.1) because `Output` serialises with it; `Dbm/Core/UserHome.cs` is created in T0.2 (not T1.1) because `doctor` checks it. T1.1 does not recreate them.
- **Added members:** `Args.BooleanFlags` (flags that never consume the next token: `quiet rebuild dry-run no-browser json human inline help force detached`; later milestones add theirs); `Output.Write(ctx, payload, exitCode)`; `CliFailure(code, message)` exception handled by `CliApp`; internal `CliApp.ExtractWorkspace` / `CliApp.Match` (test seams); `DoctorCommand.RunChecks()` and record `DoctorCommand.Check(Name, Ok, Detail)`.
- **`Json.Options`:** adds `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (consequence for T2.8 `WebExport`: when inlining JSON into `<script>`, replace `</` with `<\/`). It sets **no** `DictionaryKeyPolicy`, so dictionary keys such as `"dbo.CUST_NM"`, `"FirstName"`, `"T01"` round-trip exactly (pinned by `JsonTests.Dictionary_keys_are_never_renamed`).
- **Shapes other milestones rely on (kept):** `dbm version` → `{"version":<assembly informational version>,"runtime":…}`; `DoctorCommand` returns early for `--quiet` and otherwise ends with `return Output.Ok(ctx, new { ok, checks })` (an unhealthy report is written with exit code 1 just before that line); the global `--workspace <dir>` is accepted anywhere in argv, including after two-word commands (`dbm map auto --workspace D:\x`, pinned by `CliAppTests.Workspace_is_extracted_after_two_word_commands`).
- **Files not in the overview file map:** `plugins/db-migrate/engine/Dbm/Cli/CliFailure.cs`, `plugins/db-migrate/engine/Dbm.Tests/Support/TestEnvironment.cs`.
- **Solution format:** `dotnet new sln -n Dbm --format sln` needs .NET SDK ≥ 9.0.200 (the dev box has 10.0.301); with SDK 8 omit `--format sln`.
- **Launcher exit codes:** 3 = runtime missing, 4 = engine not built / build failed. `doctor --rebuild` is implemented by the launchers (they set `DBM_REBUILD=1` when the first argument is `doctor`); the C# command only accepts the flag.
- **`engine/dist/`** stays untracked during development (the existing `.gitignore` does not ignore it); T6.2 owns committing release output.

## Self-review

- Spec §2.2 layout: marketplace at the repo root, plugin under `plugins/db-migrate` with `.claude-plugin/plugin.json`, `bin/`, `hooks/`, `engine/Dbm`, `engine/Dbm.Tests` — all created here (skills/agents come in M1+).
- Spec §2.3 dependencies: exactly the three runtime packages + Web SDK; xUnit test-only packages at the pinned versions.
- Spec §2.4 first run: launchers run `engine/dist/Dbm.dll`, build it once with the SDK when missing or when `DBM_REBUILD=1`, print per-OS install guidance for a missing ASP.NET Core runtime; SessionStart runs `dbm doctor --quiet` (silent when healthy).
- Spec §6 CLI conventions: single-line JSON on stdout, `{"error","message"}` + exit 1 on failure, `help` as text; global `--workspace` accepted anywhere; longest two-word command match.
- M0 "done when": solution builds, `dbm doctor` works, plugin validates locally. Local install through `/plugin marketplace add` is exercised in T6.4.
