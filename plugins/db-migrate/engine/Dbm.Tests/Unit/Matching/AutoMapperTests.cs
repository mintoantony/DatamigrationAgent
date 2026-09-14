using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Matching;

public class AutoMapperTests
{
    private static readonly CatalogSnapshot Src = SampleCatalogs.Source();
    private static readonly CatalogSnapshot Tgt = SampleCatalogs.Target();
    private static readonly Dictionary<string, string> Pairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.Customers"] = "dbo.CUST", ["app.Addresses"] = "dbo.ADDR", ["app.Products"] = "dbo.PROD",
        ["app.Orders"] = "dbo.ORD_HDR", ["app.OrderLines"] = "dbo.ORD_LINE", ["app.AuditEvents"] = "dbo.AUDIT_LOG",
    };

    private static MatchContext Ctx() => new(Src, Tgt, Synonyms.Default());
    private static TableInfo S(string key) => Src.FindTable(key)!;
    private static TableInfo T(string key) => Tgt.FindTable(key)!;
    private static ColumnScore Score(string tgtTable, string tgtCol, string srcTable, string srcCol) =>
        ColumnScorer.Score(Ctx(), T(tgtTable), T(tgtTable).FindColumn(tgtCol)!, S(srcTable), S(srcTable).FindColumn(srcCol)!, Pairs);

    [Theory]
    [InlineData("cust", "customer")]
    [InlineData("addr", "address")]
    [InlineData("prod", "product")]
    [InlineData("ord", "order")]
    [InlineData("nm", "name")]
    [InlineData("dt", "date")]
    [InlineData("no", "number")]
    [InlineData("prc", "price")]
    [InlineData("amt", "amount")]
    [InlineData("qty", "quantity")]
    [InlineData("ctry", "country")]
    [InlineData("cd", "code")]
    [InlineData("zip", "postal")]
    [InlineData("zip", "code")]
    [InlineData("flg", "flag")]
    [InlineData("dob", "birth")]
    [InlineData("dob", "date")]
    [InlineData("usr", "user")]
    [InlineData("ts", "time")]
    [InlineData("txt", "text")]
    [InlineData("cmnt", "comment")]
    [InlineData("desc", "description")]
    public void Default_synonyms_cover_the_sample_vocabulary(string abbreviation, string expected)
    {
        Assert.Contains(expected, Synonyms.Default().Expand(abbreviation));
    }

    [Fact]
    public void Name_similarity_prefers_the_matching_column()
    {
        var ctx = Ctx();
        var customers = T("app.Customers");
        var cust = S("dbo.CUST");
        var email = customers.FindColumn("Email")!;
        Assert.True(ctx.ColumnNameSimilarity(customers, email, cust, cust.FindColumn("EMAIL_ADDR")!)
                    > ctx.ColumnNameSimilarity(customers, email, cust, cust.FindColumn("PHONE_NO")!));
        Assert.Equal(1.0, ctx.ColumnNameSimilarity(customers, customers.FindColumn("BirthDate")!, cust, cust.FindColumn("DOB")!), 6);
    }

    [Fact]
    public void Structure_rewards_matching_keys_and_paired_foreign_keys()
    {
        Assert.Equal(1.0, Score("app.Customers", "CustomerId", "dbo.CUST", "CUST_ID").Structure);
        Assert.Equal(1.0, Score("app.Orders", "CustomerId", "dbo.ORD_HDR", "CUST_ID").Structure);
        var unpaired = ColumnScorer.Structure(T("app.Orders"), T("app.Orders").FindColumn("CustomerId")!,
            S("dbo.ORD_HDR"), S("dbo.ORD_HDR").FindColumn("CUST_ID")!, new Dictionary<string, string>());
        Assert.Equal(0.875, unpaired);
        // PK vs non-PK, FK only on source, identity vs not, both NOT NULL
        Assert.Equal(0.25, ColumnScorer.Structure(T("app.Customers"), T("app.Customers").FindColumn("CustomerId")!,
            S("dbo.ADDR"), S("dbo.ADDR").FindColumn("CUST_ID")!, Pairs));
        // nullable target from NOT NULL source scores 0.75 on nullability
        Assert.Equal((1 + 1 + 1 + 0.75) / 4, Score("app.Customers", "Email", "dbo.CUST", "CUST_NM").Structure);
    }

    [Fact]
    public void Profile_compares_class_nulls_and_length_against_an_empty_target()
    {
        Assert.Equal(1.0, Score("app.Customers", "Email", "dbo.CUST", "EMAIL_ADDR").Profile);
        // email class vs a name column (0.3), nulls into NOT NULL (0), length fits (1)
        Assert.Equal((0.3 + 0 + 1) / 3, Score("app.Customers", "FirstName", "dbo.CUST", "EMAIL_ADDR").Profile!.Value, 6);
        Assert.Null(Score("app.Customers", "Phone", "dbo.CUST", "PHONE_NO").Profile);
    }

    [Fact]
    public void Total_uses_the_spec_weights()
    {
        var withProfile = Score("app.Customers", "Email", "dbo.CUST", "EMAIL_ADDR");
        Assert.Equal(Math.Round(0.45 * withProfile.Name + 0.20 * withProfile.Type.Score + 0.15 * withProfile.Structure + 0.20 * withProfile.Profile!.Value, 3), withProfile.Total);
        var noProfile = Score("app.Customers", "Phone", "dbo.CUST", "PHONE_NO");
        Assert.Equal(Math.Round(0.65 * noProfile.Name + 0.20 * noProfile.Type.Score + 0.15 * noProfile.Structure, 3), noProfile.Total);
        Assert.Contains("type widening", noProfile.Why);
    }

    [Fact]
    public void Table_scores_rank_the_right_source_first()
    {
        var ctx = Ctx();
        foreach (var (target, source) in AutoMapperBaseline.TablePairs)
        {
            var best = Src.Tables.OrderByDescending(s => TableScorer.Score(ctx, T(target), s).Total).First();
            Assert.Equal(source, best.Key);
        }
        Assert.Equal(1.0, TableScorer.DegreeSimilarity(0, 0));
        Assert.Equal(0.5, TableScorer.DegreeSimilarity(2, 4));
        Assert.Equal(3, TableScorer.Degree(Src, S("dbo.ORD_HDR")));
    }

    [Fact]
    public void Baseline_on_sample_catalogs()
    {
        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions());
        var misses = AutoMapperBaseline.Misses(m);
        Assert.True(misses.Count == 0, "baseline misses:\n" + string.Join("\n", misses));
    }

    [Fact]
    public void Draft_shape_follows_the_rules()
    {
        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions());

        Assert.Equal(["app.Addresses", "app.AuditEvents", "app.Customers", "app.OrderLines", "app.Orders", "app.Products"], m.Tables.Keys.Order().ToArray());
        Assert.Equal(new DropDecision("empty table with no matching target", MapMethod.Vector), m.Drops["dbo.TMP_IMPORT"]);
        Assert.False(m.Tables["app.Customers"].Columns.ContainsKey("DisplayName"));     // computed
        Assert.False(m.Tables["app.Products"].Columns.ContainsKey("RowVer"));          // rowversion
        var id = m.Tables["app.Customers"].Columns["CustomerId"];
        Assert.Equal("s.[CUST_ID]", id.Expr);
        Assert.Equal(["dbo.CUST.CUST_ID"], id.SourceColumns);
        var city = m.Tables["app.Addresses"].Columns["City"];
        Assert.Equal(MapMethod.Fuzzy, city.Method);                                    // same tokens, varchar -> nvarchar is widening
        var ctry = m.Tables["app.Addresses"].Columns["CountryCode"];
        Assert.Equal(MapMethod.Exact, ctry.Method);                                    // same tokens, char(2) -> char(2)
        Assert.Null(ctry.TypeRisk);
        var dob = m.Tables["app.Customers"].Columns["BirthDate"];
        Assert.Equal("time part dropped", dob.TypeRisk);
        Assert.All(m.Tables.Values.SelectMany(t => t.Columns.Values), c => Assert.True(c.Candidates is null || c.Candidates.Count <= 3));
        // one-to-one WITHIN each table pair (the spec's guarantee): a source column is used at most once inside any single
        // target table's column assignments. Nothing forbids two different target tables that share a primary source from
        // each mapping the same source column - that is legitimate denormalization, so this must not be asserted globally.
        Assert.All(m.Tables.Values, map =>
        {
            var used = map.Columns.Values.SelectMany(c => c.SourceColumns).ToList();
            Assert.Equal(used.Count, used.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Assert.Contains("dbo.ORD_STATUS.STATUS_CD", MappingValidator.UncoveredSourceColumns(m, Src));
        Assert.Empty(MappingValidator.Errors(m, Src, Tgt));
    }

    [Fact]
    public void Unpaired_target_gets_no_sources_and_candidates()
    {
        var tgt = Tgt with { Tables = [.. Tgt.Tables, new TableInfo("app", "Zebra", 0, 0,
            [new ColumnInfo("Stripes", 1, "int", 0, 10, 0, false, false, false, false, null, null, null)], [], [], 0, null, null)] };
        var m = AutoMapper.Map(Src, tgt, Synonyms.Default(), new MatchOptions());
        var zebra = m.Tables["app.Zebra"];
        Assert.Empty(zebra.Sources);
        Assert.Equal(MapMethod.Vector, zebra.Method);
        Assert.True(zebra.Confidence < 0.5);
        Assert.Empty(zebra.Columns);
        Assert.Contains(MappingValidator.Attention(m, new MatchOptions()), a => a.StartsWith("app.Zebra: no confident source table"));
    }

    [Fact]
    public void Output_is_deterministic()
    {
        Assert.Equal(
            Json.Serialize(AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions())),
            Json.Serialize(AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions())));
    }

    [Fact]
    public void Carry_over_keeps_agent_and_human_decisions_that_still_resolve()
    {
        var carry = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions());
        carry.Tables["app.Customers"].Columns["FirstName"] = new ColumnMap
        {
            Expr = "LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", SourceColumns = ["dbo.CUST.CUST_NM"],
            Confidence = 1, Method = MapMethod.Agent, Rationale = "First word of CUST_NM."
        };
        carry.Tables["app.Customers"].Columns["LastName"] = new ColumnMap
        {
            Expr = "s.[GONE]", SourceColumns = ["dbo.CUST.GONE"], Confidence = 1, Method = MapMethod.Human
        };
        carry.Tables["app.Orders"] = new TableMap
        {
            Kind = "merge", Sources = ["dbo.ORD_HDR", "dbo.ORD_STATUS"],
            From = "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]",
            Confidence = 1, Method = MapMethod.Agent, Rationale = "Status code comes from the lookup.",
            Columns = { ["StatusCode"] = new ColumnMap { Expr = "st.[STATUS_CD]", SourceColumns = ["dbo.ORD_STATUS.STATUS_CD", "dbo.ORD_HDR.STATUS_ID"], Confidence = 1, Method = MapMethod.Agent } }
        };
        carry.Tables["app.Gone"] = new TableMap { Sources = ["dbo.CUST"], Method = MapMethod.Human };
        carry.Drops["dbo.CUST.FAX_NO"] = new DropDecision("No fax in ShopV2.", MapMethod.Agent);
        carry.Drops["dbo.CUST.GONE"] = new DropDecision("stale", MapMethod.Human);
        carry.Notes.Add("keep me");

        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions(), carry);

        var first = m.Tables["app.Customers"].Columns["FirstName"];
        Assert.Equal(MapMethod.Carried, first.Method);
        Assert.Equal("First word of CUST_NM.", first.Rationale);
        Assert.NotEqual("s.[GONE]", m.Tables["app.Customers"].Columns["LastName"].Expr);
        var orders = m.Tables["app.Orders"];
        Assert.Equal(("merge", MapMethod.Carried), (orders.Kind, orders.Method));
        Assert.StartsWith("[dbo].[ORD_HDR] AS s JOIN", orders.From);
        Assert.Equal(MapMethod.Carried, orders.Columns["StatusCode"].Method);
        Assert.Equal("s.[ORD_ID]", orders.Columns["OrderId"].Expr);                   // remaining columns auto-assigned
        Assert.Equal(["OrderId", "CustomerId", "OrderDate", "StatusCode", "ShippingAddressId", "TotalAmount", "Comment"], orders.Columns.Keys.ToArray());
        Assert.False(m.Tables.ContainsKey("app.Gone"));
        Assert.Equal(new DropDecision("No fax in ShopV2.", MapMethod.Carried), m.Drops["dbo.CUST.FAX_NO"]);
        Assert.False(m.Drops.ContainsKey("dbo.CUST.GONE"));
        Assert.Equal(["keep me"], m.Notes);
    }

    [Fact]
    public void Carry_over_of_the_approved_mapping_is_complete()
    {
        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions(), SampleMappings.Approved());
        Assert.Empty(MappingValidator.Blockers(m, Src, Tgt));
        Assert.Empty(MappingValidator.Attention(m, new MatchOptions()));
        Assert.All(m.Tables.Values.SelectMany(t => t.Columns.Values), c => Assert.Equal(MapMethod.Carried, c.Method));
    }
}
