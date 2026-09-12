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
