using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Matching;

public sealed record TableScore(double Total, double Name, double Columns, double Fk)
{
    public string Why => string.Create(CultureInfo.InvariantCulture, $"name {Name:0.00}, columns {Columns:0.00}, fk {Fk:0.00}");
}

/// <summary>Table-pair score: 0.5·tableName + 0.4·columnSetSimilarity + 0.1·fkDegreeSimilarity.
/// columnSetSimilarity = mean over writable target columns of the best column-name cosine in the source table.</summary>
public static class TableScorer
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static TableScore Score(MatchContext ctx, TableInfo tgtTable, TableInfo srcTable)
    {
        var name = ctx.TableNameSimilarity(tgtTable, srcTable);
        var targets = tgtTable.Columns.Where(c => !c.IsComputed && !c.IsRowVersion).ToList();
        var columns = targets.Count == 0 || srcTable.Columns.Count == 0
            ? 0.0
            : targets.Average(tc => srcTable.Columns.Max(sc => ctx.ColumnNameSimilarity(tgtTable, tc, srcTable, sc)));
        var fk = DegreeSimilarity(Degree(ctx.Tgt, tgtTable), Degree(ctx.Src, srcTable));
        return new TableScore(Math.Round(0.5 * name + 0.4 * columns + 0.1 * fk, 3), name, columns, fk);
    }

    /// <summary>Outgoing + incoming foreign keys of a table within its catalog.</summary>
    public static int Degree(CatalogSnapshot catalog, TableInfo table) =>
        table.ForeignKeys.Count + catalog.Tables.Sum(t => t.ForeignKeys.Count(f => Ci.Equals(f.RefKey, table.Key)));

    public static double DegreeSimilarity(int a, int b) => Math.Max(a, b) == 0 ? 1.0 : 1.0 - Math.Abs(a - b) / (double)Math.Max(a, b);
}
