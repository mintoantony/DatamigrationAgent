using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Analysis;

public sealed class RuleContext
{
    public required CatalogSnapshot Src { get; init; }
    public required CatalogSnapshot Tgt { get; init; }

    /// <summary>Orphan row counts for untrusted/disabled source FKs, keyed by <see cref="OrphanKey"/>; missing = not counted.</summary>
    public IReadOnlyDictionary<string, long> OrphanCounts { get; init; } = new Dictionary<string, long>();

    public static string OrphanKey(string tableKey, string foreignKeyName) => $"{tableKey}|{foreignKeyName}";
}

public interface IRule
{
    string Id { get; }
    string Title { get; }
    IEnumerable<Finding> Evaluate(RuleContext ctx);
}

public static class Rules
{
    public static IReadOnlyList<IRule> All { get; } = new IRule[]
    {
        new NoPrimaryKeyRule(), new DeprecatedTypeRule(), new LobColumnsRule(), new CollationMismatchRule(),
        new TargetIdentityRule(), new TargetTriggersRule(), new TargetComputedRule(), new TargetRowVersionRule(),
        new UntrustedForeignKeysRule(), new NullHeavyColumnsRule(), new EmptySourceTablesRule(), new LargeTablesRule(),
        new TemporalTablesRule(), new TargetFkCyclesRule(), new TargetNotEmptyRule(), new VersionDowngradeRule(),
    };

    public static Finding ForTable(IRule rule, Severity severity, string side, TableInfo t, string message, long? count = null) => new()
    {
        Rule = rule.Id, Title = rule.Title, Severity = severity, Side = side, Object = t.Key,
        Anchor = $"table:{side}:{t.Key}", Message = message, Count = count,
    };

    public static Finding ForColumn(IRule rule, Severity severity, string side, TableInfo t, ColumnInfo c, string message) => new()
    {
        Rule = rule.Id, Title = rule.Title, Severity = severity, Side = side, Object = $"{t.Key}.{c.Name}",
        Anchor = $"column:{side}:{t.Key}.{c.Name}", Message = message,
    };

    public static Finding ForDatabase(IRule rule, Severity severity, string message) => new()
    {
        Rule = rule.Id, Title = rule.Title, Severity = severity, Side = "both", Object = "database", Message = message,
    };

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static IEnumerable<(string Side, CatalogSnapshot Snapshot)> BothSides(RuleContext ctx)
    {
        yield return ("src", ctx.Src);
        yield return ("tgt", ctx.Tgt);
    }
}

public sealed class NoPrimaryKeyRule : IRule
{
    public string Id => "R01";
    public string Title => "No primary or unique key";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Src.Tables.Where(t => t.BestKey() is null))
            yield return t.Rows > 0
                ? Rules.ForTable(this, Severity.High, "src", t,
                    $"No primary key or NOT NULL unique key ({Rules.N(t.Rows)} rows): the transfer cannot resume this table mid-way; it loads in one transaction and restarts from scratch after a failure.")
                : Rules.ForTable(this, Severity.Low, "src", t, "No primary key or NOT NULL unique key (the table is empty).");
        foreach (var t in ctx.Tgt.Tables.Where(t => t.BestKey() is null))
            yield return Rules.ForTable(this, Severity.Low, "tgt", t,
                "Target table has no primary or unique key: duplicate rows cannot be detected and a staging MERGE needs explicit key columns.");
    }
}

public sealed class DeprecatedTypeRule : IRule
{
    public string Id => "R02";
    public string Title => "Deprecated data type";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var (side, snapshot) in Rules.BothSides(ctx))
            foreach (var t in snapshot.Tables)
                foreach (var c in t.Columns.Where(c => TypeTraits.IsDeprecated(c.DataType)))
                {
                    var replacement = c.DataType switch { "text" => "varchar(max)", "ntext" => "nvarchar(max)", _ => "varbinary(max)" };
                    yield return Rules.ForColumn(this, Severity.Medium, side, t, c, side == "src"
                        ? $"`{c.DataType}` is deprecated; read it as CAST(... AS {replacement}) in the source query."
                        : $"Target column uses the deprecated `{c.DataType}` type; values must be converted on load ({replacement} recommended).");
                }
    }
}

