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
