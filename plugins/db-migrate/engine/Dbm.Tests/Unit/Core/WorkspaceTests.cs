using Dbm.Core;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Core;

public class WorkspaceTests
{
    [Fact]
    public void Paths_live_under_dot_dbmigrate()
    {
        using var tw = new TestWorkspace();
        var ws = tw.Ws;

        Assert.Equal(Path.Combine(tw.Root, ".dbmigrate"), ws.Dir);
        Assert.Equal(Path.Combine(ws.Dir, "state.db"), ws.StateDbPath);
        Assert.Equal(Path.Combine(ws.Dir, "server.json"), ws.ServerJsonPath);
        Assert.Equal(Path.Combine(ws.Dir, "server.log"), ws.ServerLogPath);
        Assert.Equal(Path.Combine(ws.Dir, "exports"), ws.ExportsDir);
        Assert.Equal(Path.Combine(ws.Dir, "work"), ws.WorkDir);
    }

    [Fact]
    public void EnsureCreated_makes_folders_and_a_gitignore_but_Exists_needs_state_db()
    {
        using var tw = new TestWorkspace();

        tw.Ws.EnsureCreated();

        Assert.True(Directory.Exists(tw.Ws.WorkDir));
        Assert.True(Directory.Exists(tw.Ws.ExportsDir));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(tw.Ws.Dir, ".gitignore")));
        Assert.False(tw.Ws.Exists);
        File.WriteAllText(tw.Ws.StateDbPath, "");
        Assert.True(tw.Ws.Exists);
    }

    [Fact]
    public void Relative_uses_forward_slashes()
    {
        using var tw = new TestWorkspace();

        Assert.Equal(".dbmigrate/work/a.json", tw.Ws.Relative(Path.Combine(tw.Ws.WorkDir, "a.json")));
    }

    [Fact]
    public void Resolve_prefers_the_explicit_root()
    {
        using var tw = new TestWorkspace();

        Assert.Equal(tw.Ws.Root, Workspace.Resolve(tw.Root).Root);
    }
}

[Collection(ProcessStateCollection.Name)]
public class WorkspaceResolveTests
{
    [Fact]
    public void Resolve_uses_env_then_nearest_ancestor_then_cwd()
    {
        using var project = new TestWorkspace();
        project.Ws.EnsureCreated();
        File.WriteAllText(project.Ws.StateDbPath, "");
        var nested = Directory.CreateDirectory(Path.Combine(project.Root, "a", "b")).FullName;
        using var other = new TestWorkspace();
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(nested);
            Assert.Equal(project.Ws.Root, Workspace.Resolve(null).Root);

            Environment.SetEnvironmentVariable("DBM_WORKSPACE", other.Root);
            Assert.Equal(other.Ws.Root, Workspace.Resolve(null).Root);
            Environment.SetEnvironmentVariable("DBM_WORKSPACE", null);

            Directory.SetCurrentDirectory(other.Root);
            Assert.Equal(other.Ws.Root, Workspace.Resolve(null).Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBM_WORKSPACE", null);
            Directory.SetCurrentDirectory(originalCwd);
        }
    }
}
