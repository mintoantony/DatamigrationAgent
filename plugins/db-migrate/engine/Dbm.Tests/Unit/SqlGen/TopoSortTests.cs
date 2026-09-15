using System.Globalization;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class TopoSortTests
{
    static readonly string[] SampleNodes = ["app.OrderLines", "app.Orders", "app.Customers", "app.Addresses", "app.Products", "app.AuditEvents"];
    static readonly (string Child, string Parent)[] SampleEdges =
    [
        ("app.Addresses", "app.Customers"),   // FK_Addresses_Customers (CustomerId NOT NULL)
        ("app.Customers", "app.Addresses"),   // FK_Customers_PrimaryAddress (PrimaryAddressId NULL) -> cycle
        ("app.Orders", "app.Customers"),
        ("app.Orders", "app.Addresses"),
        ("app.OrderLines", "app.Orders"),
        ("app.OrderLines", "app.Products"),
    ];

    [Fact]
    public void Chain_puts_parents_first()
    {
        var r = TopoSort.Sort(["c", "b", "a"], [("c", "b"), ("b", "a")]);
        Assert.Equal(["a", "b", "c"], r.Order);
        Assert.Empty(r.Cycles);
        Assert.Empty(r.CycleEdges);
    }

    [Fact]
    public void Independent_nodes_are_ordinal_sorted()
    {
        var r = TopoSort.Sort(["app.b", "app.C", "app.a"], []);
        Assert.Equal(["app.C", "app.a", "app.b"], r.Order);   // ordinal: 'C' (0x43) < 'a' (0x61)
    }

    [Fact]
    public void Sample_cycle_is_broken_on_the_nullable_edge()
    {
        var nullable = new HashSet<(string, string)> { ("app.Customers", "app.Addresses"), ("app.Orders", "app.Addresses") };
        var r = TopoSort.Sort(SampleNodes, SampleEdges, (c, p) => nullable.Contains((c, p)));
        Assert.Equal(["app.AuditEvents", "app.Customers", "app.Products", "app.Addresses", "app.Orders", "app.OrderLines"], r.Order);
        var cycle = Assert.Single(r.Cycles);
        Assert.Equal(["app.Addresses", "app.Customers"], cycle);
        Assert.Equal([("app.Customers", "app.Addresses")], r.CycleEdges);
    }

    [Fact]
    public void Without_preference_the_first_edge_in_ordinal_order_is_cut()
    {
        var r = TopoSort.Sort(SampleNodes, SampleEdges);
        Assert.Equal([("app.Addresses", "app.Customers")], r.CycleEdges);
        Assert.Equal(["app.Addresses", "app.AuditEvents", "app.Products", "app.Customers", "app.Orders", "app.OrderLines"], r.Order);
    }

    [Fact]
    public void Self_reference_is_a_cycle_edge()
    {
        var r = TopoSort.Sort(["hr.Employee", "hr.Dept"], [("hr.Employee", "hr.Employee"), ("hr.Employee", "hr.Dept")]);
        Assert.Equal(["hr.Dept", "hr.Employee"], r.Order);
        Assert.Equal(["hr.Employee"], Assert.Single(r.Cycles));
        Assert.Equal([("hr.Employee", "hr.Employee")], r.CycleEdges);
    }

    [Fact]
    public void Three_node_cycle_needs_one_cut()
    {
        var r = TopoSort.Sort(["a", "b", "c"], [("a", "b"), ("b", "c"), ("c", "a")]);
        Assert.Equal([("a", "b")], r.CycleEdges);
        Assert.Equal(["a", "c", "b"], r.Order);
        Assert.Equal(["a", "b", "c"], Assert.Single(r.Cycles));
    }

    [Fact]
    public void Two_separate_cycles_are_both_broken()
    {
        var r = TopoSort.Sort(["a", "b", "x", "y"], [("a", "b"), ("b", "a"), ("x", "y"), ("y", "x")]);
        Assert.Equal(2, r.Cycles.Count);
        Assert.Equal([("a", "b"), ("x", "y")], r.CycleEdges);
        Assert.Equal(["a", "x", "b", "y"], r.Order);
    }

    [Fact]
    public void Unknown_and_duplicate_edges_are_ignored()
    {
        var r = TopoSort.Sort(["a", "b"], [("b", "a"), ("b", "a"), ("b", "zzz"), ("qqq", "a")]);
        Assert.Equal(["a", "b"], r.Order);
        Assert.Empty(r.CycleEdges);
    }

    [Fact]
    public void Result_is_deterministic_regardless_of_input_order()
    {
        var a = TopoSort.Sort(SampleNodes, SampleEdges);
        var b = TopoSort.Sort(Enumerable.Reverse(SampleNodes).ToArray(), Enumerable.Reverse(SampleEdges).ToArray());
        Assert.Equal(a.Order, b.Order);
        Assert.Equal(a.CycleEdges, b.CycleEdges);
    }

    // ---- Fix round 1: ordering rules that were correct but unpinned -------------------------------------------------

    /// <summary>Runs <paramref name="body"/> under en-US, where culture collation puts "app.b" before "app.C" and ordinal does not,
    /// so a comparer that silently became culture-aware is caught whatever the build machine's own culture is.</summary>
    static void UnderEnUs(Action body)
    {
        var saved = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); body(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Nodes_released_at_a_later_level_are_ordinal_sorted()
    {
        // Level 0 = a, b. a releases y, then b releases x: without the level sort the order would be a, b, y, x.
        var r = TopoSort.Sort(["a", "b", "x", "y"], [("y", "a"), ("x", "b")]);
        Assert.Equal(["a", "b", "x", "y"], r.Order);
    }

    [Fact]
    public void Later_levels_use_ordinal_not_culture_order()
    {
        UnderEnUs(() =>
        {
            Assert.True(string.Compare("app.b", "app.C", StringComparison.CurrentCulture) < 0, "precondition: culture order differs");
            // Level 0 = app.p, app.q; they release app.b then app.C. Ordinal puts 'C' (0x43) before 'b' (0x62).
            var r = TopoSort.Sort(["app.b", "app.C", "app.p", "app.q"], [("app.b", "app.p"), ("app.C", "app.q")]);
            Assert.Equal(["app.p", "app.q", "app.C", "app.b"], r.Order);
        });
    }

    [Fact]
    public void Edge_order_is_ordinal_not_culture_so_the_same_edge_is_cut_everywhere()
    {
        UnderEnUs(() =>
        {
            // Both edges are equally good; the first in ordinal (child, parent) order is cut. Ordinal: "app.C" < "app.b".
            var r = TopoSort.Sort(["app.b", "app.C"], [("app.b", "app.C"), ("app.C", "app.b")]);
            Assert.Equal([("app.C", "app.b")], r.CycleEdges);
            Assert.Equal(["app.C", "app.b"], r.Order);
        });
    }

    [Fact]
    public void Self_loop_is_cut_before_a_preferred_edge_in_the_same_component()
    {
        // One component {a, b} holding a self-loop on a and the preferred edge (b, a). Round 1 cuts the self-loop,
        // round 2 (a <-> b still cyclic) cuts the preferred edge.
        var r = TopoSort.Sort(["a", "b"], [("a", "a"), ("a", "b"), ("b", "a")], (c, p) => (c, p) == ("b", "a"));
        Assert.Equal([("a", "a"), ("b", "a")], r.CycleEdges);
        Assert.Equal(["a", "b"], Assert.Single(r.Cycles));
        Assert.Equal(["b", "a"], r.Order);
    }

    [Fact]
    public void A_component_still_cyclic_after_one_cut_takes_more_rounds_but_is_reported_once()
    {
        // Bidirectional triangle: round 1 cuts (a,b), round 2 cuts (a,c), round 3 cuts (b,c) from the remaining {b, c} cycle.
        var r = TopoSort.Sort(["a", "b", "c"], [("a", "b"), ("b", "a"), ("b", "c"), ("c", "b"), ("a", "c"), ("c", "a")]);
        Assert.Equal([("a", "b"), ("a", "c"), ("b", "c")], r.CycleEdges);
        Assert.Equal(["a", "b", "c"], Assert.Single(r.Cycles));
        Assert.Equal(["a", "b", "c"], r.Order);
    }

    [Fact]
    public void Sort_rejects_null_arguments_naming_the_parameter()
    {
        Assert.Throws<ArgumentNullException>("nodes", () => TopoSort.Sort(null!, []));
        Assert.Throws<ArgumentNullException>("edges", () => TopoSort.Sort(["a"], null!));
        Assert.Throws<ArgumentNullException>("nodes", () => TopoSort.Sort(null!, [], (_, _) => true));
        Assert.Throws<ArgumentNullException>("edges", () => TopoSort.Sort(["a"], null!, (_, _) => true));
        Assert.Throws<ArgumentException>("nodes", () => TopoSort.Sort(["a", null!], []));
    }
}

public class SqlQuoteTests
{
    [Fact] public void Ident_doubles_closing_brackets() => Assert.Equal("[Order]]Lines]", SqlQuote.Ident("Order]Lines"));
    [Fact] public void Table_quotes_both_parts() => Assert.Equal("[app].[Orders]", SqlQuote.Table("app", "Orders"));
    [Fact] public void TableKey_splits_on_first_dot() => Assert.Equal("[dbo].[My.Table]", SqlQuote.TableKey("dbo.My.Table"));
    [Fact] public void TableKey_rejects_keys_without_schema() => Assert.Throws<ArgumentException>(() => SqlQuote.TableKey("Customer"));
    [Fact] public void TableKey_rejects_an_empty_schema() => Assert.Throws<ArgumentException>("key", () => SqlQuote.TableKey(".x"));
    [Fact] public void TableKey_rejects_an_empty_name() => Assert.Throws<ArgumentException>("key", () => SqlQuote.TableKey("x."));
    [Fact] public void Literal_is_unicode_with_doubled_quotes() => Assert.Equal("N'O''Brien'", SqlQuote.Literal("O'Brien"));
}