public sealed class LobColumnsRule : IRule
{
    public string Id => "R03";
    public string Title => "Large object column";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var (side, snapshot) in Rules.BothSides(ctx))
            foreach (var t in snapshot.Tables)
                foreach (var c in t.Columns.Where(TypeTraits.IsModernLob))
                    yield return Rules.ForColumn(this, Severity.Low, side, t, c, side == "src"
                        ? $"LOB column ({c.TypeDisplay}): streamed during the transfer; tasks reading it use 5,000-row chunks."
                        : $"Target LOB column ({c.TypeDisplay}): large values increase load time and log usage.");
    }
}

public sealed class CollationMismatchRule : IRule
{
    public string Id => "R04";
    public string Title => "Collation difference";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        var srcCollation = ctx.Src.Server.DatabaseCollation;
        var tgtCollation = ctx.Tgt.Server.DatabaseCollation;
        if (!string.Equals(srcCollation, tgtCollation, StringComparison.OrdinalIgnoreCase))
            yield return Rules.ForDatabase(this, Severity.Medium,
                $"Database collations differ (source {srcCollation}, target {tgtCollation}): sorting, case sensitivity and string lookups may behave differently after the move.");
        foreach (var (side, snapshot) in Rules.BothSides(ctx))
        {
            var dbCollation = snapshot.Server.DatabaseCollation;
            foreach (var t in snapshot.Tables)
                foreach (var c in t.Columns.Where(c => c.Collation is not null && !string.Equals(c.Collation, dbCollation, StringComparison.OrdinalIgnoreCase)))
                    yield return Rules.ForColumn(this, Severity.Info, side, t, c,
                        $"Column collation {c.Collation} differs from the database default {dbCollation}.");
        }
    }
}

public sealed class TargetIdentityRule : IRule
{
    public string Id => "R05";
    public string Title => "Identity column in target";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Tgt.Tables)
        {
            var identity = t.Columns.FirstOrDefault(c => c.IsIdentity);
            if (identity is null) continue;
            yield return Rules.ForTable(this, Severity.Info, "tgt", t,
                $"Identity column {identity.Name}: loading with IDENTITY_INSERT keeps source key values; without it the target generates new values and child rows must be re-keyed.");
        }
    }
}

public sealed class TargetTriggersRule : IRule
{
    public string Id => "R06";
    public string Title => "Triggers on target table";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Tgt.Tables.Where(t => t.TriggerCount > 0))
        {
            var names = t.TriggerNames.Count > 0 ? $" ({string.Join(", ", t.TriggerNames)})" : "";
            yield return Rules.ForTable(this, Severity.Medium, "tgt", t,
                $"{t.TriggerCount} trigger(s){names}: bulk load does not fire triggers unless FireTriggers is enabled; decide whether their side effects are required.",
                t.TriggerCount);
        }
    }
}

public sealed class TargetComputedRule : IRule
{
    public string Id => "R07";
    public string Title => "Computed column in target";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Tgt.Tables)
            foreach (var c in t.Columns.Where(c => c.IsComputed))
                yield return Rules.ForColumn(this, Severity.Info, "tgt", t, c,
                    $"Computed column{(c.ComputedDefinition is null ? "" : $" {c.ComputedDefinition}")}: derived by the server and excluded from the load.");
    }
}

public sealed class TargetRowVersionRule : IRule
{
    public string Id => "R08";
    public string Title => "Rowversion column in target";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Tgt.Tables)
            foreach (var c in t.Columns.Where(c => c.IsRowVersion))
                yield return Rules.ForColumn(this, Severity.Info, "tgt", t, c,
                    "rowversion column: generated by the server on every write and excluded from the load.");
    }
}

