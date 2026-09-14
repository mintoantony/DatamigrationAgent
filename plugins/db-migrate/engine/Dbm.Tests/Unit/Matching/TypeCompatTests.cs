using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Tests.Unit.Matching;

public class TypeCompatTests
{
    [Theory]
    // exact
    [InlineData("int", "int", null, null, null, "Exact", null)]
    [InlineData("nvarchar(50)", "nvarchar(50)", null, null, null, "Exact", null)]
    [InlineData("decimal(19,4)", "decimal(19,4)", null, null, null, "Exact", null)]
    [InlineData("datetime", "datetime", null, null, null, "Exact", null)]
    [InlineData("money", "money", null, null, null, "Exact", null)]
    [InlineData("text", "text", null, null, null, "Exact", null)]
    [InlineData("char(2)", "char(2)", null, null, null, "Exact", null)]
    [InlineData("sql_variant", "sql_variant", null, null, null, "Exact", null)]
    [InlineData("geography", "geography", null, null, null, "Exact", null)]
    // integers
    [InlineData("tinyint", "smallint", null, null, null, "Widening", null)]
    [InlineData("smallint", "int", null, null, null, "Widening", null)]
    [InlineData("int", "bigint", null, null, null, "Widening", null)]
    [InlineData("bigint", "int", null, null, null, "Risky", "overflow possible")]
    [InlineData("bigint", "int", null, "1", "1000", "Widening", "overflow possible")]
    [InlineData("int", "tinyint", null, "-5", "10", "Risky", "overflow possible")]
    [InlineData("int", "decimal(10,0)", null, null, null, "Widening", null)]
    [InlineData("int", "decimal(9,0)", null, null, null, "Risky", "overflow possible")]
    [InlineData("int", "decimal(9,0)", null, "1", "5000", "Widening", "overflow possible")]
    [InlineData("int", "money", null, null, null, "Widening", null)]
    [InlineData("int", "smallmoney", null, null, null, "Risky", "overflow possible")]
    [InlineData("int", "float", null, null, null, "Widening", null)]
    [InlineData("bigint", "float", null, null, null, "Risky", "precision loss")]
    [InlineData("int", "real", null, null, null, "Risky", "precision loss")]
    [InlineData("smallint", "real", null, null, null, "Widening", null)]
    [InlineData("int", "bit", null, null, null, "Risky", "non-zero values become 1")]
    [InlineData("int", "varchar(11)", null, null, null, "Widening", null)]
    [InlineData("int", "varchar(5)", null, null, null, "Risky", "may truncate (needs 11 characters)")]
    [InlineData("tinyint", "char(3)", null, null, null, "Widening", null)]
    [InlineData("int", "sql_variant", null, null, null, "Widening", null)]
    [InlineData("int", "uniqueidentifier", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("int", "date", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("int", "xml", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("int", "datetime", null, null, null, "Risky", "days since 1900-01-01")]
    [InlineData("int", "datetime2(7)", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("int", "varbinary(4)", null, null, null, "Risky", "stored as raw bytes")]
    [InlineData("tinyint", "int", null, null, null, "Widening", null)]
    [InlineData("smallint", "smallint", null, null, null, "Exact", null)]
    // exact decimals
    [InlineData("money", "decimal(19,4)", null, null, null, "Widening", null)]
    [InlineData("smallmoney", "money", null, null, null, "Widening", null)]
    [InlineData("decimal(19,4)", "decimal(10,2)", null, null, null, "Risky", "rounded to 2 decimal places")]
    [InlineData("decimal(19,4)", "decimal(10,4)", null, null, null, "Risky", "overflow possible (target decimal(10,4))")]
    [InlineData("decimal(10,2)", "decimal(12,2)", null, null, null, "Widening", null)]
    [InlineData("decimal(12,2)", "decimal(10,2)", null, "0", "99.5", "Widening", "overflow possible")]
    [InlineData("decimal(10,2)", "int", null, null, null, "Risky", "fractional part truncated")]
    [InlineData("decimal(9,0)", "int", null, null, null, "Widening", null)]
    [InlineData("decimal(12,0)", "int", null, null, null, "Risky", "overflow possible")]
    [InlineData("decimal(12,0)", "int", null, "0", "100", "Widening", "overflow possible")]
    [InlineData("decimal(12,2)", "int", null, "0", "100", "Risky", "fractional part truncated")]
    [InlineData("numeric(18,0)", "bigint", null, null, null, "Widening", null)]
    [InlineData("decimal(10,2)", "float", null, null, null, "Widening", null)]
    [InlineData("decimal(20,2)", "float", null, null, null, "Risky", "precision loss")]
    [InlineData("decimal(10,2)", "varchar(20)", null, null, null, "Widening", null)]
    [InlineData("decimal(10,2)", "varchar(5)", null, null, null, "Risky", "may truncate (needs 12 characters)")]
    [InlineData("decimal(10,2)", "bit", null, null, null, "Risky", "non-zero values become 1")]
    [InlineData("numeric(10,0)", "xml", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("decimal(10,2)", "date", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("decimal(10,2)", "varbinary(16)", null, null, null, "Risky", "stored as raw bytes")]
    // float / real
    [InlineData("real", "float", null, null, null, "Widening", null)]
    [InlineData("float", "real", null, null, null, "Risky", "precision loss")]
    [InlineData("float", "decimal(18,4)", null, null, null, "Risky", "rounding or overflow possible")]
    [InlineData("float", "int", null, null, null, "Risky", "rounding or overflow possible")]
    [InlineData("float", "varchar(50)", null, null, null, "Risky", "6 significant digits")]
    [InlineData("float", "bit", null, null, null, "Risky", "non-zero values become 1")]
    [InlineData("float", "date", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("float", "datetime", null, null, null, "Risky", "days since 1900-01-01")]
    // bit
    [InlineData("bit", "int", null, null, null, "Widening", null)]
    [InlineData("bit", "decimal(1,0)", null, null, null, "Widening", null)]
    [InlineData("bit", "char(1)", null, null, null, "Widening", null)]
    [InlineData("bit", "date", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("bit", "datetime", null, null, null, "Risky", "days since 1900-01-01")]
    // character strings
    [InlineData("varchar(100)", "nvarchar(100)", null, null, null, "Widening", null)]
    [InlineData("varchar(100)", "nvarchar(200)", null, null, null, "Widening", null)]
    [InlineData("varchar(100)", "nvarchar(50)", null, null, null, "Risky", "may truncate (source max 100)")]
    [InlineData("varchar(100)", "nvarchar(50)", 40, null, null, "Widening", "may truncate (source max 100)")]
    [InlineData("varchar(500)", "nvarchar(200)", 300, null, null, "Risky", "may truncate (source max 300)")]
    [InlineData("varchar(500)", "nvarchar(200)", null, null, null, "Risky", "may truncate (source max 500)")]
    [InlineData("nvarchar(50)", "varchar(50)", null, null, null, "Risky", "non-ASCII characters may be lost")]
    [InlineData("nvarchar(100)", "varchar(50)", null, null, null, "Risky", "non-ASCII characters may be lost; may truncate (source max 100)")]
    [InlineData("char(2)", "nvarchar(2)", null, null, null, "Widening", null)]
    [InlineData("varchar(10)", "char(10)", null, null, null, "Widening", null)]
    [InlineData("text", "nvarchar(max)", null, null, null, "Widening", null)]
    [InlineData("text", "varchar(max)", null, null, null, "Widening", null)]
    [InlineData("text", "nvarchar(200)", null, null, null, "Risky", "may truncate (source length unbounded)")]
    [InlineData("text", "nvarchar(200)", 150, null, null, "Widening", "may truncate (source length unbounded)")]
    [InlineData("ntext", "nvarchar(max)", null, null, null, "Widening", null)]
    [InlineData("ntext", "varchar(max)", null, null, null, "Risky", "non-ASCII characters may be lost")]
    [InlineData("varchar(max)", "text", null, null, null, "Widening", null)]
    [InlineData("char(1)", "bit", null, null, null, "Risky", "needs a CASE transform")]
    [InlineData("varchar(5)", "bit", null, null, null, "Risky", "needs a CASE transform")]
    [InlineData("varchar(20)", "int", null, null, null, "Risky", "conversion may fail")]
    [InlineData("varchar(20)", "decimal(10,2)", null, null, null, "Risky", "conversion may fail")]
    [InlineData("varchar(30)", "datetime2(7)", null, null, null, "Risky", "conversion may fail")]
    [InlineData("varchar(36)", "uniqueidentifier", null, null, null, "Risky", "conversion may fail")]
    [InlineData("nvarchar(max)", "xml", null, null, null, "Risky", "well-formed XML")]
    [InlineData("varchar(10)", "varbinary(10)", null, null, null, "Risky", "converted to raw bytes")]
    [InlineData("varchar(10)", "sql_variant", null, null, null, "Widening", null)]
    [InlineData("varchar(max)", "sql_variant", null, null, null, "Incompatible", "sql_variant cannot hold")]
    [InlineData("varchar(100)", "geography", null, null, null, "Risky", "must be WKT text")]
    [InlineData("varchar(100)", "hierarchyid", null, null, null, "Risky", "conversion may fail")]
    // binary
    [InlineData("varbinary(10)", "varbinary(20)", null, null, null, "Widening", null)]
    [InlineData("varbinary(20)", "varbinary(10)", null, null, null, "Risky", "may truncate (source max 20 bytes)")]
    [InlineData("varbinary(20)", "varbinary(10)", 8, null, null, "Widening", "may truncate (source max 20 bytes)")]
    [InlineData("image", "varbinary(max)", null, null, null, "Widening", null)]
    [InlineData("binary(16)", "uniqueidentifier", null, null, null, "Risky", "conversion may fail")]
    [InlineData("varbinary(10)", "varchar(10)", null, null, null, "Risky", "bytes reinterpreted as characters")]
    [InlineData("timestamp", "binary(8)", null, null, null, "Widening", null)]
    [InlineData("timestamp", "varbinary(8)", null, null, null, "Widening", null)]
    [InlineData("binary(8)", "date", null, null, null, "Risky", "bytes reinterpreted as a date")]
    // date
    [InlineData("date", "datetime2(0)", null, null, null, "Widening", null)]
    [InlineData("date", "datetime", null, null, null, "Risky", "dates before 1753 fail")]
    [InlineData("date", "datetime", null, "2000-01-01", "2020-12-31", "Widening", "dates before 1753 fail")]
    [InlineData("date", "smalldatetime", null, null, null, "Risky", "dates outside 1900-2079 fail")]
    [InlineData("date", "datetimeoffset(7)", null, null, null, "Risky", "time zone offset assumed +00:00")]
    [InlineData("date", "varchar(10)", null, null, null, "Widening", null)]
    [InlineData("date", "varchar(8)", null, null, null, "Risky", "may truncate (needs 10 characters)")]
    [InlineData("date", "binary(8)", null, null, null, "Risky", "stored as raw bytes")]
    [InlineData("date", "time(0)", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("date", "int", null, null, null, "Incompatible", "cannot be converted")]
    // time
    [InlineData("time(3)", "time(7)", null, null, null, "Widening", null)]
    [InlineData("time(7)", "time(3)", null, null, null, "Risky", "fractional seconds rounded to 3 digits")]
    [InlineData("time(0)", "datetime2(0)", null, null, null, "Risky", "date part set to 1900-01-01")]
    [InlineData("time(0)", "varchar(8)", null, null, null, "Widening", null)]
    [InlineData("time(7)", "varchar(8)", null, null, null, "Risky", "may truncate (needs 16 characters)")]
    [InlineData("time(0)", "date", null, null, null, "Incompatible", "cannot be converted")]
    // date + time
    [InlineData("datetime", "datetime2(3)", null, null, null, "Widening", null)]
    [InlineData("datetime", "datetime2(7)", null, null, null, "Widening", null)]
    [InlineData("datetime", "datetime2(0)", null, null, null, "Risky", "fractional seconds rounded to 0 digits")]
    [InlineData("datetime2(3)", "datetime2(7)", null, null, null, "Widening", null)]
    [InlineData("datetime2(7)", "datetime2(3)", null, null, null, "Risky", "fractional seconds rounded to 3 digits")]
    [InlineData("datetime", "date", null, null, null, "Risky", "time part dropped")]
    [InlineData("datetime", "time(3)", null, null, null, "Risky", "date part dropped")]
    [InlineData("smalldatetime", "datetime", null, null, null, "Widening", null)]
    [InlineData("datetime", "smalldatetime", null, null, null, "Risky", "seconds dropped")]
    [InlineData("datetime2(7)", "datetime", null, null, null, "Risky", "fractional seconds rounded to 1/300 s")]
    [InlineData("datetime2(0)", "datetime", null, "2000-01-01", "2001-01-01", "Widening", "dates before 1753 fail")]
    [InlineData("datetimeoffset(7)", "datetime2(7)", null, null, null, "Risky", "time zone offset dropped")]
    [InlineData("datetime", "datetimeoffset(7)", null, null, null, "Risky", "time zone offset assumed +00:00")]
    [InlineData("datetime", "varchar(30)", null, null, null, "Widening", null)]
    [InlineData("datetime", "varchar(10)", null, null, null, "Risky", "may truncate")]
    [InlineData("datetime", "int", null, null, null, "Risky", "day number")]
    [InlineData("datetime2(7)", "int", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("datetime", "varbinary(8)", null, null, null, "Risky", "stored as raw bytes")]
    // uniqueidentifier
    [InlineData("uniqueidentifier", "char(36)", null, null, null, "Widening", null)]
    [InlineData("uniqueidentifier", "nvarchar(20)", null, null, null, "Risky", "may truncate (needs 36 characters)")]
    [InlineData("uniqueidentifier", "binary(16)", null, null, null, "Risky", "stored as 16 raw bytes")]
    [InlineData("uniqueidentifier", "int", null, null, null, "Incompatible", "cannot be converted")]
    // xml
    [InlineData("xml", "nvarchar(max)", null, null, null, "Widening", null)]
    [InlineData("xml", "nvarchar(100)", null, null, null, "Risky", "may truncate")]
    [InlineData("xml", "varchar(max)", null, null, null, "Risky", "non-ASCII characters may be lost")]
    [InlineData("xml", "int", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("xml", "decimal(10,2)", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("xml", "sql_variant", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("xml", "varbinary(max)", null, null, null, "Risky", "stored as raw bytes")]
    // sql_variant
    [InlineData("sql_variant", "int", null, null, null, "Risky", "conversion may fail")]
    [InlineData("sql_variant", "xml", null, null, null, "Incompatible", "cannot be converted")]
    // spatial / hierarchyid
    [InlineData("geography", "geometry", null, null, null, "Risky", "WKT round-trip")]
    [InlineData("geometry", "nvarchar(max)", null, null, null, "Risky", "STAsText")]
    [InlineData("geography", "int", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("hierarchyid", "nvarchar(4000)", null, null, null, "Widening", null)]
    [InlineData("hierarchyid", "nvarchar(100)", null, null, null, "Risky", "may truncate")]
    [InlineData("hierarchyid", "int", null, null, null, "Incompatible", "cannot be converted")]
    [InlineData("geography", "varbinary(max)", null, null, null, "Risky", "stored as raw bytes")]
    [InlineData("hierarchyid", "varbinary(max)", null, null, null, "Risky", "stored as raw bytes")]
    // rowversion target, unknown types
    [InlineData("binary(8)", "timestamp", null, null, null, "Incompatible", "rowversion")]
    [InlineData("timestamp", "timestamp", null, null, null, "Incompatible", "rowversion")]
    [InlineData("mytype", "int", null, null, null, "Risky", "unrecognised type")]
    public void Check_follows_the_rule_table(string src, string tgt, int? maxLen, string? min, string? max, string level, string? risk)
    {
        var profile = maxLen is null && min is null && max is null
            ? null
            : new ColumnProfile(1000, 0, null, min, max, maxLen, null, null, [], []);

        var result = TypeCompat.Check(ColumnType.Parse(src), ColumnType.Parse(tgt), profile);

        var expectedLevel = Enum.Parse<CompatLevel>(level);
        Assert.Equal(expectedLevel, result.Level);
        Assert.Equal(ScoreFor(expectedLevel), result.Score);
        if (risk is null) Assert.Null(result.Risk);
        else Assert.Contains(risk, result.Risk);
    }

    private static double ScoreFor(CompatLevel level) => level switch
    {
        CompatLevel.Exact => 1.0,
        CompatLevel.Widening => 0.9,
        CompatLevel.Risky => 0.5,
        CompatLevel.Incompatible => 0.0,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    [Fact]
    public void Hard_and_sample_supported_soft_risks_combine_without_dropping_either()
    {
        // decimal(12,2) -> int hits both paths at once: the fractional part is ALWAYS truncated (Hard, unrelated to
        // any sample), while the overflow check is satisfied by the 0..100 profile (Soft). The Hard item must still
        // force Risky overall (a sample can never launder away an unrelated real risk — the point of this whole
        // fix round), and the Soft item's "sampled" evidence marker must still survive in the combined text rather
        // than being silently dropped when combined with a Hard item.
        var profile = new ColumnProfile(1000, 0, null, "0", "100", null, null, null, [], []);
        var result = TypeCompat.Check(ColumnType.Parse("decimal(12,2)"), ColumnType.Parse("int"), profile);

        Assert.Equal(CompatLevel.Risky, result.Level);
        Assert.Equal(0.5, result.Score);
        Assert.NotNull(result.Risk);
        Assert.Contains("fractional part truncated", result.Risk);
        Assert.Contains("sampled", result.Risk);
    }

    [Fact]
    public void Sample_supported_widening_keeps_the_hazard_text_and_marks_it_as_sampled()
    {
        // The invariant this pins down: a sampled profile may LOWER Risky to Widening, but must never DELETE the
        // caveat — Risk is the only channel that carries the hazard on to the mapping phase's typeRisk field.
        var profile = new ColumnProfile(1000, 0, null, "1", "1000", null, null, null, [], []);
        var result = TypeCompat.Check(ColumnType.Parse("bigint"), ColumnType.Parse("int"), profile);

        Assert.Equal(CompatLevel.Widening, result.Level);
        Assert.Equal(0.9, result.Score);
        Assert.NotNull(result.Risk);
        Assert.Contains("overflow possible", result.Risk);
        Assert.Contains("sampled", result.Risk);
        Assert.Contains("1,000", result.Risk);
    }

    [Theory]
    [InlineData("int", "int", 1.0)]
    [InlineData("int", "bigint", 0.9)]
    [InlineData("bigint", "int", 0.5)]
    [InlineData("int", "uniqueidentifier", 0.0)]
    public void Score_follows_the_level(string src, string tgt, double score)
    {
        Assert.Equal(score, TypeCompat.Check(ColumnType.Parse(src), ColumnType.Parse(tgt)).Score);
    }

    [Fact]
    public void Profile_without_sampled_rows_is_ignored()
    {
        var empty = new ColumnProfile(0, 0, null, null, null, 10, null, null, [], []);
        var result = TypeCompat.Check(ColumnType.Parse("varchar(100)"), ColumnType.Parse("nvarchar(50)"), empty);
        Assert.Equal(CompatLevel.Risky, result.Level);
    }

    [Fact]
    public void From_uses_catalog_column_fields()
    {
        var column = new ColumnInfo("Name", 2, "NVARCHAR", 150, 0, 0, false, false, false, false, null, null, null);
        Assert.Equal(new ColumnType("nvarchar", 150, 0, 0), ColumnType.From(column));
    }

    [Theory]
    [InlineData("varchar(max)", "varchar", -1, 0, 0)]
    [InlineData("decimal(19,4)", "decimal", 0, 19, 4)]
    [InlineData("datetime2", "datetime2", 0, 0, 7)]
    [InlineData("datetime2(3)", "datetime2", 0, 0, 3)]
    [InlineData("INT", "int", 0, 10, 0)]
    public void Parse_reads_declarations(string text, string type, int maxLength, int precision, int scale)
    {
        Assert.Equal(new ColumnType(type, maxLength, precision, scale), ColumnType.Parse(text));
    }

    [Theory]
    [InlineData("decimal(x,4)")]
    [InlineData("nvarchar(50")]
    [InlineData("nvarchar()")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_never_throws_on_malformed_input(string text)
    {
        var result = ColumnType.Parse(text);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("decimal(x,4)", "decimal")]
    [InlineData("nvarchar(50", "nvarchar")]
    [InlineData("nvarchar()", "nvarchar")]
    public void Parse_degrades_a_malformed_size_to_the_no_argument_result(string malformed, string bareType)
    {
        Assert.Equal(ColumnType.Parse(bareType), ColumnType.Parse(malformed));
    }

    [Theory]
    [InlineData("int", "integer")]
    [InlineData("money", "decimal")]
    [InlineData("nvarchar", "string")]
    [InlineData("datetime2", "datetime")]
    [InlineData("timestamp", "rowversion")]
    [InlineData("mystery", "other")]
    public void Family_names_the_type_group(string type, string family)
    {
        Assert.Equal(family, TypeCompat.Family(type));
    }

    // ---- Task 3.4 fix round 1: HazardClass and RiskClass -------------------------------------------------------------

    [Theory]
    [InlineData("may truncate (source max 300)", "may truncate (source max)")]
    [InlineData("may truncate (source max 297)", "may truncate (source max)")]
    [InlineData("may truncate (source length unbounded)", "may truncate (source max)")]
    [InlineData("may truncate (source max 100) (sampled 1,000 rows fit; not proof for the full table)", "may truncate (source max)")]
    [InlineData("may truncate (source max 8000 bytes)", "may truncate (source max)")]
    [InlineData("overflow possible (target int) (sampled 1,000 rows fit; not proof for the full table)", "overflow possible (target int)")]
    [InlineData("non-ASCII characters may be lost; may truncate (source max 300)", "non-ASCII characters may be lost; may truncate (source max)")]
    [InlineData("may truncate (needs 11 characters)", "may truncate (needs 11 characters)")]
    [InlineData("fractional seconds rounded to 0 digits", "fractional seconds rounded to 0 digits")]
    [InlineData("not evaluated: custom expression", TypeCompat.UnevaluatedClass)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void HazardClass_strips_only_what_a_reprofile_of_unchanged_data_can_flip(string? risk, string? expected) =>
        Assert.Equal(expected, TypeCompat.HazardClass(risk));

    [Fact]
    public void SameHazard_never_matches_null_or_the_sentinel()
    {
        Assert.True(TypeCompat.SameHazard("may truncate (source max 300)", "may truncate (source max 297) (sampled 10 rows fit; not proof for the full table)"));
        Assert.False(TypeCompat.SameHazard("may truncate (source max 300)", "time part dropped"));
        Assert.False(TypeCompat.SameHazard(TypeCompat.UnevaluatedRisk, TypeCompat.UnevaluatedRisk));
        Assert.False(TypeCompat.SameHazard(null, null));
    }

    [Fact]
    public void RiskClass_is_the_hazard_class_over_the_declared_types()
    {
        var src = new ColumnInfo("CMNT", 1, "varchar", 500, 0, 0, true, false, false, false, null, null, null);
        var tgt = new ColumnInfo("Comment", 1, "nvarchar", 200, 0, 0, true, false, false, false, null, null, null);

        Assert.Equal("may truncate (source max)|varchar(500)->nvarchar(200)", TypeCompat.RiskClass("may truncate (source max 297)", src, tgt));
        Assert.NotEqual(TypeCompat.RiskClass("may truncate (source max 300)", src, tgt),
            TypeCompat.RiskClass("may truncate (source max 300)", src, tgt with { MaxLength = 250 }));
        Assert.Null(TypeCompat.RiskClass(null, src, tgt));
        Assert.Equal(TypeCompat.UnevaluatedClass, TypeCompat.RiskClass(TypeCompat.UnevaluatedRisk, src, tgt));
        Assert.False(TypeCompat.SameRiskClass(TypeCompat.UnevaluatedClass, TypeCompat.UnevaluatedClass));
        Assert.False(TypeCompat.SameRiskClass(null, null));
    }
}
