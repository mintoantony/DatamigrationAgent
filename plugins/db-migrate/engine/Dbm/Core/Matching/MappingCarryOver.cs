using Dbm.Core.Catalog;
using Dbm.Core.Mapping;

namespace Dbm.Core.Matching;

/// <summary>Keeps agent/human decisions from a previous mapping when the auto-mapper re-runs (e.g. after re-discovery).
/// An item is carried when its method is agent, human or carried and everything it references still exists.</summary>
public static class MappingCarryOver
{
    public static bool IsKept(MapMethod method) => method is MapMethod.Agent or MapMethod.Human or MapMethod.Carried;

    /// <summary>Carried table maps keyed by target table key (catalog case). A table is carried when its own method is kept or it has
    /// kept columns, and all its Sources still exist; only kept columns whose target column and SourceColumns still exist are copied.</summary>
    public static Dictionary<string, TableMap> Tables(MappingPayload? carryOver, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        var result = new Dictionary<string, TableMap>(StringComparer.OrdinalIgnoreCase);
        if (carryOver is null) return result;
        foreach (var (key, old) in carryOver.Tables)
        {
            var table = tgt.FindTable(key);
            if (table is null) continue;
            var keptColumns = old.Columns.Where(kv => IsKept(kv.Value.Method)).ToList();
            if (!IsKept(old.Method) && keptColumns.Count == 0) continue;
            var sources = old.Sources.Select(s => src.FindTable(s)).ToList();
            if (sources.Any(s => s is null)) continue;
            var map = new TableMap
            {
                Kind = old.Kind,
                Sources = sources.Select(s => s!.Key).ToList(),
                From = old.From,
                Filter = old.Filter,
                Confidence = old.Confidence,
                Method = MapMethod.Carried,
                Rationale = old.Rationale
            };
            foreach (var (name, cm) in keptColumns)
            {
                var column = table.FindColumn(name);
                if (column is null || column.IsComputed || column.IsRowVersion) continue;
                if (cm.SourceColumns.Any(sc => MappingValidator.ResolveSourceColumn(src, sc) is null)) continue;
                var carried = new ColumnMap
                {
                    Expr = cm.Expr,
                    SourceColumns = cm.SourceColumns.ToList(),
                    Default = cm.Default,
                    Confidence = cm.Confidence,
                    Method = MapMethod.Carried,
                    Rationale = cm.Rationale,
                    TypeRisk = cm.TypeRisk
                };
                RecomputeBareRisk(map, carried, column, src);
                map.Columns[column.Name] = carried;
            }
            result[table.Key] = map;
        }
        return result;
    }

    /// <summary>Job drafts never pass through Validate, so a source type can change under a kept column between discoveries.
    /// Carry-over's own sequence (there is no stored version and no patch), first match wins:
    /// expr blank and default blank → no risk; a bare single-source reference (alias resolved against the carried table's sources
    /// and from) → recompute the text through TypeCompat with the source profile; everything else → keep the stored typeRisk
    /// verbatim, no sentinel (residual: a type change beneath a custom expression is not re-evaluated).</summary>
    private static void RecomputeBareRisk(TableMap map, ColumnMap carried, ColumnInfo target, CatalogSnapshot src)
    {
        if (string.IsNullOrWhiteSpace(carried.Expr) && string.IsNullOrWhiteSpace(carried.Default)) { carried.TypeRisk = null; return; }
        if (carried.Expr is null || MappingValidator.BareSingleSource(map, carried, src) is not { } hit) return;
        var risk = TypeCompat.Check(ColumnType.From(hit.Column), ColumnType.From(target), hit.Column.Profile).Risk;
        carried.TypeRisk = string.IsNullOrWhiteSpace(risk) ? null : risk;
    }

    /// <summary>Copies kept drop decisions whose source table/column still exists into <paramref name="into"/> (method carried).</summary>
    public static void Drops(MappingPayload? carryOver, CatalogSnapshot src, MappingPayload into)
    {
        if (carryOver is null) return;
        foreach (var (key, drop) in carryOver.Drops)
        {
            if (!IsKept(drop.Method)) continue;
            var table = src.FindTable(key);
            if (table is not null)
            {
                into.Drops[table.Key] = new DropDecision(drop.Reason, MapMethod.Carried);
                continue;
            }
            if (MappingValidator.ResolveSourceColumn(src, key) is { } hit)
                into.Drops[$"{hit.Table.Key}.{hit.Column.Name}"] = new DropDecision(drop.Reason, MapMethod.Carried);
        }
    }
}
