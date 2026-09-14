using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Workflow;
using Microsoft.Data.Sqlite;

namespace Dbm.Tests.Support;

/// <summary>
/// A throw-away workspace folder under %TEMP%, deleted on Dispose (together with every services instance it opened).
/// Public API relied on by all milestones: Ws, Root, OpenServices(modules, jobs).
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly List<DbmServices> _opened = new();

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbm-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Ws = new Workspace(Root);
    }

    public string Root { get; }
    public Workspace Ws { get; }

    /// <summary>
    /// Opens services on this workspace (real registries unless <paramref name="modules"/>/<paramref name="jobs"/> are given);
    /// creates .dbmigrate and runs ProjectRepo.Init("test") the first time. Disposed with the workspace.
    /// </summary>
    public DbmServices OpenServices(Func<DbmServices, IEnumerable<IPhaseModule>>? modules = null,
        Func<DbmServices, IEnumerable<IJobHandler>>? jobs = null)
    {
        Ws.EnsureCreated();
        var services = DbmServices.Open(Ws, null, modules, jobs);
        if (!services.Project.Exists()) services.Project.Init("test");
        lock (_opened) _opened.Add(services);
        return services;
    }

    public void Dispose()
    {
        lock (_opened)
        {
            foreach (var s in _opened) s.Dispose();
            _opened.Clear();
        }
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
