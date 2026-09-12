namespace Dbm.Core;

/// <summary>Per-user data directory (encryption key, team synonyms). Env DBM_HOME wins; read on every access.</summary>
public static class UserHome
{
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DBM_HOME");
            var dir = !string.IsNullOrWhiteSpace(env)
                ? Path.GetFullPath(env)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbmigrate");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
