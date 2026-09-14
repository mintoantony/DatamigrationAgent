using Dbm.Core.Analysis;
using Dbm.Core.Catalog;
using Dbm.Tests.Support;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Unit.Analysis;

public sealed class RulesTests
{
    private static readonly Dictionary<string, long> TwoOrphans = new() { [RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")] = 2 };

    private static List<Finding> Run(IRule rule, CatalogSnapshot? src = null, CatalogSnapshot? tgt = null, Dictionary<string, long>? orphans = null) =>
        rule.Evaluate(new RuleContext { Src = src ?? LegacyShop(), Tgt = tgt ?? ShopV2(), OrphanCounts = orphans ?? TwoOrphans }).ToList();

    private static CatalogSnapshot Change(CatalogSnapshot s, string key, Func<TableInfo, TableInfo> change) =>
        s with { Tables = s.Tables.Select(t => t.Key == key ? change(t) : t).ToList() };

    [Fact]
    public void Rule_ids_are_R01_to_R16_in_order() =>
        Assert.Equal(Enumerable.Range(1, 16).Select(i => $"R{i:00}"), Rules.All.Select(r => r.Id));

    [Fact]
    public void R01_no_primary_key()
    {
        var f = Run(new NoPrimaryKeyRule());
        Assert.Contains(f, x => x is { Object: "dbo.AUDIT_LOG", Severity: Severity.High, Side: "src", Anchor: "table:src:dbo.AUDIT_LOG" });
        Assert.Contains(f, x => x is { Object: "dbo.TMP_IMPORT", Severity: Severity.Low });
        Assert.Contains(f, x => x is { Object: "app.AuditEvents", Severity: Severity.Low, Side: "tgt" });
        Assert.Equal(3, f.Count);
    }

    [Fact]
    public void R02_deprecated_type() =>
        Assert.Equal(("dbo.CUST.NOTES", Severity.Medium, "column:src:dbo.CUST.NOTES"),
            Run(new DeprecatedTypeRule()).Select(x => (x.Object, x.Severity, x.Anchor)).Single());

    [Fact]
    public void R03_lob_columns() =>
        Assert.Equal("app.Customers.Notes", Run(new LobColumnsRule()).Single(x => x.Severity == Severity.Low).Object);

    [Fact]
    public void R04_collation_mismatch()
    {
        Assert.Empty(Run(new CollationMismatchRule()));
        var tgt = ShopV2() with { Server = Meta("ShopV2", collation: "Latin1_General_100_CI_AS_SC_UTF8") };
        var f = Run(new CollationMismatchRule(), tgt: tgt);
        Assert.Contains(f, x => x is { Object: "database", Severity: Severity.Medium, Side: "both" });
        Assert.Contains(f, x => x is { Object: "app.Customers.Email", Severity: Severity.Info });
    }

    [Fact]
    public void R05_to_R08_target_generated_values()
    {
        Assert.Equal(new[] { "app.Addresses", "app.Customers", "app.Orders", "app.Products" },
            Run(new TargetIdentityRule()).Select(x => x.Object).OrderBy(x => x, StringComparer.Ordinal));
        var trig = Run(new TargetTriggersRule()).Single();
        Assert.Equal(("app.Orders", Severity.Medium, 1L), (trig.Object, trig.Severity, trig.Count!.Value));
        Assert.Contains("trg_Orders_Audit", trig.Message);
        Assert.Equal("app.Customers.DisplayName", Run(new TargetComputedRule()).Single().Object);
        Assert.Equal("app.Products.RowVer", Run(new TargetRowVersionRule()).Single().Object);
    }

    [Fact]
    public void R09_untrusted_foreign_keys_depend_on_orphan_counts()
    {
        var high = Run(new UntrustedForeignKeysRule()).Single();
        Assert.Equal(("dbo.ORD_HDR", Severity.High, 2L), (high.Object, high.Severity, high.Count!.Value));
        Assert.Contains("FK_ORD_CUST", high.Message);
        Assert.Equal(Severity.Medium, Run(new UntrustedForeignKeysRule(), orphans: new()).Single().Severity);
        var none = new Dictionary<string, long> { [RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")] = 0 };
        Assert.Equal(Severity.Low, Run(new UntrustedForeignKeysRule(), orphans: none).Single().Severity);
    }

    [Fact]
    public void R10_to_R13_data_shape()
    {
        Assert.Equal("dbo.CUST.FAX_NO", Run(new NullHeavyColumnsRule()).Single().Object);
        Assert.Equal("dbo.TMP_IMPORT", Run(new EmptySourceTablesRule()).Single().Object);
        Assert.Empty(Run(new LargeTablesRule()));
        var big = Change(LegacyShop(), "dbo.AUDIT_LOG", t => t with { Rows = 12_000_000 });
        Assert.Equal(("dbo.AUDIT_LOG", Severity.Info), Run(new LargeTablesRule(), src: big).Select(x => (x.Object, x.Severity)).Single());
        Assert.Empty(Run(new TemporalTablesRule()));
        var temporal = Change(ShopV2(), "app.Orders", t => t with { TemporalType = "system_versioned" });
        Assert.Equal(("app.Orders", Severity.Medium, "tgt"), Run(new TemporalTablesRule(), tgt: temporal).Select(x => (x.Object, x.Severity, x.Side)).Single());
    }

    [Fact]
    public void R14_target_fk_cycles()
    {
        var f = Run(new TargetFkCyclesRule()).Single();
        Assert.Equal(("app.Addresses ↔ app.Customers", Severity.Medium, "table:tgt:app.Addresses"), (f.Object, f.Severity, f.Anchor));
        var selfRef = Change(ShopV2(), "app.Products", t => t with
        {
            ForeignKeys = new List<ForeignKeyInfo> { Fk("FK_Products_Parent", "ProductId", "app.Products", "ProductId") },
        });
        Assert.Contains(Run(new TargetFkCyclesRule(), tgt: selfRef), x => x.Object == "app.Products" && x.Message.Contains("FK_Products_Parent"));
    }

    [Fact]
    public void R15_and_R16_target_state_and_version()
    {
        Assert.Empty(Run(new TargetNotEmptyRule()));
        var filled = Change(ShopV2(), "app.Products", t => t with { Rows = 10 });
        Assert.Equal(("app.Products", Severity.High), Run(new TargetNotEmptyRule(), tgt: filled).Select(x => (x.Object, x.Severity)).Single());
        Assert.Empty(Run(new VersionDowngradeRule()));
        var older = ShopV2() with { Server = Meta("ShopV2", majorVersion: 15, compatLevel: 150) };
        Assert.Equal(Severity.Medium, Run(new VersionDowngradeRule(), tgt: older).Single().Severity);
    }

    [Fact]
    public void Analyzer_numbers_findings_in_rule_then_object_order()
    {
        var payload = Analyzer.Analyze(LegacyShop(), ShopV2(), TwoOrphans);
        var list = payload.Findings.ToList();
        Assert.Equal("F001", list[0].Key);
        Assert.Equal(("R01", "dbo.AUDIT_LOG"), (list[0].Value.Rule, list[0].Value.Object));
        Assert.Equal(("R01", "dbo.TMP_IMPORT"), (list[1].Value.Rule, list[1].Value.Object));
        Assert.Equal(("R01", "app.AuditEvents"), (list[2].Value.Rule, list[2].Value.Object));
        Assert.Equal("R02", list[3].Value.Rule);
        Assert.Equal(payload.Findings.Values.Select(f => f.Rule).OrderBy(r => r, StringComparer.Ordinal), payload.Findings.Values.Select(f => f.Rule));
        Assert.Matches(@"^\d+ findings \(0 critical, 2 high\)$", Analyzer.Summary(payload));
    }

    [Fact]
    public void Analyzer_summaries_and_estimates()
    {
        var payload = Analyzer.Analyze(LegacyShop(), ShopV2(), TwoOrphans);
        Assert.Equal(("LegacyShop", 8, 38), (payload.Source.Database, payload.Source.Tables, payload.Source.Columns));
        Assert.Equal(19_711, payload.Source.Rows);
        Assert.Equal((6, 1, 17), (payload.Target.Tables, payload.Target.Triggers, payload.Target.MajorVersion));
        Assert.Equal(19_711, payload.Estimates.TotalRows);
        Assert.Equal(0.1, payload.Estimates.EstimatedMinutes);
        Assert.Null(payload.Narrative);
        var big = Analyzer.Estimate(Change(LegacyShop(), "dbo.AUDIT_LOG", t => t with { Rows = 30_000_000 }));
        Assert.Equal(10.1, big.EstimatedMinutes);   // 30 014 711 rows / 50 000 / 60 = 10.005 → 10.1
    }

    [Fact]
    public void OrphanSql_checks_non_null_children_without_parent() =>
        Assert.Equal(
            "SELECT COUNT_BIG(*) FROM [dbo].[ORD_HDR] AS c WHERE c.[CUST_ID] IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [dbo].[CUST] AS p WHERE p.[CUST_ID] = c.[CUST_ID]);",
            AnalyzeJob.OrphanSql(LegacyShop().FindTable("dbo.ORD_HDR")!, LegacyShop().FindTable("dbo.ORD_HDR")!.ForeignKeys[0]));
}
