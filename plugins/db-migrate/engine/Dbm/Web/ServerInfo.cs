using System.Text.Json.Serialization;

namespace Dbm.Web;

/// <summary>Contents of .dbmigrate/server.json — exactly {port, pid, token, startedAt} (written by the running server, deleted when it stops).</summary>
public sealed record ServerInfo(int Port, int Pid, string Token, DateTimeOffset StartedAt)
{
    [JsonIgnore] public string BaseUrl => $"http://127.0.0.1:{Port}";
    [JsonIgnore] public string UiUrl => $"{BaseUrl}/?t={Token}";
}
