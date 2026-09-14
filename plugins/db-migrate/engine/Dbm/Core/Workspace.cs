namespace Dbm.Core;

/// <summary>A project folder. All project state lives in Root/.dbmigrate.</summary>
public sealed class Workspace
{
    public const string DirName = ".dbmigrate";

    public Workspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Dir = Path.Combine(Root, DirName);
        StateDbPath = Path.Combine(Dir, "state.db");
        ServerJsonPath = Path.Combine(Dir, "server.json");
        ServerLockPath = Path.Combine(Dir, "server.lock");
        ServerLogPath = Path.Combine(Dir, "server.log");
        ExportsDir = Path.Combine(Dir, "exports");
        WorkDir = Path.Combine(Dir, "work");
    }

    public string Root { get; }
    public string Dir { get; }
    public string StateDbPath { get; }
    public string ServerJsonPath { get; }
    public string ServerLockPath { get; }
    public string ServerLogPath { get; }
    public string ExportsDir { get; }
    public string WorkDir { get; }

    public bool Exists => Directory.Exists(Dir) && File.Exists(StateDbPath);

    /// <summary>explicit &gt; env DBM_WORKSPACE &gt; nearest ancestor of cwd containing .dbmigrate/state.db &gt; cwd.</summary>
    public static Workspace Resolve(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return new Workspace(explicitRoot);

        var env = Environment.GetEnvironmentVariable("DBM_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(env)) return new Workspace(env);

        var cwd = Directory.GetCurrentDirectory();
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, DirName, "state.db"))) return new Workspace(dir.FullName);
        }
        return new Workspace(cwd);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(ExportsDir);
        Directory.CreateDirectory(WorkDir);
        var gitignore = Path.Combine(Dir, ".gitignore");
        if (!File.Exists(gitignore)) File.WriteAllText(gitignore, "*\n");
    }

    public string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, Path.GetFullPath(absolutePath)).Replace('\\', '/');
}
