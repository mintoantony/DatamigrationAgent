using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public sealed class WebExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbm-www-" + Guid.NewGuid().ToString("N"));

    public WebExportTests()
    {
        Write("index.html", """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>db-migrate</title>
              <link rel="stylesheet" href="css/app.css">
            </head>
            <body>
              <div id="app"></div>
              <script src="js/lib/dom.js"></script>
              <script src="js/api.js"></script>
              <script src="js/app.js"></script>
            </body>
            </html>
            """);
        Write("css/app.css", ":root { --accent: #4f46e5; }");
        Write("js/lib/zz.js", "/*zz*/");
        Write("js/lib/graph.js", "/*graph*/");
        Write("js/lib/dom.js", "/*dom*/");
        Write("js/components/review.js", "/*review*/");
        Write("js/components/core.js", "/*core*/");
        Write("js/api.js", "/*api*/");
        Write("js/views/analysis.js", "/*analysis*/ var s = '</script>';");
        Write("js/app.js", "/*app*/");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    [Fact]
    public void Script_order_follows_the_ui_conventions()
    {
        Assert.Equal(
            new[] { "js/lib/dom.js", "js/lib/graph.js", "js/lib/zz.js", "js/components/core.js", "js/components/review.js", "js/views/analysis.js", "js/app.js" },
            WebExport.ScriptOrder(new DirectoryAssets(_root), "analysis"));
        Assert.Throws<ExportException>(() => WebExport.ScriptOrder(new DirectoryAssets(_root), "mapping"));
    }

    [Fact]
    public void BuildHtml_inlines_assets_and_the_export_payload()
    {
        var html = WebExport.BuildHtml(new DirectoryAssets(_root), "analysis", "Demo <1> — Analysis v1",
            new JsonObject { ["note"] = "<b>bold</b>" });

        Assert.DoesNotContain("src=\"", html);
        Assert.DoesNotContain("rel=\"stylesheet\"", html);
        Assert.Contains("<style>\n:root { --accent: #4f46e5; }\n</style>", html);
        Assert.Contains("<title>Demo &lt;1&gt; — Analysis v1</title>", html);
        Assert.Contains("window.DBM_EXPORT = {\"view\":\"analysis\"", html);
        Assert.DoesNotContain("<b>bold</b>", html);
        Assert.DoesNotContain("/*api*/", html);
        Assert.Contains("var s = '<\\/script>';", html);
        Assert.Contains("<div id=\"app\"></div>", html);
        var order = new[] { "window.DBM_EXPORT", "/*dom*/", "/*graph*/", "/*zz*/", "/*core*/", "/*review*/", "/*analysis*/", "/*app*/", "</body>" }
            .Select(marker => html.IndexOf(marker, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void Analysis_export_contains_the_current_version_and_both_catalogs()
    {
        using var project = TempProject.Create("demo");
        var services = project.Services;
        services.Catalog.Save(Side.Src, TestCatalogs.LegacyShop(), "a");
        services.Catalog.Save(Side.Tgt, TestCatalogs.ShopV2(), "b");
        var payload = Analyzer.Analyze(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2());
        services.Artifacts.Add(PhaseName.Analysis, 0, Json.Serialize(payload), "script", Analyzer.Summary(payload));
        services.Phases.SetCurrentVersion(PhaseName.Analysis, 0);

        var file = WebExport.Analysis(services, new DirectoryAssets(_root));
        var html = Encoding.UTF8.GetString(file.Content);

        Assert.Equal("analysis-v0.html", file.FileName);
        Assert.StartsWith("text/html", file.ContentType);
        Assert.Contains("<title>demo — Analysis v0</title>", html);
        Assert.Contains("\"catalog\":{\"src\":[", html);
        Assert.Contains("\"app.Customers\":{\"side\":\"tgt\"", html);
        Assert.Contains("\"refs\":[\"dbo.CUST\"]", html);

        var saved = WebExport.SaveCopy(services.Ws, file);
        Assert.Equal(Path.Combine(services.Ws.ExportsDir, "analysis-v0.html"), saved);
        Assert.True(File.Exists(saved));
    }
}
