using Dbm.Core;
using Microsoft.Data.Sqlite;

namespace Dbm.Tests.Support;

/// <summary>A throw-away workspace folder under %TEMP%, deleted on Dispose.</summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbm-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Ws = new Workspace(Root);
    }

    public string Root { get; }
    public Workspace Ws { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
