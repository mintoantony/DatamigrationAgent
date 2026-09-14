using System.Net;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

public class StaticFilesTests
{
    [Fact]
    public async Task Index_and_scripts_are_served_without_caching_and_without_a_token()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        using var anonymous = server.Anonymous();

        using var index = await anonymous.GetAsync("/");
        var html = await index.Content.ReadAsStringAsync();
        using var app = await anonymous.GetAsync("/js/app.js");
        using var css = await anonymous.GetAsync("/css/app.css");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("<div id=\"app\" class=\"app\">", html);
        Assert.Equal("no-store", index.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Index_loads_scripts_in_the_c9_order()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        string[] order = ["js/lib/dom.js", "js/lib/diff.js", "js/api.js", "js/components/core.js", "js/components/review.js",
            "js/views/setup.js", "js/views/pending.js", "js/app.js"];

        var positions = order.Select(s => html.IndexOf($"<script src=\"{s}\"></script>", StringComparison.Ordinal)).ToList();

        Assert.All(positions, p => Assert.True(p > 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("<!-- lib scripts:", html);
        Assert.Contains("<!-- view scripts:", html);
    }
}
