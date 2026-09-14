namespace Dbm.Web;

/// <summary>How to start this same engine again (used to spawn the detached server).</summary>
public static class SelfCommand
{
    public static (string FileName, IReadOnlyList<string> PrefixArgs) Get()
    {
        var dll = typeof(SelfCommand).Assembly.Location;
        var process = Environment.ProcessPath;
        var name = process is null ? "" : Path.GetFileNameWithoutExtension(process);

        // `dotnet Dbm.dll …` (the launchers) → same muxer, same dll.
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return (process!, [dll]);

        // Apphost (Dbm.exe / ./Dbm) → run it directly.
        if (name.Equals("Dbm", StringComparison.OrdinalIgnoreCase)) return (process!, []);

        // Hosted by something else (e.g. the test runner): run the dll with the dotnet muxer.
        var muxer = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return (string.IsNullOrEmpty(muxer) ? "dotnet" : muxer, [dll]);
    }
}
