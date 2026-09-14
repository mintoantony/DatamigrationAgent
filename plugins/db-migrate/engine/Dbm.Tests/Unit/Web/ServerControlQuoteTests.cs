using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class ServerControlQuoteTests
{
    [Theory]
    [InlineData(@"C:\Work\proj", @"C:\Work\proj")]
    [InlineData(@"C:\My Projects\shop", "\"C:\\My Projects\\shop\"")]
    [InlineData(@"C:\My Projects\", "\"C:\\My Projects\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Windows_arguments_follow_command_line_to_argv_rules(string arg, string expected)
    {
        Assert.Equal(expected, ServerControl.WindowsQuote(arg));
    }

    [Theory]
    [InlineData("/home/a b/proj", "'/home/a b/proj'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    public void Posix_arguments_are_single_quoted(string arg, string expected)
    {
        Assert.Equal(expected, ServerControl.PosixQuote(arg));
    }
}
