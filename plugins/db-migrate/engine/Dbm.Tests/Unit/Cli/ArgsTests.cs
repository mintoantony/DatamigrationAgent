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
