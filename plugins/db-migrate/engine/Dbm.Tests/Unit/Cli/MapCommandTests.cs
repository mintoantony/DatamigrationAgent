using Dbm.Cli;
using Dbm.Cli.Commands;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Cli;

public class MapCommandTests
{
    [Fact]
    public void Command_is_registered()
    {
        Assert.Contains(CommandRegistry.All(), c => c.Name == "map auto" && c is MapAutoCommand);
    }

    [Fact]
    public async Task Map_auto_outside_running_or_drafting_fails_with_wrong_phase()
    {
        using var project = TempProject.Create();

        var result = await CliRunner.RunAsync(project.Ws, null, "map", "auto");

        Assert.Equal(1, result.Exit);
        Assert.Equal("wrong_phase", (string?)result.Json["error"]);
        Assert.Contains("pending", (string?)result.Json["message"]);
    }

    [Fact]
    public async Task Map_auto_without_a_project_fails_with_no_project()
    {
        using var workspace = new TestWorkspace();

        var result = await CliRunner.RunAsync(workspace.Ws, null, "map", "auto");

        Assert.Equal(1, result.Exit);
        Assert.Equal("no_project", (string?)result.Json["error"]);
    }
}
