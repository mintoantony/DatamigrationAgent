using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Matching;

public enum CompatLevel { Exact, Widening, Risky, Incompatible }

public sealed record ColumnType(string DataType, int MaxLength, int Precision, int Scale)
{
    public static ColumnType From(ColumnInfo c) => new(c.DataType.ToLowerInvariant(), c.MaxLength, c.Precision, c.Scale);

    /// <summary>Parses declaration text such as "int", "nvarchar(50)", "varchar(max)", "decimal(19,4)", "datetime2(3)".
    /// Lengths follow the catalog convention: characters for (n)char/(n)varchar, bytes for (var)binary, -1 = max, 0 otherwise.
    /// Never throws: this runs inside a background job, so an unclosed paren, an empty argument list or a non-numeric
    /// size argument degrades to exactly the result the bare type name (no parentheses at all) would produce, rather
    /// than killing the job.</summary>
    public static ColumnType Parse(string declaration)
    {
        var text = declaration.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');
        var close = open < 0 ? -1 : text.IndexOf(')', open + 1);
        var name = open < 0 ? text : text[..open].Trim();
        var rawArgs = open < 0 || close < 0
            ? Array.Empty<string>()
            : text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        var args = ParseArgs(rawArgs);
        int Arg(int index, int fallback) => args.Length > index && args[index] is int n ? n : fallback;
        return name switch
        {
            "char" or "nchar" or "binary" or "varchar" or "nvarchar" or "varbinary" => new(name, Arg(0, 1), 0, 0),
            "decimal" or "numeric" => new(name, 0, Arg(0, 18), Arg(1, 0)),
            "money" => new(name, 0, 19, 4),
            "smallmoney" => new(name, 0, 10, 4),
            "float" => new(name, 0, Arg(0, 53), 0),
            "real" => new(name, 0, 24, 0),
            "tinyint" => new(name, 0, 3, 0),
            "smallint" => new(name, 0, 5, 0),
            "int" => new(name, 0, 10, 0),
            "bigint" => new(name, 0, 19, 0),
            "bit" => new(name, 0, 1, 0),
            "date" => new(name, 0, 10, 0),
            "smalldatetime" => new(name, 0, 16, 0),
            "datetime" => new(name, 0, 23, 3),
            "time" or "datetime2" or "datetimeoffset" => new(name, 0, 0, Arg(0, 7)),
            _ => new(name, 0, 0, 0)
        };
    }

