using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class TokenGuardTests
{
    [Theory]
    [InlineData("127.0.0.1:5000", true)]
    [InlineData("localhost:5000", true)]
    [InlineData("LOCALHOST:5000", true)]
    [InlineData("127.0.0.1:5001", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("evil.example:5000", false)]
    [InlineData("", false)]
    public void Only_loopback_hosts_on_our_port_are_allowed(string host, bool allowed)
    {
        Assert.Equal(allowed, TokenGuard.HostAllowed(host, 5000));
    }

    [Theory]
    [InlineData("tok", "", true)]
    [InlineData("", "tok", true)]
    [InlineData("bad", "tok", false)]
    [InlineData("", "", false)]
    [InlineData("tok2", "", false)]
    public void Token_comes_from_the_header_or_the_query(string header, string query, bool ok)
    {
        Assert.Equal(ok, TokenGuard.TokenMatches(header, query, "tok"));
    }

    [Fact]
    public void Server_info_urls()
    {
        var info = new ServerInfo(5123, 42, "abc", DateTimeOffset.UnixEpoch);

        Assert.Equal("http://127.0.0.1:5123", info.BaseUrl);
        Assert.Equal("http://127.0.0.1:5123/?t=abc", info.UiUrl);
    }

    [Fact]
    public void Self_command_runs_the_engine_dll_with_dotnet_when_hosted_elsewhere()
    {
        var (file, prefix) = SelfCommand.Get();

        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(file), ignoreCase: true);
        Assert.EndsWith("Dbm.dll", Assert.Single(prefix));
    }
}
