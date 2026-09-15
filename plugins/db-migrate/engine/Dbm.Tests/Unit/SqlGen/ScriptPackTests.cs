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
}
