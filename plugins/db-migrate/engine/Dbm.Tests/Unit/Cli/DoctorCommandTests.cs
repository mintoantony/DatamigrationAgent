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
        Assert.Equal(8, json["checks"]!.AsArray().Count);   // 5 machine checks + protector, server, dist (T6.2)
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
