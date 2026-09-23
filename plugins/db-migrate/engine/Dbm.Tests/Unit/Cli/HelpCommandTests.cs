using System.Text.Json.Nodes;
using Dbm.Cli;

namespace Dbm.Tests.Unit.Cli;

public class HelpCommandTests
{
    /// <summary>
    /// Open item 3.1: `--json` is a recognised global flag (Args.cs) but HelpCommand always returned prose, so an
    /// agent parsing `dbm help --json` got text, not JSON. <b>Harm:</b> a caller that assumes every command honours
    /// `--json` like the rest of the CLI gets unparsable output and cannot enumerate commands programmatically.
    /// </summary>
    [Fact]
    public async Task Json_flag_emits_compact_json_listing_every_command_and_its_summary()
    {
        var sw = new StringWriter();

        var exit = await CliApp.RunAsync(["help", "--json"], sw, new StringWriter());

        Assert.Equal(0, exit);
        var text = sw.ToString();
        Assert.True(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1,
            $"dbm help --json ignored the flag and printed prose: \"{text}\"");
        var json = JsonNode.Parse(text)!;
        var commands = json["commands"]!.AsArray();
        Assert.Equal(CommandRegistry.All().Count, commands.Count);
        Assert.Contains(commands, c => c!["name"]!.GetValue<string>() == "version"
            && c["help"]!.GetValue<string>() == "Print engine and runtime versions");
    }

    [Fact]
    public async Task Without_json_the_prose_output_is_unchanged()
    {
        var sw = new StringWriter();

        var exit = await CliApp.RunAsync(["help"], sw, new StringWriter());

        Assert.Equal(0, exit);
        var text = sw.ToString();
        Assert.StartsWith("dbm — SQL Server migration engine. Global option: --workspace <dir>", text);
        Assert.DoesNotContain('{', text);
    }
}
