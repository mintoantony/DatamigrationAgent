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
}
