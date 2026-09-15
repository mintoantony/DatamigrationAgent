using System.IO.Compression;
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class ScriptPackTests
{
    static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    static List<(string Name, string Content)> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.Select(e =>
        {
            using var r = new StreamReader(e.Open());
            return (e.FullName, r.ReadToEnd());
        }).ToList();
    }

    [Fact]
    public void Zip_has_pre_one_file_per_task_post_and_readme_in_order()
    {
        var entries = Unzip(ScriptPack.BuildZip(Plan(), "LegacyShop to ShopV2"));
        Assert.Equal(
            ["00_pre.sql", "01_app_AuditEvents.sql", "02_app_Customers.sql", "03_app_Products.sql", "04_app_Addresses.sql",
             "05_app_Orders.sql", "06_app_OrderLines.sql", "99_post.sql", "README.md"],
            entries.Select(e => e.Name));
    }

    [Fact]
    public void Task_file_has_header_source_query_and_count_query()
    {
        var plan = Plan();
        var files = Unzip(ScriptPack.BuildZip(plan, "demo")).ToDictionary(e => e.Name, e => e.Content);
        var orders = files["05_app_Orders.sql"];
        Assert.Contains("-- demo: task T05  app.Orders\n", orders);
        Assert.Contains("-- Mode:            direct\n", orders);
        Assert.Contains("-- Keys:            __k0\n", orders);
        Assert.Contains("-- Depends on:      T02, T04\n", orders);
        Assert.Contains("-- Identity insert: yes\n", orders);
        Assert.Contains("--   - target has 1 trigger(s); not fired unless FireTriggers\n", orders);
        Assert.Contains("-- ---- Source query ----\n-- runs on SOURCE\n", orders);
        Assert.Contains(plan.Tasks["T05"].SourceQuery + "\nGO\n", orders);
        Assert.Contains(plan.Tasks["T05"].CountSql + ";\nGO\n", orders);
        Assert.Contains("-- Keys:            (none - the table loads in a single transaction)\n", files["01_app_AuditEvents.sql"]);
    }

    [Fact]
    public void Global_files_hold_the_cycle_statements()
    {
        var files = Unzip(ScriptPack.BuildZip(Plan(), "demo")).ToDictionary(e => e.Name, e => e.Content);
        Assert.Contains("ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];\nGO\n", files["00_pre.sql"]);
        Assert.Contains("ALTER TABLE [app].[Customers] WITH CHECK CHECK CONSTRAINT [FK_Customers_PrimaryAddress];\nGO\n", files["99_post.sql"]);
        Assert.Contains("-- runs on TARGET", files["00_pre.sql"]);
    }

    [Fact]
    public void Empty_global_scripts_say_so()
    {
        var plan = Plan();
        plan.PreSql.Clear();
        var files = Unzip(ScriptPack.BuildZip(plan, "demo")).ToDictionary(e => e.Name, e => e.Content);
        Assert.Contains("-- (no global pre-load statements)", files["00_pre.sql"]);
    }

    [Fact]
    public void Staging_task_file_contains_staging_and_merge()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        var file = Unzip(ScriptPack.BuildZip(plan, "demo")).Single(e => e.Name == "04_app_Addresses.sql").Content;
        Assert.Contains("-- Mode:            staging_merge\n", file);
        Assert.Contains(plan.Tasks["T04"].StagingDdl + "\nGO\n", file);
        Assert.Contains(plan.Tasks["T04"].MergeSql + "\nGO\n", file);
    }

    [Fact]
    public void Readme_explains_streaming_and_lists_files()
    {
        var readme = Unzip(ScriptPack.BuildZip(Plan(), "demo")).Single(e => e.Name == "README.md").Content;
        Assert.StartsWith("# demo - migration script pack", readme);
        Assert.Contains("no linked server", readme);
        Assert.Contains("SqlBulkCopy", readme);
        Assert.Contains("| `05_app_Orders.sql` | T05 | app.Orders | direct | T02, T04 |", readme);
    }

    [Fact]
    public void Zip_is_byte_identical_for_the_same_plan()
    {
        var first = ScriptPack.BuildZip(Plan(), "demo");
        var second = ScriptPack.BuildZip(Plan(), "demo");
        Assert.Equal(first, second);
        Assert.Equal(ScriptPack.BuildFiles(Plan(), "demo"), Unzip(first));
    }

    /// <summary>M5: every generated fixture inserts tasks in Order, so a pack iterating the dictionary would pass the test above.
    /// Here the dictionary's insertion order is the reverse of Order; the pack must not change by a single byte.</summary>
    [Fact]
    public void Pack_follows_order_never_dictionary_insertion_order()
    {
        var plan = Plan();
        var reversed = Plan();
        reversed.Tasks = reversed.Tasks.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Assert.NotEqual(reversed.Order, reversed.Tasks.Keys);   // fixture guard: the orders really differ

        Assert.Equal(ScriptPack.BuildZip(plan, "demo"), ScriptPack.BuildZip(reversed, "demo"));
        Assert.Equal(ScriptPack.BuildFiles(plan, "demo").Select(f => f.Name), ScriptPack.BuildFiles(reversed, "demo").Select(f => f.Name));
    }

    /// <summary>M5: two zips built within one 2-second DOS-time bucket cannot tell a fixed timestamp from "now"; the entry time can.</summary>
    [Fact]
    public void Zip_entries_carry_a_fixed_timestamp()
    {
        using var zip = new ZipArchive(new MemoryStream(ScriptPack.BuildZip(Plan(), "demo")), ZipArchiveMode.Read);
        Assert.NotEmpty(zip.Entries);
        Assert.All(zip.Entries, e => Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0), e.LastWriteTime.DateTime));
    }

    /// <summary>M2: the pack is handed to a DBA; a task missing from Order (or an Order id with no task) must never make it
    /// silently incomplete.</summary>
    [Fact]
    public void A_pack_whose_order_does_not_match_its_tasks_says_so_and_omits_nothing()
    {
        var plan = Plan();
        plan.Order.Remove("T03");
        plan.Order.Insert(2, "T99");

        var files = ScriptPack.BuildFiles(plan, "demo");

        Assert.Contains(files, f => f.Content.Contains("-- demo: task T03  app.Products\n", StringComparison.Ordinal));
        var readme = files.Single(f => f.Name == "README.md").Content;
        Assert.Contains("## WARNING: this pack does not match the plan's execution order", readme);
        Assert.Contains("- T03: task is missing from order", readme);
        Assert.Contains("- order[2]: T99 is not a task", readme);
        var pre = files[0].Content;
        Assert.Contains("-- WARNING: this pack does not match the plan's execution order (see README.md)\n", pre);
        Assert.Contains("--   - T03: task is missing from order\n", pre);
    }

    const string Hostile = "x\nCANARY_LF;\rCANARY_CR;\r\nCANARY_CRLF;\u2028CANARY_LS;\u2029CANARY_PS;\u0085CANARY_NEL;\vCANARY_VT;\fCANARY_FF;";

    /// <summary>The SQL bodies ARE executable by design; every other value that reaches the pack is text.</summary>
    static readonly HashSet<string> SqlBodies = [nameof(TaskPlan.SourceQuery), nameof(TaskPlan.StagingDdl), nameof(TaskPlan.MergeSql),
        nameof(TaskPlan.PreSql), nameof(TaskPlan.PostSql), nameof(TaskPlan.CountSql)];

    /// <summary>Poisons, by reflection, every string and string-list property of the plan and its tasks that is not a SQL body —
    /// so a field added later is hostile here without anyone remembering to list it.</summary>
    static SqlPlanPayload Poisoned(SqlPlanPayload plan, bool keepMode)
    {
        foreach (var p in typeof(SqlPlanPayload).GetProperties())
            if (p.Name is not (nameof(SqlPlanPayload.PreSql) or nameof(SqlPlanPayload.PostSql) or nameof(SqlPlanPayload.Order) or nameof(SqlPlanPayload.Tasks)))
                Poison(plan, p);
        foreach (var task in plan.Tasks.Values)
        {
            foreach (var p in typeof(TaskPlan).GetProperties())
                if (!SqlBodies.Contains(p.Name) && !(keepMode && p.Name == nameof(TaskPlan.Mode))) Poison(task, p);
            task.Columns = task.Columns.Select(c => new ColumnBinding(c.Source + Hostile, c.Target + Hostile)).ToList();
        }
        var id = plan.Order[^1];
        var hostileId = id + Hostile;
        plan.Tasks = plan.Tasks.ToDictionary(kv => kv.Key == id ? hostileId : kv.Key, kv => kv.Value, StringComparer.Ordinal);
        plan.Order[^1] = hostileId;
        return plan;
    }

    static void Poison(object target, System.Reflection.PropertyInfo p)
    {
        if (p.PropertyType == typeof(string)) p.SetValue(target, (string?)p.GetValue(target) + Hostile);
        else if (p.PropertyType == typeof(List<string>)) ((List<string>)p.GetValue(target)!).Add(Hostile);
    }

    /// <summary>H2: the pack is run by a DBA with production credentials. No interpolated value may end a `--` comment and start a
    /// line of its own — for any line terminator a parser or editor honours.</summary>
    [Fact]
    public void Hostile_values_never_break_out_of_a_comment_line()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";   // a staging_merge task exercises the staging header lines too
        var packs = new[]
        {
            ScriptPack.BuildFiles(Poisoned(Plan(), keepMode: false), Hostile),
            ScriptPack.BuildFiles(Poisoned(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), keepMode: true), Hostile),
        };
        Assert.Contains(packs[1], f => f.Content.Contains("-- Mode:            staging_merge\n", StringComparison.Ordinal));

        var separators = new[] { "\r\n", "\n", "\r", "\u2028", "\u2029", "\u0085", "\v", "\f" };
        foreach (var (name, content) in packs.SelectMany(p => p))
        {
            Assert.Contains("CANARY_LF", content);   // the values did reach the file
            foreach (var line in content.Split(separators, StringSplitOptions.None))
                Assert.False(line.TrimStart().StartsWith("CANARY", StringComparison.Ordinal), $"{name}: a value escaped its comment: [{line}]");
        }
    }
}
