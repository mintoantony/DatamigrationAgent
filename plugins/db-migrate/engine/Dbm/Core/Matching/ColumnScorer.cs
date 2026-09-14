using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Matching;

public sealed record ColumnScore(double Total, double Name, TypeCompatResult Type, double Structure, double? Profile)
{
    public string Why => string.Create(CultureInfo.InvariantCulture,
        $"name {Name:0.00}, type {Type.Level.ToString().ToLowerInvariant()}, structure {Structure:0.00}, profile {(Profile is double p ? p.ToString("0.00", CultureInfo.InvariantCulture) : "n/a")}");
}

/// <summary>Column-pair score (spec §4.4): 0.45·name + 0.20·type + 0.15·structure + 0.20·profile;
/// when the source column has no profile its 0.20 moves to name.</summary>
public static class ColumnScorer
{
    public const double NameWeight = 0.45, TypeWeight = 0.20, StructureWeight = 0.15, ProfileWeight = 0.20;
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <param name="pairs">target table key → primary source table key (case-insensitive), used by the FK part of Structure.</param>
    public static ColumnScore Score(MatchContext ctx, TableInfo tgtTable, ColumnInfo tgtCol, TableInfo srcTable, ColumnInfo srcCol,
        IReadOnlyDictionary<string, string> pairs)
    {
        var name = ctx.ColumnNameSimilarity(tgtTable, tgtCol, srcTable, srcCol);
        var type = TypeCompat.Check(ColumnType.From(srcCol), ColumnType.From(tgtCol), srcCol.Profile);
        var structure = Structure(tgtTable, tgtCol, srcTable, srcCol, pairs);
        var profile = Profile(tgtTable, tgtCol, srcCol, ctx.ColumnTokens(false, tgtTable, tgtCol));
        var total = profile is double p
            ? NameWeight * name + TypeWeight * type.Score + StructureWeight * structure + ProfileWeight * p
            : (NameWeight + ProfileWeight) * name + TypeWeight * type.Score + StructureWeight * structure;
        return new ColumnScore(Math.Round(total, 3), name, type, structure, profile);
    }

    /// <summary>Mean of four parts: PK role equal; FK role (both FK and referenced tables paired = 1, both FK otherwise = 0.5,
    /// neither = 1, only one = 0); identity equal; nullability (equal = 1, target NOT NULL from nullable source = 0, else 0.75).</summary>
    public static double Structure(TableInfo tgtTable, ColumnInfo tgtCol, TableInfo srcTable, ColumnInfo srcCol,
        IReadOnlyDictionary<string, string> pairs)
    {
        var pk = InPrimaryKey(tgtTable, tgtCol) == InPrimaryKey(srcTable, srcCol) ? 1.0 : 0.0;
        var tFk = ForeignKeyOf(tgtTable, tgtCol);
        var sFk = ForeignKeyOf(srcTable, srcCol);
        double fk;
        if (tFk is null && sFk is null) fk = 1.0;
        else if (tFk is null || sFk is null) fk = 0.0;
        else fk = pairs.TryGetValue(tFk.RefKey, out var paired) && Ci.Equals(paired, sFk.RefKey) ? 1.0 : 0.5;
        var identity = tgtCol.IsIdentity == srcCol.IsIdentity ? 1.0 : 0.0;
        var nullability = tgtCol.IsNullable == srcCol.IsNullable ? 1.0 : !tgtCol.IsNullable ? 0.0 : 0.75;
        return (pk + fk + identity + nullability) / 4;
    }

    /// <summary>Mean of: semantic class (vs target profile class, else vs target type/name), null ratio (vs target profile,
    /// else NOT NULL target with source nulls = 0), distinct ratio (vs target profile, else only for single-column unique targets),
    /// length (string targets). Null when the source column has no profile.</summary>
    public static double? Profile(TableInfo tgtTable, ColumnInfo tgtCol, ColumnInfo srcCol, IReadOnlyList<string> tgtTokens)
    {
        var sp = srcCol.Profile;
        if (sp is null || sp.SampledRows == 0) return null;
        var tp = tgtCol.Profile is { SampledRows: > 0 } targetProfile ? targetProfile : null;
        var family = TypeCompat.Family(tgtCol.DataType);
        var parts = new List<double>();
        if (sp.SemanticClass is string cls)
            parts.Add(tp?.SemanticClass is string tcls ? (tcls == cls ? 1.0 : 0.0) : ClassFit(cls, tgtCol, family, tgtTokens));
        parts.Add(tp is not null ? 1 - Math.Abs(sp.NullRatio - tp.NullRatio) : !tgtCol.IsNullable && sp.Nulls > 0 ? 0.0 : 1.0);
        if (sp.DistinctRatio is double sd)
        {
            if (tp?.DistinctRatio is double td) parts.Add(1 - Math.Abs(sd - td));
            else if (IsSingleColumnUnique(tgtTable, tgtCol)) parts.Add(sd);
        }
        if (sp.MaxLen is int sm && sm > 0 && family == "string")
        {
            if (tp?.MaxLen is int tm && tm > 0) parts.Add(Math.Min(sm, tm) / (double)Math.Max(sm, tm));
            else parts.Add(tgtCol.MaxLength <= 0 || sm <= tgtCol.MaxLength ? 1.0 : (double)tgtCol.MaxLength / sm);
        }
        return parts.Average();
    }

    private static double ClassFit(string cls, ColumnInfo tgtCol, string family, IReadOnlyList<string> tokens)
    {
        bool Has(params string[] words) => words.Any(w => tokens.Contains(w, Ci));
        return cls switch
        {
            "email" => Has("email", "mail") ? 1.0 : 0.3,
            "phone" => Has("phone", "telephone", "mobile", "fax") ? 1.0 : 0.3,
            "url" => Has("url", "website", "link", "uri") ? 1.0 : 0.3,
            "postal_code" => Has("postal", "zip", "postcode") ? 1.0 : 0.3,
            "country_code" => Has("country") ? 1.0 : 0.3,
            "guid" => family == "guid" ? 1.0 : 0.3,
            "date" or "datetime" => family is "date" or "datetime" ? 1.0 : 0.3,
            "integer" => family is "integer" or "decimal" or "float" ? 1.0 : family == "string" ? 0.5 : 0.2,
            "decimal" => family is "decimal" or "float" ? 1.0 : family == "integer" ? 0.5 : 0.2,
            "flag" => family == "bit" || family == "string" && tgtCol.MaxLength is > 0 and <= 1 ? 1.0 : 0.3,
            _ => family == "string" ? 1.0 : 0.3
        };
    }

    private static bool InPrimaryKey(TableInfo t, ColumnInfo c) => t.PrimaryKey?.Columns.Contains(c.Name, Ci) == true;

    private static ForeignKeyInfo? ForeignKeyOf(TableInfo t, ColumnInfo c) =>
        t.ForeignKeys.FirstOrDefault(f => f.Columns.Contains(c.Name, Ci));

    private static bool IsSingleColumnUnique(TableInfo t, ColumnInfo c) =>
        t.Indexes.Any(i => i.IsUnique && i.Columns.Count == 1 && Ci.Equals(i.Columns[0], c.Name));
}
