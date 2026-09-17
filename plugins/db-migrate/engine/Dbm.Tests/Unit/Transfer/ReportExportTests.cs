using System.Text;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;
using Dbm.Web.Endpoints;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class ReportExportTests
{
    [Fact]
    public async Task Report_export_is_unavailable_before_completion_and_a_standalone_page_afterwards()
    {
        using var svc = new XferServices();
        var s = svc.Services;
        var broadcaster = new Broadcaster(s.Events);
        var state = new WebState
        {
            Services = s, Broadcaster = broadcaster, Presence = new AgentPresence(s, broadcaster), Info = new ServerInfo(1, 1, "token", Clock.Now()),
        };
        var assets = WebExport.LocateAssets();
        Assert.Null(ReportExport.LatestPayload(s));
        await Assert.ThrowsAsync<ExportException>(() => ReportExport.BuildAsync(state, assets, default));

        s.Artifacts.Add(PhaseName.Complete, 1, "{\"runId\":4,\"status\":\"completed\"}", "script", "done");
        Assert.Equal(4, ReportExport.LatestPayload(s)!["runId"]!.GetValue<int>());
        var file = await ReportExport.BuildAsync(state, assets, default);
        Assert.Equal(ReportExport.FileName, file.FileName);
        string html = Encoding.UTF8.GetString(file.Content);
        Assert.Contains("window.DBM_EXPORT", html);
        Assert.Contains("\"view\":\"report\"", html);
        Assert.Contains("\"artifact\":{\"version\":1", html);
        Assert.Contains("DBM.views.complete = view", html);   // views/report.js inlined
        Assert.Contains("DBM.xfer = X", html);               // lib/xfer.js inlined (all lib/*)
    }
}
