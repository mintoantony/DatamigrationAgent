using Dbm.Core.Catalog;
using Dbm.Core.Mapping;

namespace Dbm.Core.Matching;

/// <summary>Deterministic script draft of the mapping (spec §4.4). Pairs every target table with its best source table, then assigns
/// columns greedily one-to-one inside each pair; keeps agent/human decisions from <c>carryOver</c>.</summary>
public static class AutoMapper
{
    /// <summary>A column pair is only accepted when its name cosine reaches this floor, so type/structure/profile compatibility
    /// alone never produces a mapping.</summary>
    public const double MinNameForAccept = 0.3;

    /// <summary>Candidates are listed only when they carry some name evidence or reach the candidate band (keeps packets small).</summary>
    public const double MinNameForCandidate = 0.1;

    /// <summary>Table candidates below this score are noise and are not listed.</summary>
    public const double MinTableCandidateScore = 0.2;

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static MappingPayload Map(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms, MatchOptions options,
        MappingPayload? carryOver = null)
    {
        var ctx = new MatchContext(src, tgt, synonyms);
        var result = new MappingPayload();
        var carried = MappingCarryOver.Tables(carryOver, src, tgt);
        var primary = new Dictionary<string, string>(Ci);

        foreach (var t in tgt.Tables)
        {
            var ranked = src.Tables
                .Select(s => (Table: s, Score: TableScorer.Score(ctx, t, s)))
                .OrderByDescending(x => x.Score.Total).ThenBy(x => x.Table.Key, StringComparer.Ordinal)
                .ToList();
            TableMap map;
            if (carried.TryGetValue(t.Key, out var kept))
            {
                map = kept;
                map.Candidates = TableCandidates(ranked.Where(x => !kept.Sources.Contains(x.Table.Key, Ci)), options.TopK);
            }
            else if (ranked.Count > 0 && ranked[0].Score.Total >= options.Candidate)
            {
                var best = ranked[0];
                map = new TableMap
                {
                    Kind = "direct",
                    Sources = [best.Table.Key],
                    Confidence = best.Score.Total,
                    Method = TableMethod(ctx, t, best.Table, best.Score),
                    Candidates = TableCandidates(ranked.Skip(1), options.TopK)
                };
            }
            else
            {
                map = new TableMap
                {
                    Kind = "direct",
                    Confidence = ranked.Count > 0 ? ranked[0].Score.Total : 0,
                    Method = MapMethod.Vector,
                    Candidates = TableCandidates(ranked, options.TopK)
                };
            }
            if (map.Sources.Count > 0) primary[t.Key] = map.Sources[0];
            result.Tables[t.Key] = map;
        }

        foreach (var t in tgt.Tables)
        {
            var map = result.Tables[t.Key];
            if (map.Kind != "skip" && map.Sources.Count > 0 && src.FindTable(map.Sources[0]) is { } s)
                AssignColumns(ctx, t, s, map, primary, options);
        }

        MappingCarryOver.Drops(carryOver, src, result);
        var used = new HashSet<string>(result.Tables.Values.SelectMany(m => m.Sources), Ci);
        foreach (var s in src.Tables.Where(s => s.Rows == 0 && !used.Contains(s.Key) && !result.Drops.ContainsKey(s.Key)))
            result.Drops[s.Key] = new DropDecision("empty table with no matching target", MapMethod.Vector);
        if (carryOver is not null) result.Notes.AddRange(carryOver.Notes);
        return result;
    }

