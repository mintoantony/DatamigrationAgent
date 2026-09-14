using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Analysis;

public sealed class AnalysisModuleTests : IDisposable
{
    private readonly TempProject _project = TempProject.Create();
    private readonly AnalysisModule _module = new();
    private static readonly Dictionary<string, long> TwoOrphans = new() { [RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")] = 2 };

    public AnalysisModuleTests()
    {
        _project.Services.Catalog.Save(Side.Src, TestCatalogs.LegacyShop(), "src-fp");
        _project.Services.Catalog.Save(Side.Tgt, TestCatalogs.ShopV2(), "tgt-fp");
    }

    public void Dispose() => _project.Dispose();

    private static AnalysisPayload Draft() => Analyzer.Analyze(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), TwoOrphans);

    private ModuleContext Ctx(AnalysisPayload payload, params FeedbackRow[] feedback) => new()
    {
        Services = _project.Services,
        Current = new ArtifactRow(1, PhaseName.Analysis, 0, Json.Serialize(payload), "script", null, DateTimeOffset.UnixEpoch),
        OpenFeedback = feedback,
    };

    private static FeedbackRow Feedback(long id, string? anchor, string text) =>
        new(id, PhaseName.Analysis, 1, anchor, text, FeedbackStatus.Open, null, null, DateTimeOffset.UnixEpoch);

    private static JsonObject WithNarrative(AnalysisPayload payload, string summary = "LegacyShop moves 19,711 rows into ShopV2; two orphan orders and a heap need attention.")
    {
        var doc = (JsonObject)Json.ToNode(payload);
        doc["narrative"] = new JsonObject
        {
            ["summary"] = summary,
            ["risks"] = new JsonArray("2 orphan orders will be rejected by FK_Orders_Customers."),
            ["recommendations"] = new JsonArray("Decide whether to drop or re-parent the orphan orders."),
        };
        return doc;
    }

    [Fact]
    public void Identity_and_needs_agent()
    {
        Assert.Equal((PhaseName.Analysis, "schema-analyst", "analyze"), (_module.Phase, _module.Agent, _module.JobKind));
        Assert.True(_module.NeedsAgent(Json.ToNode(Draft())));
        Assert.False(_module.NeedsAgent(WithNarrative(Draft())));
    }

    [Fact]
    public void Draft_packet_has_digest_sections()
    {
        var data = _module.BuildPacket(Ctx(Draft()), PacketMode.Draft).AsObject();
        Assert.Equal(new[] { "legend", "source", "target", "estimates", "totals", "detail", "brief", "info", "largestSourceTables", "targetTables", "targetTablesTotal", "hint" },
            data.Select(p => p.Key));
        Assert.Equal(2, (int)data["totals"]!["high"]!);
        Assert.All(data["detail"]!.AsArray(), d => Assert.Equal("high", (string?)d!["sev"]));
        Assert.Contains(data["detail"]!.AsArray(), d => (string?)d!["rule"] == "R09" && (long)d!["n"]! == 2);
        Assert.Equal("dbo.ORD_LINE", (string?)data["largestSourceTables"]![0]!["key"]);
        Assert.Equal(6, (int)data["targetTablesTotal"]!);
        Assert.Equal(AnalysisModule.Hint, (string?)data["hint"]);
    }

    [Fact]
    public void Draft_packet_caps_medium_and_low_lists()
    {
        var payload = Draft();
        for (var i = 0; i < 70; i++)
            payload.Findings[$"L{i:000}"] = new Finding { Rule = "R10", Title = "t", Severity = Severity.Low, Side = "src", Object = $"dbo.T.C{i}", Message = "m" };
        var brief = _module.BuildPacket(Ctx(payload), PacketMode.Draft)["brief"]!;
        var lowTotal = payload.Findings.Values.Count(f => f.Severity == Severity.Low);
        Assert.Equal(lowTotal, (int)brief["low"]!["total"]!);
        Assert.Equal(AnalysisModule.BriefCap, brief["low"]!["items"]!.AsArray().Count);
    }

    [Fact]
    public void Rework_packet_resolves_feedback_anchors()
    {
        var payload = Json.FromNode<AnalysisPayload>(WithNarrative(Draft()));
        var data = _module.BuildPacket(Ctx(payload,
            Feedback(11, "finding:F001", "why high?"),
            Feedback(12, "table:src:dbo.CUST", "PII?"),
            Feedback(13, "column:src:dbo.CUST.EMAIL_ADDR", "normalise"),
            Feedback(14, "narrative", "shorter"),
            Feedback(15, null, "general")), PacketMode.Rework).AsObject();

        Assert.NotNull(data["currentNarrative"]);
        var context = data["feedbackContext"]!.AsArray();
        Assert.Equal(5, context.Count);
        Assert.Equal("dbo.AUDIT_LOG", (string?)context[0]!["context"]!["obj"]);
        Assert.StartsWith("src table dbo.CUST rows=1000", (string?)context[1]!["context"]);
        Assert.Contains("EMAIL_ADDR varchar(120)", (string?)context[2]!["context"]);
        Assert.NotNull(context[3]!["context"]!["summary"]);
        Assert.Null(context[4]!["context"]);
    }

    [Fact]
    public void Validate_accepts_narrative_and_commentary()
    {
        var draft = Draft();
        var doc = WithNarrative(draft);
        var r09 = draft.Findings.First(f => f.Value.Rule == "R09").Key;
        doc["findings"]![r09]!["commentary"] = "Orphans come from deleted customers; filter or re-parent.";
        var check = _module.Validate(Ctx(draft), doc);
        Assert.True(check.Ok, string.Join("; ", check.Errors));
    }

    [Fact]
    public void Validate_rejects_bad_narratives()
    {
        var draft = Draft();
        Assert.Contains(_module.Validate(Ctx(draft), Json.ToNode(draft)).Errors, e => e.StartsWith("/narrative is required", StringComparison.Ordinal));
        Assert.Contains(_module.Validate(Ctx(draft), WithNarrative(draft, "Too short.")).Errors, e => e.Contains("at least 40 characters"));
        var noRecs = WithNarrative(draft);
        noRecs["narrative"]!["recommendations"] = new JsonArray();
        Assert.Contains(_module.Validate(Ctx(draft), noRecs).Errors, e => e.Contains("recommendations needs at least one item"));
    }

    [Fact]
    public void Validate_rejects_changes_outside_narrative_and_commentary()
    {
        var draft = Draft();
        var unknown = WithNarrative(draft);
        unknown["findings"]!["F999"] = new JsonObject { ["commentary"] = "x" };
        Assert.Contains(_module.Validate(Ctx(draft), unknown).Errors, e => e.Contains("unknown finding 'F999'"));

        var estimates = WithNarrative(draft);
        estimates["estimates"]!["estimatedMinutes"] = 99;
        Assert.Contains(_module.Validate(Ctx(draft), estimates).Errors, e => e.StartsWith("/estimates must not be changed", StringComparison.Ordinal));

        var message = WithNarrative(draft);
        message["findings"]!["F001"]!["message"] = "rewritten";
        Assert.Contains(_module.Validate(Ctx(draft), message).Errors, e => e == "/findings/F001: only commentary may change");

        var nonString = WithNarrative(draft);
        nonString["findings"]!["F001"]!["commentary"] = 5;
        Assert.Contains(_module.Validate(Ctx(draft), nonString).Errors, e => e == "/findings/F001/commentary must be a string");
    }

    [Fact]
    public void Blockers_and_summary()
    {
        var draft = Draft();
        Assert.Single(_module.ApprovalBlockers(Ctx(draft), Json.ToNode(draft)));
        Assert.Empty(_module.ApprovalBlockers(Ctx(draft), WithNarrative(draft)));
        Assert.EndsWith("(0 critical, 2 high) · narrative pending", _module.Summarize(Json.ToNode(draft)));
        Assert.EndsWith("· narrative ready", _module.Summarize(WithNarrative(draft)));
    }
}
