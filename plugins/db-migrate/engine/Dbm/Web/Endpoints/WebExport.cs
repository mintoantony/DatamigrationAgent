using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dbm.Core;
using Dbm.Core.State;
using Microsoft.Extensions.FileProviders;

namespace Dbm.Web.Endpoints;

/// <summary>Read access to the UI assets (wwwroot) by '/'-separated relative path.</summary>
public interface IWebAssets
{
    string? Read(string path);
    IReadOnlyList<string> List(string directory);   // file names (not paths) of *.js files directly in the directory
}

public sealed class DirectoryAssets(string root) : IWebAssets
{
    public string Root { get; } = root;

    public string? Read(string path)
    {
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : null;
    }

    public IReadOnlyList<string> List(string directory)
    {
        var full = Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(full)
            ? Directory.GetFiles(full, "*.js").Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string>();
    }
}

public sealed class FileProviderAssets(IFileProvider provider) : IWebAssets
{
    public string? Read(string path)
    {
        var file = provider.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory) return null;
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public IReadOnlyList<string> List(string directory) =>
        provider.GetDirectoryContents(directory)
            .Where(f => !f.IsDirectory && f.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
}

public sealed class ExportException(string message) : Exception(message);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>Standalone, offline HTML exports (C9): index.html skeleton + inlined app.css, lib/*, components/*, the view and app.js.</summary>
public static class WebExport
{
    public static readonly string[] LibOrder = { "dom.js", "diff.js", "graph.js", "highlight.js" };
    public static readonly string[] ComponentOrder = { "core.js", "review.js" };

    private static readonly Regex ScriptTag = new(@"<script\b[^>]*\bsrc\s*=\s*[""'][^""']*[""'][^>]*>\s*</script>[ \t]*\r?\n?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StylesheetLink = new(@"<link\b[^>]*\brel\s*=\s*[""']stylesheet[""'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TitleTag = new(@"<title>[\s\S]*?</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // U+2028 / U+2029 are legal inside JSON strings but end a line in older JavaScript parsers.
    private static readonly string LineSeparator = ((char)0x2028).ToString();
    private static readonly string ParagraphSeparator = ((char)0x2029).ToString();

    /// <summary>The host's web root when it has index.html, else &lt;app dir&gt;/wwwroot, else the nearest ancestor's Dbm/wwwroot (dev/test runs).</summary>
    public static IWebAssets LocateAssets(IFileProvider? webRoot = null)
    {
        if (webRoot is not null && webRoot.GetFileInfo("index.html").Exists) return new FileProviderAssets(webRoot);
        var local = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(local, "index.html"))) return new DirectoryAssets(local);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Dbm", "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return new DirectoryAssets(candidate);
        }
        throw new ExportException("UI assets not found (wwwroot/index.html).");
    }

    public static IReadOnlyList<string> ScriptOrder(IWebAssets assets, string view)
    {
        var viewPath = $"js/views/{view}.js";
        if (assets.Read(viewPath) is null) throw new ExportException($"View script {viewPath} not found.");
        var scripts = new List<string>();
        scripts.AddRange(Ordered(assets.List("js/lib"), LibOrder).Select(n => "js/lib/" + n));
        scripts.AddRange(Ordered(assets.List("js/components"), ComponentOrder).Select(n => "js/components/" + n));
        scripts.Add(viewPath);
        scripts.Add("js/app.js");
        return scripts;
    }

    public static string BuildHtml(IWebAssets assets, string view, string title, JsonNode payload)
    {
        var index = assets.Read("index.html") ?? throw new ExportException("index.html not found.");
        var style = "<style>\n" + (assets.Read("css/app.css") ?? "") + "\n</style>";

        var html = ScriptTag.Replace(index, "");
        var styled = false;
        html = StylesheetLink.Replace(html, _ =>
        {
            if (styled) return "";
            styled = true;
            return style;
        });
        if (!styled) html = InsertBefore(html, "</head>", style + "\n");
        var titleTag = $"<title>{WebUtility.HtmlEncode(title)}</title>";
        html = TitleTag.IsMatch(html) ? TitleTag.Replace(html, _ => titleTag, 1) : InsertBefore(html, "</head>", titleTag + "\n");

        var scripts = new StringBuilder();
        var export = new JsonObject { ["view"] = view, ["title"] = title, ["payload"] = payload.DeepClone() };
        scripts.Append("<script>window.DBM_EXPORT = ").Append(SafeJson(export.ToJsonString(Json.Options))).Append(";</script>\n");
        foreach (var path in ScriptOrder(assets, view))
        {
            var js = assets.Read(path) ?? throw new ExportException($"{path} not found.");
            scripts.Append("<script>\n").Append(SafeScript(js)).Append("\n</script>\n");
        }
        return InsertBefore(html, "</body>", scripts.ToString());
    }

    /// <summary>Export of the Analysis phase's current version (payload + both catalogs, so the drill-down works offline).</summary>
    public static ExportFile Analysis(DbmServices services, IWebAssets assets)
    {
        var current = services.Phases.Get(PhaseName.Analysis).CurrentVersion;
        var artifact = (current is { } v ? services.Artifacts.Get(PhaseName.Analysis, v) : null)
            ?? services.Artifacts.Latest(PhaseName.Analysis)
            ?? throw new ExportException("No analysis artifact yet.");
        var title = $"{services.Project.Get().Name} — Analysis v{artifact.Version}";
        var html = BuildHtml(assets, "analysis", title, AnalysisData(services, artifact));
        return new ExportFile($"analysis-v{artifact.Version}.html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    }

    /// <summary>{project, exportedAt, artifact:{version,author,summary,createdAt,payload}, catalog:{src,tgt}, tables:{src:{key:TableDetail},tgt:{…}}}.</summary>
    public static JsonObject AnalysisData(DbmServices services, ArtifactRow artifact)
    {
        var catalog = new JsonObject();
        var tables = new JsonObject();
        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            var text = EnumText.ToText(side);
            var snapshot = services.Catalog.Get(side);
            catalog[text] = Json.ToNode(snapshot is null ? new List<TableListItem>() : CatalogEndpoints.ListTables(snapshot));
            var details = new JsonObject();
            foreach (var table in snapshot?.Tables ?? new List<Dbm.Core.Catalog.TableInfo>())
                details[table.Key] = Json.ToNode(CatalogEndpoints.Detail(side, snapshot!, table.Key));
            tables[text] = details;
        }
        return new JsonObject
        {
            ["project"] = services.Project.Get().Name,
            ["exportedAt"] = Clock.NowText(),
            ["artifact"] = new JsonObject
            {
                ["version"] = artifact.Version,
                ["author"] = artifact.Author,
                ["summary"] = artifact.Summary,
                ["createdAt"] = artifact.CreatedAt.ToString("O"),
                ["payload"] = JsonNode.Parse(artifact.PayloadJson),
            },
            ["catalog"] = catalog,
            ["tables"] = tables,
        };
    }

    public static string SaveCopy(Workspace ws, ExportFile file)
    {
        Directory.CreateDirectory(ws.ExportsDir);
        var path = Path.Combine(ws.ExportsDir, file.FileName);
        File.WriteAllBytes(path, file.Content);
        return path;
    }

    private static IEnumerable<string> Ordered(IReadOnlyList<string> names, string[] preferred) =>
        preferred.Where(p => names.Contains(p, StringComparer.Ordinal))
            .Concat(names.Where(n => !preferred.Contains(n, StringComparer.Ordinal)).OrderBy(n => n, StringComparer.Ordinal));

    private static string InsertBefore(string html, string marker, string text)
    {
        var i = html.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html + text : html.Insert(i, text);
    }

    private static string SafeScript(string js) => js.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string SafeJson(string json) =>
        json.Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(LineSeparator, "\\u2028", StringComparison.Ordinal)
            .Replace(ParagraphSeparator, "\\u2029", StringComparison.Ordinal);
}