    /// <summary>Parses every raw argument as an integer or the literal "max" (-1). If ANY argument is malformed
    /// (non-numeric, or an empty string from something like "nvarchar()"), the whole list degrades to empty so
    /// callers fall back exactly as if no parentheses were present at all — see the class doc on <see cref="Parse"/>.</summary>
    private static int?[] ParseArgs(string[] raw)
    {
        var parsed = new int?[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == "max") { parsed[i] = -1; continue; }
            if (!int.TryParse(raw[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return Array.Empty<int?>();
            parsed[i] = n;
        }
        return parsed;
    }
}

public sealed record TypeCompatResult(CompatLevel Level, double Score, string? Risk);

public static class TypeCompat
{
    private enum Fam { Int, Dec, Approx, Bit, Str, Bin, Date, Time, DateTime, Guid, Xml, Variant, Spatial, Hier, RowVer, Other }

    // Len: characters or bytes, long.MaxValue = max. P: integer digits (Int), precision (Dec/Approx). S: scale / fractional-second digits.
    private readonly record struct N(string Name, Fam Fam, long Len, int P, int S, bool Unicode = false, int Rank = 0, bool Offset = false);

    // A single risk contributor. Hard = always a real caveat, never softened by evidence. Soft = a sampled profile
    // positively confirmed safety for THIS caveat, but a TOP-n sample is evidence, not proof, so the hazard text is
    // retained (with a "sampled" marker) rather than erased — see Combine and the INVARIANT it encodes.
    private readonly record struct RiskItem(string Text, bool Hard);

    private const long Max = long.MaxValue;
    private static readonly TypeCompatResult ExactResult = new(CompatLevel.Exact, 1.0, null);
    private static readonly TypeCompatResult WideningResult = new(CompatLevel.Widening, 0.9, null);
    private static readonly TypeCompatResult RawBytes = new(CompatLevel.Risky, 0.5, "stored as raw bytes");

    /// <summary>The risk dbm stores for a changed column whose conversion it cannot evaluate. An ordinary risk text.</summary>
    public const string UnevaluatedRisk = "not evaluated: custom expression";

    /// <summary>The class of the sentinel. It matches nothing, not even another sentinel.</summary>
    public const string UnevaluatedClass = "unevaluated";

    private static readonly System.Text.RegularExpressions.Regex SampledSuffix =
        new(@"\s*\(sampled [^)]*\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ObservedLength =
        new(@"\((?:source max \d+(?: bytes)?|source length unbounded)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A stable class for a risk TEXT, derived from the text alone: every sampled-rows suffix "(sampled …)" is removed and
    /// every source length "(source max N)", "(source max N bytes)" or "(source length unbounded)" becomes
    /// "(source max)" — the parts a re-profile of unchanged data can flip. Null or blank → null; the sentinel →
    /// <see cref="UnevaluatedClass"/>. Compare with <see cref="SameHazard"/>, never with ==.</summary>
    public static string? HazardClass(string? risk)
    {
        if (string.IsNullOrWhiteSpace(risk)) return null;
        if (risk.Trim() == UnevaluatedRisk) return UnevaluatedClass;
        // Whole-text replacements, not a split on "; ": the sampled suffix itself contains "; ".
        return ObservedLength.Replace(SampledSuffix.Replace(risk.Trim(), ""), "(source max)");
    }

    /// <summary>True when two risk texts name the same hazard. Null and the sentinel match nothing.</summary>
    public static bool SameHazard(string? a, string? b) =>
        HazardClass(a) is { } ca && ca != UnevaluatedClass && ca == HazardClass(b);

    /// <summary>The stored riskClass: hazard class plus the DECLARED source and target types, e.g.
    /// "may truncate (source max)|varchar(300)->nvarchar(200)". Never observed lengths or sample counts. Null when there is no
    /// risk; <see cref="UnevaluatedClass"/> for the sentinel.</summary>
    public static string? RiskClass(string? risk, ColumnInfo source, ColumnInfo target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var hazard = HazardClass(risk);
        return hazard is null or UnevaluatedClass ? hazard : $"{hazard}|{source.TypeDisplay}->{target.TypeDisplay}";
    }

    /// <summary>True when two stored riskClass values name the same hazard over the same declared types. Null and the sentinel
    /// class match nothing.</summary>
    public static bool SameRiskClass(string? a, string? b) =>
        a is not null && a != UnevaluatedClass && string.Equals(a, b, StringComparison.Ordinal);

    public static TypeCompatResult Check(ColumnType src, ColumnType tgt, ColumnProfile? srcProfile = null)
    {
        var s = Norm(src);
        var t = Norm(tgt);
        if (t.Fam == Fam.RowVer) return Incompatible("rowversion columns are generated by the server and cannot be written");
        if (s.Name == t.Name && s.Len == t.Len && s.P == t.P && s.S == t.S) return ExactResult;
        if (s.Fam == Fam.Other || t.Fam == Fam.Other) return Risky($"unrecognised type conversion {s.Name} -> {t.Name}");
        var p = srcProfile is { SampledRows: > 0 } ? srcProfile : null;
        return s.Fam switch
        {
            Fam.Int => FromInt(s, t, p),
            Fam.Dec => FromDec(s, t, p),
            Fam.Approx => FromApprox(s, t),
            Fam.Bit => FromBit(s, t),
            Fam.Str => FromStr(s, t, p),
            Fam.Bin or Fam.RowVer => FromBin(s, t, p),
            Fam.Date => FromDate(s, t, p),
            Fam.Time => FromTime(s, t),
            Fam.DateTime => FromDateTime(s, t, p),
            Fam.Guid => FromGuid(s, t),
            Fam.Xml => t.Fam switch { Fam.Str => StrToStr(s, t, p), Fam.Bin => RawBytes, _ => Cannot(s, t) },
            Fam.Variant => t.Fam is Fam.Xml or Fam.Spatial or Fam.Hier ? Cannot(s, t) : Risky("conversion may fail"),
            Fam.Spatial => t.Fam switch
            {
                Fam.Spatial => Risky("needs a WKT round-trip (STAsText / STGeomFromText)"),
                Fam.Str => Risky("needs .STAsText()"),
                Fam.Bin => RawBytes,
                _ => Cannot(s, t)
            },
            Fam.Hier => t.Fam switch
            {
                Fam.Str => t.Len >= 4000 ? WideningResult : Risky("may truncate (needs up to 4000 characters)"),
                Fam.Bin => RawBytes,
                _ => Cannot(s, t)
            },
            _ => Cannot(s, t)
        };
    }

    /// <summary>Coarse family name used by the auto-mapper's profile scoring. Deliberately diverges from
    /// <see cref="TypeTraits.TypeClass"/>: float/real get their own "float" family here because their conversion
    /// risk profile (precision loss) differs sharply from exact decimals, and bit stays "bit" rather than being
    /// lumped as TypeTraits' "flag" or folded into integer/decimal. Do not "fix" the two into agreement — they
    /// answer different questions (conversion scoring here vs. profiling there).</summary>
    public static string Family(string dataType) => Norm(new ColumnType(dataType.ToLowerInvariant(), 0, 0, 0)).Fam switch
    {
        Fam.Int => "integer",
        Fam.Dec => "decimal",
        Fam.Approx => "float",
        Fam.Bit => "bit",
        Fam.Str => "string",
        Fam.Bin => "binary",
        Fam.Date => "date",
        Fam.Time => "time",
        Fam.DateTime => "datetime",
        Fam.Guid => "guid",
        Fam.Xml => "xml",
        Fam.Variant => "variant",
        Fam.Spatial => "spatial",
        Fam.Hier => "hierarchyid",
        Fam.RowVer => "rowversion",
        _ => "other"
    };

    private static N Norm(ColumnType c)
    {
        var name = c.DataType.ToLowerInvariant();
        long Len(int n) => n < 0 ? Max : n;
        return name switch
        {
            "tinyint" => new(name, Fam.Int, 0, 3, 0, Rank: 1),
            "smallint" => new(name, Fam.Int, 0, 5, 0, Rank: 2),
            "int" => new(name, Fam.Int, 0, 10, 0, Rank: 3),
            "bigint" => new(name, Fam.Int, 0, 19, 0, Rank: 4),
            "decimal" or "numeric" => new(name, Fam.Dec, 0, c.Precision > 0 ? c.Precision : 18, c.Scale),
            "money" => new(name, Fam.Dec, 0, 19, 4),
            "smallmoney" => new(name, Fam.Dec, 0, 10, 4),
            "float" => new(name, Fam.Approx, 0, c.Precision is > 0 and <= 24 ? 24 : 53, 0),
            "real" => new(name, Fam.Approx, 0, 24, 0),
            "bit" => new(name, Fam.Bit, 0, 1, 0),
            "char" or "varchar" => new(name, Fam.Str, Len(c.MaxLength), 0, 0),
            "text" => new(name, Fam.Str, Max, 0, 0),
            "nchar" or "nvarchar" => new(name, Fam.Str, Len(c.MaxLength), 0, 0, Unicode: true),
            "ntext" => new(name, Fam.Str, Max, 0, 0, Unicode: true),
            "binary" or "varbinary" => new(name, Fam.Bin, Len(c.MaxLength), 0, 0),
            "image" => new(name, Fam.Bin, Max, 0, 0),
            "timestamp" or "rowversion" => new("rowversion", Fam.RowVer, 8, 0, 0),
            "date" => new(name, Fam.Date, 0, 0, 0),
            "time" => new(name, Fam.Time, 0, 0, c.Scale),
            "smalldatetime" => new(name, Fam.DateTime, 0, 0, 0),
            "datetime" => new(name, Fam.DateTime, 0, 0, 3),
            "datetime2" => new(name, Fam.DateTime, 0, 0, c.Scale),
            "datetimeoffset" => new(name, Fam.DateTime, 0, 0, c.Scale, Offset: true),
            "uniqueidentifier" => new(name, Fam.Guid, 0, 0, 0),
            "xml" => new(name, Fam.Xml, Max, 0, 0, Unicode: true),
            "sql_variant" => new(name, Fam.Variant, 0, 0, 0),
            "geography" or "geometry" => new(name, Fam.Spatial, 0, 0, 0),
            "hierarchyid" => new(name, Fam.Hier, 0, 0, 0),
            _ => new(name, Fam.Other, 0, 0, 0)
        };
    }

    private static TypeCompatResult FromInt(N s, N t, ColumnProfile? p) => t.Fam switch
    {
        Fam.Int => t.Rank >= s.Rank
            ? WideningResult
            : p is { } prof && Fits(prof, IntMin(t), IntMax(t))
                ? SoftenedWidening($"overflow possible (target {t.Name})", prof)
                : Risky($"overflow possible (target {t.Name})"),
        Fam.Dec => t.P - t.S >= s.P
            ? WideningResult
            : p is { } prof && Fits(prof, -DecLimit(t), DecLimit(t))
                ? SoftenedWidening($"overflow possible (target {Display(t)})", prof)
                : Risky($"overflow possible (target {Display(t)})"),
        Fam.Approx => t.P == 24 && s.Rank >= 3 ? Risky("precision loss above 7 significant digits")
            : t.P == 53 && s.Rank == 4 ? Risky("precision loss above 15 significant digits")
            : WideningResult,
        Fam.Bit => Risky("non-zero values become 1"),
        Fam.Str => ToText(t, s.Rank == 1 ? 3 : s.P + 1),
        Fam.Variant => WideningResult,
        Fam.DateTime => NumberToDateTime(s, t),
        Fam.Bin => RawBytes,
        _ => Cannot(s, t)
    };

    private static TypeCompatResult FromDec(N s, N t, ColumnProfile? p)
    {
        var risks = new List<RiskItem>();
        switch (t.Fam)
        {
            case Fam.Int:
                if (s.S > 0) risks.Add(Hard("fractional part truncated"));
                if (s.P - s.S > t.P - 1)
                    risks.Add(p is { } prof && Fits(prof, IntMin(t), IntMax(t))
                        ? Soft($"overflow possible (target {t.Name})", prof)
                        : Hard($"overflow possible (target {t.Name})"));
                return Combine(risks);
            case Fam.Dec:
                if (t.P - t.S < s.P - s.S)
                    risks.Add(p is { } prof2 && Fits(prof2, -DecLimit(t), DecLimit(t))
                        ? Soft($"overflow possible (target {Display(t)})", prof2)
                        : Hard($"overflow possible (target {Display(t)})"));
                if (t.S < s.S) risks.Add(Hard($"rounded to {t.S} decimal places"));
                return Combine(risks);
            case Fam.Approx:
                return s.P <= (t.P == 24 ? 7 : 15) ? WideningResult : Risky("precision loss (approximate type)");
            case Fam.Bit:
                return Risky("non-zero values become 1");
            case Fam.Str:
                return ToText(t, s.P + (s.S > 0 ? 1 : 0) + 1);
            case Fam.Variant:
                return WideningResult;
            case Fam.DateTime:
                return NumberToDateTime(s, t);
            case Fam.Bin:
                return RawBytes;
            default:
                return Cannot(s, t);
        }
    }

    private static TypeCompatResult FromApprox(N s, N t) => t.Fam switch
    {
        Fam.Approx => t.P >= s.P ? WideningResult : Risky("precision loss (float to real)"),
        Fam.Int or Fam.Dec => Risky("rounding or overflow possible"),
        Fam.Bit => Risky("non-zero values become 1"),
        Fam.Str => Risky("string conversion rounds to 6 significant digits unless CONVERT style 3 is used"),
        Fam.Variant => WideningResult,
        Fam.DateTime => NumberToDateTime(s, t),
        Fam.Bin => RawBytes,
        _ => Cannot(s, t)
    };

    private static TypeCompatResult FromBit(N s, N t) => t.Fam switch
    {
        Fam.Int or Fam.Dec or Fam.Approx or Fam.Variant => WideningResult,
        Fam.Str => ToText(t, 1),
        Fam.DateTime => NumberToDateTime(s, t),
        Fam.Bin => RawBytes,
        _ => Cannot(s, t)
    };

    private static TypeCompatResult FromStr(N s, N t, ColumnProfile? p) => t.Fam switch
    {
        Fam.Str => StrToStr(s, t, p),
        Fam.Bit => Risky("needs a CASE transform"),
        Fam.Int or Fam.Dec or Fam.Approx or Fam.Date or Fam.Time or Fam.DateTime or Fam.Guid or Fam.Hier => Risky("conversion may fail"),
        Fam.Xml => Risky("conversion may fail (must be well-formed XML)"),
        Fam.Bin => Risky("converted to raw bytes"),
        Fam.Variant => s.Len == Max ? Incompatible("sql_variant cannot hold (max) or LOB values") : WideningResult,
        Fam.Spatial => Risky("conversion may fail (must be WKT text)"),
        _ => Cannot(s, t)
    };

    private static TypeCompatResult StrToStr(N s, N t, ColumnProfile? p)
    {
        var risks = new List<RiskItem>();
        if (s.Unicode && !t.Unicode) risks.Add(Hard("non-ASCII characters may be lost"));
        if (t.Len < s.Len)
        {
            var declaredHazard = s.Len == Max ? "may truncate (source length unbounded)" : $"may truncate (source max {s.Len})";
            var observed = p?.MaxLen;
            if (observed is int m && m > t.Len)
                risks.Add(Hard($"may truncate (source max {m})"));
            else if (observed is int && p is { } prof)
                risks.Add(Soft(declaredHazard, prof));
            else
                risks.Add(Hard(declaredHazard));
        }
        return Combine(risks);
    }

    private static TypeCompatResult FromBin(N s, N t, ColumnProfile? p)
    {
        switch (t.Fam)
        {
            case Fam.Bin:
                if (t.Len >= s.Len) return WideningResult;
                var declaredHazard = s.Len == Max ? "may truncate (source length unbounded)" : $"may truncate (source max {s.Len} bytes)";
                var observed = p?.MaxLen;
                if (observed is int m && m > t.Len) return Risky($"may truncate (source max {m} bytes)");
                if (observed is int && p is { } prof) return SoftenedWidening(declaredHazard, prof);
                return Risky(declaredHazard);
            case Fam.Str:
                return Risky("bytes reinterpreted as characters");
            case Fam.Guid:
                return Risky("conversion may fail");
            case Fam.Date:
                return Risky("conversion may fail (bytes reinterpreted as a date)");
            case Fam.Variant:
                return s.Len == Max ? Incompatible("sql_variant cannot hold (max) or LOB values") : WideningResult;
            default:
                return Cannot(s, t);
        }
    }

    private static TypeCompatResult FromDate(N s, N t, ColumnProfile? p) => t.Fam switch
    {
        Fam.DateTime => t.Name switch
        {
            "smalldatetime" => DateRisk(p, 1900, 2079, "dates outside 1900-2079 fail"),
            "datetime" => DateRisk(p, 1753, 9999, "dates before 1753 fail"),
            "datetimeoffset" => Risky("time zone offset assumed +00:00"),
            _ => WideningResult
        },
        Fam.Str => ToText(t, 10),
        Fam.Variant => WideningResult,
        Fam.Bin => RawBytes,
        _ => Cannot(s, t)
    };

    private static TypeCompatResult FromTime(N s, N t) => t.Fam switch
    {
        Fam.Time => t.S >= s.S ? WideningResult : Risky($"fractional seconds rounded to {t.S} digits"),
        Fam.DateTime => Risky("date part set to 1900-01-01"),
        Fam.Str => ToText(t, 8 + (s.S > 0 ? s.S + 1 : 0)),
        Fam.Variant => WideningResult,
        _ => Cannot(s, t)
    };

    private static TypeCompatResult FromDateTime(N s, N t, ColumnProfile? p)
    {
        switch (t.Fam)
        {
            case Fam.DateTime:
                var risks = new List<RiskItem>();
                if (t.Name == "smalldatetime")
                {
                    if (s.Name != "smalldatetime") risks.Add(Hard("seconds dropped"));
                    risks.Add(DateRiskItem(p, 1900, 2079, "dates outside 1900-2079 fail"));
                }
                else if (t.Name == "datetime")
                {
                    if (s.Name is "datetime2" or "datetimeoffset")
                    {
                        if (s.S > 0) risks.Add(Hard("fractional seconds rounded to 1/300 s"));
                        risks.Add(DateRiskItem(p, 1753, 9999, "dates before 1753 fail"));
                    }
                }
                else if (t.S < s.S)
                {
                    risks.Add(Hard($"fractional seconds rounded to {t.S} digits"));
                }
                if (s.Offset && !t.Offset) risks.Add(Hard("time zone offset dropped"));
                if (!s.Offset && t.Offset) risks.Add(Hard("time zone offset assumed +00:00"));
                return Combine(risks);
            case Fam.Date:
                return Risky("time part dropped");
            case Fam.Time:
                return Risky("date part dropped");
            case Fam.Int or Fam.Dec or Fam.Approx:
                return s.Name is "datetime" or "smalldatetime" ? Risky("converted to a day number since 1900-01-01") : Cannot(s, t);
            case Fam.Bin:
                return RawBytes;
            case Fam.Str:
                return ToText(t, 19 + (s.S > 0 ? s.S + 1 : 0) + (s.Offset ? 7 : 0));
            case Fam.Variant:
                return WideningResult;
            default:
                return Cannot(s, t);
        }
    }

    private static TypeCompatResult FromGuid(N s, N t) => t.Fam switch
    {
        Fam.Str => ToText(t, 36),
        Fam.Bin => Risky("stored as 16 raw bytes"),
        Fam.Variant => WideningResult,
        _ => Cannot(s, t)
    };

    private static TypeCompatResult NumberToDateTime(N s, N t) =>
        t.Name is "datetime" or "smalldatetime" ? Risky("number interpreted as days since 1900-01-01") : Cannot(s, t);

    private static TypeCompatResult ToText(N t, int needed) =>
        t.Len >= needed ? WideningResult : Risky($"may truncate (needs {needed} characters)");

    /// <summary>A date/datetime range caveat: Risky by default, softened to a sampled Widening when the profile's
    /// observed Min/Max prove the sampled rows fall inside [fromYear, toYear].</summary>
    private static TypeCompatResult DateRisk(ColumnProfile? p, int fromYear, int toYear, string hazard) =>
        Combine(new List<RiskItem> { DateRiskItem(p, fromYear, toYear, hazard) });

    private static RiskItem DateRiskItem(ColumnProfile? p, int fromYear, int toYear, string hazard) =>
        p is { } prof && DatesWithin(prof, fromYear, toYear) ? Soft(hazard, prof) : Hard(hazard);

    private static RiskItem Hard(string text) => new(text, true);

    private static RiskItem Soft(string hazard, ColumnProfile p) => new(SampledCaveat(hazard, p), false);

    /// <summary>INVARIANT: a sampled profile may lower a caveat's Level from Risky to Widening, but it must never
    /// delete the caveat — Risk is the only channel that carries the hazard on to Task 3.3's mapping and the
    /// mapping-architect agent's CAST/CONVERT/CASE decisions. So the declared hazard text is always retained, with an
    /// explicit "sampled" marker (and the sample size) making clear a TOP-n read is evidence, not proof.</summary>
    private static string SampledCaveat(string hazard, ColumnProfile p) =>
        $"{hazard} (sampled {p.SampledRows.ToString("N0", CultureInfo.InvariantCulture)} rows fit; not proof for the full table)";

    private static TypeCompatResult SoftenedWidening(string hazard, ColumnProfile p) =>
        new(CompatLevel.Widening, 0.9, SampledCaveat(hazard, p));

    private static TypeCompatResult Combine(List<RiskItem> risks)
    {
        if (risks.Count == 0) return WideningResult;
        var text = string.Join("; ", risks.Select(r => r.Text));
        return risks.Any(r => r.Hard) ? Risky(text) : new TypeCompatResult(CompatLevel.Widening, 0.9, text);
    }

    private static TypeCompatResult Risky(string risk) => new(CompatLevel.Risky, 0.5, risk);
    private static TypeCompatResult Incompatible(string risk) => new(CompatLevel.Incompatible, 0.0, risk);
    private static TypeCompatResult Cannot(N s, N t) => Incompatible($"{s.Name} cannot be converted to {t.Name}");

    private static string Display(N t) => t.Fam == Fam.Dec && t.Name is "decimal" or "numeric" ? $"{t.Name}({t.P},{t.S})" : t.Name;

    private static double IntMin(N t) => t.Name switch { "tinyint" => 0, "smallint" => short.MinValue, "int" => int.MinValue, _ => long.MinValue };
    private static double IntMax(N t) => t.Name switch { "tinyint" => 255, "smallint" => short.MaxValue, "int" => int.MaxValue, _ => long.MaxValue };
    private static double DecLimit(N t) => Math.Pow(10, t.P - t.S) - Math.Pow(10, -t.S);

    private static bool Fits(ColumnProfile p, double min, double max) =>
        double.TryParse(p.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
        && double.TryParse(p.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)
        && lo >= min && hi <= max;

    private static bool DatesWithin(ColumnProfile p, int fromYear, int toYear) =>
        DateTime.TryParse(p.Min, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lo)
        && DateTime.TryParse(p.Max, CultureInfo.InvariantCulture, DateTimeStyles.None, out var hi)
        && lo.Year >= fromYear && hi.Year <= toYear;
}
