using System.Data;
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class StagingMappingTests
{
    [Fact]
    public void Staging_maps_each_destination_column_once()
    {
        // The shape of every generated task: Source == Target, and the column rounds, so Normalize has written a column of
        // its own. Harm: the binding maps from the normalised column and the trailing loop maps the raw column to the same
        // #stg destination. SqlBulkCopy accepts the pair and silently keeps one of them; a client that kept the other, or
        // rejected the pair, would undo the per-binding rounding on every staging task.
        var binding = new ColumnBinding("At", "At");
        var table = new DataTable();
        table.Columns.Add("At", typeof(DateTime));
        table.Columns.Add(TargetShape.NormalizedColumn(binding), typeof(DateTime));
        table.Columns.Add("__k0", typeof(int));
        var staging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["At"] = "At", ["__k0"] = "__k0" };

        var (mappings, unmapped) = BulkLoader.StagingMappings("dbo.T", table, [binding], staging);

        Assert.Empty(unmapped);
        var twice = mappings.GroupBy(m => m.DestinationColumn, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} <- {string.Join(" and ", g.Select(m => m.SourceColumn))}").ToList();
        Assert.True(twice.Count == 0, "the same #stg column is mapped twice: " + string.Join("; ", twice));
        Assert.Equal(TargetShape.NormalizedColumn(binding),
            mappings.Single(m => string.Equals(m.DestinationColumn, "At", StringComparison.OrdinalIgnoreCase)).SourceColumn);
        Assert.Contains(mappings, m => m.SourceColumn == "__k0");   // the key alias still reaches #stg
    }

    [Fact]
    public void A_one_source_fan_out_into_a_source_named_stg_column_is_refused_naming_both_targets()
    {
        // Open item 21: A->a and A->b against a hand-written #stg (k, A) both resolve to #stg.A. The refusal is right; the harm is the
        // sentence - "binds two source columns ... A and A" names the one source three times and neither target, so the one person who
        // meets it cannot tell which bindings to fix.
        var table = new DataTable();
        table.Columns.Add("k", typeof(int));
        table.Columns.Add("A", typeof(string));
        var staging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["k"] = "k", ["A"] = "A" };
        ColumnBinding[] bindings = [new("k", "k"), new("A", "a"), new("A", "b")];

        var ex = Assert.Throws<TransferException>(() => BulkLoader.StagingMappings("dbo.F", table, bindings, staging));

        Assert.Equal("bad_task", ex.Code);
        const string Wanted = "Task for dbo.F binds source column A to two targets (a, b) which both land in the one #stg column A; "
                              + "give #stg a column per target.";
        Assert.True(ex.Message.StartsWith(Wanted, StringComparison.Ordinal),
            "the fan-out refusal does not name the one source and its two targets: " + ex.Message);
        Assert.Equal(new[] { "A", "a", "b" }, ex.Details);
    }

    [Fact]
    public void Two_different_sources_into_one_stg_column_keep_the_two_sources_wording()
    {
        var table = new DataTable();
        table.Columns.Add("Note", typeof(string));
        table.Columns.Add("Note2", typeof(string));
        var staging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["note"] = "Note" };
        ColumnBinding[] bindings = [new("Note", "note"), new("Note2", "note")];

        var ex = Assert.Throws<TransferException>(() => BulkLoader.StagingMappings("dbo.C", table, bindings, staging));

        Assert.True(ex.Message.StartsWith("Task for dbo.C binds two source columns to the one #stg column Note: Note and Note2.",
            StringComparison.Ordinal), "two different sources lost their wording: " + ex.Message);
    }
}
