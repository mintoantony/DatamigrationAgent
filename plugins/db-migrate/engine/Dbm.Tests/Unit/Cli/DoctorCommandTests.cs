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

    [Fact]
    public void Quiet_report_prints_one_line_per_failing_check_and_nothing_else()
    {
        var checks = new DoctorCommand.Check[]
        {
            new("runtime", true, ".NET 8.0.28"),
            new("protector", false, "no key store"),
            new("home", true, "C:\\Users\\me\\.dbmigrate"),
            new("dist", false, "engine/dist is 0.3.0 but plugin.json is 0.4.0"),
        };

        var (exit, lines) = DoctorCommand.QuietReport(checks);

        Assert.True(exit != 0, "doctor --quiet exited 0 although two checks failed");
        Assert.Equal(
            new[] { "dbm doctor: protector: no key store", "dbm doctor: dist: engine/dist is 0.3.0 but plugin.json is 0.4.0" },
            lines);
    }

    [Fact]
    public void Quiet_report_is_empty_and_exits_0_when_every_check_passes()
    {
        var (exit, lines) = DoctorCommand.QuietReport([new("runtime", true, "ok"), new("dist", true, "ok")]);

        Assert.Equal(0, exit);
        Assert.Empty(lines);
    }
}
