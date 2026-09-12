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