public sealed class UntrustedForeignKeysRule : IRule
{
    public string Id => "R09";
    public string Title => "Untrusted or disabled foreign key";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Src.Tables)
            foreach (var fk in t.ForeignKeys.Where(f => f.IsNotTrusted || f.IsDisabled))
            {
                var state = fk.IsDisabled ? "disabled" : "not trusted";
                var reference = $"{fk.Name} ({string.Join(", ", fk.Columns)} → {fk.RefKey})";
                if (!ctx.OrphanCounts.TryGetValue(RuleContext.OrphanKey(t.Key, fk.Name), out var orphans))
                    yield return Rules.ForTable(this, Severity.Medium, "src", t,
                        $"Foreign key {reference} is {state}; orphan rows were not counted (check skipped).");
                else if (orphans > 0)
                    yield return Rules.ForTable(this, Severity.High, "src", t,
                        $"{Rules.N(orphans)} orphan row(s) violate {reference}, which is {state}: the target's foreign key will reject them unless they are fixed, filtered or re-parented.",
                        orphans);
                else
                    yield return Rules.ForTable(this, Severity.Low, "src", t,
                        $"Foreign key {reference} is {state}, but no orphan rows were found.", 0);
            }
    }
}

public sealed class NullHeavyColumnsRule : IRule
{
    public const double Threshold = 0.95;
    public string Id => "R10";
    public string Title => "Mostly NULL column";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var (side, snapshot) in Rules.BothSides(ctx))
            foreach (var t in snapshot.Tables)
                foreach (var c in t.Columns.Where(c => c.Profile is { SampledRows: > 0 } p && p.NullRatio > Threshold))
                {
                    var p = c.Profile!;
                    yield return Rules.ForColumn(this, Severity.Low, side, t, c,
                        $"{(p.NullRatio * 100).ToString("0.#", CultureInfo.InvariantCulture)}% NULL in the sample ({Rules.N(p.Nulls)} of {Rules.N(p.SampledRows)} rows): confirm it is still used or drop it.");
                }
    }
}

public sealed class EmptySourceTablesRule : IRule
{
    public string Id => "R11";
    public string Title => "Empty source table";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Src.Tables.Where(t => t.Rows == 0))
            yield return Rules.ForTable(this, Severity.Low, "src", t,
                "Source table is empty: nothing to transfer; record a drop decision in the mapping unless data is expected later.");
    }
}

public sealed class LargeTablesRule : IRule
{
    public const long Threshold = 10_000_000;
    public string Id => "R12";
    public string Title => "Large table";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Src.Tables.Where(t => t.Rows > Threshold))
            yield return Rules.ForTable(this, Severity.Info, "src", t,
                $"{Rules.N(t.Rows)} rows ({t.SizeMb.ToString("N0", CultureInfo.InvariantCulture)} MB): loaded in keyset chunks with checkpoints; expect a long-running task.",
                t.Rows);
    }
}

public sealed class TemporalTablesRule : IRule
{
    public string Id => "R13";
    public string Title => "Temporal table";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var (side, snapshot) in Rules.BothSides(ctx))
            foreach (var t in snapshot.Tables.Where(t => t.TemporalType is not null))
                yield return Rules.ForTable(this, Severity.Medium, side, t, (side, t.TemporalType) switch
                {
                    ("src", "history") => "Temporal history table: history rows are only migrated if they are mapped explicitly.",
                    ("src", _) => "System-versioned temporal table: only current rows are read; its history table is not migrated automatically.",
                    ("tgt", "history") => "Target temporal history table: it is maintained by the server and cannot be loaded while system versioning is on.",
                    _ => "Target is system-versioned: period columns are generated by the server; consider SYSTEM_VERSIONING = OFF during the load.",
                });
    }
}

public sealed class TargetFkCyclesRule : IRule
{
    public string Id => "R14";
    public string Title => "Foreign-key cycle in target";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        var tables = ctx.Tgt.Tables.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var t in tables)
            foreach (var fk in t.ForeignKeys.Where(f => string.Equals(f.RefKey, t.Key, StringComparison.OrdinalIgnoreCase)))
                yield return Rules.ForTable(this, Severity.Medium, "tgt", t,
                    $"Self-referencing foreign key {fk.Name}: rows must be loaded parents-first or with the constraint set to NOCHECK during the load.");
        foreach (var cycle in FkGraph.Cycles(tables))
        {
            var members = string.Join(" ↔ ", cycle);
            yield return new Finding
            {
                Rule = Id, Title = Title, Severity = Severity.Medium, Side = "tgt", Object = members, Anchor = $"table:tgt:{cycle[0]}",
                Message = $"Foreign-key cycle between {members}: load with the cycle's foreign keys set to NOCHECK and re-enable them WITH CHECK afterwards.",
            };
        }
    }
}