    private static void AssignColumns(MatchContext ctx, TableInfo t, TableInfo s, TableMap map,
        IReadOnlyDictionary<string, string> primary, MatchOptions options)
    {
        var targets = t.Columns.Where(c => !c.IsComputed && !c.IsRowVersion && !map.Columns.ContainsKey(c.Name)).ToList();
        var taken = new HashSet<string>(map.Columns.Values.SelectMany(cm => cm.SourceColumns), Ci);
        var sourceKey = s.BestKey() ?? [];
        var scored = new List<(ColumnInfo T, ColumnInfo S, ColumnScore Score)>();
        foreach (var tc in targets)
            foreach (var sc in s.Columns)
                scored.Add((tc, sc, ColumnScorer.Score(ctx, t, tc, s, sc, primary)));

        var assigned = new Dictionary<string, (ColumnInfo S, ColumnScore Score)>(Ci);
        foreach (var x in scored.OrderByDescending(x => x.Score.Total).ThenBy(x => x.T.Ordinal).ThenBy(x => x.S.Ordinal))
        {
            if (x.Score.Total < options.Candidate) break;
            if (assigned.ContainsKey(x.T.Name) || taken.Contains($"{s.Key}.{x.S.Name}")) continue;
            if (x.Score.Name < MinNameForAccept) continue;
            if (x.T.IsIdentity && !sourceKey.Contains(x.S.Name, Ci)) continue;
            assigned[x.T.Name] = (x.S, x.Score);
            taken.Add($"{s.Key}.{x.S.Name}");
        }

        foreach (var tc in targets)
        {
            var ranked = scored.Where(x => x.T.Name == tc.Name)
                .OrderByDescending(x => x.Score.Total).ThenBy(x => x.S.Ordinal)
                .Select(x => (x.S, x.Score)).ToList();
            if (assigned.TryGetValue(tc.Name, out var a))
            {
                map.Columns[tc.Name] = new ColumnMap
                {
                    Expr = "s." + QuoteName(a.S.Name),
                    SourceColumns = [$"{s.Key}.{a.S.Name}"],
                    Confidence = a.Score.Total,
                    Method = ColumnMethod(ctx, t, tc, s, a.S, a.Score),
                    TypeRisk = a.Score.Type.Risk,
                    Candidates = ColumnCandidates(s, ranked.Where(x => x.S.Name != a.S.Name), options)
                };
            }
            else if (!tc.IsIdentity)
            {
                map.Columns[tc.Name] = new ColumnMap
                {
                    Confidence = ranked.Count > 0 ? ranked[0].Score.Total : 0,
                    Method = MapMethod.Vector,
                    Candidates = ColumnCandidates(s, ranked, options)
                };
            }
        }
        map.Columns = map.Columns
            .OrderBy(kv => t.FindColumn(kv.Key)?.Ordinal ?? int.MaxValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static MapMethod TableMethod(MatchContext ctx, TableInfo t, TableInfo s, TableScore score) =>
        ctx.TableTokens(false, t).SequenceEqual(ctx.TableTokens(true, s)) ? MapMethod.Exact
        : score.Name >= 0.8 ? MapMethod.Fuzzy
        : MapMethod.Vector;

    private static MapMethod ColumnMethod(MatchContext ctx, TableInfo t, ColumnInfo tc, TableInfo s, ColumnInfo sc, ColumnScore score) =>
        ctx.ColumnTokens(false, t, tc).SequenceEqual(ctx.ColumnTokens(true, s, sc)) && score.Type.Level == CompatLevel.Exact ? MapMethod.Exact
        : score.Name >= 0.8 ? MapMethod.Fuzzy
        : MapMethod.Vector;

    private static List<Candidate>? TableCandidates(IEnumerable<(TableInfo Table, TableScore Score)> ranked, int topK)
    {
        var list = ranked.Where(x => x.Score.Total >= MinTableCandidateScore).Take(topK)
            .Select(x => new Candidate(x.Table.Key, x.Score.Total, x.Score.Why)).ToList();
        return list.Count == 0 ? null : list;
    }

    private static List<Candidate>? ColumnCandidates(TableInfo s, IEnumerable<(ColumnInfo S, ColumnScore Score)> ranked,
        MatchOptions options)
    {
        var list = ranked.Where(x => x.Score.Name >= MinNameForCandidate || x.Score.Total >= options.Candidate).Take(options.TopK)
            .Select(x => new Candidate($"{s.Key}.{x.S.Name}", x.Score.Total, x.Score.Why)).ToList();
        return list.Count == 0 ? null : list;
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";
}
