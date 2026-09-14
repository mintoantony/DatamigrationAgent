using Dbm.Core;

namespace Dbm.Web;

/// <summary>Tiny append-only server log (.dbmigrate/server.log). Never pass connection strings to it.</summary>
public sealed class FileLog(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Write(string level, string message)
    {
        try
        {
            lock (_gate) File.AppendAllText(Path, $"{Clock.NowText()} {level.ToUpperInvariant()} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // logging must never take the server down
        }
    }
}