public sealed class TargetNotEmptyRule : IRule
{
    public string Id => "R15";
    public string Title => "Target table already has rows";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var t in ctx.Tgt.Tables.Where(t => t.Rows > 0))
            yield return Rules.ForTable(this, Severity.High, "tgt", t,
                $"Target table already has {Rules.N(t.Rows)} rows: migrated rows may collide with existing keys; decide between truncating first and merging.",
                t.Rows);
    }
}

public sealed class VersionDowngradeRule : IRule
{
    public string Id => "R16";
    public string Title => "Target older than source";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        var s = ctx.Src.Server;
        var t = ctx.Tgt.Server;
        if (t.MajorVersion < s.MajorVersion || t.CompatLevel < s.CompatLevel)
            yield return Rules.ForDatabase(this, Severity.Medium,
                $"Target is older than source (major version {s.MajorVersion} → {t.MajorVersion}, compatibility level {s.CompatLevel} → {t.CompatLevel}): functions and types used by source queries may be unavailable on the target.");
    }
}

/// <summary>Cycle detection over a catalog's FK graph (Tarjan SCC). Self-references are not reported here.</summary>
public static class FkGraph
{
    /// <summary>
    /// Strongly connected components with two or more tables; members sorted, components sorted by first member.
    /// Iterative (explicit-stack) Tarjan: a recursive DFS walks to the depth of the longest FK reference *path*
    /// before any frame pops, which a purely acyclic chain of only a few thousand tables is enough to overflow
    /// the CLR call stack (uncatchable in .NET, and fatal to the whole process). The explicit stack below keeps
    /// the same low-link algorithm but on the heap, so depth is bounded only by available memory.
    /// </summary>
    public static List<List<string>> Cycles(IReadOnlyList<TableInfo> tables)
    {
        var keys = tables.Select(t => t.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var known = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var edges = tables.ToDictionary(
            t => t.Key,
            t => t.ForeignKeys.Select(f => f.RefKey).Where(r => known.Contains(r) && !string.Equals(r, t.Key, StringComparison.OrdinalIgnoreCase))
                .Select(r => keys.First(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<List<string>>();

        // Each call-stack frame is (node, index of the next outgoing edge of that node still to visit); it
        // stands in for one activation of the recursive Visit(v) below the point where it examines edges[v][i].
        var frames = new Stack<(string Node, int EdgeIndex)>();

        foreach (var start in keys)
        {
            if (indices.ContainsKey(start)) continue;
            indices[start] = low[start] = index++;
            stack.Push(start);
            onStack.Add(start);
            frames.Push((start, 0));

            while (frames.Count > 0)
            {
                var (v, i) = frames.Pop();
                var vEdges = edges[v];
                if (i < vEdges.Count)
                {
                    var w = vEdges[i];
                    frames.Push((v, i + 1));   // resume v after w (its outgoing edge) is dealt with
                    if (!indices.ContainsKey(w))
                    {
                        indices[w] = low[w] = index++;
                        stack.Push(w);
                        onStack.Add(w);
                        frames.Push((w, 0));   // "recurse" into w before v resumes
                    }
                    else if (onStack.Contains(w))
                    {
                        low[v] = Math.Min(low[v], indices[w]);
                    }
                    continue;
                }

                // v has no more outgoing edges to examine: finish it, same as the end of a recursive Visit(v).
                if (low[v] == indices[v])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    } while (!string.Equals(member, v, StringComparison.OrdinalIgnoreCase));
                    if (component.Count > 1) result.Add(component.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
                }
                // Propagate v's low-link to whichever node "called" it, i.e. the frame now on top (a no-op tree
                // edge is folded away for a root, since frames is then empty).
                if (frames.Count > 0)
                {
                    var (parent, _) = frames.Peek();
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }

        return result.OrderBy(c => c[0], StringComparer.OrdinalIgnoreCase).ToList();
    }
}
