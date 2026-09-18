using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>
/// `dbm export &lt;analysis|sql|sqlpack|report&gt; [--out path]` — the same files the UI's Export buttons produce.
/// The project server builds them (POST /api/export/{what}), so CLI and UI exports are identical.
/// </summary>
public sealed class ExportCommand : ICommand
{
    /// <summary>The kinds the server registers (T2.8 analysis, T4.4 sql/sqlpack, T5.6 report).</summary>
    public static readonly string[] Kinds = ["analysis", "sql", "sqlpack", "report"];

    public string Name => "export";
    public string Help => "Write a standalone export: analysis, sql or report (HTML), sqlpack (zip), to .dbmigrate/exports or --out";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var what = args.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(what)) throw new CliFailure("usage", $"dbm export <{string.Join('|', Kinds)}> [--out path]");

        var ws = ctx.RequireProject();
        var info = await ServerControl.EnsureRunningAsync(ws);

        using var http = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        var route = $"/api/export/{Uri.EscapeDataString(what)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add(TokenGuard.Header, info.Token);
        using var response = await http.SendAsync(request);
        var body = await ReadJsonObjectAsync(response, $"POST {info.BaseUrl}{route}");
        if (!response.IsSuccessStatusCode)
        {
            throw new CliFailure(body?["error"]?.GetValue<string>() ?? "export_failed",
                body?["message"]?.GetValue<string>() ?? $"The export request failed with HTTP {(int)response.StatusCode}.");
        }

        var file = body!["file"]!.GetValue<string>();
        var saved = Path.Combine(ws.Root, body["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
        var path = CopyToOut(args.Opt("out"), saved, file);
        return Output.Ok(ctx, new { ok = true, what, file, path, bytes = new FileInfo(path).Length });
    }

    /// <summary>
    /// Ruling 158: an answer that is not a JSON object (a proxy's HTML page, some other process on a stale port) is
    /// `server_error` naming the method, the full URL, the HTTP status and the first 200 characters of the body.
    /// </summary>
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response, string requestLine)
    {
        var status = (int)response.StatusCode;
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new CliFailure("server_error", $"{requestLine} returned HTTP {status}, but its body could not be read: {ex.Message}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            node = null;
        }
        if (node is JsonObject body) return body;

        var head = text.Length > 200 ? text[..200] + "…" : text;
        var kind = node is null ? "a non-JSON body" : "a JSON body that is not an object";
        throw new CliFailure("server_error", $"{requestLine} returned HTTP {status} with {kind}: '{head}'.");
    }

    /// <summary>
    /// Without --out the saved copy under .dbmigrate/exports is the result. --out naming an existing directory (or ending
    /// with a separator) keeps the file name inside it; any other --out is the exact target file.
    /// </summary>
    public static string CopyToOut(string? outOption, string savedPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(outOption)) return savedPath;
        var full = Path.GetFullPath(outOption);
        var target = Directory.Exists(full) || outOption.EndsWith('/') || outOption.EndsWith('\\')
            ? Path.Combine(full, fileName)
            : full;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(savedPath, target, overwrite: true);
        return target;
    }
}
