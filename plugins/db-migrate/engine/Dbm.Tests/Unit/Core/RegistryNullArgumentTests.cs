using Dbm.Core;

namespace Dbm.Tests.Unit.Core;

public class RegistryNullArgumentTests
{
    /// <summary>
    /// Open item 3.2: <c>ModuleRegistry.Create</c> is an iterator method, so its old
    /// <c>if (s is null) yield break;</c> guard never runs until the sequence is enumerated - the exception is
    /// deferred and, on the "no module registered" caller's happy path, never observed at all: zero modules
    /// register silently. <b>Harm:</b> an operator sees "No module is registered for phase X" instead of the real
    /// null-argument fault (this already cost a round this session, per the open item).
    /// </summary>
    [Fact]
    public void ModuleRegistry_Create_throws_immediately_for_a_null_services_argument()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ModuleRegistry.Create(null!));
        Assert.Equal("s", ex.ParamName);
    }

    [Fact]
    public void JobRegistry_Create_throws_immediately_for_a_null_services_argument()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => JobRegistry.Create(null!));
        Assert.Equal("s", ex.ParamName);
    }
}
