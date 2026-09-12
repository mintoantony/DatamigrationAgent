using System.Runtime.CompilerServices;

namespace Dbm.Tests.Support;

/// <summary>Runs once when the test assembly loads: isolates every test run from the real user profile.</summary>
internal static class TestEnvironment
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), $"dbm-tests-home-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("DBM_HOME", Home);
        Environment.SetEnvironmentVariable("DBM_WORKSPACE", null);
        Environment.SetEnvironmentVariable("DBM_NO_BROWSER", "1");   // honoured by ServerControl.OpenBrowser (T1.7)
    }
}
