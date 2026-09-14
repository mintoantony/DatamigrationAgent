# Milestone 3 — Mapping

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** a deterministic auto-mapper drafts the source→target mapping from the stored catalogs, the `mapping-architect` subagent resolves what the script could not decide (splits, merges, lookups, transforms, drops), and the human reviews and edits the mapping in the web UI until nothing blocks approval. After this milestone the MAPPING phase runs the full review loop: `automap` job → agent draft → human review / direct edit / feedback → rework → approve. The C15 sample pair (LegacyShop → ShopV2) is the fixture for every test: `SampleCatalogs` mirrors its schema in code (kept honest by an integration test against the real databases), and `SampleMappings.Approved()` is the ground-truth mapping that M4/M5 tests build on.

**Tasks:** 3.1 type compatibility · 3.2 mapping payload + validator (+ sample catalogs/mapping) · 3.3 auto-mapper + `automap` job + `dbm map auto` · 3.4 mapping module + `mapping-architect` agent · 3.5 mapping UI + context endpoint.

**Conventions used by every task below**

- Paths are relative to the repository root `D:\DatamigrationAgent`; the engine lives in `plugins/db-migrate/engine`.
- Test commands run from the repository root. Unit tests of one class: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~<Class>"`. Integration tests need SQL Server (env `DBM_TEST_SQL`, default LocalDB) and carry `[Trait("Category","Integration")]`.
- Commits use Git Bash (`git commit -F - <<'EOF' … EOF`) so the two attribution lines from the Global Constraints stay one trailer block.
- The existing `Dbm.Tests` project already has `<Using Include="Xunit" />` (T0.1). If it does not, add `using Xunit;` at the top of each test file below.

---

### Task 3.1: Type compatibility

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Matching/TypeCompat.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/TypeCompatTests.cs`

**Interfaces:**
- Consumes (C10, T2.2/T2.3): `ColumnInfo(Name, Ordinal, DataType, MaxLength, Precision, Scale, IsNullable, IsIdentity, IsComputed, IsRowVersion, DefaultDefinition, Collation, Description)`; `ColumnProfile(SampledRows, Nulls, Distinct, Min, Max, MaxLen, AvgLen, SemanticClass, TopPatterns, Samples)`. Catalog convention: `DataType` = lower-case `sys.types` name (rowversion columns report `timestamp`), `MaxLength` = characters for (n)char/(n)varchar, bytes for (var)binary, `-1` = max, `0` for every other type.
- Produces (C11):
  ```csharp
  namespace Dbm.Core.Matching;
  public enum CompatLevel { Exact, Widening, Risky, Incompatible }
  public sealed record ColumnType(string DataType, int MaxLength, int Precision, int Scale)
  {
      public static ColumnType From(ColumnInfo c);
      public static ColumnType Parse(string declaration);          // added: "nvarchar(50)", "varchar(max)", "decimal(19,4)", "datetime2(3)", "int"
  }
  public sealed record TypeCompatResult(CompatLevel Level, double Score, string? Risk);   // exact 1.0, widening 0.9, risky 0.5, incompatible 0.0
  public static class TypeCompat
  {
      public static TypeCompatResult Check(ColumnType src, ColumnType tgt, ColumnProfile? srcProfile = null);
      public static string Family(string dataType);   // added: integer|decimal|float|bit|string|binary|date|time|datetime|guid|xml|variant|spatial|hierarchyid|rowversion|other
  }
  ```

**Rule summary (what the tests pin down).** Families: integers (tinyint < smallint < int < bigint); exact decimals (decimal/numeric by precision/scale, money ≈ decimal(19,4), smallmoney ≈ decimal(10,4)); float/real; bit; character strings (char/varchar/text ≈ varchar(max); nchar/nvarchar/ntext ≈ nvarchar(max); xml behaves like an unbounded Unicode string when converted to text); binary/varbinary/image (≈ varbinary(max)), rowversion ≈ binary(8) as a source; date; time(p); smalldatetime/datetime/datetime2(p)/datetimeoffset(p); uniqueidentifier; xml; sql_variant; geography/geometry; hierarchyid. Same type and size → Exact. Same family and fits → Widening, with Risk null when no profile was involved. Anything that can lose data → Risky with an explicit Risk text; a source profile whose sample fits (MaxLen, Min/Max, date range) lowers a size/range risk to Widening (score 0.9) **but never clears the Risk text** — the softened text keeps the hazard phrase, names the sample size, and contains the token `sampled`. A `TOP n` read is evidence, not proof, and `typeRisk` is the only channel carrying the hazard to the mapping-architect agent (see its Procedure step 3) and the human approver, so erasing it would report a silently truncating conversion as safe. (Amended after the Task 3.1 review; the original text mandated a full flip to Risk null.) Note how the two halves are pinned: the profile-flip rows in the table below assert the **hazard phrase** (proving it survives the softening, which is the substantive half), while the dedicated `Sample_supported_widening_keeps_the_hazard_text_and_marks_it_as_sampled` Fact asserts the `sampled` marker, and `Hard_and_sample_supported_soft_risks_combine_without_dropping_either` asserts both at once. `Assert.Contains` pins only one substring per row, which is why the coverage is split this way rather than duplicated. **Incompatible only when SQL Server has no conversion at all** (`"<src> cannot be converted to <tgt>"`, e.g. int↔uniqueidentifier, numbers→date/time/datetime2, datetime2→int, xml↔numbers); anything SQL Server converts implicitly or with `CAST` is Widening or Risky — e.g. int→bit Risky "non-zero values become 1" (the result of `CASE … THEN 1 ELSE 0 END` into a bit column), int/decimal/float/bit→datetime Risky "number interpreted as days since 1900-01-01", datetime→int Risky, numbers/dates/xml/spatial/hierarchyid→binary Risky "stored as raw bytes", binary→date Risky, string→geography Risky. M4's SQL validation relies on this (the sample plan must validate with warnings, not errors). Any rowversion **target** is Incompatible (the server generates it). Unknown type names → Risky `"unrecognised type conversion …"`.

- [ ] **Step 1: Write the failing test**

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/TypeCompatTests.cs`:

```csharp
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
    // Mixed Hard+Soft in one result: the fractional-truncation hazard is Hard (a range sample is no evidence about it)
    // and forces Risky, even though the 0..100 profile satisfies the overflow hazard. Pins that sample evidence can
    // never launder away an unrelated real risk — the invariant the T3.1 review's Critical turned on.
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
    // AMENDED after the Task 3.1 review (see the SDD ledger, "Task 3.1 review"): a source profile may LOWER the level but
    // must never NULL the risk. Sample-supported conversions are (Widening, 0.9, non-null risk) that retains the hazard
    // phrase and contains the token "sampled" — a TOP n read is evidence, not proof, and typeRisk is the only channel
    // that carries the hazard to the mapping-architect agent and the human approver. The nine profile-flip rows below
    // therefore expect a risk substring, not null. Unsampled widenings still carry Risk == null.
    public void Check_follows_the_rule_table(string src, string tgt, int? maxLen, string? min, string? max, string level, string? risk)
    {
        var profile = maxLen is null && min is null && max is null
            ? null
            : new ColumnProfile(1000, 0, null, min, max, maxLen, null, null, [], []);

        var result = TypeCompat.Check(ColumnType.Parse(src), ColumnType.Parse(tgt), profile);

        Assert.Equal(Enum.Parse<CompatLevel>(level), result.Level);
        if (risk is null) Assert.Null(result.Risk);
        else Assert.Contains(risk, result.Risk);
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
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~TypeCompatTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'TypeCompat' could not be found` (also `ColumnType`, `CompatLevel`).

- [ ] **Step 3: Implement `TypeCompat.cs`**

Create `plugins/db-migrate/engine/Dbm/Core/Matching/TypeCompat.cs`:

```csharp
using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Matching;

public enum CompatLevel { Exact, Widening, Risky, Incompatible }

public sealed record ColumnType(string DataType, int MaxLength, int Precision, int Scale)
{
    public static ColumnType From(ColumnInfo c) => new(c.DataType.ToLowerInvariant(), c.MaxLength, c.Precision, c.Scale);

    /// <summary>Parses declaration text such as "int", "nvarchar(50)", "varchar(max)", "decimal(19,4)", "datetime2(3)".
    /// Lengths follow the catalog convention: characters for (n)char/(n)varchar, bytes for (var)binary, -1 = max, 0 otherwise.</summary>
    public static ColumnType Parse(string declaration)
    {
        var text = declaration.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');
        var name = open < 0 ? text : text[..open].Trim();
        var args = open < 0
            ? Array.Empty<string>()
            : text[(open + 1)..text.LastIndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
        int Arg(int index, int fallback) => args.Length <= index ? fallback
            : args[index] == "max" ? -1 : int.Parse(args[index], CultureInfo.InvariantCulture);
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
}

public sealed record TypeCompatResult(CompatLevel Level, double Score, string? Risk);

public static class TypeCompat
{
    private enum Fam { Int, Dec, Approx, Bit, Str, Bin, Date, Time, DateTime, Guid, Xml, Variant, Spatial, Hier, RowVer, Other }

    // Len: characters or bytes, long.MaxValue = max. P: integer digits (Int), precision (Dec/Approx). S: scale / fractional-second digits.
    private readonly record struct N(string Name, Fam Fam, long Len, int P, int S, bool Unicode = false, int Rank = 0, bool Offset = false);

    private const long Max = long.MaxValue;
    private static readonly TypeCompatResult ExactResult = new(CompatLevel.Exact, 1.0, null);
    private static readonly TypeCompatResult WideningResult = new(CompatLevel.Widening, 0.9, null);
    private static readonly TypeCompatResult RawBytes = new(CompatLevel.Risky, 0.5, "stored as raw bytes");

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

    /// <summary>Coarse family name used by the auto-mapper's profile scoring.</summary>
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
        Fam.Int => t.Rank >= s.Rank || Fits(p, IntMin(t), IntMax(t)) ? WideningResult : Risky($"overflow possible (target {t.Name})"),
        Fam.Dec => t.P - t.S >= s.P || Fits(p, -DecLimit(t), DecLimit(t)) ? WideningResult : Risky($"overflow possible (target {Display(t)})"),
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
        var risks = new List<string>();
        switch (t.Fam)
        {
            case Fam.Int:
                if (s.S > 0) risks.Add("fractional part truncated");
                if (s.P - s.S > t.P - 1 && !Fits(p, IntMin(t), IntMax(t))) risks.Add($"overflow possible (target {t.Name})");
                return Combine(risks);
            case Fam.Dec:
                if (t.P - t.S < s.P - s.S && !Fits(p, -DecLimit(t), DecLimit(t))) risks.Add($"overflow possible (target {Display(t)})");
                if (t.S < s.S) risks.Add($"rounded to {t.S} decimal places");
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
        var risks = new List<string>();
        if (s.Unicode && !t.Unicode) risks.Add("non-ASCII characters may be lost");
        if (t.Len < s.Len)
        {
            var observed = p?.MaxLen;
            if (observed is null || observed > t.Len)
                risks.Add(observed is int m ? $"may truncate (source max {m})"
                    : s.Len == Max ? "may truncate (source length unbounded)"
                    : $"may truncate (source max {s.Len})");
        }
        return Combine(risks);
    }

    private static TypeCompatResult FromBin(N s, N t, ColumnProfile? p)
    {
        switch (t.Fam)
        {
            case Fam.Bin:
                if (t.Len >= s.Len || p?.MaxLen is int fits && fits <= t.Len) return WideningResult;
                return Risky(p?.MaxLen is int m ? $"may truncate (source max {m} bytes)"
                    : s.Len == Max ? "may truncate (source length unbounded)"
                    : $"may truncate (source max {s.Len} bytes)");
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
            "smalldatetime" => DatesWithin(p, 1900, 2079) ? WideningResult : Risky("dates outside 1900-2079 fail"),
            "datetime" => DatesWithin(p, 1753, 9999) ? WideningResult : Risky("dates before 1753 fail"),
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
                var risks = new List<string>();
                if (t.Name == "smalldatetime")
                {
                    if (s.Name != "smalldatetime") risks.Add("seconds dropped");
                    if (!DatesWithin(p, 1900, 2079)) risks.Add("dates outside 1900-2079 fail");
                }
                else if (t.Name == "datetime")
                {
                    if (s.Name is "datetime2" or "datetimeoffset")
                    {
                        if (s.S > 0) risks.Add("fractional seconds rounded to 1/300 s");
                        if (!DatesWithin(p, 1753, 9999)) risks.Add("dates before 1753 fail");
                    }
                }
                else if (t.S < s.S)
                {
                    risks.Add($"fractional seconds rounded to {t.S} digits");
                }
                if (s.Offset && !t.Offset) risks.Add("time zone offset dropped");
                if (!s.Offset && t.Offset) risks.Add("time zone offset assumed +00:00");
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

    private static TypeCompatResult Combine(List<string> risks) =>
        risks.Count == 0 ? WideningResult : Risky(string.Join("; ", risks));

    private static TypeCompatResult Risky(string risk) => new(CompatLevel.Risky, 0.5, risk);
    private static TypeCompatResult Incompatible(string risk) => new(CompatLevel.Incompatible, 0.0, risk);
    private static TypeCompatResult Cannot(N s, N t) => Incompatible($"{s.Name} cannot be converted to {t.Name}");

    private static string Display(N t) => t.Fam == Fam.Dec && t.Name is "decimal" or "numeric" ? $"{t.Name}({t.P},{t.S})" : t.Name;

    private static double IntMin(N t) => t.Name switch { "tinyint" => 0, "smallint" => short.MinValue, "int" => int.MinValue, _ => long.MinValue };
    private static double IntMax(N t) => t.Name switch { "tinyint" => 255, "smallint" => short.MaxValue, "int" => int.MaxValue, _ => long.MaxValue };
    private static double DecLimit(N t) => Math.Pow(10, t.P - t.S) - Math.Pow(10, -t.S);

    private static bool Fits(ColumnProfile? p, double min, double max) =>
        p is not null
        && double.TryParse(p.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
        && double.TryParse(p.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)
        && lo >= min && hi <= max;

    private static bool DatesWithin(ColumnProfile? p, int fromYear, int toYear) =>
        p is not null
        && DateTime.TryParse(p.Min, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lo)
        && DateTime.TryParse(p.Max, CultureInfo.InvariantCulture, DateTimeStyles.None, out var hi)
        && lo.Year >= fromYear && hi.Year <= toYear;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~TypeCompatTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 182`.

- [ ] **Step 5: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Matching/TypeCompat.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/TypeCompatTests.cs
git commit -F - <<'EOF'
feat(matching): type compatibility matrix with explicit risk texts (T3.1)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 3.2: Mapping payload + validator (+ sample catalogs and ground-truth mapping)

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Matching/MatchOptions.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingPayload.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingValidator.cs`
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/SampleCatalogs.cs`, `plugins/db-migrate/engine/Dbm.Tests/Support/SampleMappings.cs`, `plugins/db-migrate/engine/Dbm.Tests/Support/SampleExtract.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingValidatorTests.cs`
- Test (integration): `plugins/db-migrate/engine/Dbm.Tests/Integration/Mapping/SampleCatalogFidelityTests.cs`

**Interfaces:**
- Consumes: C10 catalog records (`CatalogSnapshot.FindTable`, `TableInfo.FindColumn/Key/PrimaryKey/BestKey`, `ColumnInfo`, `IndexInfo`, `ForeignKeyInfo.RefKey`, `ColumnProfile`, `ObjectCounts`), `ServerMeta` (C3), `Json`/`EnumText` (C1), `Fingerprint.Compute` (T2.5), `CatalogExtractor.ExtractAsync`, `Profiler.ProfileAsync`, `ProfileOptions` (T2.2/T2.3), `SqlConnect.ProbeAsync/OpenAsync` (T1.3), `SamplePairFixture` (T2.1: `IClassFixture` giving one seeded scale-1 `SamplePair` with `SourceCs` / `TargetCs`).
- Produces:
  ```csharp
  namespace Dbm.Core.Matching;
  public sealed record MatchOptions(double AutoAccept = 0.85, double Candidate = 0.50, int TopK = 3);   // C11 (placed here because the validator needs it)

  namespace Dbm.Core.Mapping;   // C12 exactly: MapMethod, MappingPayload, TableMap, ColumnMap, Candidate, DropDecision, and
  public static class MappingValidator
  {
      public static List<string> Errors(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt);
      public static List<string> Blockers(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt);
      public static List<string> Attention(MappingPayload m, MatchOptions options);
      // added members:
      public static readonly IReadOnlyList<string> Kinds;                                    // direct, merge, lookup, skip
      public static bool NeedsReview(MapMethod method, double confidence, MatchOptions options);
      public static List<string> UncoveredSourceColumns(MappingPayload m, CatalogSnapshot src);
      public static (TableInfo Table, ColumnInfo Column)? ResolveSourceColumn(CatalogSnapshot catalog, string key);   // "schema.table.column", split on the last '.'
      public static TableMap? FindTableMap(MappingPayload m, string targetKey);              // exact key first, then case-insensitive
      public static ColumnMap? FindColumnMap(TableMap map, string column);
  }

  namespace Dbm.Tests.Support;
  public static class SampleCatalogs { public const string Collation; public static CatalogSnapshot Source(); public static CatalogSnapshot Target();
                                       public static List<string> Diff(CatalogSnapshot expected, CatalogSnapshot actual); }
  public static class SampleMappings { public static MappingPayload Approved(); }
  public static class SampleExtract  { public static Task<CatalogSnapshot> CatalogAsync(string connectionString, bool profile); }
  ```

**Validator messages (exact strings — the UI mirrors them in T3.5 and tests assert them):**

| Kind | Message |
|---|---|
| Error | `unknown target table '<key>'` · `target table '<key>' must be written as '<Catalog.Key>' (catalog case)` · `<t>: invalid kind '<k>' (use direct, merge, lookup or skip)` · `<t>: unknown source table '<s>'` · `unknown target column '<t>.<c>'` · `target column '<t>.<c>' must be written as '<Name>' (catalog case)` · `<t>.<c> is computed and cannot be written` / `<t>.<c> is a rowversion and cannot be written` · `<t>.<c>: expr is empty (use null for unmapped)` · `<t>.<c>: unknown source column '<sc>'` · `unknown drop '<k>' (use schema.table or schema.table.column)` · `drop '<k>' needs a reason` · `target table '<key>' has no mapping (expected an object)` · `<t>.<c>: no column mapping (expected an object)` · `drop '<k>' has no decision (expected an object)` |
| Blocker | `<t>: no table mapping (map a source table or set kind "skip")` · `<t>: no source table (choose one or set kind "skip")` · `<t>.<c>: NOT NULL without default needs an expression or default` · `source table <s> is not mapped or dropped (<n> columns)` (every column of the table uncovered) · `source column <s>.<c> is not mapped or dropped` |
| Attention | `<t>: no confident source table (best 0.42)` · `<t>: source <s> needs review (confidence 0.70, vector)` · `<t>.<c>: unmapped (best candidate 0.31)` · `<t>.<c>: <expr> needs review (confidence 0.62, fuzzy)` — only items with Method fuzzy/vector and Confidence < AutoAccept, skip maps excluded, tables in ordinal key order |

Null-tolerance rule (AMENDED after the Task 3.2 review; see the SDD ledger). `Errors`, `Blockers` and `Attention` must never throw on any input `Json.Deserialize<MappingPayload>` accepts. System.Text.Json on net8.0 accepts JSON null for a non-nullable dictionary value, so `{"tables":{"app.Customers":null}}` deserializes and then NREs inside the validator. (`RespectNullableAnnotations` is .NET 9+ and unavailable here — guard explicitly.) A null `TableMap`, `ColumnMap` or `DropDecision` produces its "has no mapping / no column mapping / has no decision (expected an object)" Error and is then skipped for the remaining checks on that entry, so one malformed entry yields one message rather than a cascade. Null *collections* (`sources`, `columns`, `sourceColumns`, `notes`) are treated as empty, not errors — a null `sources` is already caught by the `no source table` blocker, which is the better message. Null *elements inside* a collection are covered by the same blanket promise and are the easy case to miss: `{"sourceColumns":[null]}` deserializes fine, and `ResolveSourceColumn` must therefore guard its own `key` parameter (it is public API, so T3.5 can pass a null directly) rather than relying on its call sites. A null element produces the existing `<t>.<c>: unknown source column '<sc>'` message with an empty rendering, matching how a null `sources` entry already behaves — no new message string. The guarantee is the blanket one, not this enumeration: the enumeration has already been wrong once by stopping at collections. This matters because T3.4 and T3.5 feed this validator agent-authored and human-edited mapping JSON.

Ordering rule: `Attention` returns tables in `StringComparer.Ordinal` key order AND flagged columns within each table in `StringComparer.Ordinal` column-name order. Raw `Dictionary` enumeration order is not contractually guaranteed by .NET, and this output reaches a user-facing report. (`Attention` takes no catalog, so catalog column position is not available to sort by.)

Coverage rule: a source column is covered when it appears in the `SourceColumns` of any column of a non-`skip` table map, or is dropped (column key or whole-table key). Join keys used only in `From` must therefore be listed in the `SourceColumns` of the column that depends on the join (the ground truth lists `dbo.ORD_HDR.STATUS_ID` under `Orders.StatusCode`).

- [ ] **Step 1: Write the sample catalogs (test support)**

These mirror the C15 schemas exactly as `CatalogExtractor` reports them: `DataType` lower-case `sys.types` names (the rowversion column is `timestamp`), `Precision`/`Scale` as in `sys.columns` (int 10/0, tinyint 3/0, smallint 5/0, bit 1/0, money 19/4, decimal(19,4) 19/4, datetime 23/3, datetime2(0) 19/0, datetime2(3) 23/3, date 10/0, character types 0/0), `MaxLength` in characters for (n)char/(n)varchar (`-1` for max, `0` for `text` and all non-character types), `Collation` only on character columns (`text` included), `TemporalType`/`Description` null, one clustered PK index per table except the heaps, `ComputedDefinition` on the computed `DisplayName` and `TriggerNames` on `app.Orders` (M2 additions to C10; both are part of the fingerprint). ORD_HDR has 3005 rows (3000·s + 5 fixed anomalies). Profiles are plausible values for the key columns at scale 1 (they do not affect fingerprints).

Create `plugins/db-migrate/engine/Dbm.Tests/Support/SampleCatalogs.cs`:

```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Sql;

namespace Dbm.Tests.Support;

/// <summary>In-code catalogs identical in structure to the C15 LegacyShop (source) and ShopV2 (target) sample databases.
/// Integration test SampleCatalogFidelityTests keeps them faithful to what CatalogExtractor reads from the real schema.</summary>
public static class SampleCatalogs
{
    public const string Collation = "SQL_Latin1_General_CP1_CI_AS";
    public static readonly DateTimeOffset ExtractedAt = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    public static CatalogSnapshot Source() => new(
        Meta("LegacyShop"),
        [Addr(), AuditLog(), Cust(), OrdHdr(), OrdLine(), OrdStatus(), Prod(), TmpImport()],
        new ObjectCounts(Views: 0, Procedures: 0, Functions: 0, Triggers: 0, Synonyms: 0),
        ExtractedAt);

    public static CatalogSnapshot Target() => new(
        Meta("ShopV2"),
        [Addresses(), AuditEvents(), Customers(), OrderLines(), Orders(), Products()],
        new ObjectCounts(Views: 0, Procedures: 0, Functions: 0, Triggers: 1, Synonyms: 0),
        ExtractedAt);

    // ---------------- LegacyShop (dbo) ----------------

    private static TableInfo Cust() => Table("dbo", "CUST", 1000, 0.3,
        [
            Int("CUST_ID", 1, identity: true) with { Profile = Prof(1000, 0, 1000, "1", "1000", null, "integer") },
            Str("CUST_NM", 2, "varchar", 100, false) with { Profile = Prof(1000, 0, 1000, null, null, 19, "text", "First1 Last1", "First2 Last2", "First3 Last3") },
            Str("EMAIL_ADDR", 3, "varchar", 120, true) with { Profile = Prof(1000, 100, 900, null, null, 34, "email", "first1.last1@example.com", "first2.last2@example.com", "first3.last3@example.com") },
            Str("PHONE_NO", 4, "varchar", 30, true),
            DateTimeCol("DOB", 5, true),
            DateTimeCol("CRT_DT", 6, false, "(getdate())"),
            Str("FAX_NO", 7, "varchar", 30, true),
            new ColumnInfo("NOTES", 8, "text", 0, 0, 0, true, false, false, false, null, Collation, null)
        ],
        [Pk("PK_CUST", "CUST_ID")],
        []);

    private static TableInfo Addr() => Table("dbo", "ADDR", 1500, 0.2,
        [
            Int("ADDR_ID", 1, identity: true) with { Profile = Prof(1500, 0, 1500, "1", "1500", null, "integer") },
            Int("CUST_ID", 2) with { Profile = Prof(1500, 0, 1000, "1", "1000", null, "integer") },
            Str("LINE1", 3, "varchar", 200, false),
            Str("CITY", 4, "varchar", 80, false),
            Str("ZIP", 5, "varchar", 12, true) with { Profile = Prof(1500, 0, 1500, null, null, 5, "postal_code", "10001", "10002", "10003") },
            Str("CTRY_CD", 6, "char", 2, false) with { Profile = Prof(1500, 0, 4, null, null, 2, "country_code", "US", "GB", "DE") }
        ],
        [Pk("PK_ADDR", "ADDR_ID")],
        [Fk("FK_ADDR_CUST", "CUST_ID", "dbo", "CUST", "CUST_ID")]);

    private static TableInfo Prod() => Table("dbo", "PROD", 200, 0.1,
        [
            Int("PROD_ID", 1, identity: true) with { Profile = Prof(200, 0, 200, "1", "200", null, "integer") },
            Str("PROD_NM", 2, "varchar", 150, false),
            Str("PROD_DESC", 3, "varchar", 500, true),
            Money("UNIT_PRC", 4) with { Profile = Prof(200, 0, 200, "1.0000", "200.0000", null, "decimal") },
            Str("ACTIVE_FLG", 5, "char", 1, false) with { Profile = Prof(200, 0, 2, "N", "Y", 1, "flag", "Y", "N") }
        ],
        [Pk("PK_PROD", "PROD_ID")],
        []);

    private static TableInfo OrdStatus() => Table("dbo", "ORD_STATUS", 4, 0.01,
        [
            new ColumnInfo("STATUS_ID", 1, "tinyint", 0, 3, 0, false, false, false, false, null, null, null) with { Profile = Prof(4, 0, 4, "1", "4", null, "integer") },
            Str("STATUS_CD", 2, "varchar", 10, false) with { Profile = Prof(4, 0, 4, null, null, 9, "text", "NEW", "PAID", "SHIPPED") },
            Str("STATUS_DESC", 3, "varchar", 50, false)
        ],
        [Pk("PK_ORD_STATUS", "STATUS_ID")],
        []);

    private static TableInfo OrdHdr() => Table("dbo", "ORD_HDR", 3005, 0.4,
        [
            Int("ORD_ID", 1, identity: true) with { Profile = Prof(3005, 0, 3005, "1", "3005", null, "integer") },
            Int("CUST_ID", 2) with { Profile = Prof(3005, 0, 1002, "-2", "1000", null, "integer") },
            DateTimeCol("ORD_DT", 3, false),
            new ColumnInfo("STATUS_ID", 4, "tinyint", 0, 3, 0, false, false, false, false, null, null, null) with { Profile = Prof(3005, 0, 4, "1", "4", null, "integer") },
            Int("SHIP_ADDR_ID", 5, nullable: true),
            Money("TOTAL_AMT", 6) with { Profile = Prof(3005, 0, 2900, "1.0000", "9999.0000", null, "decimal") },
            Str("CMNT", 7, "varchar", 500, true) with { Profile = Prof(3005, 2500, 400, null, null, 300, "text", "Leave at the door") }
        ],
        [Pk("PK_ORD_HDR", "ORD_ID")],
        [
            Fk("FK_ORD_CUST", "CUST_ID", "dbo", "CUST", "CUST_ID", notTrusted: true),
            Fk("FK_ORD_STATUS", "STATUS_ID", "dbo", "ORD_STATUS", "STATUS_ID")
        ]);

    private static TableInfo OrdLine() => Table("dbo", "ORD_LINE", 9002, 0.6,
        [
            Int("ORD_ID", 1) with { Profile = Prof(9002, 0, 3002, "1", "3002", null, "integer") },
            new ColumnInfo("LINE_NO", 2, "smallint", 0, 5, 0, false, false, false, false, null, null, null) with { Profile = Prof(9002, 0, 3, "1", "3", null, "integer") },
            Int("PROD_ID", 3) with { Profile = Prof(9002, 0, 200, "1", "200", null, "integer") },
            Int("QTY", 4) with { Profile = Prof(9002, 0, 10, "0", "10", null, "integer") },
            Money("UNIT_PRC", 5) with { Profile = Prof(9002, 0, 200, "1.0000", "200.0000", null, "decimal") }
        ],
        [new IndexInfo("PK_ORD_LINE", true, true, true, ["ORD_ID", "LINE_NO"])],
        [
            Fk("FK_LINE_ORD", "ORD_ID", "dbo", "ORD_HDR", "ORD_ID"),
            Fk("FK_LINE_PROD", "PROD_ID", "dbo", "PROD", "PROD_ID")
        ]);

    private static TableInfo AuditLog() => Table("dbo", "AUDIT_LOG", 5000, 0.5,
        [
            DateTimeCol("LOG_TS", 1, false),
            Str("USR", 2, "varchar", 50, false),
            Str("ACTION_TXT", 3, "varchar", 200, false)
        ],
        [],
        []);

    private static TableInfo TmpImport() => Table("dbo", "TMP_IMPORT", 0, 0,
        [Int("X", 1, nullable: true)],
        [],
        []);

    // ---------------- ShopV2 (app) ----------------

    private static TableInfo Customers() => Table("app", "Customers", 0, 0,
        [
            Int("CustomerId", 1, identity: true),
            Str("FirstName", 2, "nvarchar", 50, false),
            Str("LastName", 3, "nvarchar", 50, false),
            Str("Email", 4, "nvarchar", 120, true),
            Str("Phone", 5, "nvarchar", 30, true),
            new ColumnInfo("BirthDate", 6, "date", 0, 10, 0, true, false, false, false, null, null, null),
            new ColumnInfo("CreatedAt", 7, "datetime2", 0, 19, 0, false, false, false, false, "(sysutcdatetime())", null, null),
            Int("PrimaryAddressId", 8, nullable: true),
            Str("Notes", 9, "nvarchar", -1, true),
            new ColumnInfo("DisplayName", 10, "nvarchar", 101, 0, 0, false, false, true, false, null, Collation, null)
                { ComputedDefinition = "(([FirstName]+N' ')+[LastName])" }
        ],
        [Pk("PK_Customers", "CustomerId")],
        [Fk("FK_Customers_PrimaryAddress", "PrimaryAddressId", "app", "Addresses", "AddressId")]);

    private static TableInfo Addresses() => Table("app", "Addresses", 0, 0,
        [
            Int("AddressId", 1, identity: true),
            Int("CustomerId", 2),
            Str("Line1", 3, "nvarchar", 200, false),
            Str("City", 4, "nvarchar", 80, false),
            Str("PostalCode", 5, "nvarchar", 12, true),
            Str("CountryCode", 6, "char", 2, false)
        ],
        [Pk("PK_Addresses", "AddressId")],
        [Fk("FK_Addresses_Customers", "CustomerId", "app", "Customers", "CustomerId")]);

    private static TableInfo Products() => Table("app", "Products", 0, 0,
        [
            Int("ProductId", 1, identity: true),
            Str("Name", 2, "nvarchar", 150, false),
            Str("Description", 3, "nvarchar", 500, true),
            Dec194("UnitPrice", 4),
            new ColumnInfo("IsActive", 5, "bit", 0, 1, 0, false, false, false, false, null, null, null),
            new ColumnInfo("RowVer", 6, "timestamp", 0, 0, 0, false, false, false, true, null, null, null)
        ],
        [Pk("PK_Products", "ProductId")],
        []);

    private static TableInfo Orders() => Table("app", "Orders", 0, 0,
        [
            Int("OrderId", 1, identity: true),
            Int("CustomerId", 2),
            new ColumnInfo("OrderDate", 3, "datetime2", 0, 19, 0, false, false, false, false, null, null, null),
            Str("StatusCode", 4, "varchar", 10, false),
            Int("ShippingAddressId", 5, nullable: true),
            Dec194("TotalAmount", 6),
            Str("Comment", 7, "nvarchar", 200, true)
        ],
        [Pk("PK_Orders", "OrderId")],
        [
            Fk("FK_Orders_Addresses", "ShippingAddressId", "app", "Addresses", "AddressId"),
            Fk("FK_Orders_Customers", "CustomerId", "app", "Customers", "CustomerId")
        ],
        triggers: ["trg_Orders_Audit"]);

    private static TableInfo OrderLines() => Table("app", "OrderLines", 0, 0,
        [
            Int("OrderId", 1),
            new ColumnInfo("LineNumber", 2, "smallint", 0, 5, 0, false, false, false, false, null, null, null),
            Int("ProductId", 3),
            Int("Quantity", 4),
            Dec194("UnitPrice", 5)
        ],
        [new IndexInfo("PK_OrderLines", true, true, true, ["OrderId", "LineNumber"])],
        [
            Fk("FK_OrderLines_Orders", "OrderId", "app", "Orders", "OrderId"),
            Fk("FK_OrderLines_Products", "ProductId", "app", "Products", "ProductId")
        ]);

    private static TableInfo AuditEvents() => Table("app", "AuditEvents", 0, 0,
        [
            new ColumnInfo("EventTime", 1, "datetime2", 0, 23, 3, false, false, false, false, null, null, null),
            Str("UserName", 2, "nvarchar", 50, false),
            Str("Action", 3, "nvarchar", 200, false)
        ],
        [],
        []);

    // ---------------- helpers ----------------

    private static ServerMeta Meta(string database) => new(
        Server: @"(localdb)\MSSQLLocalDB", Database: database, Version: "Microsoft SQL Server 2022", ProductVersion: "16.0.1000.6",
        MajorVersion: 16, Edition: "Express Edition (64-bit)", ServerCollation: Collation, DatabaseCollation: Collation,
        CompatLevel: 160, AuthSummary: "integrated");

    private static TableInfo Table(string schema, string name, long rows, double sizeMb, List<ColumnInfo> columns,
        List<IndexInfo> indexes, List<ForeignKeyInfo> fks, string[]? triggers = null) =>
        new(schema, name, rows, sizeMb, columns, indexes, fks, triggers?.Length ?? 0, null, null) { TriggerNames = (triggers ?? []).ToList() };

    private static ColumnInfo Int(string name, int ordinal, bool nullable = false, bool identity = false) =>
        new(name, ordinal, "int", 0, 10, 0, nullable, identity, false, false, null, null, null);

    private static ColumnInfo Str(string name, int ordinal, string type, int length, bool nullable) =>
        new(name, ordinal, type, length, 0, 0, nullable, false, false, false, null, Collation, null);

    private static ColumnInfo DateTimeCol(string name, int ordinal, bool nullable, string? dflt = null) =>
        new(name, ordinal, "datetime", 0, 23, 3, nullable, false, false, false, dflt, null, null);

    private static ColumnInfo Money(string name, int ordinal) =>
        new(name, ordinal, "money", 0, 19, 4, false, false, false, false, null, null, null);

    private static ColumnInfo Dec194(string name, int ordinal) =>
        new(name, ordinal, "decimal", 0, 19, 4, false, false, false, false, null, null, null);

    private static IndexInfo Pk(string name, string column) => new(name, true, true, true, [column]);

    private static ForeignKeyInfo Fk(string name, string column, string refSchema, string refTable, string refColumn, bool notTrusted = false) =>
        new(name, [column], refSchema, refTable, [refColumn], false, notTrusted);

    private static ColumnProfile Prof(long rows, long nulls, long? distinct, string? min, string? max, int? maxLen, string? semanticClass,
        params string[] samples) =>
        new(rows, nulls, distinct, min, max, maxLen, maxLen is null ? null : maxLen * 0.8, semanticClass, [], samples.ToList());

    /// <summary>Human-readable structural differences (profiles, rows, sizes and ExtractedAt ignored). Empty = identical structure.</summary>
    public static List<string> Diff(CatalogSnapshot expected, CatalogSnapshot actual)
    {
        var diffs = new List<string>();
        if (expected.Objects != actual.Objects) diffs.Add($"objects: expected {expected.Objects} actual {actual.Objects}");
        foreach (var e in expected.Tables)
        {
            var a = actual.FindTable(e.Key);
            if (a is null) { diffs.Add($"missing table {e.Key}"); continue; }
            if (e.TriggerCount != a.TriggerCount || !e.TriggerNames.Order().SequenceEqual(a.TriggerNames.Order()))
                diffs.Add($"{e.Key}: triggers [{string.Join(",", e.TriggerNames)}] vs [{string.Join(",", a.TriggerNames)}]");
            if (e.TemporalType != a.TemporalType) diffs.Add($"{e.Key}: temporal '{e.TemporalType}' vs '{a.TemporalType}'");
            foreach (var ec in e.Columns)
            {
                var ac = a.FindColumn(ec.Name);
                if (ac is null) { diffs.Add($"missing column {e.Key}.{ec.Name}"); continue; }
                if (ec with { Profile = null } != ac with { Profile = null }) diffs.Add($"{e.Key}.{ec.Name}: expected {ec with { Profile = null }} actual {ac with { Profile = null }}");
            }
            foreach (var ac in a.Columns.Where(c => e.FindColumn(c.Name) is null)) diffs.Add($"unexpected column {e.Key}.{ac.Name}");
            string Ix(IndexInfo i) => $"{i.Name}|{i.IsPrimaryKey}|{i.IsUnique}|{i.IsClustered}|{string.Join(",", i.Columns)}";
            string F(ForeignKeyInfo f) => $"{f.Name}|{string.Join(",", f.Columns)}|{f.RefKey}|{string.Join(",", f.RefColumns)}|{f.IsDisabled}|{f.IsNotTrusted}";
            var ei = e.Indexes.Select(Ix).Order().ToList(); var ai = a.Indexes.Select(Ix).Order().ToList();
            if (!ei.SequenceEqual(ai)) diffs.Add($"{e.Key}: indexes [{string.Join("; ", ei)}] vs [{string.Join("; ", ai)}]");
            var ef = e.ForeignKeys.Select(F).Order().ToList(); var af = a.ForeignKeys.Select(F).Order().ToList();
            if (!ef.SequenceEqual(af)) diffs.Add($"{e.Key}: foreign keys [{string.Join("; ", ef)}] vs [{string.Join("; ", af)}]");
        }
        foreach (var a in actual.Tables.Where(t => expected.FindTable(t.Key) is null)) diffs.Add($"unexpected table {a.Key}");
        return diffs;
    }
}
```

Create `plugins/db-migrate/engine/Dbm.Tests/Support/SampleMappings.cs`:

```csharp
using Dbm.Core.Mapping;

namespace Dbm.Tests.Support;

/// <summary>The C15 ground-truth mapping LegacyShop → ShopV2 (Method human, Confidence 1). Used by M3–M5 tests.</summary>
public static class SampleMappings
{
    public static MappingPayload Approved()
    {
        var m = new MappingPayload();

        m.Tables["app.Customers"] = Direct("dbo.CUST", new()
        {
            ["CustomerId"] = C("s.[CUST_ID]", "dbo.CUST.CUST_ID"),
            ["FirstName"] = C("LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", "dbo.CUST.CUST_NM"),
            ["LastName"] = C("LTRIM(SUBSTRING(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') + 1, 100))", "dbo.CUST.CUST_NM"),
            ["Email"] = C("s.[EMAIL_ADDR]", "dbo.CUST.EMAIL_ADDR"),
            ["Phone"] = C("s.[PHONE_NO]", "dbo.CUST.PHONE_NO"),
            ["BirthDate"] = C("CAST(s.[DOB] AS date)", "dbo.CUST.DOB"),
            ["CreatedAt"] = C("s.[CRT_DT]", "dbo.CUST.CRT_DT"),
            ["PrimaryAddressId"] = C("(SELECT MIN(a.[ADDR_ID]) FROM [dbo].[ADDR] AS a WHERE a.[CUST_ID] = s.[CUST_ID])",
                "dbo.ADDR.ADDR_ID", "dbo.ADDR.CUST_ID", "dbo.CUST.CUST_ID"),
            ["Notes"] = C("CAST(s.[NOTES] AS nvarchar(max))", "dbo.CUST.NOTES"),
        });

        m.Tables["app.Addresses"] = Direct("dbo.ADDR", new()
        {
            ["AddressId"] = C("s.[ADDR_ID]", "dbo.ADDR.ADDR_ID"),
            ["CustomerId"] = C("s.[CUST_ID]", "dbo.ADDR.CUST_ID"),
            ["Line1"] = C("s.[LINE1]", "dbo.ADDR.LINE1"),
            ["City"] = C("s.[CITY]", "dbo.ADDR.CITY"),
            ["PostalCode"] = C("s.[ZIP]", "dbo.ADDR.ZIP"),
            ["CountryCode"] = C("s.[CTRY_CD]", "dbo.ADDR.CTRY_CD"),
        });

        m.Tables["app.Products"] = Direct("dbo.PROD", new()
        {
            ["ProductId"] = C("s.[PROD_ID]", "dbo.PROD.PROD_ID"),
            ["Name"] = C("s.[PROD_NM]", "dbo.PROD.PROD_NM"),
            ["Description"] = C("s.[PROD_DESC]", "dbo.PROD.PROD_DESC"),
            ["UnitPrice"] = C("s.[UNIT_PRC]", "dbo.PROD.UNIT_PRC"),
            ["IsActive"] = C("CASE WHEN s.[ACTIVE_FLG] = 'Y' THEN 1 ELSE 0 END", "dbo.PROD.ACTIVE_FLG"),
        });

        m.Tables["app.Orders"] = new TableMap
        {
            Kind = "merge",
            Sources = ["dbo.ORD_HDR", "dbo.ORD_STATUS"],
            From = "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]",
            Confidence = 1,
            Method = MapMethod.Human,
            Rationale = "Order header joined to the status lookup to obtain the status code.",
            Columns = new()
            {
                ["OrderId"] = C("s.[ORD_ID]", "dbo.ORD_HDR.ORD_ID"),
                ["CustomerId"] = C("s.[CUST_ID]", "dbo.ORD_HDR.CUST_ID"),
                ["OrderDate"] = C("s.[ORD_DT]", "dbo.ORD_HDR.ORD_DT"),
                ["StatusCode"] = C("st.[STATUS_CD]", "dbo.ORD_STATUS.STATUS_CD", "dbo.ORD_HDR.STATUS_ID"),
                ["ShippingAddressId"] = C("s.[SHIP_ADDR_ID]", "dbo.ORD_HDR.SHIP_ADDR_ID"),
                ["TotalAmount"] = C("s.[TOTAL_AMT]", "dbo.ORD_HDR.TOTAL_AMT"),
                ["Comment"] = C("s.[CMNT]", "dbo.ORD_HDR.CMNT"),
            }
        };

        m.Tables["app.OrderLines"] = Direct("dbo.ORD_LINE", new()
        {
            ["OrderId"] = C("s.[ORD_ID]", "dbo.ORD_LINE.ORD_ID"),
            ["LineNumber"] = C("s.[LINE_NO]", "dbo.ORD_LINE.LINE_NO"),
            ["ProductId"] = C("s.[PROD_ID]", "dbo.ORD_LINE.PROD_ID"),
            ["Quantity"] = C("s.[QTY]", "dbo.ORD_LINE.QTY"),
            ["UnitPrice"] = C("s.[UNIT_PRC]", "dbo.ORD_LINE.UNIT_PRC"),
        });

        m.Tables["app.AuditEvents"] = Direct("dbo.AUDIT_LOG", new()
        {
            ["EventTime"] = C("s.[LOG_TS]", "dbo.AUDIT_LOG.LOG_TS"),
            ["UserName"] = C("s.[USR]", "dbo.AUDIT_LOG.USR"),
            ["Action"] = C("s.[ACTION_TXT]", "dbo.AUDIT_LOG.ACTION_TXT"),
        });

        m.Drops["dbo.TMP_IMPORT"] = new DropDecision("Empty import staging table; not part of ShopV2.", MapMethod.Human);
        m.Drops["dbo.CUST.FAX_NO"] = new DropDecision("ShopV2 no longer stores fax numbers.", MapMethod.Human);
        m.Drops["dbo.ORD_STATUS.STATUS_ID"] = new DropDecision("Surrogate lookup key; Orders stores the status code instead.", MapMethod.Human);
        m.Drops["dbo.ORD_STATUS.STATUS_DESC"] = new DropDecision("Status descriptions are not stored in ShopV2.", MapMethod.Human);
        return m;
    }

    private static TableMap Direct(string source, Dictionary<string, ColumnMap> columns) => new()
    {
        Kind = "direct",
        Sources = [source],
        Confidence = 1,
        Method = MapMethod.Human,
        Columns = columns
    };

    private static ColumnMap C(string expr, params string[] sourceColumns) => new()
    {
        Expr = expr,
        SourceColumns = sourceColumns.ToList(),
        Confidence = 1,
        Method = MapMethod.Human
    };
}
```

- [ ] **Step 2: Write the failing validator tests**

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingValidatorTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingValidatorTests
{
    private static readonly MatchOptions Options = new();

    private static List<string> Errors(MappingPayload m) => MappingValidator.Errors(m, SampleCatalogs.Source(), SampleCatalogs.Target());
    private static List<string> Blockers(MappingPayload m) => MappingValidator.Blockers(m, SampleCatalogs.Source(), SampleCatalogs.Target());

    [Fact]
    public void Approved_sample_mapping_is_clean()
    {
        var m = SampleMappings.Approved();
        Assert.Empty(Errors(m));
        Assert.Empty(Blockers(m));
        Assert.Empty(MappingValidator.Attention(m, Options));
        Assert.Empty(MappingValidator.UncoveredSourceColumns(m, SampleCatalogs.Source()));
    }

    [Fact]
    public void Payload_round_trips_through_json_with_snake_case_methods()
    {
        var json = Json.Serialize(SampleMappings.Approved());
        Assert.Contains("\"method\":\"human\"", json);
        Assert.Contains("\"app.Customers\"", json);
        Assert.Contains("\"FirstName\"", json);
        Assert.DoesNotContain("\"default\":null", json);
        var back = Json.Deserialize<MappingPayload>(json);
        Assert.Equal("merge", back.Tables["app.Orders"].Kind);
        Assert.Equal(MapMethod.Human, back.Drops["dbo.CUST.FAX_NO"].Method);
        Assert.Empty(Blockers(back));
    }

    [Fact]
    public void Missing_table_map_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Tables.Remove("app.AuditEvents");
        var blockers = Blockers(m);
        Assert.Contains(blockers, b => b.StartsWith("app.AuditEvents: no table mapping"));
        Assert.Contains("source table dbo.AUDIT_LOG is not mapped or dropped (3 columns)", blockers);
    }

    [Fact]
    public void Table_map_without_sources_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.AuditEvents"].Sources.Clear();
        Assert.Contains(Blockers(m), b => b.StartsWith("app.AuditEvents: no source table"));
    }

    [Fact]
    public void Uncovered_source_column_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Drops.Remove("dbo.CUST.FAX_NO");
        Assert.Equal(["source column dbo.CUST.FAX_NO is not mapped or dropped"], Blockers(m));
        Assert.Equal(["dbo.CUST.FAX_NO"], MappingValidator.UncoveredSourceColumns(m, SampleCatalogs.Source()));
    }

    [Fact]
    public void Undropped_empty_table_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Drops.Remove("dbo.TMP_IMPORT");
        Assert.Equal(["source table dbo.TMP_IMPORT is not mapped or dropped (1 columns)"], Blockers(m));
    }

    [Fact]
    public void Not_null_column_without_expression_or_default_is_a_blocker()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Customers"].Columns["FirstName"].Expr = null;
        Assert.Contains("app.Customers.FirstName: NOT NULL without default needs an expression or default", Blockers(m));

        m.Tables["app.Customers"].Columns["FirstName"].Default = "N''";
        Assert.Empty(Blockers(m));
    }

    [Fact]
    public void Missing_column_map_for_not_null_column_is_a_blocker_but_nullable_identity_and_defaulted_columns_are_not()
    {
        var m = SampleMappings.Approved();
        var customers = m.Tables["app.Customers"].Columns;
        customers.Remove("CustomerId");      // identity
        customers.Remove("CreatedAt");       // has default
        customers.Remove("Email");           // nullable
        m.Drops["dbo.CUST.EMAIL_ADDR"] = new DropDecision("not needed", MapMethod.Human);
        m.Drops["dbo.CUST.CRT_DT"] = new DropDecision("not needed", MapMethod.Human);
        Assert.Empty(Blockers(m));

        m.Tables["app.Products"].Columns.Remove("IsActive");
        m.Drops["dbo.PROD.ACTIVE_FLG"] = new DropDecision("not needed", MapMethod.Human);
        Assert.Equal(["app.Products.IsActive: NOT NULL without default needs an expression or default"], Blockers(m));
    }

    [Fact]
    public void Skip_map_needs_no_columns_but_does_not_cover_sources()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.AuditEvents"] = new TableMap { Kind = "skip", Method = MapMethod.Human, Rationale = "not migrated" };
        Assert.Equal(["source table dbo.AUDIT_LOG is not mapped or dropped (3 columns)"], Blockers(m));

        m.Drops["dbo.AUDIT_LOG"] = new DropDecision("audit history stays in the legacy system", MapMethod.Human);
        Assert.Empty(Blockers(m));
        Assert.Empty(Errors(m));
    }

    [Fact]
    public void Unknown_names_and_invalid_kind_are_errors()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Nope"] = new TableMap { Sources = ["dbo.CUST"] };
        m.Tables["app.Products"].Kind = "weird";
        m.Tables["app.Products"].Sources.Add("dbo.NOPE");
        m.Tables["app.Products"].Columns["Nope"] = new ColumnMap { Expr = "1" };
        m.Tables["app.Products"].Columns["Name"].SourceColumns = ["dbo.PROD.NOPE"];
        m.Drops["dbo.GONE"] = new DropDecision("x", MapMethod.Human);
        m.Drops["dbo.CUST.PHONE_NO"] = new DropDecision(" ", MapMethod.Human);

        var errors = Errors(m);

        Assert.Contains("unknown target table 'app.Nope'", errors);
        Assert.Contains("app.Products: invalid kind 'weird' (use direct, merge, lookup or skip)", errors);
        Assert.Contains("app.Products: unknown source table 'dbo.NOPE'", errors);
        Assert.Contains("unknown target column 'app.Products.Nope'", errors);
        Assert.Contains("app.Products.Name: unknown source column 'dbo.PROD.NOPE'", errors);
        Assert.Contains("unknown drop 'dbo.GONE' (use schema.table or schema.table.column)", errors);
        Assert.Contains("drop 'dbo.CUST.PHONE_NO' needs a reason", errors);
    }

    [Fact]
    public void Keys_must_use_catalog_case()
    {
        var m = SampleMappings.Approved();
        var map = m.Tables["app.Addresses"];
        m.Tables.Remove("app.Addresses");
        m.Tables["app.addresses"] = map;
        map.Columns["city"] = map.Columns["City"];
        map.Columns.Remove("City");

        var errors = Errors(m);

        Assert.Contains("target table 'app.addresses' must be written as 'app.Addresses' (catalog case)", errors);
        Assert.Contains("target column 'app.Addresses.city' must be written as 'City' (catalog case)", errors);
        Assert.Empty(Blockers(m));
    }

    [Fact]
    public void Computed_and_rowversion_columns_cannot_be_written()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Customers"].Columns["DisplayName"] = new ColumnMap { Expr = "s.[CUST_NM]", SourceColumns = ["dbo.CUST.CUST_NM"] };
        m.Tables["app.Products"].Columns["RowVer"] = new ColumnMap { Default = "0x00" };
        m.Tables["app.Products"].Columns["Name"].Expr = "  ";

        var errors = Errors(m);

        Assert.Contains("app.Customers.DisplayName is computed and cannot be written", errors);
        Assert.Contains("app.Products.RowVer is a rowversion and cannot be written", errors);
        Assert.Contains("app.Products.Name: expr is empty (use null for unmapped)", errors);
    }

    [Fact]
    public void Attention_lists_script_proposals_below_auto_accept()
    {
        var m = SampleMappings.Approved();
        var email = m.Tables["app.Customers"].Columns["Email"];
        email.Method = MapMethod.Fuzzy;
        email.Confidence = 0.62;
        var phone = m.Tables["app.Customers"].Columns["Phone"];
        phone.Method = MapMethod.Vector;
        phone.Confidence = 0.9;                                  // above the band: no attention
        var notes = m.Tables["app.Customers"].Columns["Notes"];
        notes.Method = MapMethod.Agent;
        notes.Confidence = 0.4;                                  // agent-decided: no attention
        m.Tables["app.Orders"].Method = MapMethod.Vector;
        m.Tables["app.Orders"].Confidence = 0.7;
        m.Tables["app.AuditEvents"].Sources.Clear();
        m.Tables["app.AuditEvents"].Method = MapMethod.Vector;
        m.Tables["app.AuditEvents"].Confidence = 0.42;
        m.Tables["app.OrderLines"].Columns["Quantity"] = new ColumnMap { Method = MapMethod.Vector, Confidence = 0.31 };

        var attention = MappingValidator.Attention(m, Options);

        Assert.Equal(
        [
            "app.AuditEvents: no confident source table (best 0.42)",
            "app.Customers.Email: s.[EMAIL_ADDR] needs review (confidence 0.62, fuzzy)",
            "app.OrderLines.Quantity: unmapped (best candidate 0.31)",
            "app.Orders: source dbo.ORD_HDR needs review (confidence 0.70, vector)",
        ], attention);
    }

    [Theory]
    [InlineData("dbo.CUST.CUST_NM", true)]
    [InlineData("DBO.cust.cust_nm", true)]
    [InlineData("dbo.CUST", false)]
    [InlineData("dbo.CUST.", false)]
    [InlineData("CUST_NM", false)]
    public void ResolveSourceColumn_splits_on_the_last_dot(string key, bool found)
    {
        Assert.Equal(found, MappingValidator.ResolveSourceColumn(SampleCatalogs.Source(), key) is not null);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingValidatorTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Mapping' does not exist in the namespace 'Dbm.Core'` (and `MatchOptions` not found).

- [ ] **Step 4: Implement `MatchOptions`, `MappingPayload`, `MappingValidator`**

Create `plugins/db-migrate/engine/Dbm/Core/Matching/MatchOptions.cs`:

```csharp
namespace Dbm.Core.Matching;

/// <summary>Auto-mapper bands (spec §4.4): score ≥ AutoAccept is auto-accepted, ≥ Candidate is proposed, below is unmapped.</summary>
public sealed record MatchOptions(double AutoAccept = 0.85, double Candidate = 0.50, int TopK = 3);
```

Create `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingPayload.cs`:

```csharp
namespace Dbm.Core.Mapping;

public enum MapMethod { Exact, Fuzzy, Vector, Agent, Human, Carried }

public sealed class MappingPayload
{
    public Dictionary<string, TableMap> Tables { get; set; } = new();        // key = target "schema.table" (exact catalog case)
    public Dictionary<string, DropDecision> Drops { get; set; } = new();     // key = source "schema.table" (whole table) or "schema.table.column"
    public List<string> Notes { get; set; } = new();
}

public sealed class TableMap
{
    public string Kind { get; set; } = "direct";          // "direct" | "merge" | "lookup" | "skip"  (a split = several TableMaps sharing a source)
    public List<string> Sources { get; set; } = new();    // source table keys; Sources[0] is the primary source, aliased "s"
    public string? From { get; set; }                     // optional full FROM clause (without FROM)
    public string? Filter { get; set; }                   // optional WHERE predicate (without WHERE)
    public double Confidence { get; set; }
    public MapMethod Method { get; set; }
    public string? Rationale { get; set; }
    public Dictionary<string, ColumnMap> Columns { get; set; } = new();     // key = target column name
    public List<Candidate>? Candidates { get; set; }
}

public sealed class ColumnMap
{
    public string? Expr { get; set; }                     // T-SQL expression over the FROM aliases; null = unmapped
    public List<string> SourceColumns { get; set; } = new();   // "schema.table.column" values referenced by Expr (drives coverage)
    public string? Default { get; set; }                  // T-SQL expression used when Expr is null
    public double Confidence { get; set; }
    public MapMethod Method { get; set; }
    public string? Rationale { get; set; }
    public string? TypeRisk { get; set; }
    public List<Candidate>? Candidates { get; set; }
}

public sealed record Candidate(string Source, double Score, string Why);

public sealed record DropDecision(string Reason, MapMethod Method);
```

Create `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingValidator.cs`:

```csharp
using System.Globalization;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.Mapping;

public static class MappingValidator
{
    public static readonly IReadOnlyList<string> Kinds = ["direct", "merge", "lookup", "skip"];
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static List<string> Errors(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        var errors = new List<string>();
        foreach (var (key, map) in m.Tables)
        {
            var table = tgt.FindTable(key);
            if (table is null) { errors.Add($"unknown target table '{key}'"); continue; }
            if (table.Key != key) errors.Add($"target table '{key}' must be written as '{table.Key}' (catalog case)");
            if (!Kinds.Contains(map.Kind)) errors.Add($"{table.Key}: invalid kind '{map.Kind}' (use direct, merge, lookup or skip)");
            foreach (var source in map.Sources)
                if (src.FindTable(source) is null) errors.Add($"{table.Key}: unknown source table '{source}'");
            foreach (var (name, col) in map.Columns)
            {
                var tc = table.FindColumn(name);
                if (tc is null) { errors.Add($"unknown target column '{table.Key}.{name}'"); continue; }
                if (tc.Name != name) errors.Add($"target column '{table.Key}.{name}' must be written as '{tc.Name}' (catalog case)");
                if ((tc.IsComputed || tc.IsRowVersion) && (col.Expr is not null || col.Default is not null))
                    errors.Add($"{table.Key}.{tc.Name} is {(tc.IsComputed ? "computed" : "a rowversion")} and cannot be written");
                if (col.Expr is not null && string.IsNullOrWhiteSpace(col.Expr))
                    errors.Add($"{table.Key}.{tc.Name}: expr is empty (use null for unmapped)");
                foreach (var sc in col.SourceColumns)
                    if (ResolveSourceColumn(src, sc) is null) errors.Add($"{table.Key}.{tc.Name}: unknown source column '{sc}'");
            }
        }
        foreach (var (key, drop) in m.Drops)
        {
            if (src.FindTable(key) is null && ResolveSourceColumn(src, key) is null)
                errors.Add($"unknown drop '{key}' (use schema.table or schema.table.column)");
            if (string.IsNullOrWhiteSpace(drop.Reason)) errors.Add($"drop '{key}' needs a reason");
        }
        return errors;
    }

    public static List<string> Blockers(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt)
    {
        var blockers = new List<string>();
        foreach (var table in tgt.Tables)
        {
            var map = FindTableMap(m, table.Key);
            if (map is null) { blockers.Add($"{table.Key}: no table mapping (map a source table or set kind \"skip\")"); continue; }
            if (map.Kind == "skip") continue;
            if (map.Sources.Count == 0) blockers.Add($"{table.Key}: no source table (choose one or set kind \"skip\")");
            foreach (var col in table.Columns)
            {
                if (col.IsNullable || col.IsIdentity || col.IsComputed || col.IsRowVersion || col.DefaultDefinition is not null) continue;
                var cm = FindColumnMap(map, col.Name);
                if (cm is null || string.IsNullOrWhiteSpace(cm.Expr) && string.IsNullOrWhiteSpace(cm.Default))
                    blockers.Add($"{table.Key}.{col.Name}: NOT NULL without default needs an expression or default");
            }
        }
        foreach (var (table, missing) in UncoveredByTable(m, src))
        {
            if (missing.Count == table.Columns.Count)
                blockers.Add($"source table {table.Key} is not mapped or dropped ({missing.Count} columns)");
            else
                blockers.AddRange(missing.Select(c => $"source column {table.Key}.{c.Name} is not mapped or dropped"));
        }
        return blockers;
    }

    public static List<string> Attention(MappingPayload m, MatchOptions options)
    {
        var list = new List<string>();
        foreach (var (key, map) in m.Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (map.Kind == "skip") continue;
            if (NeedsReview(map.Method, map.Confidence, options))
                list.Add(map.Sources.Count == 0
                    ? Inv($"{key}: no confident source table (best {map.Confidence:0.00})")
                    : Inv($"{key}: source {map.Sources[0]} needs review (confidence {map.Confidence:0.00}, {EnumText.ToText(map.Method)})"));
            foreach (var (name, cm) in map.Columns)
            {
                if (!NeedsReview(cm.Method, cm.Confidence, options)) continue;
                list.Add(cm.Expr is null
                    ? Inv($"{key}.{name}: unmapped (best candidate {cm.Confidence:0.00})")
                    : Inv($"{key}.{name}: {cm.Expr} needs review (confidence {cm.Confidence:0.00}, {EnumText.ToText(cm.Method)})"));
            }
        }
        return list;
    }

    /// <summary>True for script-proposed items (fuzzy/vector) below the auto-accept band.</summary>
    public static bool NeedsReview(MapMethod method, double confidence, MatchOptions options) =>
        method is MapMethod.Fuzzy or MapMethod.Vector && confidence < options.AutoAccept;

    /// <summary>Source columns ("schema.table.column") neither referenced by a non-skip map nor dropped.</summary>
    public static List<string> UncoveredSourceColumns(MappingPayload m, CatalogSnapshot src) =>
        UncoveredByTable(m, src).SelectMany(x => x.Missing.Select(c => $"{x.Table.Key}.{c.Name}")).ToList();

    /// <summary>Resolves "schema.table.column" (split on the last '.') against a catalog.</summary>
    public static (TableInfo Table, ColumnInfo Column)? ResolveSourceColumn(CatalogSnapshot catalog, string key)
    {
        var dot = key.LastIndexOf('.');
        if (dot <= 0 || dot == key.Length - 1) return null;
        var table = catalog.FindTable(key[..dot]);
        var column = table?.FindColumn(key[(dot + 1)..]);
        return table is null || column is null ? null : (table, column);
    }

    public static TableMap? FindTableMap(MappingPayload m, string targetKey) =>
        m.Tables.TryGetValue(targetKey, out var exact) ? exact
        : m.Tables.FirstOrDefault(kv => Ci.Equals(kv.Key, targetKey)).Value;

    public static ColumnMap? FindColumnMap(TableMap map, string column) =>
        map.Columns.TryGetValue(column, out var exact) ? exact
        : map.Columns.FirstOrDefault(kv => Ci.Equals(kv.Key, column)).Value;

    private static IEnumerable<(TableInfo Table, List<ColumnInfo> Missing)> UncoveredByTable(MappingPayload m, CatalogSnapshot src)
    {
        var covered = new HashSet<string>(Ci);
        foreach (var map in m.Tables.Values.Where(t => t.Kind != "skip"))
            foreach (var cm in map.Columns.Values)
                foreach (var sc in cm.SourceColumns) covered.Add(sc);
        var dropped = new HashSet<string>(m.Drops.Keys, Ci);
        foreach (var table in src.Tables)
        {
            if (dropped.Contains(table.Key)) continue;
            var missing = table.Columns
                .Where(c => !covered.Contains($"{table.Key}.{c.Name}") && !dropped.Contains($"{table.Key}.{c.Name}"))
                .ToList();
            if (missing.Count > 0) yield return (table, missing);
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingValidatorTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 18`.

- [ ] **Step 6: Write the fidelity integration test (sample catalogs vs. the real sample databases)**

Create `plugins/db-migrate/engine/Dbm.Tests/Support/SampleExtract.cs`:

```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Sql;

namespace Dbm.Tests.Support;

/// <summary>Reads catalogs from the real sample databases created by SampleDatabases (integration tests only).</summary>
public static class SampleExtract
{
    public static async Task<CatalogSnapshot> CatalogAsync(string connectionString, bool profile)
    {
        var meta = await SqlConnect.ProbeAsync(connectionString, CancellationToken.None);
        await using var conn = await SqlConnect.OpenAsync(connectionString, CancellationToken.None);
        var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, CancellationToken.None);
        return profile
            ? await Profiler.ProfileAsync(conn, snapshot, new ProfileOptions(100_000, SampleValues: true), null, CancellationToken.None)
            : snapshot;
    }
}
```

Create `plugins/db-migrate/engine/Dbm.Tests/Integration/Mapping/SampleCatalogFidelityTests.cs`:

```csharp
using Dbm.Core.Catalog;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Mapping;

[Trait("Category", "Integration")]
public class SampleCatalogFidelityTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Sample_catalogs_match_the_extracted_sample_databases()
    {
        AssertSameStructure(SampleCatalogs.Source(), await SampleExtract.CatalogAsync(fixture.Pair.SourceCs, profile: false));
        AssertSameStructure(SampleCatalogs.Target(), await SampleExtract.CatalogAsync(fixture.Pair.TargetCs, profile: false));
    }

    private static void AssertSameStructure(CatalogSnapshot expected, CatalogSnapshot actual)
    {
        // The sample databases use the server's default collation; compare modulo that one value.
        var normalized = actual with
        {
            Tables = actual.Tables.Select(t => t with
            {
                Columns = t.Columns.Select(c => c.Collation is not null && c.Collation == actual.Server.DatabaseCollation
                    ? c with { Collation = SampleCatalogs.Collation } : c).ToList()
            }).ToList()
        };
        var diffs = SampleCatalogs.Diff(expected, normalized);
        Assert.True(diffs.Count == 0, "SampleCatalogs differ from the real schema; fix SampleCatalogs.cs:\n" + string.Join("\n", diffs));
        Assert.Equal(Fingerprint.Compute(normalized), Fingerprint.Compute(expected));
    }
}
```

- [ ] **Step 7: Run the integration test**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~SampleCatalogFidelityTests"`
Expected: PASS (1 test). If it fails, the assertion message lists every structural difference (`<table>.<column>: expected ColumnInfo { … } actual ColumnInfo { … }`, index/FK lines, object counts). Change **`SampleCatalogs.cs`** to match what the extractor reports (the extractor is T2.2's and is the source of truth for conventions such as `Precision` on non-decimal types or `MaxLength` of `text`); only if a difference contradicts C10 (e.g. `MaxLength` in bytes for `nvarchar`) stop and report the extractor bug. Re-run until it passes, then re-run Step 5 (the unit tests must still pass).

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Matching/MatchOptions.cs plugins/db-migrate/engine/Dbm/Core/Mapping/MappingPayload.cs plugins/db-migrate/engine/Dbm/Core/Mapping/MappingValidator.cs plugins/db-migrate/engine/Dbm.Tests/Support/SampleCatalogs.cs plugins/db-migrate/engine/Dbm.Tests/Support/SampleMappings.cs plugins/db-migrate/engine/Dbm.Tests/Support/SampleExtract.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingValidatorTests.cs plugins/db-migrate/engine/Dbm.Tests/Integration/Mapping/SampleCatalogFidelityTests.cs
git commit -F - <<'EOF'
feat(mapping): mapping payload, validator, sample catalogs and ground-truth mapping (T3.2)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 3.3: Auto-mapper, `automap` job, `dbm map auto`

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Matching/MatchContext.cs`, `ColumnScorer.cs`, `TableScorer.cs`, `MappingCarryOver.cs`, `AutoMapper.cs`, `AutomapJob.cs` (all in `Dbm/Core/Matching/`)
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/MapCommands.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs` (one line), `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` (one line)
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/AutoMapperBaseline.cs`, `plugins/db-migrate/engine/Dbm.Tests/Support/MappingTestData.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/AutoMapperTests.cs`, `plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/AutomapJobTests.cs`, `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/MapCommandTests.cs`
- Test (integration): `plugins/db-migrate/engine/Dbm.Tests/Integration/Mapping/AutoMapperBaselineIntegrationTests.cs`

**Interfaces:**
- Consumes: `Synonyms.Default()/ForProject(ws)/Expand`, `NameNormalizer.Tokens(identifier, synonyms, contextTokens)`, `IVectorizer`, `NgramTfidfVectorizer()`, `SparseVector.Dot` (C11/T2.4); `TypeCompat`, `ColumnType` (T3.1); C12 + `MappingValidator` members + `MatchOptions` (T3.2); `DbmServices` (`Ws`, `Catalog`, `Project`, `Artifacts`, `Phases`, `Jobs`, `Workflow`, `JobHandlers`), `IJobHandler`, `JobContext`, `JobResult`, `JobRunner.RunPendingAsync`, `JobRegistry` (C6); `CatalogRepo.Save/Get`, `ProjectRepo.Init/GetSettings/SaveSettings`, `ArtifactRepo.Latest/NextVersion/Add`, `PhaseRepo.Get/SetStatus/SetCurrentVersion` (C2); `WorkflowEngine.RetryJob`, `WorkflowEngine.Order` (C5); `ICommand`, `Args.Flag`, `CliContext.OpenProject()`, `Output.Ok/Fail`, `CommandRegistry.All()` (C8); test helpers `TempProject`, `TestWorkspace`, `CliRunner.RunAsync(ws, factory, args)`, `SamplePairFixture` (M1/M2); `UserHome.Dir`, `Workspace`, `Json`, `EnumText` (C1); `Fingerprint.Compute` (T2.5).
- Produces:
  ```csharp
  namespace Dbm.Core.Matching;
  public static class AutoMapper                                                           // C11
  {
      public const double MinNameForAccept = 0.3, MinNameForCandidate = 0.1, MinTableCandidateScore = 0.2;
      public static MappingPayload Map(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms, MatchOptions options, MappingPayload? carryOver = null);
  }
  public sealed class MatchContext
  {
      public MatchContext(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms);
      public CatalogSnapshot Src { get; }  public CatalogSnapshot Tgt { get; }
      public IReadOnlyList<string> TableTokens(bool isSource, TableInfo table);
      public IReadOnlyList<string> ColumnTokens(bool isSource, TableInfo table, ColumnInfo column);
      public double TableNameSimilarity(TableInfo tgtTable, TableInfo srcTable);
      public double ColumnNameSimilarity(TableInfo tgtTable, ColumnInfo tgtColumn, TableInfo srcTable, ColumnInfo srcColumn);
  }
  public sealed record ColumnScore(double Total, double Name, TypeCompatResult Type, double Structure, double? Profile) { public string Why { get; } }
  public static class ColumnScorer
  {
      public static ColumnScore Score(MatchContext ctx, TableInfo tgtTable, ColumnInfo tgtCol, TableInfo srcTable, ColumnInfo srcCol, IReadOnlyDictionary<string, string> pairs);
      public static double Structure(TableInfo tgtTable, ColumnInfo tgtCol, TableInfo srcTable, ColumnInfo srcCol, IReadOnlyDictionary<string, string> pairs);
      public static double? Profile(TableInfo tgtTable, ColumnInfo tgtCol, ColumnInfo srcCol, IReadOnlyList<string> tgtTokens);
  }
  public sealed record TableScore(double Total, double Name, double Columns, double Fk) { public string Why { get; } }
  public static class TableScorer
  {
      public static TableScore Score(MatchContext ctx, TableInfo tgtTable, TableInfo srcTable);
      public static int Degree(CatalogSnapshot catalog, TableInfo table);
      public static double DegreeSimilarity(int a, int b);
  }
  public static class MappingCarryOver
  {
      public static bool IsKept(MapMethod method);                                          // agent | human | carried
      public static Dictionary<string, TableMap> Tables(MappingPayload? carryOver, CatalogSnapshot src, CatalogSnapshot tgt);
      public static void Drops(MappingPayload? carryOver, CatalogSnapshot src, MappingPayload into);
  }
  public sealed class AutomapJob : IJobHandler { public string Kind => "automap"; }        // JobResult(payload, "N tables mapped, A need attention, B blockers")

  namespace Dbm.Cli.Commands;
  public sealed class MapAutoCommand : ICommand { public string Name => "map auto"; }      // --inline

  namespace Dbm.Tests.Support;
  public static class AutoMapperBaseline { TablePairs; ColumnPairs; public static List<string> Misses(MappingPayload m); }
  public static class MappingTestData                                                    // extensions for TempProject.Services (T2.1)
  {
      public static DbmServices WithSampleCatalogs(this DbmServices services);           // saves SampleCatalogs via CatalogRepo
      public static ArtifactRow AddMapping(this DbmServices services, MappingPayload m, PhaseStatus status, string author = "script");
      public static void ApproveBefore(this DbmServices services, PhaseName phase);
  }
  ```

**Algorithm (what the code below does).**
1. `MatchContext` tokenises every table name (`NameNormalizer.Tokens(name, synonyms)`) and every column name with its owning table's tokens as context (so `CUST.CUST_ID` → `[id]`, `ADDR.CUST_ID` → `[customer, id]`), fits one `NgramTfidfVectorizer` over all table token lists and one over all column token lists (both catalogs), and caches the L2-normalised vectors. Name similarity = cosine (`Dot`), clamped to [0,1].
2. Table pairing: every target table is scored against every source table with `0.5·tableName + 0.4·columnSetSimilarity + 0.1·fkDegreeSimilarity` (columnSet = mean over writable target columns of the best column-name cosine in the source table; degree = outgoing + incoming FKs). Best ≥ `Candidate` → `Sources = [best]`, `Kind = "direct"`, `Method` = Exact when the table tokens are equal, Fuzzy when name ≥ 0.8, else Vector; `Confidence` = score; `Candidates` = next `TopK` with score ≥ 0.2. Below `Candidate` → `Sources` empty, Method Vector, Confidence = best score, Candidates = top `TopK` (the validator reports it as attention and blocker).
3. Column assignment inside each pair (primary source only): score all pairs with `0.45·name + 0.20·type + 0.15·structure + 0.20·profile` (weight of profile moves to name when the source column has no profile); sort descending (ties: target ordinal, source ordinal); greedily accept one-to-one when score ≥ `Candidate` **and** name ≥ 0.3; an identity target column only accepts a column of the source table's `BestKey()` (keeps keys). Accepted: `Expr = "s.[COL]"`, `SourceColumns = ["schema.table.COL"]`, Method Exact (tokens equal and type Exact) / Fuzzy (name ≥ 0.8) / Vector, `Confidence` = score, `TypeRisk` = TypeCompat risk, `Candidates` = next `TopK` (name ≥ 0.1 or score ≥ Candidate). Unaccepted non-identity columns get a map with `Expr` null, Method Vector, Confidence = best score and the candidates; unaccepted identity columns and all computed/rowversion columns get no entry.
4. Structure = mean(PK role equal; FK role: both FK and the target FK's referenced table is paired with the source FK's referenced table = 1, both FK otherwise 0.5, neither 1, only one 0; identity equal; nullability equal 1, NOT NULL target from nullable source 0, else 0.75). Profile (source profile with rows only) = mean(semantic class vs target profile class, else vs target type/name tokens; null ratio vs target profile, else 0 when a NOT NULL target receives a column with nulls; distinct ratio vs target profile, else only for single-column unique targets; length vs target profile MaxLen, else declared length).
5. Carry-over: tables of `carryOver` whose method is agent/human/carried, or that contain such columns, and whose sources all still exist are copied first (Kind/Sources/From/Filter/Rationale/Confidence kept, Method Carried) with only their kept columns whose target column and all `SourceColumns` still exist; the auto-assignment then fills the remaining columns. Kept drops whose key still exists are copied as Carried; notes are copied.
6. Empty source tables (`Rows == 0`) that are no table's source and not already dropped get `Drops[key] = ("empty table with no matching target", Vector)`.

- [ ] **Step 1: Write the failing auto-mapper tests**

Create `plugins/db-migrate/engine/Dbm.Tests/Support/AutoMapperBaseline.cs`:

```csharp
using Dbm.Core.Mapping;

namespace Dbm.Tests.Support;

/// <summary>C15 auto-mapper baseline: the six target tables pair with the right primary source and these column pairs are accepted.</summary>
public static class AutoMapperBaseline
{
    public static readonly IReadOnlyDictionary<string, string> TablePairs = new Dictionary<string, string>
    {
        ["app.Customers"] = "dbo.CUST",
        ["app.Addresses"] = "dbo.ADDR",
        ["app.Products"] = "dbo.PROD",
        ["app.Orders"] = "dbo.ORD_HDR",
        ["app.OrderLines"] = "dbo.ORD_LINE",
        ["app.AuditEvents"] = "dbo.AUDIT_LOG",
    };

    public static readonly IReadOnlyList<(string Target, string Column, string Source)> ColumnPairs =
    [
        ("app.Customers", "CustomerId", "dbo.CUST.CUST_ID"),
        ("app.Customers", "Email", "dbo.CUST.EMAIL_ADDR"),
        ("app.Customers", "Phone", "dbo.CUST.PHONE_NO"),
        ("app.Customers", "BirthDate", "dbo.CUST.DOB"),
        ("app.Addresses", "AddressId", "dbo.ADDR.ADDR_ID"),
        ("app.Addresses", "CustomerId", "dbo.ADDR.CUST_ID"),
        ("app.Addresses", "Line1", "dbo.ADDR.LINE1"),
        ("app.Addresses", "City", "dbo.ADDR.CITY"),
        ("app.Addresses", "PostalCode", "dbo.ADDR.ZIP"),
        ("app.Addresses", "CountryCode", "dbo.ADDR.CTRY_CD"),
        ("app.Products", "ProductId", "dbo.PROD.PROD_ID"),
        ("app.Products", "UnitPrice", "dbo.PROD.UNIT_PRC"),
        ("app.Orders", "OrderId", "dbo.ORD_HDR.ORD_ID"),
        ("app.Orders", "CustomerId", "dbo.ORD_HDR.CUST_ID"),
        ("app.Orders", "OrderDate", "dbo.ORD_HDR.ORD_DT"),
        ("app.Orders", "TotalAmount", "dbo.ORD_HDR.TOTAL_AMT"),
        ("app.Orders", "Comment", "dbo.ORD_HDR.CMNT"),
        ("app.OrderLines", "OrderId", "dbo.ORD_LINE.ORD_ID"),
        ("app.OrderLines", "LineNumber", "dbo.ORD_LINE.LINE_NO"),
        ("app.OrderLines", "ProductId", "dbo.ORD_LINE.PROD_ID"),
        ("app.OrderLines", "Quantity", "dbo.ORD_LINE.QTY"),
        ("app.OrderLines", "UnitPrice", "dbo.ORD_LINE.UNIT_PRC"),
        // AuditEvents was the one baseline table with its pairing checked but no column-level assertion, so a
        // regression swapping EventTime and UserName passed every test including the LocalDB integration one.
        ("app.AuditEvents", "EventTime", "dbo.AUDIT_LOG.LOG_TS"),
        ("app.AuditEvents", "UserName", "dbo.AUDIT_LOG.USR"),
        ("app.AuditEvents", "Action", "dbo.AUDIT_LOG.ACTION_TXT"),
    ];

    /// <summary>Returns every deviation from the baseline (empty = pass), so a failing test lists all misses at once.</summary>
    public static List<string> Misses(MappingPayload m)
    {
        var misses = new List<string>();
        foreach (var (target, source) in TablePairs)
        {
            if (!m.Tables.TryGetValue(target, out var map)) { misses.Add($"{target}: no table map"); continue; }
            var actual = map.Sources.FirstOrDefault() ?? "(none)";
            if (!string.Equals(actual, source, StringComparison.OrdinalIgnoreCase))
                misses.Add($"{target}: paired with {actual} (confidence {map.Confidence}), expected {source}");
        }
        foreach (var (target, column, source) in ColumnPairs)
        {
            if (!m.Tables.TryGetValue(target, out var map) || !map.Columns.TryGetValue(column, out var cm))
            {
                misses.Add($"{target}.{column}: no column map");
                continue;
            }
            var actual = cm.SourceColumns.FirstOrDefault() ?? "(unmapped)";
            if (!string.Equals(actual, source, StringComparison.OrdinalIgnoreCase))
                misses.Add($"{target}.{column}: got {actual} (confidence {cm.Confidence}; candidates {string.Join(", ", (cm.Candidates ?? []).Select(c => $"{c.Source} {c.Score} [{c.Why}]"))}), expected {source}");
        }
        return misses;
    }
}
```

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/AutoMapperTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Matching;

public class AutoMapperTests
{
    private static readonly CatalogSnapshot Src = SampleCatalogs.Source();
    private static readonly CatalogSnapshot Tgt = SampleCatalogs.Target();
    private static readonly Dictionary<string, string> Pairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.Customers"] = "dbo.CUST", ["app.Addresses"] = "dbo.ADDR", ["app.Products"] = "dbo.PROD",
        ["app.Orders"] = "dbo.ORD_HDR", ["app.OrderLines"] = "dbo.ORD_LINE", ["app.AuditEvents"] = "dbo.AUDIT_LOG",
    };

    private static MatchContext Ctx() => new(Src, Tgt, Synonyms.Default());
    private static TableInfo S(string key) => Src.FindTable(key)!;
    private static TableInfo T(string key) => Tgt.FindTable(key)!;
    private static ColumnScore Score(string tgtTable, string tgtCol, string srcTable, string srcCol) =>
        ColumnScorer.Score(Ctx(), T(tgtTable), T(tgtTable).FindColumn(tgtCol)!, S(srcTable), S(srcTable).FindColumn(srcCol)!, Pairs);

    [Theory]
    [InlineData("cust", "customer")]
    [InlineData("addr", "address")]
    [InlineData("prod", "product")]
    [InlineData("ord", "order")]
    [InlineData("nm", "name")]
    [InlineData("dt", "date")]
    [InlineData("no", "number")]
    [InlineData("prc", "price")]
    [InlineData("amt", "amount")]
    [InlineData("qty", "quantity")]
    [InlineData("ctry", "country")]
    [InlineData("cd", "code")]
    [InlineData("zip", "postal")]
    [InlineData("zip", "code")]
    [InlineData("flg", "flag")]
    [InlineData("dob", "birth")]
    [InlineData("dob", "date")]
    [InlineData("usr", "user")]
    [InlineData("ts", "time")]
    [InlineData("txt", "text")]
    [InlineData("cmnt", "comment")]
    [InlineData("desc", "description")]
    public void Default_synonyms_cover_the_sample_vocabulary(string abbreviation, string expected)
    {
        Assert.Contains(expected, Synonyms.Default().Expand(abbreviation));
    }

    [Fact]
    public void Name_similarity_prefers_the_matching_column()
    {
        var ctx = Ctx();
        var customers = T("app.Customers");
        var cust = S("dbo.CUST");
        var email = customers.FindColumn("Email")!;
        Assert.True(ctx.ColumnNameSimilarity(customers, email, cust, cust.FindColumn("EMAIL_ADDR")!)
                    > ctx.ColumnNameSimilarity(customers, email, cust, cust.FindColumn("PHONE_NO")!));
        Assert.Equal(1.0, ctx.ColumnNameSimilarity(customers, customers.FindColumn("BirthDate")!, cust, cust.FindColumn("DOB")!), 6);
    }

    [Fact]
    public void Structure_rewards_matching_keys_and_paired_foreign_keys()
    {
        Assert.Equal(1.0, Score("app.Customers", "CustomerId", "dbo.CUST", "CUST_ID").Structure);
        Assert.Equal(1.0, Score("app.Orders", "CustomerId", "dbo.ORD_HDR", "CUST_ID").Structure);
        var unpaired = ColumnScorer.Structure(T("app.Orders"), T("app.Orders").FindColumn("CustomerId")!,
            S("dbo.ORD_HDR"), S("dbo.ORD_HDR").FindColumn("CUST_ID")!, new Dictionary<string, string>());
        Assert.Equal(0.875, unpaired);
        // PK vs non-PK, FK only on source, identity vs not, both NOT NULL
        Assert.Equal(0.25, ColumnScorer.Structure(T("app.Customers"), T("app.Customers").FindColumn("CustomerId")!,
            S("dbo.ADDR"), S("dbo.ADDR").FindColumn("CUST_ID")!, Pairs));
        // nullable target from NOT NULL source scores 0.75 on nullability
        Assert.Equal((1 + 1 + 1 + 0.75) / 4, Score("app.Customers", "Email", "dbo.CUST", "CUST_NM").Structure);
    }

    [Fact]
    public void Profile_compares_class_nulls_and_length_against_an_empty_target()
    {
        Assert.Equal(1.0, Score("app.Customers", "Email", "dbo.CUST", "EMAIL_ADDR").Profile);
        // email class vs a name column (0.3), nulls into NOT NULL (0), length fits (1)
        Assert.Equal((0.3 + 0 + 1) / 3, Score("app.Customers", "FirstName", "dbo.CUST", "EMAIL_ADDR").Profile!.Value, 6);
        Assert.Null(Score("app.Customers", "Phone", "dbo.CUST", "PHONE_NO").Profile);
    }

    [Fact]
    public void Total_uses_the_spec_weights()
    {
        var withProfile = Score("app.Customers", "Email", "dbo.CUST", "EMAIL_ADDR");
        Assert.Equal(Math.Round(0.45 * withProfile.Name + 0.20 * withProfile.Type.Score + 0.15 * withProfile.Structure + 0.20 * withProfile.Profile!.Value, 3), withProfile.Total);
        var noProfile = Score("app.Customers", "Phone", "dbo.CUST", "PHONE_NO");
        Assert.Equal(Math.Round(0.65 * noProfile.Name + 0.20 * noProfile.Type.Score + 0.15 * noProfile.Structure, 3), noProfile.Total);
        Assert.Contains("type widening", noProfile.Why);
    }

    [Fact]
    public void Table_scores_rank_the_right_source_first()
    {
        var ctx = Ctx();
        foreach (var (target, source) in AutoMapperBaseline.TablePairs)
        {
            var best = Src.Tables.OrderByDescending(s => TableScorer.Score(ctx, T(target), s).Total).First();
            Assert.Equal(source, best.Key);
        }
        Assert.Equal(1.0, TableScorer.DegreeSimilarity(0, 0));
        Assert.Equal(0.5, TableScorer.DegreeSimilarity(2, 4));
        Assert.Equal(3, TableScorer.Degree(Src, S("dbo.ORD_HDR")));
    }

    [Fact]
    public void Baseline_on_sample_catalogs()
    {
        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions());
        var misses = AutoMapperBaseline.Misses(m);
        Assert.True(misses.Count == 0, "baseline misses:\n" + string.Join("\n", misses));
    }

    [Fact]
    public void Draft_shape_follows_the_rules()
    {
        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions());

        Assert.Equal(["app.Addresses", "app.AuditEvents", "app.Customers", "app.OrderLines", "app.Orders", "app.Products"], m.Tables.Keys.Order().ToArray());
        Assert.Equal(new DropDecision("empty table with no matching target", MapMethod.Vector), m.Drops["dbo.TMP_IMPORT"]);
        Assert.False(m.Tables["app.Customers"].Columns.ContainsKey("DisplayName"));     // computed
        Assert.False(m.Tables["app.Products"].Columns.ContainsKey("RowVer"));          // rowversion
        var id = m.Tables["app.Customers"].Columns["CustomerId"];
        Assert.Equal("s.[CUST_ID]", id.Expr);
        Assert.Equal(["dbo.CUST.CUST_ID"], id.SourceColumns);
        var city = m.Tables["app.Addresses"].Columns["City"];
        Assert.Equal(MapMethod.Fuzzy, city.Method);                                    // same tokens, varchar -> nvarchar is widening
        var ctry = m.Tables["app.Addresses"].Columns["CountryCode"];
        Assert.Equal(MapMethod.Exact, ctry.Method);                                    // same tokens, char(2) -> char(2)
        Assert.Null(ctry.TypeRisk);
        var dob = m.Tables["app.Customers"].Columns["BirthDate"];
        Assert.Equal("time part dropped", dob.TypeRisk);
        Assert.All(m.Tables.Values.SelectMany(t => t.Columns.Values), c => Assert.True(c.Candidates is null || c.Candidates.Count <= 3));
        var sources = m.Tables.Values.SelectMany(t => t.Columns.Values).SelectMany(c => c.SourceColumns).ToList();
        Assert.Equal(sources.Count, sources.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // one-to-one per pair
        Assert.Contains("dbo.ORD_STATUS.STATUS_CD", MappingValidator.UncoveredSourceColumns(m, Src));
        Assert.Empty(MappingValidator.Errors(m, Src, Tgt));
    }

    [Fact]
    public void Unpaired_target_gets_no_sources_and_candidates()
    {
        var tgt = Tgt with { Tables = [.. Tgt.Tables, new TableInfo("app", "Zebra", 0, 0,
            [new ColumnInfo("Stripes", 1, "int", 0, 10, 0, false, false, false, false, null, null, null)], [], [], 0, null, null)] };
        var m = AutoMapper.Map(Src, tgt, Synonyms.Default(), new MatchOptions());
        var zebra = m.Tables["app.Zebra"];
        Assert.Empty(zebra.Sources);
        Assert.Equal(MapMethod.Vector, zebra.Method);
        Assert.True(zebra.Confidence < 0.5);
        Assert.Empty(zebra.Columns);
        Assert.Contains(MappingValidator.Attention(m, new MatchOptions()), a => a.StartsWith("app.Zebra: no confident source table"));
    }

    [Fact]
    public void Output_is_deterministic()
    {
        Assert.Equal(
            Json.Serialize(AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions())),
            Json.Serialize(AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions())));
    }

    [Fact]
    public void Carry_over_keeps_agent_and_human_decisions_that_still_resolve()
    {
        var carry = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions());
        carry.Tables["app.Customers"].Columns["FirstName"] = new ColumnMap
        {
            Expr = "LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", SourceColumns = ["dbo.CUST.CUST_NM"],
            Confidence = 1, Method = MapMethod.Agent, Rationale = "First word of CUST_NM."
        };
        carry.Tables["app.Customers"].Columns["LastName"] = new ColumnMap
        {
            Expr = "s.[GONE]", SourceColumns = ["dbo.CUST.GONE"], Confidence = 1, Method = MapMethod.Human
        };
        carry.Tables["app.Orders"] = new TableMap
        {
            Kind = "merge", Sources = ["dbo.ORD_HDR", "dbo.ORD_STATUS"],
            From = "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]",
            Confidence = 1, Method = MapMethod.Agent, Rationale = "Status code comes from the lookup.",
            Columns = { ["StatusCode"] = new ColumnMap { Expr = "st.[STATUS_CD]", SourceColumns = ["dbo.ORD_STATUS.STATUS_CD", "dbo.ORD_HDR.STATUS_ID"], Confidence = 1, Method = MapMethod.Agent } }
        };
        carry.Tables["app.Gone"] = new TableMap { Sources = ["dbo.CUST"], Method = MapMethod.Human };
        carry.Drops["dbo.CUST.FAX_NO"] = new DropDecision("No fax in ShopV2.", MapMethod.Agent);
        carry.Drops["dbo.CUST.GONE"] = new DropDecision("stale", MapMethod.Human);
        carry.Notes.Add("keep me");

        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions(), carry);

        var first = m.Tables["app.Customers"].Columns["FirstName"];
        Assert.Equal(MapMethod.Carried, first.Method);
        Assert.Equal("First word of CUST_NM.", first.Rationale);
        Assert.NotEqual("s.[GONE]", m.Tables["app.Customers"].Columns["LastName"].Expr);
        var orders = m.Tables["app.Orders"];
        Assert.Equal(("merge", MapMethod.Carried), (orders.Kind, orders.Method));
        Assert.StartsWith("[dbo].[ORD_HDR] AS s JOIN", orders.From);
        Assert.Equal(MapMethod.Carried, orders.Columns["StatusCode"].Method);
        Assert.Equal("s.[ORD_ID]", orders.Columns["OrderId"].Expr);                   // remaining columns auto-assigned
        Assert.Equal(["OrderId", "CustomerId", "OrderDate", "StatusCode", "ShippingAddressId", "TotalAmount", "Comment"], orders.Columns.Keys.ToArray());
        Assert.False(m.Tables.ContainsKey("app.Gone"));
        Assert.Equal(new DropDecision("No fax in ShopV2.", MapMethod.Carried), m.Drops["dbo.CUST.FAX_NO"]);
        Assert.False(m.Drops.ContainsKey("dbo.CUST.GONE"));
        Assert.Equal(["keep me"], m.Notes);
    }

    [Fact]
    public void Carry_over_of_the_approved_mapping_is_complete()
    {
        var m = AutoMapper.Map(Src, Tgt, Synonyms.Default(), new MatchOptions(), SampleMappings.Approved());
        Assert.Empty(MappingValidator.Blockers(m, Src, Tgt));
        Assert.Empty(MappingValidator.Attention(m, new MatchOptions()));
        Assert.All(m.Tables.Values.SelectMany(t => t.Columns.Values), c => Assert.Equal(MapMethod.Carried, c.Method));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AutoMapperTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'MatchContext' could not be found` (also `ColumnScore`, `ColumnScorer`, `TableScorer`, `AutoMapper`).

- [ ] **Step 3: Implement the scorers, carry-over and `AutoMapper`**

Create `plugins/db-migrate/engine/Dbm/Core/Matching/MatchContext.cs`:

```csharp
using Dbm.Core.Catalog;

namespace Dbm.Core.Matching;

/// <summary>Name tokens and TF-IDF vectors for every table and column of both catalogs, fitted once per auto-map run.
/// Column tokens use the owning table's tokens as context (so CUST.CUST_ID → [id]).</summary>
public sealed class MatchContext
{
    private readonly Dictionary<string, IReadOnlyList<string>> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SparseVector> _vectors = new(StringComparer.OrdinalIgnoreCase);

    public CatalogSnapshot Src { get; }
    public CatalogSnapshot Tgt { get; }

    public MatchContext(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms)
    {
        Src = src;
        Tgt = tgt;
        var tableDocs = new List<(string Key, IReadOnlyList<string> Tokens)>();
        var columnDocs = new List<(string Key, IReadOnlyList<string> Tokens)>();
        foreach (var (isSource, catalog) in new[] { (true, src), (false, tgt) })
        {
            foreach (var table in catalog.Tables)
            {
                var tableTokens = NameNormalizer.Tokens(table.Name, synonyms);
                tableDocs.Add((TableKey(isSource, table), tableTokens));
                var context = new HashSet<string>(tableTokens, StringComparer.OrdinalIgnoreCase);
                foreach (var column in table.Columns)
                    columnDocs.Add((ColumnKey(isSource, table, column), NameNormalizer.Tokens(column.Name, synonyms, context)));
            }
        }
        Index(tableDocs, new NgramTfidfVectorizer());
        Index(columnDocs, new NgramTfidfVectorizer());
    }

    public IReadOnlyList<string> TableTokens(bool isSource, TableInfo table) => _tokens[TableKey(isSource, table)];

    public IReadOnlyList<string> ColumnTokens(bool isSource, TableInfo table, ColumnInfo column) => _tokens[ColumnKey(isSource, table, column)];

    public double TableNameSimilarity(TableInfo tgtTable, TableInfo srcTable) =>
        Cosine(TableKey(false, tgtTable), TableKey(true, srcTable));

    public double ColumnNameSimilarity(TableInfo tgtTable, ColumnInfo tgtColumn, TableInfo srcTable, ColumnInfo srcColumn) =>
        Cosine(ColumnKey(false, tgtTable, tgtColumn), ColumnKey(true, srcTable, srcColumn));

    private void Index(List<(string Key, IReadOnlyList<string> Tokens)> docs, IVectorizer vectorizer)
    {
        vectorizer.Fit(docs.Select(d => d.Tokens));
        foreach (var (key, tokens) in docs)
        {
            _tokens[key] = tokens;
            _vectors[key] = vectorizer.Transform(tokens);
        }
    }

    private double Cosine(string a, string b) => Math.Clamp(_vectors[a].Dot(_vectors[b]), 0.0, 1.0);

    private static string TableKey(bool isSource, TableInfo t) => $"T|{(isSource ? "src" : "tgt")}|{t.Key}";

    private static string ColumnKey(bool isSource, TableInfo t, ColumnInfo c) => $"C|{(isSource ? "src" : "tgt")}|{t.Key}|{c.Name}";
}
```

Create `plugins/db-migrate/engine/Dbm/Core/Matching/ColumnScorer.cs`:

```csharp
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
```

Create `plugins/db-migrate/engine/Dbm/Core/Matching/TableScorer.cs`:

```csharp
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
```

Create `plugins/db-migrate/engine/Dbm/Core/Matching/MappingCarryOver.cs`:

```csharp
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
                map.Columns[column.Name] = new ColumnMap
                {
                    Expr = cm.Expr,
                    SourceColumns = cm.SourceColumns.ToList(),
                    Default = cm.Default,
                    Confidence = cm.Confidence,
                    Method = MapMethod.Carried,
                    Rationale = cm.Rationale,
                    TypeRisk = cm.TypeRisk
                };
            }
            result[table.Key] = map;
        }
        return result;
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
```

Create `plugins/db-migrate/engine/Dbm/Core/Matching/AutoMapper.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AutoMapperTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 33`.

`Default_synonyms_cover_the_sample_vocabulary` pins the M2 dictionary entries the baseline needs (M2's embedded `synonyms.json` has them all; `ts` reaches `time` through `ts → timestamp` plus the `time`/`timestamp` group). If `Baseline_on_sample_catalogs` fails, its message lists every miss with scores and `why` parts — fix the scoring code in this task; never shrink `AutoMapperBaseline`.

- [ ] **Step 5: Write the failing job and command tests**

Create `plugins/db-migrate/engine/Dbm.Tests/Support/MappingTestData.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Tests.Support;

/// <summary>M3 helpers for services opened by TempProject / TestWorkspace.OpenServices.</summary>
public static class MappingTestData
{
    /// <summary>Saves SampleCatalogs as the discovered source and target catalogs (as DiscoverJob would).</summary>
    public static DbmServices WithSampleCatalogs(this DbmServices services)
    {
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();
        services.Catalog.Save(Side.Src, src, Fingerprint.Compute(src));
        services.Catalog.Save(Side.Tgt, tgt, Fingerprint.Compute(tgt));
        return services;
    }

    /// <summary>Stores <paramref name="m"/> as the next Mapping version, makes it current and sets the phase status.</summary>
    public static ArtifactRow AddMapping(this DbmServices services, MappingPayload m, PhaseStatus status, string author = "script")
    {
        var version = services.Artifacts.NextVersion(PhaseName.Mapping);
        var row = services.Artifacts.Add(PhaseName.Mapping, version, Json.Serialize(m), author, null);
        services.Phases.SetCurrentVersion(PhaseName.Mapping, version);
        services.Phases.SetStatus(PhaseName.Mapping, status);
        return row;
    }

    /// <summary>Marks every phase before <paramref name="phase"/> approved (for Mapping: Setup, Discovery, Analysis).</summary>
    public static void ApproveBefore(this DbmServices services, PhaseName phase)
    {
        foreach (var p in WorkflowEngine.Order.TakeWhile(p => p != phase))
            services.Phases.SetStatus(p, PhaseStatus.Approved);
    }
}
```

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/AutomapJobTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Matching;

public class AutomapJobTests
{
    private static JobContext Ctx(DbmServices services, List<string> log) => new()
    {
        Services = services,
        Job = new JobRow(1, "automap", PhaseName.Mapping, JobStatus.Running, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
        Log = log.Add
    };

    [Fact]
    public void Kind_is_automap_and_the_handler_is_registered()
    {
        Assert.Equal("automap", new AutomapJob().Kind);
        using var project = TempProject.Create();
        Assert.IsType<AutomapJob>(project.Services.JobHandlers["automap"]);
    }

    [Fact]
    public async Task Drafts_a_mapping_from_the_stored_catalogs()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var log = new List<string>();

        var result = await new AutomapJob().RunAsync(Ctx(services, log), CancellationToken.None);

        var m = Json.FromNode<MappingPayload>(result.DraftPayload!);
        Assert.Equal(6, m.Tables.Count);
        var misses = AutoMapperBaseline.Misses(m);
        Assert.True(misses.Count == 0, string.Join("\n", misses));
        Assert.Matches(@"^6 tables mapped, \d+ need attention, \d+ blockers$", result.Summary);
        Assert.Contains(log, l => l.Contains("carry-over none"));
    }

    [Fact]
    public async Task Carries_over_the_latest_mapping_artifact()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.AddMapping(SampleMappings.Approved(), PhaseStatus.Running, "human");
        var log = new List<string>();

        var result = await new AutomapJob().RunAsync(Ctx(services, log), CancellationToken.None);

        Assert.Equal("6 tables mapped, 0 need attention, 0 blockers", result.Summary);
        var m = Json.FromNode<MappingPayload>(result.DraftPayload!);
        Assert.Equal(MapMethod.Carried, m.Tables["app.Customers"].Columns["FirstName"].Method);
        Assert.Contains(log, l => l.Contains("carry-over v0"));
    }

    [Fact]
    public async Task Uses_the_project_auto_accept_setting()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var strict = await new AutomapJob().RunAsync(Ctx(services, []), CancellationToken.None);
        services.Project.SaveSettings(services.Project.GetSettings() with { AutoAcceptScore = 0.5 });

        var lenient = await new AutomapJob().RunAsync(Ctx(services, []), CancellationToken.None);

        static int Attention(string? summary) => int.Parse(summary!.Split(", ")[1].Split(' ')[0]);
        Assert.True(Attention(lenient.Summary) < Attention(strict.Summary), $"{lenient.Summary} vs {strict.Summary}");
    }

    [Fact]
    public async Task Fails_when_discovery_has_not_run()
    {
        using var project = TempProject.Create();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new AutomapJob().RunAsync(Ctx(project.Services, []), CancellationToken.None));
        Assert.Contains("run discovery first", ex.Message);
    }
}
```

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/MapCommandTests.cs`:

```csharp
using Dbm.Cli;
using Dbm.Cli.Commands;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Cli;

public class MapCommandTests
{
    [Fact]
    public void Command_is_registered()
    {
        Assert.Contains(CommandRegistry.All(), c => c.Name == "map auto" && c is MapAutoCommand);
    }

    [Fact]
    public async Task Map_auto_outside_running_or_drafting_fails_with_wrong_phase()
    {
        using var project = TempProject.Create();

        var result = await CliRunner.RunAsync(project.Ws, null, "map", "auto");

        Assert.Equal(1, result.Exit);
        Assert.Equal("wrong_phase", (string?)result.Json["error"]);
        Assert.Contains("pending", (string?)result.Json["message"]);
    }

    [Fact]
    public async Task Map_auto_without_a_project_fails_with_no_project()
    {
        using var workspace = new TestWorkspace();

        var result = await CliRunner.RunAsync(workspace.Ws, null, "map", "auto");

        Assert.Equal(1, result.Exit);
        Assert.Equal("no_project", (string?)result.Json["error"]);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AutomapJobTests|FullyQualifiedName~MapCommandTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'AutomapJob' could not be found` and `'MapAutoCommand' could not be found`.

- [ ] **Step 7: Implement `AutomapJob`, `MapAutoCommand` and register both**

Create `plugins/db-migrate/engine/Dbm/Core/Matching/AutomapJob.cs`:

```csharp
using System.Text.Json;
using Dbm.Core.Jobs;
using Dbm.Core.Mapping;
using Dbm.Core.State;

namespace Dbm.Core.Matching;

/// <summary>Job "automap": builds the script draft of the mapping from the stored catalogs, carrying over agent/human decisions
/// from the latest Mapping artifact.</summary>
public sealed class AutomapJob : IJobHandler
{
    public string Kind => "automap";

    public Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var s = ctx.Services;
        var src = s.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("source catalog missing; run discovery first");
        var tgt = s.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("target catalog missing; run discovery first");
        var synonyms = Synonyms.ForProject(s.Ws);
        var settings = s.Project.GetSettings();
        var options = new MatchOptions(settings.AutoAcceptScore, settings.CandidateScore);

        MappingPayload? carryOver = null;
        var latest = s.Artifacts.Latest(PhaseName.Mapping);
        if (latest is not null)
        {
            try { carryOver = Json.Deserialize<MappingPayload>(latest.PayloadJson); }
            catch (JsonException e) { ctx.Log($"automap: ignoring unreadable mapping v{latest.Version}: {e.Message}"); }
        }
        ctx.Log($"automap: {tgt.Tables.Count} target tables, {src.Tables.Count} source tables, carry-over {(carryOver is null ? "none" : $"v{latest!.Version}")}");
        ct.ThrowIfCancellationRequested();

        var payload = AutoMapper.Map(src, tgt, synonyms, options, carryOver);
        var mapped = payload.Tables.Values.Count(t => t.Sources.Count > 0);
        var attention = MappingValidator.Attention(payload, options).Count;
        var blockers = MappingValidator.Blockers(payload, src, tgt).Count;
        var summary = $"{mapped} tables mapped, {attention} need attention, {blockers} blockers";
        ctx.Log($"automap: {summary}");
        return Task.FromResult(new JobResult(Json.ToNode(payload), summary));
    }
}
```

Create `plugins/db-migrate/engine/Dbm/Cli/Commands/MapCommands.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.State;

namespace Dbm.Cli.Commands;

/// <summary><c>dbm map auto [--inline]</c>: re-runs the auto-mapper while Mapping is running or drafting.</summary>
public sealed class MapAutoCommand : ICommand
{
    public string Name => "map auto";
    public string Help => "Re-run the auto-mapper (only while Mapping is running or drafting) [--inline runs it now]";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var status = services.Phases.Get(PhaseName.Mapping).Status;
        if (status is not (PhaseStatus.Running or PhaseStatus.Drafting))
            return Output.Fail(ctx, "wrong_phase",
                $"Mapping is {EnumText.ToText(status)}; `map auto` only runs while Mapping is running or drafting.");

        // A queued or running automap job (e.g. right after Analysis was approved) is reused instead of queueing a second one.
        if (!services.Jobs.Active().Any(j => j.Phase == PhaseName.Mapping)) services.Workflow.RetryJob(PhaseName.Mapping);
        if (!args.Flag("inline")) return Output.Ok(ctx, new { ok = true, queued = "automap" });

        var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
        var job = services.Jobs.LatestFor(PhaseName.Mapping);
        if (job is { Status: JobStatus.Failed })
            return Output.Fail(ctx, "job_failed", job.Error ?? "automap failed");
        var phase = services.Phases.Get(PhaseName.Mapping);
        return Output.Ok(ctx, new
        {
            ok = true,
            ran,
            status = EnumText.ToText(phase.Status),
            version = phase.CurrentVersion,
            summary = services.Artifacts.Latest(PhaseName.Mapping)?.Summary
        });
    }
}
```

Register the job — in `plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs`, below the `// milestone registrations below` marker, directly after the line `        yield return new Dbm.Core.Analysis.AnalyzeJob();   // T2.6`, add:

```csharp
        yield return new Dbm.Core.Matching.AutomapJob();   // T3.3
```

Register the command — in `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs`, directly after the line `        new ShowCommand(),       // T2.5`, add:

```csharp
        new MapAutoCommand(),    // T3.3
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AutomapJobTests|FullyQualifiedName~MapCommandTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 8`.

- [ ] **Step 9: Write the baseline integration test**

Create `plugins/db-migrate/engine/Dbm.Tests/Integration/Mapping/AutoMapperBaselineIntegrationTests.cs`:

```csharp
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Mapping;

[Trait("Category", "Integration")]
public class AutoMapperBaselineIntegrationTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Baseline_on_the_profiled_sample_pair()
    {
        var src = await SampleExtract.CatalogAsync(fixture.Pair.SourceCs, profile: true);
        var tgt = await SampleExtract.CatalogAsync(fixture.Pair.TargetCs, profile: true);

        var m = AutoMapper.Map(src, tgt, Synonyms.Default(), new MatchOptions());

        var misses = AutoMapperBaseline.Misses(m);
        Assert.True(misses.Count == 0, "baseline misses:\n" + string.Join("\n", misses));
        Assert.Equal(MapMethod.Vector, m.Drops["dbo.TMP_IMPORT"].Method);
        Assert.Empty(MappingValidator.Errors(m, src, tgt));
        Assert.Empty(MappingValidator.Errors(SampleMappings.Approved(), src, tgt));
        Assert.Empty(MappingValidator.Blockers(SampleMappings.Approved(), src, tgt));
    }
}
```

- [ ] **Step 10: Run the integration test and the whole unit suite**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AutoMapperBaselineIntegrationTests"`
Expected: PASS (1 test). A failure message lists each miss with its scores; real profiles differ from the in-code ones (e.g. semantic classes), so fix the scoring in this task until both baselines pass.

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`
Expected: PASS, 0 failures.

- [ ] **Step 11: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Matching/MatchContext.cs plugins/db-migrate/engine/Dbm/Core/Matching/ColumnScorer.cs plugins/db-migrate/engine/Dbm/Core/Matching/TableScorer.cs plugins/db-migrate/engine/Dbm/Core/Matching/MappingCarryOver.cs plugins/db-migrate/engine/Dbm/Core/Matching/AutoMapper.cs plugins/db-migrate/engine/Dbm/Core/Matching/AutomapJob.cs plugins/db-migrate/engine/Dbm/Cli/Commands/MapCommands.cs plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs plugins/db-migrate/engine/Dbm.Tests/Support/AutoMapperBaseline.cs plugins/db-migrate/engine/Dbm.Tests/Support/MappingTestData.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/AutoMapperTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Matching/AutomapJobTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/MapCommandTests.cs plugins/db-migrate/engine/Dbm.Tests/Integration/Mapping/AutoMapperBaselineIntegrationTests.cs
git commit -F - <<'EOF'
feat(matching): auto-mapper with carry-over, automap job and `dbm map auto` (T3.3)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 3.4: Mapping module + `mapping-architect` agent

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingPacket.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingModule.cs`
- Create: `plugins/db-migrate/agents/mapping-architect.md`
- Modify: `plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs` (one entry)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingPacketTests.cs`, `MappingModuleTests.cs`, `MappingFlowTests.cs`, `MappingArchitectAgentTests.cs` (all in `Dbm.Tests/Unit/Mapping/`)

**Interfaces:**
- Consumes: `IPhaseModule`, `ModuleContext { Services, Current, OpenFeedback }`, `PacketMode`, `PayloadCheck`, `WorkflowEngine.Next/ApplyPatch/Approve`, `WorkflowException.Details`, `NextAction` (C5); `Patch`, `PatchOp`, `JsonPatch.Apply` (C4); `ArtifactRow`, `FeedbackRow`, `FeedbackStatus`, `PhaseName`, `PhaseStatus`, `Side` (C2); `DbmServices.Catalog/Project/Modules` (C6); `CliApp.RunAsync` (C8); `ColumnInfo.TypeDisplay` (C10); T3.2/T3.3 types (`MappingValidator`, `AutoMapper`, `MatchOptions`, `Synonyms`, `SampleCatalogs`, `SampleMappings`, `MappingTestData`); M1/M2 test helpers `TempProject`, `CliRunner`; `Patch.Parse` (C4).
- Produces:
  ```csharp
  namespace Dbm.Core.Mapping;
  public sealed class MappingModule(DbmServices services) : IPhaseModule
  {   // Phase = PhaseName.Mapping, Agent = "mapping-architect", JobKind = "automap"
      public bool NeedsAgent(JsonNode draft);                                  // Attention or Blockers non-empty
      public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode);         // MappingPacket.Draft / MappingPacket.Rework
      public PayloadCheck Validate(ModuleContext ctx, JsonNode payload);       // Errors = MappingValidator.Errors; Warnings = Blockers + Attention
      public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload);   // MappingValidator.Blockers
      public string Summarize(JsonNode payload);                               // "N tables, M columns mapped, D drops, A attention, B blockers"
  }
  public static class MappingPacket
  {
      public const int MaxSourceTables = 3, MaxSamples = 3;
      public const string Hint;
      public static JsonObject Legend();
      public static string Summary(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options);
      public static JsonObject Draft(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options);
      public static JsonObject Rework(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options, IReadOnlyList<FeedbackRow> feedback);
      public static JsonNode? Resolve(string? anchor, MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options);
      public static JsonObject TableDetail(TableInfo t, TableMap? map, CatalogSnapshot src);
      public static JsonObject ColumnDetail(TableInfo t, ColumnInfo c, MappingPayload m, CatalogSnapshot src);
      public static JsonObject TargetColumn(TableInfo t, ColumnInfo c);
      public static JsonObject SourceTable(TableInfo s);
      public static JsonObject SourceColumn(TableInfo s, ColumnInfo c);
  }
  ```

**Packet `data` shapes (normative for the agent playbook).**
- Draft: `{legend, options:{autoAccept,candidate}, blockers[], attention[], confident:[{target, source, confidence, columns}], detail:[{target, kind, sources, from?, filter?, confidence, method, rationale?, tableCandidates?, targetColumns:[{name,type,nullable,identity?,computed?,rowversion?,default?,pk?,fk?}], columns:{<Col>:{expr, sourceColumns?, default?, confidence, method, typeRisk?, rationale?, candidates?:[{source,score,why}]}}, sourceTables:[{key, rows, columns:[{name,type,nullable,pk?,fk?,nullRatio?,distinctRatio?,class?,maxLen?,samples?}]}]}], uncovered:["schema.table.column type"], drops:{key: reason}, hint}`. A table is *confident* when no blocker mentions it, it has a source (or is `skip`), and neither it nor any of its columns needs review. `sourceTables` = primary source + other sources + table candidates, distinct, at most 3; `samples` at most 3.
- Rework: `{legend, options, summary, blockers[], attention[], contexts:[{id, anchor, context?}], hint}`. Anchors: `tablemap:<t>` / `table:tgt:<t>` → `TableDetail`; `colmap:<t>.<c>` / `column:tgt:<t>.<c>` → `ColumnDetail` (target column, map, `sources`, `from`, and the profiles of every referenced or candidate source column with `key`); `column:src:<s>.<c>` → `{table, column, usedBy[], drop?}`; `table:src:<s>` → source table + `usedBy[]` + `drop?`; `null`/`general`/`narrative` → `{uncovered, drops}`; any other anchor → no `context`; a key that does not resolve → `{error}`.
- Legend and hint avoid characters that `System.Text.Json` escapes by default (quotes, apostrophes, backticks, `<`, `>`, `+`) so the packet stays readable.

- [ ] **Step 1: Write the failing packet tests**

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingPacketTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingPacketTests
{
    private static readonly MatchOptions Options = new();

    private static JsonObject Draft(MappingPayload m) => MappingPacket.Draft(m, SampleCatalogs.Source(), SampleCatalogs.Target(), Options);

    private static IEnumerable<string?> Texts(JsonNode? array) => array!.AsArray().Select(n => (string?)n);

    private static FeedbackRow Feedback(long id, string? anchor) =>
        new(id, PhaseName.Mapping, 1, anchor, "please fix", FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Approved_mapping_is_summarised_as_confident_tables_only()
    {
        var data = Draft(SampleMappings.Approved());

        Assert.Equal(6, data["confident"]!.AsArray().Count);
        Assert.Empty(data["detail"]!.AsArray());
        Assert.Empty(data["blockers"]!.AsArray());
        Assert.Empty(data["uncovered"]!.AsArray());
        Assert.Equal(4, data["drops"]!.AsObject().Count);
        var orders = data["confident"]!.AsArray().Single(n => (string?)n!["target"] == "app.Orders")!;
        Assert.Equal("dbo.ORD_HDR, dbo.ORD_STATUS", (string?)orders["source"]);
        Assert.Equal(7, (int?)orders["columns"]);
        Assert.NotNull(data["legend"]);
        Assert.Contains("dbm show", (string?)data["hint"]);
    }

    [Fact]
    public void Uncertain_table_is_detailed_with_target_columns_map_and_source_profiles()
    {
        var m = SampleMappings.Approved();
        m.Tables["app.Customers"].Columns["Email"].Method = MapMethod.Fuzzy;
        m.Tables["app.Customers"].Columns["Email"].Confidence = 0.6;

        var data = Draft(m);

        Assert.Equal(5, data["confident"]!.AsArray().Count);
        var detail = data["detail"]!.AsArray().Single()!;
        Assert.Equal("app.Customers", (string?)detail["target"]);
        var targetColumns = detail["targetColumns"]!.AsArray();
        Assert.Equal(10, targetColumns.Count);
        var id = targetColumns.Single(c => (string?)c!["name"] == "CustomerId")!;
        Assert.True((bool?)id["identity"]);
        Assert.True((bool?)id["pk"]);
        var primaryAddress = targetColumns.Single(c => (string?)c!["name"] == "PrimaryAddressId")!;
        Assert.Equal("app.Addresses.AddressId", (string?)primaryAddress["fk"]);
        Assert.Equal("(sysutcdatetime())", (string?)targetColumns.Single(c => (string?)c!["name"] == "CreatedAt")!["default"]);
        Assert.True((bool?)targetColumns.Single(c => (string?)c!["name"] == "DisplayName")!["computed"]);
        Assert.Equal("fuzzy", (string?)detail["columns"]!["Email"]!["method"]);
        var source = detail["sourceTables"]!.AsArray().Single()!;
        Assert.Equal("dbo.CUST", (string?)source["key"]);
        var name = source["columns"]!.AsArray().Single(c => (string?)c!["name"] == "CUST_NM")!;
        Assert.Equal("text", (string?)name["class"]);
        Assert.Equal(3, name["samples"]!.AsArray().Count);
    }

    [Fact]
    public void Auto_draft_packet_lists_blockers_uncovered_columns_and_caps_source_tables()
    {
        var m = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), Options);

        var data = Draft(m);

        Assert.Contains("source table dbo.ORD_STATUS is not mapped or dropped (3 columns)", Texts(data["blockers"]));
        Assert.Contains("dbo.ORD_STATUS.STATUS_CD varchar(10)", Texts(data["uncovered"]));
        Assert.Contains(data["detail"]!.AsArray(), d => (string?)d!["target"] == "app.Orders");
        Assert.All(data["detail"]!.AsArray(), d => Assert.InRange(d!["sourceTables"]!.AsArray().Count, 1, MappingPacket.MaxSourceTables));
        Assert.Equal("empty table with no matching target", (string?)data["drops"]!["dbo.TMP_IMPORT"]);
        Assert.DoesNotContain("Server=", data.ToJsonString());
    }

    [Fact]
    public void Samples_are_capped_at_three()
    {
        var src = SampleCatalogs.Source();
        var cust = src.FindTable("dbo.CUST")!;
        var many = cust.Columns[1] with { Profile = cust.Columns[1].Profile! with { Samples = ["a", "b", "c", "d", "e"] } };
        var node = MappingPacket.SourceColumn(cust, many);
        Assert.Equal(["a", "b", "c"], node["samples"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Rework_resolves_each_anchor_to_its_slice()
    {
        var m = SampleMappings.Approved();
        var feedback = new[]
        {
            Feedback(1, "colmap:app.Customers.Email"),
            Feedback(2, "tablemap:app.Orders"),
            Feedback(3, "column:src:dbo.CUST.FAX_NO"),
            Feedback(4, null),
            Feedback(5, "table:src:dbo.ORD_STATUS"),
            Feedback(6, "finding:12"),
            Feedback(7, "colmap:app.Nope.X"),
        };

        var data = MappingPacket.Rework(m, SampleCatalogs.Source(), SampleCatalogs.Target(), Options, feedback);
        var contexts = data["contexts"]!.AsArray();

        Assert.Equal(7, contexts.Count);
        var email = contexts[0]!["context"]!;
        Assert.Equal("Email", (string?)email["column"]!["name"]);
        Assert.Equal("s.[EMAIL_ADDR]", (string?)email["map"]!["expr"]);
        Assert.Equal("dbo.CUST.EMAIL_ADDR", (string?)email["sourceColumns"]![0]!["key"]);
        var orders = contexts[1]!["context"]!;
        Assert.Equal("app.Orders", (string?)orders["target"]);
        Assert.StartsWith("[dbo].[ORD_HDR] AS s JOIN", (string?)orders["from"]);
        Assert.Equal(2, orders["sourceTables"]!.AsArray().Count);
        Assert.Equal("ShopV2 no longer stores fax numbers.", (string?)contexts[2]!["context"]!["drop"]);
        Assert.NotNull(contexts[3]!["context"]!["uncovered"]);
        Assert.Contains("app.Orders", Texts(contexts[4]!["context"]!["usedBy"]));
        Assert.Null(contexts[5]!["context"]);
        Assert.Contains("does not resolve", (string?)contexts[6]!["context"]!["error"]);
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers", (string?)data["summary"]);
    }

    [Fact]
    public void Source_column_context_lists_the_target_columns_that_use_it()
    {
        var node = MappingPacket.Resolve("column:src:dbo.CUST.CUST_NM", SampleMappings.Approved(),
            SampleCatalogs.Source(), SampleCatalogs.Target(), Options)!;
        Assert.Equal(["app.Customers.FirstName", "app.Customers.LastName"], node["usedBy"]!.AsArray().Select(n => (string)n!).ToArray());
    }

    [Fact]
    public void Summary_counts_tables_columns_drops_attention_and_blockers()
    {
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers",
            MappingPacket.Summary(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target(), Options));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingPacketTests"`
Expected: build FAILS with `error CS0103: The name 'MappingPacket' does not exist in the current context`.

- [ ] **Step 3: Implement `MappingPacket.cs`**

Create `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingPacket.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;

namespace Dbm.Core.Mapping;

/// <summary>Builds the compact "data" section of mapping work packets: confident tables summarised, uncertain tables detailed,
/// rework feedback resolved to the slice each anchor points at. Never contains connection strings.</summary>
public static class MappingPacket
{
    public const int MaxSourceTables = 3;
    public const int MaxSamples = 3;

    // Legend and hint avoid characters that System.Text.Json escapes by default (quotes, apostrophes, backticks, angle brackets, plus).
    public const string Hint =
        "Verify names before using them: dbm show schema.table --side src|tgt prints one table; " +
        "dbm search words --side src -k 8 finds columns by meaning. Never invent tables or columns.";

    public static JsonObject Legend() => new()
    {
        ["confidence"] = "0..1; script proposals below options.autoAccept need review",
        ["method"] = "exact|fuzzy|vector = script proposal; agent|human|carried = decided",
        ["expr"] = "T-SQL over the FROM aliases: s = sources[0]; other aliases are declared in from",
        ["sourceColumns"] = "every schema.table.column the expr or its join reads (drives coverage)",
        ["candidates"] = "next best sources with score and why (name, type, structure, profile)",
        ["paths"] = "/tables/{target}/{field}, /tables/{target}/columns/{column}/{field}, /drops/{schema.table or schema.table.column}"
    };

    public static string Summary(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        var live = m.Tables.Values.Where(t => t.Kind != "skip").ToList();
        var tables = live.Count(t => t.Sources.Count > 0);
        var columns = live.Sum(t => t.Columns.Values.Count(c => !string.IsNullOrWhiteSpace(c.Expr)));
        var attention = MappingValidator.Attention(m, options).Count;
        var blockers = MappingValidator.Blockers(m, src, tgt).Count;
        return $"{tables} tables, {columns} columns mapped, {m.Drops.Count} drops, {attention} attention, {blockers} blockers";
    }

    public static JsonObject Draft(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        var blockers = MappingValidator.Blockers(m, src, tgt);
        var attention = MappingValidator.Attention(m, options);
        var confident = new JsonArray();
        var detail = new JsonArray();
        foreach (var t in tgt.Tables)
        {
            var map = MappingValidator.FindTableMap(m, t.Key);
            if (map is not null && IsConfident(t, map, blockers, options))
                confident.Add(new JsonObject
                {
                    ["target"] = t.Key,
                    ["source"] = map.Kind == "skip" ? "(skip)" : string.Join(", ", map.Sources),
                    ["confidence"] = R(map.Confidence),
                    ["columns"] = map.Columns.Values.Count(c => !string.IsNullOrWhiteSpace(c.Expr))
                });
            else
                detail.Add(TableDetail(t, map, src));
        }
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["options"] = new JsonObject { ["autoAccept"] = options.AutoAccept, ["candidate"] = options.Candidate },
            ["blockers"] = Strings(blockers),
            ["attention"] = Strings(attention),
            ["confident"] = confident,
            ["detail"] = detail,
            ["uncovered"] = Uncovered(m, src),
            ["drops"] = Drops(m),
            ["hint"] = Hint
        };
    }

    public static JsonObject Rework(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options,
        IReadOnlyList<FeedbackRow> feedback)
    {
        var contexts = new JsonArray();
        foreach (var f in feedback)
        {
            var item = new JsonObject { ["id"] = f.Id, ["anchor"] = f.Anchor };
            var context = Resolve(f.Anchor, m, src, tgt, options);
            if (context is not null) item["context"] = context;
            contexts.Add(item);
        }
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["options"] = new JsonObject { ["autoAccept"] = options.AutoAccept, ["candidate"] = options.Candidate },
            ["summary"] = Summary(m, src, tgt, options),
            ["blockers"] = Strings(MappingValidator.Blockers(m, src, tgt)),
            ["attention"] = Strings(MappingValidator.Attention(m, options)),
            ["contexts"] = contexts,
            ["hint"] = Hint
        };
    }

    /// <summary>Context for one feedback anchor: tablemap:/table:tgt: → table detail; colmap:/column:tgt: → column detail;
    /// column:src: → source column profile and usage; table:src: → source table and usage; anything else → overall uncovered list.</summary>
    public static JsonNode? Resolve(string? anchor, MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt, MatchOptions options)
    {
        if (anchor is null or "general" or "narrative")
            return new JsonObject { ["uncovered"] = Uncovered(m, src), ["drops"] = Drops(m) };
        if (TryStrip(anchor, "tablemap:", out var tkey) || TryStrip(anchor, "table:tgt:", out tkey))
        {
            var t = tgt.FindTable(tkey);
            return t is null ? Unknown(anchor) : TableDetail(t, MappingValidator.FindTableMap(m, t.Key), src);
        }
        if (TryStrip(anchor, "colmap:", out var ckey) || TryStrip(anchor, "column:tgt:", out ckey))
        {
            var hit = MappingValidator.ResolveSourceColumn(tgt, ckey);
            return hit is null ? Unknown(anchor) : ColumnDetail(hit.Value.Table, hit.Value.Column, m, src);
        }
        if (TryStrip(anchor, "column:src:", out var skey))
        {
            var hit = MappingValidator.ResolveSourceColumn(src, skey);
            if (hit is null) return Unknown(anchor);
            var (table, column) = hit.Value;
            var key = $"{table.Key}.{column.Name}";
            var obj = new JsonObject { ["table"] = table.Key, ["column"] = SourceColumn(table, column), ["usedBy"] = UsedBy(m, key) };
            if ((DropOf(m, key) ?? DropOf(m, table.Key)) is { } drop) obj["drop"] = drop.Reason;
            return obj;
        }
        if (TryStrip(anchor, "table:src:", out var stkey))
        {
            var table = src.FindTable(stkey);
            if (table is null) return Unknown(anchor);
            var obj = SourceTable(table);
            obj["usedBy"] = Strings(m.Tables.Where(kv => kv.Value.Sources.Contains(table.Key, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key));
            if (DropOf(m, table.Key) is { } drop) obj["drop"] = drop.Reason;
            return obj;
        }
        return null;
    }

    public static JsonObject TableDetail(TableInfo t, TableMap? map, CatalogSnapshot src)
    {
        var obj = new JsonObject { ["target"] = t.Key };
        if (map is null) obj["map"] = null;
        else
        {
            obj["kind"] = map.Kind;
            obj["sources"] = Strings(map.Sources);
            if (map.From is not null) obj["from"] = map.From;
            if (map.Filter is not null) obj["filter"] = map.Filter;
            obj["confidence"] = R(map.Confidence);
            obj["method"] = EnumText.ToText(map.Method);
            if (map.Rationale is not null) obj["rationale"] = map.Rationale;
            if (map.Candidates is { Count: > 0 }) obj["tableCandidates"] = Candidates(map.Candidates);
        }
        obj["targetColumns"] = new JsonArray(t.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)TargetColumn(t, c)).ToArray());
        var columns = new JsonObject();
        if (map is not null)
            foreach (var c in t.Columns.OrderBy(c => c.Ordinal))
                if (MappingValidator.FindColumnMap(map, c.Name) is { } cm) columns[c.Name] = ColumnMapNode(cm);
        obj["columns"] = columns;
        var sourceKeys = new List<string>();
        if (map is not null)
        {
            sourceKeys.AddRange(map.Sources);
            sourceKeys.AddRange((map.Candidates ?? []).Select(c => c.Source));
        }
        obj["sourceTables"] = new JsonArray(sourceKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(src.FindTable).OfType<TableInfo>()
            .Take(MaxSourceTables)
            .Select(s => (JsonNode)SourceTable(s)).ToArray());
        return obj;
    }

    public static JsonObject ColumnDetail(TableInfo t, ColumnInfo c, MappingPayload m, CatalogSnapshot src)
    {
        var map = MappingValidator.FindTableMap(m, t.Key);
        var cm = map is null ? null : MappingValidator.FindColumnMap(map, c.Name);
        var obj = new JsonObject { ["target"] = t.Key, ["column"] = TargetColumn(t, c) };
        if (map is not null)
        {
            obj["sources"] = Strings(map.Sources);
            if (map.From is not null) obj["from"] = map.From;
        }
        if (cm is not null) obj["map"] = ColumnMapNode(cm);
        var keys = (cm?.SourceColumns ?? []).Concat((cm?.Candidates ?? []).Select(x => x.Source)).Distinct(StringComparer.OrdinalIgnoreCase);
        var profiles = new JsonArray();
        foreach (var key in keys)
            if (MappingValidator.ResolveSourceColumn(src, key) is { } hit)
            {
                var node = SourceColumn(hit.Table, hit.Column);
                node["key"] = $"{hit.Table.Key}.{hit.Column.Name}";
                profiles.Add(node);
            }
        obj["sourceColumns"] = profiles;
        return obj;
    }

    public static JsonObject TargetColumn(TableInfo t, ColumnInfo c)
    {
        var obj = new JsonObject { ["name"] = c.Name, ["type"] = c.TypeDisplay, ["nullable"] = c.IsNullable };
        if (c.IsIdentity) obj["identity"] = true;
        if (c.IsComputed) obj["computed"] = true;
        if (c.IsRowVersion) obj["rowversion"] = true;
        if (c.DefaultDefinition is not null) obj["default"] = c.DefaultDefinition;
        if (t.PrimaryKey?.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true) obj["pk"] = true;
        if (FkRef(t, c) is { } fk) obj["fk"] = fk;
        return obj;
    }

    public static JsonObject SourceTable(TableInfo s) => new()
    {
        ["key"] = s.Key,
        ["rows"] = s.Rows,
        ["columns"] = new JsonArray(s.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)SourceColumn(s, c)).ToArray())
    };

    public static JsonObject SourceColumn(TableInfo s, ColumnInfo c)
    {
        var obj = new JsonObject { ["name"] = c.Name, ["type"] = c.TypeDisplay, ["nullable"] = c.IsNullable };
        if (s.PrimaryKey?.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true) obj["pk"] = true;
        if (FkRef(s, c) is { } fk) obj["fk"] = fk;
        if (c.Profile is { SampledRows: > 0 } p)
        {
            obj["nullRatio"] = R(p.NullRatio);
            if (p.DistinctRatio is double d) obj["distinctRatio"] = R(d);
            if (p.SemanticClass is not null) obj["class"] = p.SemanticClass;
            if (p.MaxLen is int maxLen) obj["maxLen"] = maxLen;
            if (p.Samples.Count > 0) obj["samples"] = Strings(p.Samples.Take(MaxSamples));
        }
        return obj;
    }

    private static JsonObject ColumnMapNode(ColumnMap cm)
    {
        var obj = new JsonObject { ["expr"] = cm.Expr };
        if (cm.SourceColumns.Count > 0) obj["sourceColumns"] = Strings(cm.SourceColumns);
        if (cm.Default is not null) obj["default"] = cm.Default;
        obj["confidence"] = R(cm.Confidence);
        obj["method"] = EnumText.ToText(cm.Method);
        if (cm.TypeRisk is not null) obj["typeRisk"] = cm.TypeRisk;
        if (cm.Rationale is not null) obj["rationale"] = cm.Rationale;
        if (cm.Candidates is { Count: > 0 }) obj["candidates"] = Candidates(cm.Candidates);
        return obj;
    }

    private static bool IsConfident(TableInfo t, TableMap map, List<string> blockers, MatchOptions options)
    {
        if (blockers.Any(b => Mentions(b, t.Key))) return false;
        if (map.Kind == "skip") return true;
        if (map.Sources.Count == 0 || MappingValidator.NeedsReview(map.Method, map.Confidence, options)) return false;
        return !map.Columns.Values.Any(c => MappingValidator.NeedsReview(c.Method, c.Confidence, options));
    }

    private static bool Mentions(string item, string tableKey) =>
        item.StartsWith(tableKey + ":", StringComparison.OrdinalIgnoreCase) || item.StartsWith(tableKey + ".", StringComparison.OrdinalIgnoreCase);

    private static JsonArray Uncovered(MappingPayload m, CatalogSnapshot src) =>
        Strings(MappingValidator.UncoveredSourceColumns(m, src).Select(key =>
            MappingValidator.ResolveSourceColumn(src, key) is { } hit ? $"{key} {hit.Column.TypeDisplay}" : key));

    private static JsonObject Drops(MappingPayload m)
    {
        var obj = new JsonObject();
        foreach (var (key, drop) in m.Drops) obj[key] = drop.Reason;
        return obj;
    }

    private static JsonArray UsedBy(MappingPayload m, string sourceColumnKey) =>
        Strings(m.Tables.SelectMany(t => t.Value.Columns
            .Where(c => c.Value.SourceColumns.Contains(sourceColumnKey, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"{t.Key}.{c.Key}")));

    private static DropDecision? DropOf(MappingPayload m, string key) =>
        m.Drops.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? FkRef(TableInfo t, ColumnInfo c)
    {
        foreach (var f in t.ForeignKeys)
        {
            var i = f.Columns.FindIndex(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return $"{f.RefKey}.{(i < f.RefColumns.Count ? f.RefColumns[i] : "?")}";
        }
        return null;
    }

    private static JsonArray Candidates(IEnumerable<Candidate> candidates) =>
        new(candidates.Select(c => (JsonNode)new JsonObject { ["source"] = c.Source, ["score"] = R(c.Score), ["why"] = c.Why }).ToArray());

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject Unknown(string anchor) => new() { ["error"] = $"anchor '{anchor}' does not resolve to a catalog object" };

    private static bool TryStrip(string anchor, string prefix, out string rest)
    {
        rest = anchor.StartsWith(prefix, StringComparison.Ordinal) ? anchor[prefix.Length..] : "";
        return rest.Length > 0;
    }

    private static double R(double value) => Math.Round(value, 2);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingPacketTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 5: Write the failing module and flow tests**

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingModuleTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingModuleTests
{
    private static ModuleContext Ctx(DbmServices s, MappingPayload m, params FeedbackRow[] feedback) => new()
    {
        Services = s,
        Current = new ArtifactRow(1, PhaseName.Mapping, 3, Json.Serialize(m), "agent", null, DateTimeOffset.UtcNow),
        OpenFeedback = feedback
    };

    [Fact]
    public void Identity_and_registration()
    {
        var module = new MappingModule(null!);
        Assert.Equal((PhaseName.Mapping, "mapping-architect", "automap"), (module.Phase, module.Agent, module.JobKind));
        using var project = TempProject.Create();
        Assert.IsType<MappingModule>(project.Services.Modules[PhaseName.Mapping]);
    }

    [Fact]
    public void Needs_agent_while_attention_or_blockers_remain()
    {
        using var project = TempProject.Create();
        var module = new MappingModule(project.Services.WithSampleCatalogs());
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        Assert.True(module.NeedsAgent(Json.ToNode(draft)));
        Assert.False(module.NeedsAgent(Json.ToNode(SampleMappings.Approved())));
    }

    [Fact]
    public void Validate_rejects_errors_and_reports_blockers_and_attention_as_warnings()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approved = SampleMappings.Approved();
        var ok = module.Validate(Ctx(services, approved), Json.ToNode(approved));
        Assert.True(ok.Ok);
        Assert.Empty(ok.Warnings);

        var bad = SampleMappings.Approved();
        bad.Tables["app.Nope"] = new TableMap();
        bad.Drops.Remove("dbo.CUST.FAX_NO");
        bad.Tables["app.Customers"].Columns["Email"].Method = MapMethod.Vector;
        bad.Tables["app.Customers"].Columns["Email"].Confidence = 0.6;
        var check = module.Validate(Ctx(services, bad), Json.ToNode(bad));
        Assert.False(check.Ok);
        Assert.Contains("unknown target table 'app.Nope'", check.Errors);
        Assert.Contains("source column dbo.CUST.FAX_NO is not mapped or dropped", check.Warnings);
        Assert.Contains(check.Warnings, w => w.StartsWith("app.Customers.Email:"));

        var garbage = JsonNode.Parse("""{"tables":{"app.Customers":{"method":"robot"}}}""")!;
        var rejected = module.Validate(Ctx(services, approved), garbage);
        Assert.StartsWith("payload is not a valid mapping", Assert.Single(rejected.Errors));
    }

    [Fact]
    public void Approval_blockers_and_summary_come_from_the_validator()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approved = SampleMappings.Approved();
        Assert.Empty(module.ApprovalBlockers(Ctx(services, approved), Json.ToNode(approved)));
        Assert.Equal("6 tables, 35 columns mapped, 4 drops, 0 attention, 0 blockers", module.Summarize(Json.ToNode(approved)));

        approved.Drops.Remove("dbo.TMP_IMPORT");
        Assert.Equal(["source table dbo.TMP_IMPORT is not mapped or dropped (1 columns)"],
            module.ApprovalBlockers(Ctx(services, approved), Json.ToNode(approved)));
    }

    [Fact]
    public void Build_packet_uses_the_draft_or_rework_shape()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        var module = new MappingModule(services);
        var approved = SampleMappings.Approved();

        var draft = module.BuildPacket(Ctx(services, approved), PacketMode.Draft);
        Assert.Equal(6, draft["confident"]!.AsArray().Count);

        var feedback = new FeedbackRow(12, PhaseName.Mapping, 3, "colmap:app.Customers.Email", "Lower-case the email",
            FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);
        var rework = module.BuildPacket(Ctx(services, approved, feedback), PacketMode.Rework);
        Assert.Equal(12, (long?)rework["contexts"]![0]!["id"]);
        Assert.Equal("Email", (string?)rework["contexts"]![0]!["context"]!["column"]!["name"]);
    }
}
```

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingFlowTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingFlowTests
{
    [Fact]
    public async Task Map_auto_inline_drafts_and_hands_over_to_the_mapping_architect()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        services.Phases.SetStatus(PhaseName.Mapping, PhaseStatus.Running);

        var result = await CliRunner.RunAsync(project.Ws, null, "map", "auto", "--inline");

        Assert.Equal(0, result.Exit);
        Assert.Equal("drafting", (string?)result.Json["status"]);
        Assert.Equal("script", services.Artifacts.Latest(PhaseName.Mapping)!.Author);
        var next = services.Workflow.Next();
        Assert.Equal(("agent", "mapping-architect", "draft"), (next.Action, next.Agent, next.Mode));
        var packet = JsonNode.Parse(File.ReadAllText(next.Packet!))!;
        Assert.Contains("dbo.ORD_STATUS.STATUS_CD varchar(10)", packet["data"]!["uncovered"]!.AsArray().Select(n => (string?)n));
        Assert.Contains(packet["data"]!["detail"]!.AsArray(), d => (string?)d!["target"] == "app.Orders");
    }

    [Fact]
    public void Playbook_style_patch_resolves_the_blockers()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        services.AddMapping(draft, PhaseStatus.Drafting);
        static JsonNode J(string json) => JsonNode.Parse(json)!;
        var ops = new List<PatchOp>
        {
            new("replace", "/tables/app.Customers/columns/FirstName", J("""{"expr":"LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)","sourceColumns":["dbo.CUST.CUST_NM"],"confidence":1,"method":"agent","rationale":"First word of CUST_NM."}""")),
            new("replace", "/tables/app.Customers/columns/LastName", J("""{"expr":"LTRIM(SUBSTRING(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') + 1, 100))","sourceColumns":["dbo.CUST.CUST_NM"],"confidence":1,"method":"agent","rationale":"Remainder of CUST_NM after the first space."}""")),
            new("replace", "/tables/app.Orders/kind", J("\"merge\"")),
            new("replace", "/tables/app.Orders/sources", J("""["dbo.ORD_HDR","dbo.ORD_STATUS"]""")),
            new("add", "/tables/app.Orders/from", J("\"[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]\"")),
            new("replace", "/tables/app.Orders/method", J("\"agent\"")),
            new("replace", "/tables/app.Orders/columns/StatusCode", J("""{"expr":"st.[STATUS_CD]","sourceColumns":["dbo.ORD_STATUS.STATUS_CD","dbo.ORD_HDR.STATUS_ID"],"confidence":1,"method":"agent","rationale":"Status code from the ORD_STATUS lookup."}""")),
            new("add", "/drops/dbo.CUST.FAX_NO", J("""{"reason":"ShopV2 has no fax column.","method":"agent"}""")),
            new("add", "/drops/dbo.ORD_STATUS.STATUS_ID", J("""{"reason":"Lookup key; Orders stores the code.","method":"agent"}""")),
            new("add", "/drops/dbo.ORD_STATUS.STATUS_DESC", J("""{"reason":"Descriptions are not stored in ShopV2.","method":"agent"}""")),
        };
        var patch = new Patch("mapping", 0, ops, [], "Split CUST_NM, joined ORD_STATUS, dropped fax and status lookup columns.");

        var dry = services.Workflow.ApplyPatch(patch, dryRun: true);
        Assert.True(dry.Ok, string.Join("\n", dry.Errors));
        var applied = services.Workflow.ApplyPatch(patch);

        Assert.True(applied.Ok, string.Join("\n", applied.Errors));
        Assert.Equal(1, applied.Version);
        Assert.DoesNotContain(applied.Warnings, w => w.Contains("not mapped or dropped") || w.Contains("NOT NULL"));
        Assert.Equal(PhaseStatus.AwaitingReview, services.Phases.Get(PhaseName.Mapping).Status);
    }

    [Fact]
    public void Approve_is_blocked_by_blockers_and_allowed_for_the_approved_mapping()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(PhaseName.Mapping);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        services.AddMapping(draft, PhaseStatus.AwaitingReview);

        var ex = Assert.Throws<WorkflowException>(() => services.Workflow.Approve(PhaseName.Mapping));
        Assert.Contains(ex.Details, d => d.Contains("dbo.ORD_STATUS"));

        services.AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");
        services.Workflow.Approve(PhaseName.Mapping);
        Assert.Equal(PhaseStatus.Approved, services.Phases.Get(PhaseName.Mapping).Status);
        Assert.Equal(PhaseStatus.Running, services.Phases.Get(PhaseName.Sql).Status);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingModuleTests|FullyQualifiedName~MappingFlowTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'MappingModule' could not be found`.

- [ ] **Step 7: Implement `MappingModule` and register it**

Create `plugins/db-migrate/engine/Dbm/Core/Mapping/MappingModule.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.Mapping;

/// <summary>Phase module for MAPPING: automap job → mapping-architect agent → human review.</summary>
public sealed class MappingModule(DbmServices services) : IPhaseModule
{
    public PhaseName Phase => PhaseName.Mapping;
    public string Agent => "mapping-architect";
    public string JobKind => "automap";

    public bool NeedsAgent(JsonNode draft)
    {
        var (src, tgt) = Catalogs();
        var m = Parse(draft);
        return MappingValidator.Attention(m, Options()).Count > 0 || MappingValidator.Blockers(m, src, tgt).Count > 0;
    }

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        var (src, tgt) = Catalogs();
        var m = Json.Deserialize<MappingPayload>(ctx.Current.PayloadJson);
        return mode == PacketMode.Draft
            ? MappingPacket.Draft(m, src, tgt, Options())
            : MappingPacket.Rework(m, src, tgt, Options(), ctx.OpenFeedback);
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        MappingPayload m;
        try { m = Parse(payload); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new PayloadCheck([$"payload is not a valid mapping: {e.Message}"], []);
        }
        var (src, tgt) = Catalogs();
        var warnings = MappingValidator.Blockers(m, src, tgt);
        warnings.AddRange(MappingValidator.Attention(m, Options()));
        return new PayloadCheck(MappingValidator.Errors(m, src, tgt), warnings);
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload)
    {
        var (src, tgt) = Catalogs();
        return MappingValidator.Blockers(Parse(payload), src, tgt);
    }

    public string Summarize(JsonNode payload)
    {
        var (src, tgt) = Catalogs();
        return MappingPacket.Summary(Parse(payload), src, tgt, Options());
    }

    private MatchOptions Options()
    {
        var settings = services.Project.GetSettings();
        return new MatchOptions(settings.AutoAcceptScore, settings.CandidateScore);
    }

    private (CatalogSnapshot Src, CatalogSnapshot Tgt) Catalogs() =>
        (services.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("source catalog missing; run discovery first"),
         services.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("target catalog missing; run discovery first"));

    private static MappingPayload Parse(JsonNode node) => Json.FromNode<MappingPayload>(node);
}
```

Register the module — in `plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs`, below the `// milestone registrations below` marker, directly after the line `        yield return new Dbm.Core.Analysis.AnalysisModule();   // T2.7`, add:

```csharp
        yield return new Dbm.Core.Mapping.MappingModule(s);    // T3.4
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingModuleTests|FullyQualifiedName~MappingFlowTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 8`.

- [ ] **Step 9: Write the agent file and its test**

Create `plugins/db-migrate/agents/mapping-architect.md` with exactly this content:

````markdown
---
name: mapping-architect
description: Resolves the db-migrate MAPPING phase. Reads one mapping work packet (auto-mapper draft or reviewer feedback), decides table and column mappings, T-SQL transforms, splits, merges, lookups, defaults and drops, writes one JSON patch and verifies it with a dry run. Use when `dbm next` returns agent "mapping-architect".
tools: Bash, Read, Write
model: inherit
---

# Mapping architect

You turn the auto-mapper's draft into a mapping a DBA would sign off, or rework it from reviewer feedback. You work from **one work packet**, you write **one patch file**, and you reply with **one line**.

## Hard rules

1. **Never invent a table or column.** Every name you write must appear in the packet or in the output of `dbm show <schema.table> --side src|tgt`. Use `dbm search "<words>" --side src -k 8` to find source columns by meaning.
2. **Never print, read or ask for connection strings. Never open `.dbmigrate/state.db`.** The only `dbm` commands you run are `dbm show`, `dbm search`, `dbm artifact mapping --path <pointer>` and `dbm apply <patchPath> --dry-run`. Do not run `dbm apply` without `--dry-run` — the orchestrator applies your patch.
3. **Draft mode:** resolve every item in `data.blockers` and `data.attention`. **Rework mode:** answer every feedback id in the envelope's `feedback` list with a response (`addressed` or `declined` with a reason).
4. **Keep identity keys.** Map identity key columns to the source key (`CustomerId ← s.[CUST_ID]`) unless feedback says otherwise; child tables' foreign keys depend on those values.
5. **Every column map you write** sets `"method": "agent"`, a `"confidence"` (1 when you are sure), a one-sentence `"rationale"`, and `"sourceColumns"` listing **every** source column the expression reads — including join keys used in `from` and columns read inside subqueries. `sourceColumns` drives coverage: a source column that is neither listed anywhere nor dropped blocks approval.
6. Computed and rowversion target columns are never written. A nullable target column may stay empty only as an explicit decision: `"expr": null`, `"method": "agent"`, and a rationale.
7. A drop needs a reason a reviewer can check ("ShopV2 has no fax column", not "not needed").

## The packet

Read the packet file (absolute path given by the orchestrator) with the Read tool. Envelope: `phase`, `mode` (`draft` | `rework`), `baseVersion`, `patchPath`, `feedback` (`id`, `anchor`, `text`), `rules`, `data`.

`data` in **draft** mode:
- `legend`, `options` (`autoAccept`, `candidate`), `hint`
- `blockers` — why approval is impossible now; `attention` — script proposals below the auto-accept band
- `confident` — tables that need nothing (`target`, `source`, `confidence`, `columns`)
- `detail` — tables that need work: `kind`, `sources`, `from`, `filter`, `confidence`, `method`, `tableCandidates`, `targetColumns` (name, type, nullable, identity, computed, rowversion, default, pk, fk → referenced column), `columns` (the current map per target column: expr, sourceColumns, default, confidence, method, typeRisk, candidates with score and why), `sourceTables` (up to 3 source tables with column types, null/distinct ratios, semantic class, max length, up to 3 sample values)
- `uncovered` — source columns neither mapped nor dropped (`schema.table.column type`); `drops` — current drop decisions

`data` in **rework** mode: `summary`, `blockers`, `attention`, `contexts` (one per feedback id: the slice its anchor points at — a table detail for `tablemap:`, a column detail with candidate profiles for `colmap:`, a source column profile plus `usedBy` for `column:src:`), `hint`.

## Mapping model (JSON, camelCase)

```json
{"tables": {"<target schema.table>": {
    "kind": "direct | merge | lookup | skip",
    "sources": ["<primary source, alias s>", "<other sources>"],
    "from": "optional FROM clause without FROM; must alias sources[0] as s",
    "filter": "optional WHERE predicate without WHERE",
    "confidence": 1, "method": "agent", "rationale": "one sentence",
    "columns": {"<TargetColumn>": {"expr": "T-SQL", "sourceColumns": ["schema.table.column"], "default": "T-SQL when expr is null",
                                   "confidence": 1, "method": "agent", "rationale": "one sentence", "typeRisk": "risk text from the auto-mapper"}}}},
 "drops": {"<schema.table>|<schema.table.column>": {"reason": "why it is not migrated", "method": "agent"}},
 "notes": []}
```

- **direct** — one source table. **merge** — several sources joined in `from`. **lookup** — the primary source plus lookup tables, also expressed with `from`. **skip** — the target table is not loaded (needs a rationale). A **split** is several target tables whose `sources[0]` is the same source table.
- Expressions are T-SQL scalar expressions over the FROM aliases; `s` is always `sources[0]`. Quote identifiers with brackets (`s.[CUST_NM]`). Target keys (table and column names) use the exact catalog case.

## The patch

```json
{"phase": "mapping", "baseVersion": 0, "ops": [], "responses": [], "summary": "one line"}
```

- `baseVersion` = the packet's `baseVersion`.
- `ops` = JSON Patch subset (`add`, `replace`, `remove`) with JSON pointers into the mapping: `/tables/<target>/<field>`, `/tables/<target>/columns/<Column>`, `/tables/<target>/columns/<Column>/<field>`, `/drops/<key>`, `/notes`. Escape `~` as `~0` and `/` as `~1` inside a key. Use **`add`** for keys that do not exist yet (for example `from`, `rationale`, a new drop, a column missing from the map) and **`replace`** for keys that exist. When you change more than one field of a column, replace the whole column object.
- `responses` (rework mode): one per feedback id — `{"feedbackId": 12, "status": "addressed" | "declined", "note": "what you did / why not"}`.
- Use `dbm artifact mapping --path /tables/app.Orders` to read the current value of any pointer when you are unsure whether a key exists.

## Procedure

**Draft mode**
1. Read the packet. Skip `confident` tables.
2. For each `detail` table: check `sources[0]` against `tableCandidates` and the column overlap; fix `sources`, `kind`, `from` when the pairing is wrong or a lookup is needed.
3. For each target column in that table: accept the proposal (rewrite it with `method: agent`), change the expression, add a default, or leave it null with a rationale. Read `typeRisk`: add `CAST`/`CONVERT`/`CASE`/`LEFT` when the risk is real.
4. For each `uncovered` source column: map it into the target column that should consume it (and list it in `sourceColumns`), or drop it with a reason. A source table that feeds nothing is dropped with its table key.
5. Verify unfamiliar names with `dbm show` / `dbm search`.
6. Write the patch to `patchPath` with the Write tool.
7. Run `dbm apply <patchPath> --dry-run`. It prints one JSON object with `ok`, `errors` and `warnings`. Fix every error and repeat until `"ok":true`. Warnings list blockers and attention items still open — resolve them too; if one truly cannot be resolved, say why in `summary`.
8. Reply with exactly one line: `<patchPath> ops=<n> responses=<m>`.

**Rework mode** — same loop, driven by `feedback` and `data.contexts`; every feedback id gets a response; keep changes limited to what the feedback asks for unless a blocker forces more.

## Worked examples (LegacyShop → ShopV2 sample)

**Split `CUST_NM` ("First Last") into `FirstName` / `LastName`** — both columns read the same source column:

```json
{"op": "replace", "path": "/tables/app.Customers/columns/FirstName",
 "value": {"expr": "LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", "sourceColumns": ["dbo.CUST.CUST_NM"],
           "confidence": 1, "method": "agent", "rationale": "CUST_NM holds 'First Last'; the first word is the first name."}}
```

**`ACTIVE_FLG char(1)` → `IsActive bit`** — a type risk "needs a CASE transform" means exactly this:

```json
{"op": "replace", "path": "/tables/app.Products/columns/IsActive",
 "value": {"expr": "CASE WHEN s.[ACTIVE_FLG] = 'Y' THEN 1 ELSE 0 END", "sourceColumns": ["dbo.PROD.ACTIVE_FLG"],
           "confidence": 1, "method": "agent", "rationale": "Y/N flag becomes a bit; anything other than Y is inactive."}}
```

**`StatusCode` from the `ORD_STATUS` lookup (merge)** — change the table to a merge with a `from` clause, then read the code through the join alias; list the join key of the primary source in `sourceColumns`:

```json
{"op": "replace", "path": "/tables/app.Orders/kind", "value": "merge"},
{"op": "replace", "path": "/tables/app.Orders/sources", "value": ["dbo.ORD_HDR", "dbo.ORD_STATUS"]},
{"op": "add", "path": "/tables/app.Orders/from", "value": "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]"},
{"op": "replace", "path": "/tables/app.Orders/columns/StatusCode",
 "value": {"expr": "st.[STATUS_CD]", "sourceColumns": ["dbo.ORD_STATUS.STATUS_CD", "dbo.ORD_HDR.STATUS_ID"],
           "confidence": 1, "method": "agent", "rationale": "ShopV2 stores the status code (NEW, PAID, ...) instead of the numeric id."}}
```

**`PrimaryAddressId` from a correlated subquery** — the subquery's columns are source columns too:

```json
{"op": "replace", "path": "/tables/app.Customers/columns/PrimaryAddressId",
 "value": {"expr": "(SELECT MIN(a.[ADDR_ID]) FROM [dbo].[ADDR] AS a WHERE a.[CUST_ID] = s.[CUST_ID])",
           "sourceColumns": ["dbo.ADDR.ADDR_ID", "dbo.ADDR.CUST_ID", "dbo.CUST.CUST_ID"],
           "confidence": 0.9, "method": "agent", "rationale": "LegacyShop has no primary-address flag; the customer's first address is used."}}
```

**Drops** — `{"op": "add", "path": "/drops/dbo.CUST.FAX_NO", "value": {"reason": "ShopV2 has no fax column.", "method": "agent"}}`.

**Rework** — feedback `{"id": 12, "anchor": "colmap:app.Customers.Email", "text": "Lower-case and trim the email"}` and `{"id": 13, "anchor": "column:src:dbo.CUST.FAX_NO", "text": "Keep the fax numbers"}`:

```json
{"phase": "mapping", "baseVersion": 3,
 "ops": [{"op": "replace", "path": "/tables/app.Customers/columns/Email",
          "value": {"expr": "LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))", "sourceColumns": ["dbo.CUST.EMAIL_ADDR"],
                    "confidence": 1, "method": "agent", "rationale": "Normalised as requested: trimmed and lower-cased."}}],
 "responses": [{"feedbackId": 12, "status": "addressed", "note": "Email is now trimmed and lower-cased."},
               {"feedbackId": 13, "status": "declined", "note": "ShopV2 has no fax column; keeping fax numbers needs a target schema change, which is out of scope."}],
 "summary": "Normalised Email; kept FAX_NO dropped."}
```

**Complete draft-mode patch for the sample pair** (resolves the auto-mapper's blockers — the `FirstName` split, the uncovered `FAX_NO` and `ORD_STATUS` columns — and confirms the proposals it flagged for review):

<!-- example-patch -->
```json
{"phase": "mapping", "baseVersion": 0,
 "ops": [
  {"op": "replace", "path": "/tables/app.Customers/columns/FirstName", "value": {"expr": "LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", "sourceColumns": ["dbo.CUST.CUST_NM"], "confidence": 1, "method": "agent", "rationale": "CUST_NM holds 'First Last'; the first word is the first name."}},
  {"op": "replace", "path": "/tables/app.Customers/columns/LastName", "value": {"expr": "LTRIM(SUBSTRING(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') + 1, 100))", "sourceColumns": ["dbo.CUST.CUST_NM"], "confidence": 1, "method": "agent", "rationale": "Everything after the first space of CUST_NM."}},
  {"op": "replace", "path": "/tables/app.Customers/columns/Email", "value": {"expr": "s.[EMAIL_ADDR]", "sourceColumns": ["dbo.CUST.EMAIL_ADDR"], "confidence": 1, "method": "agent", "rationale": "Same data; the profile class is email."}},
  {"op": "replace", "path": "/tables/app.Customers/columns/Phone", "value": {"expr": "s.[PHONE_NO]", "sourceColumns": ["dbo.CUST.PHONE_NO"], "confidence": 1, "method": "agent", "rationale": "Same phone number; lengths match."}},
  {"op": "replace", "path": "/tables/app.Customers/columns/CreatedAt", "value": {"expr": "s.[CRT_DT]", "sourceColumns": ["dbo.CUST.CRT_DT"], "confidence": 1, "method": "agent", "rationale": "Creation timestamp; datetime2(0) drops the milliseconds, which is acceptable."}},
  {"op": "replace", "path": "/tables/app.Customers/columns/PrimaryAddressId", "value": {"expr": "(SELECT MIN(a.[ADDR_ID]) FROM [dbo].[ADDR] AS a WHERE a.[CUST_ID] = s.[CUST_ID])", "sourceColumns": ["dbo.ADDR.ADDR_ID", "dbo.ADDR.CUST_ID", "dbo.CUST.CUST_ID"], "confidence": 0.9, "method": "agent", "rationale": "LegacyShop has no primary-address flag; the customer's first address is used."}},
  {"op": "replace", "path": "/tables/app.Products/columns/IsActive", "value": {"expr": "CASE WHEN s.[ACTIVE_FLG] = 'Y' THEN 1 ELSE 0 END", "sourceColumns": ["dbo.PROD.ACTIVE_FLG"], "confidence": 1, "method": "agent", "rationale": "Y/N flag becomes a bit; anything other than Y is inactive."}},
  {"op": "replace", "path": "/tables/app.Orders/kind", "value": "merge"},
  {"op": "replace", "path": "/tables/app.Orders/sources", "value": ["dbo.ORD_HDR", "dbo.ORD_STATUS"]},
  {"op": "add", "path": "/tables/app.Orders/from", "value": "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]"},
  {"op": "replace", "path": "/tables/app.Orders/confidence", "value": 1},
  {"op": "replace", "path": "/tables/app.Orders/method", "value": "agent"},
  {"op": "add", "path": "/tables/app.Orders/rationale", "value": "Order header plus the status lookup, which supplies the status code."},
  {"op": "replace", "path": "/tables/app.Orders/columns/StatusCode", "value": {"expr": "st.[STATUS_CD]", "sourceColumns": ["dbo.ORD_STATUS.STATUS_CD", "dbo.ORD_HDR.STATUS_ID"], "confidence": 1, "method": "agent", "rationale": "ShopV2 stores the status code (NEW, PAID, ...) instead of the numeric id."}},
  {"op": "replace", "path": "/tables/app.Orders/columns/ShippingAddressId", "value": {"expr": "s.[SHIP_ADDR_ID]", "sourceColumns": ["dbo.ORD_HDR.SHIP_ADDR_ID"], "confidence": 1, "method": "agent", "rationale": "Addresses keep their ids, so the shipping address id carries over."}},
  {"op": "replace", "path": "/tables/app.AuditEvents/confidence", "value": 1},
  {"op": "replace", "path": "/tables/app.AuditEvents/method", "value": "agent"},
  {"op": "add", "path": "/tables/app.AuditEvents/rationale", "value": "AUDIT_LOG is the only audit table in LegacyShop."},
  {"op": "replace", "path": "/tables/app.AuditEvents/columns/UserName", "value": {"expr": "s.[USR]", "sourceColumns": ["dbo.AUDIT_LOG.USR"], "confidence": 1, "method": "agent", "rationale": "User who performed the action."}},
  {"op": "replace", "path": "/tables/app.AuditEvents/columns/Action", "value": {"expr": "s.[ACTION_TXT]", "sourceColumns": ["dbo.AUDIT_LOG.ACTION_TXT"], "confidence": 1, "method": "agent", "rationale": "Text of the audited action."}},
  {"op": "add", "path": "/drops/dbo.CUST.FAX_NO", "value": {"reason": "ShopV2 has no fax column.", "method": "agent"}},
  {"op": "add", "path": "/drops/dbo.ORD_STATUS.STATUS_ID", "value": {"reason": "Lookup key; only used in the Orders join.", "method": "agent"}},
  {"op": "add", "path": "/drops/dbo.ORD_STATUS.STATUS_DESC", "value": {"reason": "ShopV2 does not store status descriptions.", "method": "agent"}},
  {"op": "replace", "path": "/drops/dbo.TMP_IMPORT", "value": {"reason": "Empty import staging table.", "method": "agent"}}
 ],
 "responses": [],
 "summary": "Split CUST_NM, joined ORD_STATUS for StatusCode, CASE for IsActive, first address as PrimaryAddressId, dropped fax and status lookup columns."}
```

Then: `dbm apply <patchPath> --dry-run` → output with `"ok":true` → reply `<patchPath> ops=24 responses=0`.
````

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingArchitectAgentTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Patching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingArchitectAgentTests
{
    private static string AgentText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plugins", "db-migrate", "agents", "mapping-architect.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "plugins", "db-migrate", "agents", "mapping-architect.md")).Replace("\r\n", "\n");
    }

    [Fact]
    public void Agent_file_has_frontmatter_and_the_playbook_rules()
    {
        var text = AgentText();
        Assert.StartsWith("---\nname: mapping-architect\n", text);
        Assert.Contains("\ntools: Bash, Read, Write\n", text);
        Assert.Contains("\nmodel: inherit\n", text);
        foreach (var phrase in new[] { "dbm apply <patchPath> --dry-run", "patchPath", "sourceColumns", "\"method\": \"agent\"", "dbm show", "dbm search", "state.db", "feedbackId" })
            Assert.Contains(phrase, text);
    }

    [Fact]
    public void Complete_example_patch_applies_to_the_sample_draft_and_clears_the_blockers()
    {
        var text = AgentText();
        var marker = text.IndexOf("<!-- example-patch -->", StringComparison.Ordinal);
        var start = text.IndexOf("```json", marker, StringComparison.Ordinal) + "```json".Length;
        var patch = Patch.Parse(text[start..text.IndexOf("```", start, StringComparison.Ordinal)]);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());

        var result = Json.FromNode<MappingPayload>(JsonPatch.Apply(Json.ToNode(draft), patch.Ops));

        Assert.Equal(("mapping", 0, 24), (patch.Phase, patch.BaseVersion, patch.Ops.Count));
        Assert.Empty(MappingValidator.Errors(result, SampleCatalogs.Source(), SampleCatalogs.Target()));
        Assert.Empty(MappingValidator.Blockers(result, SampleCatalogs.Source(), SampleCatalogs.Target()));
    }
}
```

- [ ] **Step 10: Run the agent tests and the whole unit suite**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingArchitectAgentTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 2`.

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`
Expected: PASS, 0 failures.

- [ ] **Step 11: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Mapping/MappingPacket.cs plugins/db-migrate/engine/Dbm/Core/Mapping/MappingModule.cs plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs plugins/db-migrate/agents/mapping-architect.md plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingPacketTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingModuleTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingFlowTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Mapping/MappingArchitectAgentTests.cs
git commit -F - <<'EOF'
feat(mapping): mapping module, work packets and mapping-architect agent (T3.4)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 3.5: Mapping UI + context endpoint + human edits

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Web/Endpoints/MappingEndpoints.cs`
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/views/mapping.js`
- Modify: `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs` (one line), `plugins/db-migrate/engine/Dbm/wwwroot/index.html` (one script tag), `plugins/db-migrate/engine/Dbm/wwwroot/css/app.css` (append one block)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Web/MappingEndpointsTests.cs`, `plugins/db-migrate/engine/Dbm.Tests/js/mapping-ops.test.cjs`

**Interfaces:**
- Consumes: `WebState.Services`, `EndpointRegistry.MapAll`, the token guard and `POST /api/edit/{phase}` → `WorkflowEngine.HumanEdit` → `ApplyResult {ok, version, errors, warnings}` (C7, T1.7); `WebTestServer.StartAsync(ws, factory)` / `Anonymous()` / `GetJsonAsync(path)` (M1 test helper), `TempProject` (T2.1); C9 UI conventions: `DBM.h`, `DBM.fmt.num`, `DBM.components.{reviewBar, kpi, emptyState, modal}`, view contract `DBM.views[phase] = {title, render(root, ctx), onEvent?(evt, ctx)}` with `ctx = {state, artifact, version, readOnly, api, refresh(), toast(msg, kind), commentable(el, anchor, label)}`, class vocabulary, CSS tokens; `window.DBM_EXPORT` (T2.8); T3.2–T3.4 types.
- Produces:
  ```csharp
  namespace Dbm.Web.Endpoints;
  public static class MappingEndpoints
  {
      public static void Map(IEndpointRouteBuilder app, WebState state);        // GET /api/mapping/context[?version=n]
      public static JsonObject BuildContext(DbmServices services, int? version = null);
  }
  ```
  `GET /api/mapping/context` → `{version, autoAccept, candidate, source:[{key, rows, columns:[{name,type,nullable}]}], target:[{key, columns:[{name,type,nullable,identity,computed,rowversion,hasDefault}]}], blockers:[…], attention:[…], uncovered:[…]}` for the requested version (default: the phase's current version; validation lists are empty when there is no mapping artifact or no catalog).
  ```js
  // views/mapping.js (classic script, runs under node for tests)
  DBM.views.mapping = { title: 'Mapping', render, onEvent };
  DBM.mappingOps = { diff(before, after), changeCount(ops), pointer(...segments), parseSourceColumns(text), splitColumnKey(key), quoteName(name),
                     needsReview(item, autoAccept), tableStatus(key, map, blockers, autoAccept), columnStatus(tableKey, column, cm, blockers, autoAccept),
                     attentionCount(payload, autoAccept), uncovered(context, payload), blockers(context, payload),
                     editColumn(payload, tableKey, column, fields), restoreIfUnchanged(before, after, tableKey, column), editTable(payload, tableKey, fields),
                     setSource(payload, tableKey, sourceTable), skipTable(payload, tableKey, reason), dropSource(payload, key, reason),
                     undropSource(payload, key), mapToTarget(payload, sourceKey, tableKey, column), useCandidate(payload, tableKey, column, source),
                     mapTargetsFor(context, payload, sourceKey) };
  ```

**Screen (C9 vocabulary, view-scoped classes `map-*`):** `reviewBar(ctx)` → page header (version · author · summary) → KPI tiles (tables mapped, columns mapped, needs review, blockers, drops) → blockers panel (each item jumps to its table or to the unmapped panel) → **Tables** card: filter chips (All / Needs review / Blockers) and a grid `target | source(s) | kind | confidence bar | method tag | status` whose rows expand (toggle button with `aria-expanded`, or click on the row) into table fields (kind, sources, FROM, WHERE) and a column table `target column + type + flags | expression (monospace textarea) | source columns | default | confidence | method | type risk | candidates popover` → **Unmapped source columns** card grouped by source table with `Map to…` (modal with the eligible target columns) and `Drop…` (modal, reason required; whole-table drop when every column is unmapped) → **Drops** card with `Undo` → sticky save bar (`N unsaved changes`, Discard, Save as new version). Blockers, statuses and the unmapped list are recomputed client-side from the working copy (same rules and messages as `MappingValidator`), so they react to edits before saving. Commentable anchors: `tablemap:<target>` (target cell), `colmap:<target>.<column>` (column-name cell), `column:src:<key>` (unmapped source column and column drops), `table:src:<key>` (table drops). Editing is enabled only when `!ctx.readOnly`, the Mapping phase is `awaiting_review` and the viewed version is the current one; a human edit sets the column/table `method` to `human` and `confidence` to 1; Save posts `{phase:"mapping", baseVersion, ops, responses:[], summary}` to `POST /api/edit/mapping` and calls `ctx.refresh()` on success. In an export (`window.DBM_EXPORT`) the view renders read-only from the payload alone.

- [ ] **Step 1: Write the failing endpoint tests**

Create `plugins/db-migrate/engine/Dbm.Tests/Unit/Web/MappingEndpointsTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public class MappingEndpointsTests
{
    private static IEnumerable<string?> Texts(JsonNode? array) => array!.AsArray().Select(n => (string?)n);

    [Fact]
    public void Context_lists_catalog_columns_and_validation_of_the_current_version()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.AddMapping(AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions()),
            PhaseStatus.AwaitingReview);

        var ctx = MappingEndpoints.BuildContext(services);

        Assert.Equal(0, (int?)ctx["version"]);
        Assert.Equal(0.85, (double?)ctx["autoAccept"]);
        var customers = ctx["target"]!.AsArray().Single(t => (string?)t!["key"] == "app.Customers")!["columns"]!.AsArray();
        Assert.True((bool?)customers.Single(c => (string?)c!["name"] == "DisplayName")!["computed"]);
        Assert.True((bool?)customers.Single(c => (string?)c!["name"] == "CustomerId")!["identity"]);
        Assert.True((bool?)customers.Single(c => (string?)c!["name"] == "CreatedAt")!["hasDefault"]);
        var cust = ctx["source"]!.AsArray().Single(t => (string?)t!["key"] == "dbo.CUST")!;
        Assert.Equal(8, cust["columns"]!.AsArray().Count);
        Assert.Equal("varchar(100)", (string?)cust["columns"]![1]!["type"]);
        Assert.Contains("source table dbo.ORD_STATUS is not mapped or dropped (3 columns)", Texts(ctx["blockers"]));
        Assert.NotEmpty(ctx["attention"]!.AsArray());
        Assert.Contains("dbo.CUST.FAX_NO", Texts(ctx["uncovered"]));
    }

    [Fact]
    public void Context_follows_the_requested_version()
    {
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.AddMapping(AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions()),
            PhaseStatus.AwaitingReview);
        services.AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");

        Assert.Empty(MappingEndpoints.BuildContext(services)["blockers"]!.AsArray());
        Assert.NotEmpty(MappingEndpoints.BuildContext(services, 0)["blockers"]!.AsArray());
        Assert.Equal(1, (int?)MappingEndpoints.BuildContext(services)["version"]);
    }

    [Fact]
    public void Context_without_a_mapping_has_catalogs_but_no_validation()
    {
        using var project = TempProject.Create();
        var ctx = MappingEndpoints.BuildContext(project.Services.WithSampleCatalogs());
        Assert.Null(ctx["version"]);
        Assert.Equal(6, ctx["target"]!.AsArray().Count);
        Assert.Empty(ctx["blockers"]!.AsArray());
    }

    [Fact]
    public async Task Http_endpoint_requires_the_token_and_serves_the_context()
    {
        using var project = TempProject.Create();
        project.Services.WithSampleCatalogs().AddMapping(SampleMappings.Approved(), PhaseStatus.AwaitingReview, "human");
        await using var server = await WebTestServer.StartAsync(project.Ws, ws => DbmServices.Open(ws));

        using var anonymous = server.Anonymous();
        var denied = await anonymous.GetAsync("/api/mapping/context");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var json = await server.GetJsonAsync("/api/mapping/context?version=0");
        Assert.Equal(0, (int?)json["version"]);
        Assert.Empty(json["blockers"]!.AsArray());
        Assert.Equal(6, json["target"]!.AsArray().Count);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingEndpointsTests"`
Expected: build FAILS with `error CS0103: The name 'MappingEndpoints' does not exist in the current context`.

- [ ] **Step 3: Implement the endpoint and register it**

Create `plugins/db-migrate/engine/Dbm/Web/Endpoints/MappingEndpoints.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;

namespace Dbm.Web.Endpoints;

/// <summary>GET /api/mapping/context[?version=n] — catalog column lists plus validator results for the mapping screen.
/// Human edits go through the core POST /api/edit/mapping endpoint.</summary>
public static class MappingEndpoints
{
    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        app.MapGet("/api/mapping/context", (int? version) =>
            Results.Content(BuildContext(state.Services, version).ToJsonString(), "application/json"));
    }

    public static JsonObject BuildContext(DbmServices services, int? version = null)
    {
        var src = services.Catalog.Get(Side.Src);
        var tgt = services.Catalog.Get(Side.Tgt);
        var settings = services.Project.GetSettings();
        var options = new MatchOptions(settings.AutoAcceptScore, settings.CandidateScore);
        var v = version ?? services.Phases.Get(PhaseName.Mapping).CurrentVersion;
        var artifact = v is int n ? services.Artifacts.Get(PhaseName.Mapping, n) : null;

        var result = new JsonObject
        {
            ["version"] = artifact?.Version,
            ["autoAccept"] = options.AutoAccept,
            ["candidate"] = options.Candidate,
            ["source"] = new JsonArray((src?.Tables ?? []).Select(t => (JsonNode)new JsonObject
            {
                ["key"] = t.Key,
                ["rows"] = t.Rows,
                ["columns"] = new JsonArray(t.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name,
                    ["type"] = c.TypeDisplay,
                    ["nullable"] = c.IsNullable
                }).ToArray())
            }).ToArray()),
            ["target"] = new JsonArray((tgt?.Tables ?? []).Select(t => (JsonNode)new JsonObject
            {
                ["key"] = t.Key,
                ["columns"] = new JsonArray(t.Columns.OrderBy(c => c.Ordinal).Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name,
                    ["type"] = c.TypeDisplay,
                    ["nullable"] = c.IsNullable,
                    ["identity"] = c.IsIdentity,
                    ["computed"] = c.IsComputed,
                    ["rowversion"] = c.IsRowVersion,
                    ["hasDefault"] = c.DefaultDefinition is not null
                }).ToArray())
            }).ToArray()),
            ["blockers"] = new JsonArray(),
            ["attention"] = new JsonArray(),
            ["uncovered"] = new JsonArray()
        };
        if (artifact is null || src is null || tgt is null) return result;

        var m = Json.Deserialize<MappingPayload>(artifact.PayloadJson);
        result["blockers"] = Strings(MappingValidator.Blockers(m, src, tgt));
        result["attention"] = Strings(MappingValidator.Attention(m, options));
        result["uncovered"] = Strings(MappingValidator.UncoveredSourceColumns(m, src));
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
}
```

Register it — in `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs`, inside `MapAll`, replace the placeholder comment line

```csharp
        // T3.5: MappingEndpoints.Map(app, state);
```

with

```csharp
        MappingEndpoints.Map(app, state);   // T3.5
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~MappingEndpointsTests"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 4`.

- [ ] **Step 5: Write the failing node test for the pure helpers**

Create `plugins/db-migrate/engine/Dbm.Tests/js/mapping-ops.test.cjs`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

globalThis.window = globalThis;
vm.runInThisContext(
  fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'views', 'mapping.js'), 'utf8'),
  { filename: 'mapping.js' });
const ops = globalThis.DBM.mappingOps;

function payload() {
  return {
    tables: {
      'app.Customers': {
        kind: 'direct', sources: ['dbo.CUST'], confidence: 0.84, method: 'exact',
        columns: {
          CustomerId: { expr: 's.[CUST_ID]', sourceColumns: ['dbo.CUST.CUST_ID'], confidence: 1, method: 'exact' },
          FirstName: { sourceColumns: [], confidence: 0.73, method: 'vector', candidates: [{ source: 'dbo.CUST.CUST_NM', score: 0.73, why: 'name 0.45' }] },
          Email: { expr: 's.[EMAIL_ADDR]', sourceColumns: ['dbo.CUST.EMAIL_ADDR'], confidence: 0.8, method: 'vector' }
        }
      },
      'app.AuditEvents': { kind: 'direct', sources: [], confidence: 0.3, method: 'vector', columns: {} }
    },
    drops: { 'dbo.TMP_IMPORT': { reason: 'empty table with no matching target', method: 'vector' } },
    notes: []
  };
}

function context() {
  return {
    autoAccept: 0.85,
    source: [
      { key: 'dbo.CUST', rows: 1000, columns: [
        { name: 'CUST_ID', type: 'int', nullable: false }, { name: 'CUST_NM', type: 'varchar(100)', nullable: false },
        { name: 'EMAIL_ADDR', type: 'varchar(120)', nullable: true }, { name: 'FAX_NO', type: 'varchar(30)', nullable: true }] },
      { key: 'dbo.AUDIT_LOG', rows: 5000, columns: [{ name: 'LOG_TS', type: 'datetime', nullable: false }, { name: 'USR', type: 'varchar(50)', nullable: false }] },
      { key: 'dbo.TMP_IMPORT', rows: 0, columns: [{ name: 'X', type: 'int', nullable: true }] }
    ],
    target: [
      { key: 'app.Customers', columns: [
        { name: 'CustomerId', type: 'int', nullable: false, identity: true, computed: false, rowversion: false, hasDefault: false },
        { name: 'FirstName', type: 'nvarchar(50)', nullable: false, identity: false, computed: false, rowversion: false, hasDefault: false },
        { name: 'Email', type: 'nvarchar(120)', nullable: true, identity: false, computed: false, rowversion: false, hasDefault: false },
        { name: 'CreatedAt', type: 'datetime2(0)', nullable: false, identity: false, computed: false, rowversion: false, hasDefault: true },
        { name: 'DisplayName', type: 'nvarchar(101)', nullable: false, identity: false, computed: true, rowversion: false, hasDefault: false }] },
      { key: 'app.AuditEvents', columns: [
        { name: 'EventTime', type: 'datetime2(3)', nullable: false, identity: false, computed: false, rowversion: false, hasDefault: false }] }
    ]
  };
}

// Minimal RFC 6902 subset (same semantics as the engine's JsonPatch) used to check that diff() output round-trips.
function applyOps(doc, list) {
  const out = JSON.parse(JSON.stringify(doc));
  for (const o of list) {
    const segs = o.path.split('/').slice(1).map((s) => s.replace(/~1/g, '/').replace(/~0/g, '~'));
    let node = out;
    for (let i = 0; i < segs.length - 1; i++) {
      if (!(segs[i] in node)) node[segs[i]] = {};
      node = node[segs[i]];
    }
    const last = segs[segs.length - 1];
    if (o.op === 'remove') { assert.ok(last in node, 'remove of missing ' + o.path); delete node[last]; }
    else if (o.op === 'replace') { assert.ok(last in node, 'replace of missing ' + o.path); node[last] = o.value; }
    else node[last] = o.value;
  }
  return out;
}

test('diff of identical payloads is empty', () => {
  assert.deepEqual(ops.diff(payload(), payload()), []);
});

test('changing an expression is one replace op on the column field', () => {
  const after = payload();
  after.tables['app.Customers'].columns.Email.expr = 'LOWER(s.[EMAIL_ADDR])';
  assert.deepEqual(ops.diff(payload(), after), [
    { op: 'replace', path: '/tables/app.Customers/columns/Email/expr', value: 'LOWER(s.[EMAIL_ADDR])' }
  ]);
});

test('setting a missing field is add, clearing a field is remove', () => {
  const after = payload();
  after.tables['app.Customers'].columns.FirstName.expr = 'LEFT(s.[CUST_NM], 10)';
  delete after.tables['app.Customers'].columns.Email.expr;
  assert.deepEqual(ops.diff(payload(), after), [
    { op: 'remove', path: '/tables/app.Customers/columns/Email/expr' },
    { op: 'add', path: '/tables/app.Customers/columns/FirstName/expr', value: 'LEFT(s.[CUST_NM], 10)' }
  ]);
});

test('whole column maps and tables are added and removed as objects', () => {
  const after = payload();
  after.tables['app.Customers'].columns.Phone = { expr: 's.[PHONE_NO]', sourceColumns: ['dbo.CUST.PHONE_NO'], confidence: 1, method: 'human' };
  delete after.tables['app.Customers'].columns.CustomerId;
  delete after.tables['app.AuditEvents'];
  after.tables['app.Products'] = { kind: 'skip', sources: [], confidence: 1, method: 'human', columns: {} };
  const list = ops.diff(payload(), after);
  assert.deepEqual(list.map((o) => o.op + ' ' + o.path), [
    'remove /tables/app.AuditEvents',
    'remove /tables/app.Customers/columns/CustomerId',
    'add /tables/app.Customers/columns/Phone',
    'add /tables/app.Products'
  ]);
});

test('drops are added, replaced and removed; pointer segments are escaped', () => {
  const before = payload();
  before.drops['dbo.A/B~C'] = { reason: 'odd name', method: 'human' };
  const after = payload();
  after.drops['dbo.CUST.FAX_NO'] = { reason: 'no fax in ShopV2', method: 'human' };
  after.drops['dbo.TMP_IMPORT'] = { reason: 'staging table', method: 'human' };
  assert.deepEqual(ops.diff(before, after), [
    { op: 'remove', path: '/drops/dbo.A~1B~0C' },
    { op: 'add', path: '/drops/dbo.CUST.FAX_NO', value: { reason: 'no fax in ShopV2', method: 'human' } },
    { op: 'replace', path: '/drops/dbo.TMP_IMPORT', value: { reason: 'staging table', method: 'human' } }
  ]);
  assert.equal(ops.pointer('tables', 'a/b', 'x~y'), '/tables/a~1b/x~0y');
});

test('notes are replaced as a whole', () => {
  const after = payload();
  after.notes = ['check the orphan orders'];
  assert.deepEqual(ops.diff(payload(), after), [{ op: 'replace', path: '/notes', value: ['check the orphan orders'] }]);
});

test('diff output applied to before yields after', () => {
  const before = payload();
  const after = payload();
  ops.editColumn(after, 'app.Customers', 'FirstName', { expr: "LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", sourceColumns: 'dbo.CUST.CUST_NM' });
  ops.setSource(after, 'app.AuditEvents', 'dbo.AUDIT_LOG');
  ops.mapToTarget(after, 'dbo.AUDIT_LOG.LOG_TS', 'app.AuditEvents', 'EventTime');
  ops.dropSource(after, 'dbo.CUST.FAX_NO', 'no fax');
  ops.undropSource(after, 'dbo.TMP_IMPORT');
  ops.editTable(after, 'app.Customers', { filter: 's.[CUST_ID] > 0' });
  assert.deepEqual(applyOps(before, ops.diff(before, after)), after);
});

test('editColumn records a human decision and removes blank fields', () => {
  const p = payload();
  const cm = ops.editColumn(p, 'app.Customers', 'Email', { expr: '', 'default': "N''" });
  assert.equal(cm.expr, undefined);
  assert.equal(cm['default'], "N''");
  assert.equal(cm.method, 'human');
  assert.equal(cm.confidence, 1);
  assert.deepEqual(ops.editColumn(p, 'app.Customers', 'Notes', { sourceColumns: 'dbo.CUST.NOTES, dbo.CUST.CUST_ID' }).sourceColumns,
    ['dbo.CUST.NOTES', 'dbo.CUST.CUST_ID']);
  assert.throws(() => ops.editColumn(p, 'app.Nope', 'X', { expr: '1' }), /unknown target table/);
});

test('restoreIfUnchanged drops edits that end where they started', () => {
  const before = payload();
  const after = payload();
  ops.editColumn(after, 'app.Customers', 'Email', { expr: 'x' });
  ops.editColumn(after, 'app.Customers', 'Email', { expr: 's.[EMAIL_ADDR]' });
  assert.equal(ops.restoreIfUnchanged(before, after, 'app.Customers', 'Email'), true);
  assert.deepEqual(ops.diff(before, after), []);
  ops.editColumn(after, 'app.Customers', 'Phone', { expr: '' });
  assert.equal(ops.restoreIfUnchanged(before, after, 'app.Customers', 'Phone'), true);
  assert.equal('Phone' in after.tables['app.Customers'].columns, false);
});

test('mapToTarget writes s.[COL] for the primary source and adopts a source for unpaired tables', () => {
  const p = payload();
  const cm = ops.mapToTarget(p, 'dbo.CUST.CUST_NM', 'app.Customers', 'FirstName');
  assert.equal(cm.expr, 's.[CUST_NM]');
  assert.deepEqual(cm.sourceColumns, ['dbo.CUST.CUST_NM']);
  ops.mapToTarget(p, 'dbo.AUDIT_LOG.USR', 'app.AuditEvents', 'UserName');
  assert.deepEqual(p.tables['app.AuditEvents'].sources, ['dbo.AUDIT_LOG']);
  assert.throws(() => ops.mapToTarget(p, 'dbo.AUDIT_LOG.LOG_TS', 'app.Customers', 'CreatedAt'), /not in the primary source/);
  assert.equal(ops.quoteName('a]b'), '[a]]b]');
});

test('useCandidate maps the candidate column', () => {
  const p = payload();
  ops.useCandidate(p, 'app.Customers', 'FirstName', 'dbo.CUST.CUST_NM');
  assert.equal(p.tables['app.Customers'].columns.FirstName.expr, 's.[CUST_NM]');
});

test('mapTargetsFor offers writable columns of tables fed by that source or without a source', () => {
  const choices = ops.mapTargetsFor(context(), payload(), 'dbo.CUST.FAX_NO');
  assert.deepEqual(choices.map((c) => c.table + '.' + c.column), [
    'app.Customers.CustomerId', 'app.Customers.FirstName', 'app.Customers.Email', 'app.Customers.CreatedAt', 'app.AuditEvents.EventTime'
  ]);
  assert.equal(choices.find((c) => c.column === 'Email').mapped, true);
  assert.deepEqual(ops.mapTargetsFor(context(), payload(), 'dbo.AUDIT_LOG.USR').map((c) => c.table), ['app.AuditEvents']);
});

test('uncovered honours column maps, drops, table drops and skip maps', () => {
  const p = payload();
  assert.deepEqual(ops.uncovered(context(), p), ['dbo.CUST.CUST_NM', 'dbo.CUST.FAX_NO', 'dbo.AUDIT_LOG.LOG_TS', 'dbo.AUDIT_LOG.USR']);
  ops.dropSource(p, 'dbo.AUDIT_LOG', 'history stays behind');
  ops.dropSource(p, 'dbo.cust.fax_no', 'no fax');
  assert.deepEqual(ops.uncovered(context(), p), ['dbo.CUST.CUST_NM']);
  p.tables['app.Customers'].kind = 'skip';
  assert.equal(ops.uncovered(context(), p).length, 3);
});

test('blockers mirror MappingValidator messages', () => {
  const p = payload();
  assert.deepEqual(ops.blockers(context(), p), [
    'app.Customers.FirstName: NOT NULL without default needs an expression or default',
    'app.AuditEvents: no source table (choose one or set kind "skip")',
    'app.AuditEvents.EventTime: NOT NULL without default needs an expression or default',
    'source column dbo.CUST.CUST_NM is not mapped or dropped',
    'source column dbo.CUST.FAX_NO is not mapped or dropped',
    'source table dbo.AUDIT_LOG is not mapped or dropped (2 columns)'
  ]);
  delete p.tables['app.AuditEvents'];
  assert.equal(ops.blockers(context(), p)[1], 'app.AuditEvents: no table mapping (map a source table or set kind "skip")');
  ops.skipTable(p, 'app.AuditEvents', 'not migrated');
  assert.equal(ops.blockers(context(), p).some((b) => b.startsWith('app.AuditEvents')), false);
});

test('statuses and attention follow the auto-accept band', () => {
  const p = payload();
  const b = ops.blockers(context(), p);
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], b, 0.85), 'blocker');
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], [], 0.85), 'attention');
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], [], 0.75), 'attention');
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], [], 0.7), 'ok');
  assert.equal(ops.tableStatus('app.Missing', undefined, [], 0.85), 'blocker');
  assert.equal(ops.columnStatus('app.Customers', 'FirstName', p.tables['app.Customers'].columns.FirstName, b, 0.85), 'blocker');
  assert.equal(ops.columnStatus('app.Customers', 'Email', p.tables['app.Customers'].columns.Email, b, 0.85), 'attention');
  assert.equal(ops.needsReview({ method: 'agent', confidence: 0.1 }, 0.85), false);
  assert.equal(ops.attentionCount(p, 0.85), 3);
});

test('changeCount counts edited entities, not ops', () => {
  const after = payload();
  ops.editColumn(after, 'app.Customers', 'Email', { expr: 'LOWER(s.[EMAIL_ADDR])' });
  ops.dropSource(after, 'dbo.CUST.FAX_NO', 'no fax');
  ops.editTable(after, 'app.Customers', { filter: 's.[CUST_ID] > 0' });
  const list = ops.diff(payload(), after);
  assert.equal(list.length, 7);
  assert.equal(ops.changeCount(list), 3);
  assert.equal(ops.changeCount([]), 0);
});

test('parseSourceColumns splits on commas, semicolons and whitespace', () => {
  assert.deepEqual(ops.parseSourceColumns(' dbo.A.X, dbo.A.Y;dbo.B.Z\n'), ['dbo.A.X', 'dbo.A.Y', 'dbo.B.Z']);
  assert.deepEqual(ops.parseSourceColumns(''), []);
});

test('the view registers itself', () => {
  assert.equal(globalThis.DBM.views.mapping.title, 'Mapping');
  assert.equal(typeof globalThis.DBM.views.mapping.render, 'function');
});
```

- [ ] **Step 6: Run the node test to verify it fails**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/mapping-ops.test.cjs`
Expected: FAIL — `Error: ENOENT: no such file or directory, open '…/Dbm/wwwroot/js/views/mapping.js'`.

- [ ] **Step 7: Implement `views/mapping.js`**

Create `plugins/db-migrate/engine/Dbm/wwwroot/js/views/mapping.js`:

```js
(function (DBM) {
  'use strict';

  // =====================================================================
  // Pure helpers (no DOM). Exposed as DBM.mappingOps and unit-tested with
  // node --test (Dbm.Tests/js/mapping-ops.test.cjs).
  // =====================================================================

  var TABLE_PROPS = ['kind', 'sources', 'from', 'filter', 'confidence', 'method', 'rationale', 'candidates'];
  var COLUMN_PROPS = ['expr', 'sourceColumns', 'default', 'confidence', 'method', 'rationale', 'typeRisk', 'candidates'];
  var EDIT_FIELDS = ['expr', 'sourceColumns', 'default'];

  function escSeg(s) { return String(s).replace(/~/g, '~0').replace(/\//g, '~1'); }
  function pointer() {
    var parts = [];
    for (var i = 0; i < arguments.length; i++) parts.push(escSeg(arguments[i]));
    return '/' + parts.join('/');
  }
  function isNil(v) { return v === undefined || v === null; }
  function blank(v) { return isNil(v) || String(v).trim() === ''; }
  function clone(v) { return v === undefined ? undefined : JSON.parse(JSON.stringify(v)); }
  function stable(v) {
    if (Array.isArray(v)) return '[' + v.map(stable).join(',') + ']';
    if (v && typeof v === 'object') {
      return '{' + Object.keys(v).sort().filter(function (k) { return v[k] !== undefined; })
        .map(function (k) { return JSON.stringify(k) + ':' + stable(v[k]); }).join(',') + '}';
    }
    return JSON.stringify(isNil(v) ? null : v);
  }
  function same(a, b) { return stable(a) === stable(b); }
  function unionKeys(a, b) {
    var seen = {};
    Object.keys(a || {}).concat(Object.keys(b || {})).forEach(function (k) { seen[k] = true; });
    return Object.keys(seen).sort();
  }
  function orderedProps(known, a, b) {
    var all = unionKeys(a, b);
    var out = known.filter(function (k) { return all.indexOf(k) >= 0; });
    all.forEach(function (k) { if (out.indexOf(k) < 0) out.push(k); });
    return out;
  }
  function findKey(obj, key) {
    if (!obj) return undefined;
    if (Object.prototype.hasOwnProperty.call(obj, key)) return obj[key];
    var lower = String(key).toLowerCase();
    var keys = Object.keys(obj);
    for (var i = 0; i < keys.length; i++) if (keys[i].toLowerCase() === lower) return obj[keys[i]];
    return undefined;
  }
  function splitColumnKey(key) {
    var s = String(key);
    var i = s.lastIndexOf('.');
    return { table: s.slice(0, i), column: s.slice(i + 1) };
  }
  function quoteName(name) { return '[' + String(name).replace(/]/g, ']]') + ']'; }
  function parseSourceColumns(text) {
    return String(text || '').split(/[\s,;]+/).map(function (s) { return s.trim(); }).filter(Boolean);
  }

  function propOps(ops, base, before, after, known, skip) {
    orderedProps(known, before, after).forEach(function (k) {
      if (skip && skip.indexOf(k) >= 0) return;
      var b = before ? before[k] : undefined;
      var a = after ? after[k] : undefined;
      var path = base + '/' + escSeg(k);
      if (isNil(a) && isNil(b)) return;
      if (isNil(a)) ops.push({ op: 'remove', path: path });
      else if (isNil(b)) ops.push({ op: 'add', path: path, value: clone(a) });
      else if (!same(a, b)) ops.push({ op: 'replace', path: path, value: clone(a) });
    });
  }

  /** JSON-patch ops (add | replace | remove) that turn mapping payload `before` into `after`. */
  function diff(before, after) {
    var ops = [];
    var bt = (before && before.tables) || {};
    var at = (after && after.tables) || {};
    unionKeys(bt, at).forEach(function (t) {
      var base = pointer('tables', t);
      if (!(t in at)) { ops.push({ op: 'remove', path: base }); return; }
      if (!(t in bt)) { ops.push({ op: 'add', path: base, value: clone(at[t]) }); return; }
      propOps(ops, base, bt[t], at[t], TABLE_PROPS, ['columns']);
      var bc = bt[t].columns || {};
      var ac = at[t].columns || {};
      if (!bt[t].columns && at[t].columns) { ops.push({ op: 'add', path: base + '/columns', value: clone(ac) }); return; }
      unionKeys(bc, ac).forEach(function (c) {
        var cbase = base + '/columns/' + escSeg(c);
        if (!(c in ac)) ops.push({ op: 'remove', path: cbase });
        else if (!(c in bc)) ops.push({ op: 'add', path: cbase, value: clone(ac[c]) });
        else propOps(ops, cbase, bc[c], ac[c], COLUMN_PROPS);
      });
    });
    var bd = (before && before.drops) || {};
    var ad = (after && after.drops) || {};
    unionKeys(bd, ad).forEach(function (k) {
      var path = pointer('drops', k);
      if (!(k in ad)) ops.push({ op: 'remove', path: path });
      else if (!(k in bd)) ops.push({ op: 'add', path: path, value: clone(ad[k]) });
      else if (!same(bd[k], ad[k])) ops.push({ op: 'replace', path: path, value: clone(ad[k]) });
    });
    var bn = (before && before.notes) || [];
    var an = (after && after.notes) || [];
    if (!same(bn, an)) ops.push({ op: before && before.notes ? 'replace' : 'add', path: '/notes', value: clone(an) });
    return ops;
  }

  /** Number of edited entities (a column map, a table's own fields, a drop, the notes) behind a list of ops. */
  function changeCount(list) {
    var seen = {};
    (list || []).forEach(function (o) {
      var segs = String(o.path).split('/');
      var key;
      if (segs[1] === 'tables') key = segs[3] === 'columns' && segs.length > 4 ? segs.slice(0, 5).join('/') : segs.slice(0, 3).join('/');
      else if (segs[1] === 'drops') key = segs.slice(0, 3).join('/');
      else key = '/' + segs[1];
      seen[key] = true;
    });
    return Object.keys(seen).length;
  }

  function needsReview(item, autoAccept) {
    return !!item && (item.method === 'fuzzy' || item.method === 'vector') && (Number(item.confidence) || 0) < autoAccept;
  }
  function mentions(line, key) {
    var l = String(line).toLowerCase();
    var k = String(key).toLowerCase();
    return l.indexOf(k + ':') === 0 || l.indexOf(k + '.') === 0;
  }
  function tableStatus(key, map, blockers, autoAccept) {
    if (!map || (blockers || []).some(function (b) { return mentions(b, key); })) return 'blocker';
    if (map.kind === 'skip') return 'ok';
    if (needsReview(map, autoAccept)) return 'attention';
    var cols = map.columns || {};
    return Object.keys(cols).some(function (c) { return needsReview(cols[c], autoAccept); }) ? 'attention' : 'ok';
  }
  function columnStatus(tableKey, column, cm, blockers, autoAccept) {
    var prefix = (tableKey + '.' + column + ':').toLowerCase();
    if ((blockers || []).some(function (b) { return String(b).toLowerCase().indexOf(prefix) === 0; })) return 'blocker';
    return needsReview(cm, autoAccept) ? 'attention' : 'ok';
  }
  function attentionCount(payload, autoAccept) {
    var n = 0;
    var tables = (payload && payload.tables) || {};
    Object.keys(tables).forEach(function (k) {
      var m = tables[k];
      if (m.kind === 'skip') return;
      if (needsReview(m, autoAccept)) n++;
      var cols = m.columns || {};
      Object.keys(cols).forEach(function (c) { if (needsReview(cols[c], autoAccept)) n++; });
    });
    return n;
  }

  /** Source columns ("schema.table.column") neither referenced by a non-skip map nor dropped (mirrors MappingValidator). */
  function uncovered(context, payload) {
    var covered = {};
    var dropped = {};
    var tables = (payload && payload.tables) || {};
    Object.keys(tables).forEach(function (k) {
      var m = tables[k];
      if (m.kind === 'skip') return;
      var cols = m.columns || {};
      Object.keys(cols).forEach(function (c) {
        (cols[c].sourceColumns || []).forEach(function (sc) { covered[String(sc).toLowerCase()] = true; });
      });
    });
    Object.keys((payload && payload.drops) || {}).forEach(function (k) { dropped[k.toLowerCase()] = true; });
    var out = [];
    ((context && context.source) || []).forEach(function (s) {
      if (dropped[s.key.toLowerCase()]) return;
      s.columns.forEach(function (c) {
        var key = s.key + '.' + c.name;
        if (!covered[key.toLowerCase()] && !dropped[key.toLowerCase()]) out.push(key);
      });
    });
    return out;
  }

  /** Approval blockers with the same messages as MappingValidator.Blockers (C#). */
  function blockers(context, payload) {
    var out = [];
    var tables = (payload && payload.tables) || {};
    ((context && context.target) || []).forEach(function (t) {
      var map = findKey(tables, t.key);
      if (!map) { out.push(t.key + ': no table mapping (map a source table or set kind "skip")'); return; }
      if (map.kind === 'skip') return;
      if (!map.sources || !map.sources.length) out.push(t.key + ': no source table (choose one or set kind "skip")');
      t.columns.forEach(function (c) {
        if (c.nullable || c.identity || c.computed || c.rowversion || c.hasDefault) return;
        var cm = findKey(map.columns || {}, c.name);
        if (!cm || (blank(cm.expr) && blank(cm['default']))) {
          out.push(t.key + '.' + c.name + ': NOT NULL without default needs an expression or default');
        }
      });
    });
    var missing = {};
    uncovered(context, payload).forEach(function (key) {
      var p = splitColumnKey(key);
      (missing[p.table] = missing[p.table] || []).push(key);
    });
    ((context && context.source) || []).forEach(function (s) {
      var miss = missing[s.key];
      if (!miss) return;
      if (miss.length === s.columns.length) out.push('source table ' + s.key + ' is not mapped or dropped (' + miss.length + ' columns)');
      else miss.forEach(function (key) { out.push('source column ' + key + ' is not mapped or dropped'); });
    });
    return out;
  }

  function markHuman(item) { item.method = 'human'; item.confidence = 1; }

  /** Applies a human edit to one column map (creating it if needed); blank values remove the field. */
  function editColumn(payload, tableKey, column, fields) {
    var t = findKey(payload.tables, tableKey);
    if (!t) throw new Error('unknown target table ' + tableKey);
    t.columns = t.columns || {};
    var cm = t.columns[column] || (t.columns[column] = { sourceColumns: [] });
    Object.keys(fields).forEach(function (k) {
      var v = fields[k];
      if (k === 'sourceColumns') cm.sourceColumns = Array.isArray(v) ? v : parseSourceColumns(v);
      else if (blank(v)) delete cm[k];
      else cm[k] = v;
    });
    if (!cm.sourceColumns) cm.sourceColumns = [];
    markHuman(cm);
    return cm;
  }

  /** Puts the original column map back when the editable fields ended up unchanged (no spurious ops). */
  function restoreIfUnchanged(before, after, tableKey, column) {
    var bt = findKey((before && before.tables) || {}, tableKey);
    var at = findKey((after && after.tables) || {}, tableKey);
    var bc = bt && bt.columns && bt.columns[column];
    var ac = at && at.columns && at.columns[column];
    if (!ac) return false;
    function pick(c) { return EDIT_FIELDS.map(function (f) { return f === 'sourceColumns' ? (c.sourceColumns || []) : (blank(c[f]) ? null : c[f]); }); }
    if (bc && same(pick(bc), pick(ac))) { at.columns[column] = clone(bc); return true; }
    if (!bc && same(pick(ac), [null, [], null])) { delete at.columns[column]; return true; }
    return false;
  }

  function editTable(payload, tableKey, fields) {
    var t = findKey(payload.tables, tableKey);
    if (!t) throw new Error('unknown target table ' + tableKey);
    Object.keys(fields).forEach(function (k) {
      var v = fields[k];
      if (k === 'sources') t.sources = Array.isArray(v) ? v : parseSourceColumns(v);
      else if (blank(v)) delete t[k];
      else t[k] = v;
    });
    if (!t.sources) t.sources = [];
    markHuman(t);
    return t;
  }

  function setSource(payload, tableKey, sourceTable) {
    var t = findKey(payload.tables, tableKey);
    if (!t) t = payload.tables[tableKey] = { kind: 'direct', sources: [], confidence: 1, method: 'human', columns: {} };
    if (t.kind === 'skip') t.kind = 'direct';
    t.sources = [sourceTable];
    t.columns = t.columns || {};
    markHuman(t);
    return t;
  }

  function skipTable(payload, tableKey, reason) {
    payload.tables[tableKey] = { kind: 'skip', sources: [], confidence: 1, method: 'human', rationale: reason, columns: {} };
    return payload.tables[tableKey];
  }

  function dropSource(payload, key, reason) {
    if (blank(reason)) throw new Error('A drop needs a reason.');
    payload.drops = payload.drops || {};
    payload.drops[key] = { reason: String(reason).trim(), method: 'human' };
    return payload.drops[key];
  }

  function undropSource(payload, key) {
    if (payload.drops) delete payload.drops[key];
  }

  /** Maps source column `sourceKey` to a target column as s.[COL]; the target table must use that source table as Sources[0]
   *  (a table without sources adopts it). */
  function mapToTarget(payload, sourceKey, tableKey, column) {
    var sc = splitColumnKey(sourceKey);
    var t = findKey(payload.tables, tableKey);
    if (!t || !t.sources || !t.sources.length || t.kind === 'skip') t = setSource(payload, tableKey, sc.table);
    if (String(t.sources[0]).toLowerCase() !== sc.table.toLowerCase()) {
      throw new Error(sourceKey + ' is not in the primary source (' + t.sources[0] + ') of ' + tableKey);
    }
    return editColumn(payload, tableKey, column, { expr: 's.' + quoteName(sc.column), sourceColumns: [sourceKey], rationale: 'Mapped by hand in the mapping screen.' });
  }

  function useCandidate(payload, tableKey, column, candidateSource) {
    return mapToTarget(payload, candidateSource, tableKey, column);
  }

  /** Target columns a source column can be mapped to: writable columns of tables whose primary source is that table, or that
   *  have no source yet. */
  function mapTargetsFor(context, payload, sourceKey) {
    var srcTable = splitColumnKey(sourceKey).table.toLowerCase();
    var out = [];
    ((context && context.target) || []).forEach(function (t) {
      var map = findKey((payload && payload.tables) || {}, t.key);
      if (map && map.kind === 'skip') return;
      var primary = map && map.sources && map.sources.length ? String(map.sources[0]).toLowerCase() : null;
      if (primary !== null && primary !== srcTable) return;
      t.columns.forEach(function (c) {
        if (c.computed || c.rowversion) return;
        var cm = map && map.columns && findKey(map.columns, c.name);
        out.push({ table: t.key, column: c.name, type: c.type, mapped: !!(cm && !blank(cm.expr)) });
      });
    });
    return out;
  }

  DBM.mappingOps = {
    diff: diff,
    changeCount: changeCount,
    pointer: pointer,
    parseSourceColumns: parseSourceColumns,
    splitColumnKey: splitColumnKey,
    quoteName: quoteName,
    needsReview: needsReview,
    tableStatus: tableStatus,
    columnStatus: columnStatus,
    attentionCount: attentionCount,
    uncovered: uncovered,
    blockers: blockers,
    editColumn: editColumn,
    restoreIfUnchanged: restoreIfUnchanged,
    editTable: editTable,
    setSource: setSource,
    skipTable: skipTable,
    dropSource: dropSource,
    undropSource: undropSource,
    mapToTarget: mapToTarget,
    useCandidate: useCandidate,
    mapTargetsFor: mapTargetsFor
  };

  // =====================================================================
  // View (browser only). Everything below touches the DOM lazily.
  // =====================================================================

  var ops = DBM.mappingOps;
  var current = null;

  function flat(list, out) {
    list.forEach(function (c) {
      if (Array.isArray(c)) flat(c, out);
      else if (c !== null && c !== undefined && c !== false) out.push(typeof c === 'number' ? String(c) : c);
    });
    return out;
  }
  function el(tag, attrs) {
    return DBM.h.apply(null, [tag, attrs || {}].concat(flat(Array.prototype.slice.call(arguments, 2), [])));
  }
  function pct(v) { return Math.round((Number(v) || 0) * 100) + '%'; }

  function bar(v) {
    var n = Math.max(0, Math.min(1, Number(v) || 0));
    var b = el('span', { class: 'bar map-bar', role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': '1', 'aria-valuenow': n.toFixed(2), title: 'Confidence ' + pct(n) });
    b.style.setProperty('--v', n.toFixed(3));
    return el('span', { class: 'map-conf' }, b, el('span', { class: 'num small' }, pct(n)));
  }
  function methodTag(method) { return el('span', { class: 'tag map-m map-m-' + (method || 'none') }, method || '—'); }
  function statusPill(st) {
    return el('span', { class: 'map-st map-st-' + st }, st === 'ok' ? 'OK' : st === 'attention' ? 'Needs review' : 'Blocker');
  }
  function flagTag(text) { return el('span', { class: 'tag map-flag' }, text); }
  function autoGrow(ta) { ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight + 2, 240) + 'px'; }

  function contextFromPayload(payload) {
    var tables = (payload && payload.tables) || {};
    return {
      version: null, autoAccept: 0.85, candidate: 0.5, source: [], blockers: [], attention: [], uncovered: [],
      target: Object.keys(tables).sort().map(function (k) {
        return {
          key: k,
          columns: Object.keys(tables[k].columns || {}).map(function (c) {
            return { name: c, type: '', nullable: true, identity: false, computed: false, rowversion: false, hasDefault: false };
          })
        };
      })
    };
  }

  function loadContext(ctx, payload) {
    if (window.DBM_EXPORT || !ctx.api) return Promise.resolve(contextFromPayload(payload));
    var q = isNil(ctx.version) ? '' : '?version=' + encodeURIComponent(ctx.version);
    return ctx.api.get('/api/mapping/context' + q);
  }

  function localBlockers(view) {
    var cx = view.context;
    return cx.source && cx.source.length ? ops.blockers(cx, view.work) : (cx.blockers || []);
  }

  function render(root, ctx) {
    var artifact = ctx.artifact || {};
    var payload = artifact.payload || { tables: {}, drops: {}, notes: [] };
    var phase = ((ctx.state && ctx.state.phases) || []).filter(function (p) { return p.name === 'mapping'; })[0] || {};
    var view = {
      ctx: ctx,
      before: clone(payload),
      work: clone(payload),
      context: null,
      expanded: {},
      filter: 'all',
      saving: false,
      bar: null,
      body: null,
      editable: !ctx.readOnly && phase.status === 'awaiting_review' && ctx.version === phase.currentVersion
    };
    current = view;
    root.textContent = '';
    var page = el('div', { class: 'page map-page' });
    root.appendChild(page);
    if (DBM.components.reviewBar && !window.DBM_EXPORT) page.appendChild(DBM.components.reviewBar(ctx));
    view.body = el('div', { class: 'stack' }, el('div', { class: 'empty is-loading' }, 'Loading mapping…'));
    page.appendChild(view.body);
    loadContext(ctx, payload).then(function (context) {
      view.context = context;
      draw(view);
    }).catch(function (e) {
      view.body.textContent = '';
      view.body.appendChild(DBM.components.emptyState('Mapping context unavailable', (e && e.message) || String(e)));
    });
  }

  function redraw(view) {
    var y = window.scrollY;
    draw(view);
    window.scrollTo(0, y);
  }

  function draw(view) {
    var ctx = view.ctx;
    var a = ctx.artifact || {};
    var blockersNow = localBlockers(view);
    view.bar = null;
    view.body.textContent = '';
    view.body.appendChild(el('div', { class: 'page-h' },
      el('div', { class: 'h1' }, 'Mapping'),
      el('div', { class: 'muted small' }, ['v' + (isNil(ctx.version) ? '?' : ctx.version), a.author, a.summary].filter(Boolean).join(' · '))));
    view.body.appendChild(kpis(view, blockersNow));
    if (!view.editable && !ctx.readOnly) {
      view.body.appendChild(el('p', { class: 'muted small map-note' },
        'Direct edits are available while the phase is awaiting review and you are viewing the latest version.'));
    }
    if (blockersNow.length) view.body.appendChild(blockersPanel(view, blockersNow));
    view.body.appendChild(tablesCard(view, blockersNow));
    view.body.appendChild(uncoveredCard(view));
    view.body.appendChild(dropsCard(view));
    if (view.editable) view.body.appendChild(saveBar(view));
  }

  function kpis(view, blockersNow) {
    var cx = view.context;
    var w = view.work;
    var K = DBM.components.kpi;
    var targets = cx.target || [];
    var tablesMapped = 0;
    var colsMapped = 0;
    var colsTotal = 0;
    targets.forEach(function (t) {
      var m = findKey(w.tables, t.key);
      if (m && m.kind !== 'skip' && m.sources && m.sources.length) tablesMapped++;
      t.columns.forEach(function (c) {
        if (c.computed || c.rowversion) return;
        colsTotal++;
        var cm = m && findKey(m.columns || {}, c.name);
        if (cm && !blank(cm.expr)) colsMapped++;
      });
    });
    return el('div', { class: 'grid-kpi' },
      K('Tables mapped', tablesMapped + ' / ' + targets.length, 'target tables with a source'),
      K('Columns mapped', colsMapped + ' / ' + colsTotal, 'writable target columns'),
      K('Needs review', String(ops.attentionCount(w, cx.autoAccept)), 'proposals below ' + pct(cx.autoAccept)),
      K('Blockers', String(blockersNow.length), blockersNow.length ? 'approval disabled' : 'nothing blocks approval'),
      K('Drops', String(Object.keys(w.drops || {}).length), 'source tables/columns not migrated'));
  }

  function blockerTarget(view, text) {
    if (String(text).indexOf('source ') === 0) return '#uncovered';
    var hit = (view.context.target || []).filter(function (t) { return mentions(text, t.key); })[0];
    return hit ? hit.key : null;
  }

  function jump(view, target) {
    if (target === '#uncovered') {
      var card = document.getElementById('map-uncovered');
      if (card) card.scrollIntoView({ block: 'start' });
      return;
    }
    view.expanded[target] = true;
    view.filter = 'all';
    draw(view);
    var sel = '[data-key="' + (window.CSS && CSS.escape ? CSS.escape(target) : target) + '"]';
    var row = view.body.querySelector(sel);
    if (row) {
      row.scrollIntoView({ block: 'center' });
      var btn = row.querySelector('button');
      if (btn) btn.focus();
    }
  }

  function blockersPanel(view, list) {
    return el('section', { class: 'card map-blockers', 'aria-label': 'Blockers' },
      el('div', { class: 'card-h row row-wrap' },
        el('span', { class: 'map-st map-st-blocker h3' }, 'Blockers'),
        el('span', { class: 'muted small' }, 'Approval stays disabled until each item is mapped, defaulted, skipped or dropped.')),
      el('div', { class: 'card-b' },
        el('ul', { class: 'map-issues' }, list.map(function (b) {
          var target = blockerTarget(view, b);
          return el('li', {}, target
            ? el('button', { class: 'btn btn-ghost btn-sm map-jump', on: { click: function () { jump(view, target); } } }, b)
            : el('span', { class: 'small' }, b));
        }))));
  }

  function tablesCard(view, blockersNow) {
    var cx = view.context;
    var rows = [];
    (cx.target || []).forEach(function (t) {
      var map = findKey(view.work.tables, t.key);
      var st = tableStatus(t.key, map, blockersNow, cx.autoAccept);
      if (view.filter !== 'all' && st !== view.filter) return;
      var open = !!view.expanded[t.key];
      var toggle = function () { view.expanded[t.key] = !view.expanded[t.key]; redraw(view); };
      var nameCell = el('td', { class: 'mono' }, t.key);
      view.ctx.commentable(nameCell, 'tablemap:' + t.key, t.key);
      var tr = el('tr', { class: 'tr-click map-row' + (open ? ' is-active' : ''), data: { key: t.key } },
        el('td', { class: 'map-toggle-cell' },
          el('button', {
            class: 'btn btn-ghost btn-sm map-toggle', 'aria-expanded': String(open),
            'aria-label': (open ? 'Hide' : 'Show') + ' columns of ' + t.key, on: { click: toggle }
          }, open ? '▾' : '▸')),
        nameCell,
        el('td', { class: 'mono small' }, map && map.kind === 'skip' ? el('span', { class: 'muted' }, 'skipped') :
          map && map.sources && map.sources.length ? map.sources.join(', ') : el('span', { class: 'muted' }, 'none')),
        el('td', {}, el('span', { class: 'pill map-kind' }, map ? map.kind : '—')),
        el('td', {}, map ? bar(map.confidence) : el('span', { class: 'muted small' }, '—')),
        el('td', {}, methodTag(map && map.method)),
        el('td', {}, statusPill(st)));
      tr.addEventListener('click', function (e) {
        if (!e.target.closest('button, a, input, textarea, select, .commentable button')) toggle();
      });
      rows.push(tr);
      if (open) rows.push(el('tr', { class: 'map-detail' }, el('td', { colspan: '7' }, columnsPanel(view, t, map, blockersNow))));
    });
    var filters = el('div', { class: 'row map-filter', role: 'group', 'aria-label': 'Filter tables' },
      [['all', 'All'], ['attention', 'Needs review'], ['blocker', 'Blockers']].map(function (f) {
        return el('button', {
          class: 'chip' + (view.filter === f[0] ? ' is-active' : ''), 'aria-pressed': String(view.filter === f[0]),
          on: { click: function () { view.filter = f[0]; redraw(view); } }
        }, f[1]);
      }));
    var head = el('thead', {}, el('tr', {},
      el('th', { scope: 'col' }, el('span', { class: 'map-sr' }, 'Expand')), el('th', { scope: 'col' }, 'Target'),
      el('th', { scope: 'col' }, 'Source'), el('th', { scope: 'col' }, 'Kind'), el('th', { scope: 'col' }, 'Confidence'),
      el('th', { scope: 'col' }, 'Method'), el('th', { scope: 'col' }, 'Status')));
    var body = rows.length
      ? el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-sticky map-grid' }, head, el('tbody', {}, rows)))
      : DBM.components.emptyState('No tables match this filter', 'Choose "All" to see every target table.');
    return el('section', { class: 'card' },
      el('div', { class: 'card-h row row-wrap' }, el('div', { class: 'h3' }, 'Tables'), el('span', { class: 'spacer' }), filters),
      el('div', { class: 'card-b' }, body));
  }

  function columnsPanel(view, t, map, blockersNow) {
    var parts = [];
    if (!map || map.kind === 'skip' || !map.sources || !map.sources.length) parts.push(sourcePicker(view, t, map));
    if (map && map.kind === 'skip') {
      parts.push(el('p', { class: 'muted small' }, 'This target table is skipped' + (map.rationale ? ': ' + map.rationale : '.')));
      return el('div', { class: 'stack map-colpanel' }, parts);
    }
    if (map && map.sources && map.sources.length) parts.push(tableFields(view, t, map));
    var effective = map || { columns: {}, sources: [] };
    var head = el('thead', {}, el('tr', {}, ['Target column', 'Expression', 'Source columns', 'Default', 'Confidence', 'Method', 'Type risk', 'Candidates']
      .map(function (x) { return el('th', { scope: 'col' }, x); })));
    parts.push(el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-compact map-cols' }, head,
      el('tbody', {}, t.columns.map(function (c) { return columnRow(view, t, effective, c, blockersNow); })))));
    return el('div', { class: 'stack map-colpanel' }, parts);
  }

  function sourcePicker(view, t, map) {
    if (!view.editable) return el('p', { class: 'muted small' }, map && map.kind === 'skip' ? '' : 'No source table is mapped to this target yet.');
    if (map && map.kind === 'skip') {
      return el('div', { class: 'row' }, el('button', {
        class: 'btn btn-sm', on: { click: function () { editTable(view.work, t.key, { kind: 'direct', rationale: '' }); redraw(view); } }
      }, 'Stop skipping'));
    }
    var select = el('select', { class: 'select', 'aria-label': 'Source table for ' + t.key },
      el('option', { value: '' }, 'Choose a source table…'),
      (view.context.source || []).map(function (s) { return el('option', { value: s.key }, s.key + ' (' + DBM.fmt.num(s.rows) + ' rows)'); }));
    return el('div', { class: 'row row-wrap map-pick' },
      el('span', { class: 'small' }, 'No source table yet.'),
      select,
      el('button', {
        class: 'btn btn-sm btn-primary',
        on: { click: function () { if (!select.value) return; setSource(view.work, t.key, select.value); view.expanded[t.key] = true; redraw(view); } }
      }, 'Use as source'),
      el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { skipFlow(view, t.key); } } }, 'Skip table…'));
  }

  function tableFields(view, t, map) {
    if (!view.editable) {
      return el('div', { class: 'stack map-tablefields-ro' },
        map.from ? el('div', {}, el('span', { class: 'muted small' }, 'FROM '), el('code', { class: 'mono small' }, map.from)) : null,
        map.filter ? el('div', {}, el('span', { class: 'muted small' }, 'WHERE '), el('code', { class: 'mono small' }, map.filter)) : null,
        map.rationale ? el('div', { class: 'muted small' }, map.rationale) : null);
    }
    function field(label, control) { return el('label', { class: 'stack map-field' }, el('span', { class: 'small muted' }, label), control); }
    function input(value, placeholder, apply) {
      var i = el('input', { class: 'input mono', value: value, placeholder: placeholder, spellcheck: 'false', on: { input: function () { apply(i.value); updateBar(view); } } });
      return i;
    }
    var kind = el('select', { class: 'select', on: { change: function () { editTable(view.work, t.key, { kind: kind.value }); updateBar(view); } } },
      ['direct', 'merge', 'lookup'].map(function (k) { var o = el('option', { value: k }, k); if (map.kind === k) o.selected = true; return o; }));
    return el('div', { class: 'map-tablefields' },
      field('Kind', kind),
      field('Sources (first is alias s)', input((map.sources || []).join(', '), 'dbo.TABLE, dbo.LOOKUP', function (v) { editTable(view.work, t.key, { sources: v }); })),
      field('FROM clause (optional)', input(map.from || '', '[dbo].[A] AS s JOIN [dbo].[B] AS b ON b.[ID] = s.[B_ID]', function (v) { editTable(view.work, t.key, { from: v }); })),
      field('WHERE predicate (optional)', input(map.filter || '', 's.[DELETED] = 0', function (v) { editTable(view.work, t.key, { filter: v }); })),
      el('div', { class: 'small muted map-hint' }, 'Expressions use alias s for the first source; declare other aliases with JOINs in the FROM clause.'));
  }

  function columnRow(view, t, map, c, blockersNow) {
    var cm = findKey(map.columns || {}, c.name);
    var fixed = c.computed || c.rowversion;
    var st = fixed ? 'ok' : columnStatus(t.key, c.name, cm, blockersNow, view.context.autoAccept);
    var nameCell = el('td', { class: 'map-colname-cell', data: { label: 'Target column' } },
      el('div', { class: 'map-col-name' }, c.name),
      el('div', { class: 'map-col-type' }, (c.type || '') + (c.nullable ? '' : ' not null')),
      el('div', { class: 'map-col-flags' },
        c.identity ? flagTag('identity') : null, c.computed ? flagTag('computed') : null,
        c.rowversion ? flagTag('rowversion') : null, c.hasDefault ? flagTag('default') : null,
        st !== 'ok' ? statusPill(st) : null));
    view.ctx.commentable(nameCell, 'colmap:' + t.key + '.' + c.name, t.key + '.' + c.name);
    if (fixed) {
      return el('tr', { class: 'map-fixed' }, nameCell, el('td', { colspan: '7', class: 'muted small', data: { label: 'Note' } },
        c.computed ? 'Computed by the target; never written.' : 'Rowversion; generated by the server.'));
    }
    var exprCell;
    var srcCell;
    var defCell;
    if (view.editable) {
      var changed = function () { ops.restoreIfUnchanged(view.before, view.work, t.key, c.name); updateBar(view); };
      var ta = el('textarea', {
        class: 'textarea mono map-expr', rows: '1', spellcheck: 'false', 'aria-label': 'Expression for ' + c.name,
        placeholder: 'unmapped (e.g. s.[COLUMN])',
        on: { input: function () { editColumn(view.work, t.key, c.name, { expr: ta.value }); autoGrow(ta); changed(); } }
      }, (cm && cm.expr) || '');
      setTimeout(function () { autoGrow(ta); }, 0);
      var sc = el('input', {
        class: 'input mono map-srccols', value: ((cm && cm.sourceColumns) || []).join(', '), spellcheck: 'false',
        'aria-label': 'Source columns for ' + c.name, placeholder: 'schema.table.column, …',
        on: { input: function () { editColumn(view.work, t.key, c.name, { sourceColumns: sc.value }); changed(); } }
      });
      var df = el('input', {
        class: 'input mono map-default', value: (cm && cm['default']) || '', spellcheck: 'false',
        'aria-label': 'Default for ' + c.name, placeholder: 'e.g. 0 or N\'\'',
        on: { input: function () { editColumn(view.work, t.key, c.name, { 'default': df.value }); changed(); } }
      });
      exprCell = el('td', { data: { label: 'Expression' } }, ta);
      srcCell = el('td', { data: { label: 'Source columns' } }, sc);
      defCell = el('td', { data: { label: 'Default' } }, df);
    } else {
      exprCell = el('td', { data: { label: 'Expression' } }, cm && cm.expr ? el('code', { class: 'mono small map-code' }, cm.expr) : el('span', { class: 'muted small' }, 'unmapped'));
      srcCell = el('td', { class: 'mono small', data: { label: 'Source columns' } }, ((cm && cm.sourceColumns) || []).join(', ') || '—');
      defCell = el('td', { class: 'mono small', data: { label: 'Default' } }, (cm && cm['default']) || '—');
    }
    return el('tr', { class: 'map-colrow map-row-' + st }, nameCell, exprCell, srcCell, defCell,
      el('td', { data: { label: 'Confidence' } }, cm ? bar(cm.confidence) : el('span', { class: 'muted small' }, '—')),
      el('td', { data: { label: 'Method' } }, methodTag(cm && cm.method)),
      el('td', { data: { label: 'Type risk' } }, cm && cm.typeRisk ? el('span', { class: 'tag map-risk', title: cm.typeRisk }, cm.typeRisk) : el('span', { class: 'muted small' }, '—')),
      el('td', { data: { label: 'Candidates' } }, candidatesControl(view, t, map, c, cm)));
  }

  function candidatesControl(view, t, map, c, cm) {
    var list = (cm && cm.candidates) || [];
    if (!list.length) return el('span', { class: 'muted small' }, '—');
    var primary = map.sources && map.sources[0] ? String(map.sources[0]).toLowerCase() : '';
    var wrap;
    var btn;
    function onDoc(e) { if (wrap && !wrap.contains(e.target)) close(); }
    function onKey(e) { if (e.key === 'Escape') { close(); btn.focus(); } }
    function open() {
      pop.hidden = false;
      btn.setAttribute('aria-expanded', 'true');
      document.addEventListener('mousedown', onDoc);
      document.addEventListener('keydown', onKey);
      var first = pop.querySelector('button');
      if (first) first.focus();
    }
    function close() {
      pop.hidden = true;
      btn.setAttribute('aria-expanded', 'false');
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    }
    var pop = el('div', { class: 'map-pop', role: 'dialog', 'aria-label': 'Candidates for ' + c.name },
      el('div', { class: 'small muted' }, 'Other possible sources'),
      list.map(function (cand) {
        var usable = view.editable && splitColumnKey(cand.source).table.toLowerCase() === primary;
        return el('div', { class: 'map-pop-item' },
          el('div', { class: 'row' }, el('span', { class: 'mono small ellipsis', title: cand.source }, cand.source), el('span', { class: 'spacer' }), bar(cand.score)),
          el('div', { class: 'muted small' }, cand.why),
          usable ? el('div', { class: 'row' }, el('span', { class: 'spacer' }), el('button', {
            class: 'btn btn-sm', on: { click: function () { useCandidate(view.work, t.key, c.name, cand.source); close(); redraw(view); } }
          }, 'Use this')) : null);
      }));
    pop.hidden = true;
    btn = el('button', {
      class: 'btn btn-ghost btn-sm', 'aria-haspopup': 'dialog', 'aria-expanded': 'false', 'aria-label': list.length + ' candidates for ' + c.name,
      on: { click: function (e) { e.stopPropagation(); if (pop.hidden) open(); else close(); } }
    }, list.length + ' ▾');
    wrap = el('span', { class: 'map-popwrap' }, btn, pop);
    return wrap;
  }

  function uncoveredCard(view) {
    var cx = view.context;
    var list = ops.uncovered(cx, view.work);
    var head = el('div', { class: 'card-h row' }, el('div', { class: 'h3' }, 'Unmapped source columns'), el('span', { class: 'spacer' }), el('span', { class: 'chip' }, String(list.length)));
    if (!cx.source || !cx.source.length) {
      return el('section', { class: 'card', id: 'map-uncovered' }, head,
        el('div', { class: 'card-b' }, DBM.components.emptyState('Source catalog not available', 'This view only shows the mapping payload.')));
    }
    var byTable = {};
    list.forEach(function (key) { var p = splitColumnKey(key); (byTable[p.table] = byTable[p.table] || []).push(p.column); });
    var rows = [];
    cx.source.forEach(function (s) {
      var miss = byTable[s.key];
      if (!miss) return;
      var whole = miss.length === s.columns.length;
      rows.push(el('tr', { class: 'map-group-h' },
        el('td', { colspan: '2', class: 'mono' }, s.key + (whole ? ' — whole table unmapped' : '')),
        el('td', { class: 'map-actions' }, view.editable && whole
          ? el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { dropFlow(view, s.key, true); } } }, 'Drop table…') : null)));
      miss.forEach(function (col) {
        var key = s.key + '.' + col;
        var info = s.columns.filter(function (x) { return x.name === col; })[0] || {};
        var nameCell = el('td', { class: 'mono' }, col);
        view.ctx.commentable(nameCell, 'column:src:' + key, key);
        rows.push(el('tr', {}, nameCell,
          el('td', { class: 'mono small muted' }, (info.type || '') + (info.nullable === false ? ' not null' : '')),
          el('td', { class: 'map-actions' }, view.editable ? [
            el('button', { class: 'btn btn-sm', on: { click: function () { mapFlow(view, key); } } }, 'Map to…'),
            el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { dropFlow(view, key, false); } } }, 'Drop…')
          ] : null)));
      });
    });
    var body = rows.length
      ? el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-compact map-uncovered' },
        el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, 'Source column'), el('th', { scope: 'col' }, 'Type'), el('th', { scope: 'col' }, 'Actions'))),
        el('tbody', {}, rows)))
      : DBM.components.emptyState('Every source column is mapped or dropped', 'Nothing on the source side blocks approval.');
    return el('section', { class: 'card', id: 'map-uncovered' }, head, el('div', { class: 'card-b' }, body));
  }

  function dropsCard(view) {
    var drops = view.work.drops || {};
    var keys = Object.keys(drops).sort();
    var rows = keys.map(function (k) {
      var d = drops[k];
      var cell = el('td', { class: 'mono' }, k);
      view.ctx.commentable(cell, (k.split('.').length > 2 ? 'column:src:' : 'table:src:') + k, k);
      return el('tr', {}, cell, el('td', { class: 'small' }, d.reason), el('td', {}, methodTag(d.method)),
        el('td', { class: 'map-actions' }, view.editable
          ? el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { undropSource(view.work, k); redraw(view); } } }, 'Undo') : null));
    });
    var body = rows.length
      ? el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-compact map-drops' },
        el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, 'Source'), el('th', { scope: 'col' }, 'Reason'), el('th', { scope: 'col' }, 'Method'), el('th', { scope: 'col' }, 'Actions'))),
        el('tbody', {}, rows)))
      : DBM.components.emptyState('No drops', 'Source tables and columns that are intentionally not migrated appear here.');
    return el('section', { class: 'card' },
      el('div', { class: 'card-h row' }, el('div', { class: 'h3' }, 'Drops'), el('span', { class: 'spacer' }), el('span', { class: 'chip' }, String(keys.length))),
      el('div', { class: 'card-b' }, body));
  }

  function dropFlow(view, key, wholeTable) {
    var input = el('input', { class: 'input', placeholder: 'Why is it safe not to migrate this?', 'aria-label': 'Reason for dropping ' + key });
    var body = el('div', { class: 'stack' },
      el('p', { class: 'small' }, wholeTable ? 'Drop every column of this source table:' : 'Drop this source column:'),
      el('code', { class: 'mono' }, key),
      input);
    DBM.components.modal({ title: wholeTable ? 'Drop source table' : 'Drop source column', body: body, confirmText: 'Drop' }).then(function (ok) {
      if (!ok) return;
      if (blank(input.value)) { view.ctx.toast('A drop needs a reason.', 'warn'); return; }
      dropSource(view.work, key, input.value);
      redraw(view);
    });
  }

  function skipFlow(view, tableKey) {
    var input = el('input', { class: 'input', placeholder: 'Why is this target table not loaded?', 'aria-label': 'Reason for skipping ' + tableKey });
    DBM.components.modal({ title: 'Skip target table', body: el('div', { class: 'stack' }, el('code', { class: 'mono' }, tableKey), input), confirmText: 'Skip' }).then(function (ok) {
      if (!ok) return;
      if (blank(input.value)) { view.ctx.toast('Skipping a table needs a reason.', 'warn'); return; }
      skipTable(view.work, tableKey, input.value.trim());
      redraw(view);
    });
  }

  function mapFlow(view, key) {
    var choices = mapTargetsFor(view.context, view.work, key);
    if (!choices.length) {
      view.ctx.toast('No target table uses ' + splitColumnKey(key).table + ' as its primary source. Pick it as the source of a target table first.', 'warn');
      return;
    }
    var select = el('select', { class: 'select', 'aria-label': 'Target column for ' + key },
      choices.map(function (o, i) {
        return el('option', { value: String(i) }, o.table + '.' + o.column + ' — ' + (o.type || '?') + (o.mapped ? ' (replaces current)' : ''));
      }));
    var body = el('div', { class: 'stack' },
      el('p', { class: 'small muted' }, 'The expression becomes s.' + quoteName(splitColumnKey(key).column) + '. Refine it afterwards in the column table.'),
      select);
    DBM.components.modal({ title: 'Map ' + key, body: body, confirmText: 'Map' }).then(function (ok) {
      if (!ok) return;
      var o = choices[Number(select.value)];
      try {
        mapToTarget(view.work, key, o.table, o.column);
        view.expanded[o.table] = true;
        redraw(view);
      } catch (e) {
        view.ctx.toast(e.message, 'err');
      }
    });
  }

  function saveBar(view) {
    var status = el('span', { class: 'small', 'aria-live': 'polite' });
    var discard = el('button', { class: 'btn btn-ghost', on: { click: function () { view.work = clone(view.before); redraw(view); } } }, 'Discard');
    var save = el('button', { class: 'btn btn-primary', on: { click: function () { saveEdits(view); } } }, 'Save as new version');
    view.bar = { status: status, discard: discard, save: save };
    updateBar(view);
    return el('div', { class: 'toolbar map-savebar' }, status, el('span', { class: 'spacer' }), discard, save);
  }

  function updateBar(view) {
    if (!view.bar) return;
    var n = changeCount(diff(view.before, view.work));
    view.bar.status.textContent = n ? n + ' unsaved change' + (n === 1 ? '' : 's') : 'No unsaved changes';
    view.bar.status.classList.toggle('map-dirty', n > 0);
    view.bar.save.disabled = n === 0 || view.saving;
    view.bar.discard.disabled = n === 0 || view.saving;
  }

  function saveEdits(view) {
    var list = diff(view.before, view.work);
    if (!list.length || view.saving) return;
    view.saving = true;
    view.bar.save.classList.add('is-loading');
    updateBar(view);
    var n = changeCount(list);
    var patch = {
      phase: 'mapping', baseVersion: view.ctx.version, ops: list, responses: [],
      summary: 'Human edit: ' + n + ' change' + (n === 1 ? '' : 's') + ' in the mapping screen'
    };
    function failed(message) {
      view.saving = false;
      if (view.bar) { view.bar.save.classList.remove('is-loading'); updateBar(view); }
      view.ctx.toast('Not saved: ' + message, 'err');
    }
    view.ctx.api.post('/api/edit/mapping', patch).then(function (res) {
      if (res && res.ok === false) { failed((res.errors || []).join('; ') || 'rejected'); return; }
      view.saving = false;
      var open = (res && res.warnings) || [];
      view.ctx.toast('Saved as v' + res.version + (open.length ? ' — ' + open.length + ' open item' + (open.length === 1 ? '' : 's') : ''), 'ok');
      view.ctx.refresh();
    }).catch(function (e) {
      var details = e && e.details && e.details.length ? ' (' + e.details.join('; ') + ')' : '';
      failed(((e && e.message) || 'request failed') + details);
    });
  }

  function isDirty(view) { return !!view && !!view.context && diff(view.before, view.work).length > 0; }

  function onEvent(evt, ctx) {
    var type = evt && evt.type;
    var data = (evt && (evt.data || evt.payload)) || {};
    if (type !== 'artifact_created' || data.phase !== 'mapping') return;
    if (isDirty(current)) ctx.toast('A new mapping version was created. Save or discard your edits, then reload the view.', 'warn');
    else ctx.refresh();
  }

  DBM.views = DBM.views || {};
  DBM.views.mapping = { title: 'Mapping', render: render, onEvent: onEvent };
})(window.DBM = window.DBM || {});
```

- [ ] **Step 8: Run the node test to verify it passes**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/mapping-ops.test.cjs`
Expected: PASS — `ℹ tests 18` … `ℹ pass 18`, `ℹ fail 0`.

- [ ] **Step 9: Wire the view into the shell (script tag + styles)**

In `plugins/db-migrate/engine/Dbm/wwwroot/index.html`, in the view-scripts block (the `<script src="js/views/…">` lines that come before `<script src="js/app.js"></script>`), add directly after the analysis view line:

```html
    <script src="js/views/analysis.js"></script>
    <script src="js/views/mapping.js"></script>
```

(only the second line is new).

Append this block at the **very end** of `plugins/db-migrate/engine/Dbm/wwwroot/css/app.css` (it only uses the C9 tokens, so light and dark themes follow automatically; `.map-pop[hidden]` is needed because `.map-pop` sets `display`):

```css
/* ---- Mapping view (T3.5, js/views/mapping.js) ---- */
.map-page, .map-page .stack > *, .map-page .card, .map-page .card-b { min-width: 0; }
.map-scroll { max-width: 100%; overflow-x: auto; -webkit-overflow-scrolling: touch; }
.map-pop[hidden] { display: none; }
.map-grid th, .map-grid td { white-space: nowrap; vertical-align: middle; }
.map-grid .map-toggle-cell { width: 2.5rem; }
.map-grid .map-row.is-active > td { background: var(--surface-2); }
.map-detail > td { background: var(--surface-2); padding: 12px 16px; white-space: normal; }
.map-colpanel { gap: 12px; }
.map-cols { background: var(--surface); }
.map-cols td { vertical-align: top; white-space: normal; }
.map-cols .map-expr { width: 100%; min-width: 16rem; min-height: 2.25rem; resize: vertical; font-family: var(--font-mono); font-size: 12px; line-height: 1.5; }
.map-cols .map-srccols, .map-cols .map-default { width: 100%; min-width: 9rem; font-family: var(--font-mono); font-size: 12px; }
.map-code { display: block; white-space: pre-wrap; word-break: break-word; }
.map-col-name { font-weight: 600; }
.map-col-type { color: var(--text-muted); font-family: var(--font-mono); font-size: 12px; }
.map-col-flags { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 4px; }
.map-flag { font-size: 11px; }
.map-fixed td { color: var(--text-muted); }
.map-conf { display: inline-flex; align-items: center; gap: 8px; min-width: 7rem; }
.map-conf .map-bar { flex: 1 1 auto; min-width: 3.5rem; }
.map-st { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; font-weight: 600; white-space: nowrap; }
.map-st::before { content: ""; flex: none; width: 8px; height: 8px; border-radius: 50%; background: currentColor; }
.map-st-ok { color: var(--ok); }
.map-st-attention { color: var(--warn); }
.map-st-blocker { color: var(--err); }
.map-m { display: inline-flex; align-items: center; gap: 4px; font-size: 11px; text-transform: lowercase; }
.map-m::before { content: ""; flex: none; width: 6px; height: 6px; border-radius: 50%; background: var(--border); }
.map-m-exact::before { background: var(--ok); }
.map-m-fuzzy::before { background: var(--info); }
.map-m-vector::before { background: var(--warn); }
.map-m-agent::before, .map-m-human::before { background: var(--accent); }
.map-m-carried::before { background: var(--text-muted); }
.map-kind { font-size: 11px; text-transform: lowercase; }
.map-risk { display: inline-block; max-width: 14rem; color: var(--warn); font-size: 11px; white-space: normal; }
.map-popwrap { position: relative; display: inline-block; }
.map-pop { position: absolute; top: calc(100% + 4px); right: 0; z-index: 20; display: grid; gap: 8px; width: min(22rem, 80vw); padding: 8px;
  background: var(--surface); color: var(--text); border: 1px solid var(--border); border-radius: var(--radius); box-shadow: var(--shadow);
  text-align: left; white-space: normal; }
.map-pop-item { display: grid; gap: 4px; padding: 8px; border-radius: calc(var(--radius) - 2px); background: var(--surface-2); }
.map-issues { display: grid; gap: 4px; margin: 0; padding: 0; list-style: none; }
.map-issues .map-jump { height: auto; text-align: left; white-space: normal; }
.map-blockers { border-color: var(--err); }
.map-filter { gap: 4px; }
.map-group-h > td { background: var(--surface-2); font-weight: 600; }
.map-actions { text-align: right; white-space: nowrap; }
.map-actions .btn + .btn { margin-left: 4px; }
.map-tablefields { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 8px 12px; }
.map-field { gap: 4px; }
.map-hint { grid-column: 1 / -1; }
.map-pick { gap: 8px; align-items: center; }
.map-note { margin: 0; }
.map-savebar { position: sticky; bottom: 0; z-index: 10; margin-top: 8px; padding: 8px 16px; background: var(--surface);
  border: 1px solid var(--border); border-radius: var(--radius); box-shadow: var(--shadow); }
.map-dirty { color: var(--warn); font-weight: 600; }
.map-sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
@media (max-width: 640px) {
  .map-cols thead { display: none; }
  .map-cols, .map-cols tbody, .map-cols tr, .map-cols td { display: block; width: 100%; box-sizing: border-box; }
  .map-cols tr { padding: 8px 0; border-bottom: 1px solid var(--border); }
  .map-cols td { padding: 4px 0; border: 0; }
  .map-cols td[data-label]::before { content: attr(data-label); display: block; margin-bottom: 2px; font-size: 11px; letter-spacing: .04em;
    text-transform: uppercase; color: var(--text-muted); }
  .map-cols .map-expr, .map-cols .map-srccols, .map-cols .map-default { min-width: 0; }
  .map-detail > td { padding: 8px; }
  .map-pop { right: auto; left: 0; }
}
```

- [ ] **Step 10: Manual verification (light + dark, desktop and ~400 px)**

Prepare a project that reaches Mapping `awaiting_review` (PowerShell, from the repository root):

```powershell
dotnet build plugins/db-migrate/engine/Dbm
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "CREATE DATABASE LegacyShopM3; CREATE DATABASE ShopV2M3;"
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d LegacyShopM3 -i plugins/db-migrate/samples/legacyshop-to-shopv2/legacyshop.sql
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d LegacyShopM3 -i plugins/db-migrate/samples/legacyshop-to-shopv2/seed.sql -v scale=1
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d ShopV2M3 -i plugins/db-migrate/samples/legacyshop-to-shopv2/shopv2.sql
New-Item -ItemType Directory -Force $env:TEMP\dbm-m3-ui | Out-Null; Set-Location $env:TEMP\dbm-m3-ui
dbm init m3-ui
```

In the browser tab that opens, save the connection strings `Server=(localdb)\MSSQLLocalDB;Database=LegacyShopM3;Integrated Security=true;TrustServerCertificate=true` (source) and the same with `Database=ShopV2M3` (target). In Claude Code, in that folder, run `/db-migrate` and let it work; approve Analysis in the UI when asked. Continue until the stepper shows **Mapping · awaiting review** (the mapping-architect has produced v1). Then check, with the browser at full width and again with DevTools device toolbar at **400 × 800**, each in light and dark theme (toggle the app theme or the OS setting):

- [ ] The Mapping view renders under the review bar: header `vN · agent · <summary>`, five KPI tiles, no console errors.
- [ ] The tables grid lists the 6 ShopV2 tables with source, kind pill, confidence bar filled to the percentage shown, method tag with coloured dot, status (OK green / Needs review amber / Blocker red); filter chips narrow the list and show `aria-pressed`.
- [ ] Clicking a row or its ▸ button (also via keyboard: Tab to it, Enter) expands it; ▾ collapses it; `aria-expanded` follows.
- [ ] In the column table, `DisplayName` (computed) and `RowVer` (rowversion) are greyed with a note and no inputs; identity/default flags appear; type risks show in amber.
- [ ] Editing an expression shows `1 unsaved change`; typing it back to the original returns to `No unsaved changes`; Discard restores the saved version.
- [ ] The candidates button opens a popover only for that column (all others stay closed), with score bars and `why`; Escape closes it and focus returns to the button; `Use this` fills `s.[COL]` and the source columns.
- [ ] Removing a drop with `Undo` makes the column appear under **Unmapped source columns** and a new blocker appears immediately; `Drop…` requires a reason (empty reason → toast), `Map to…` lists only eligible target columns and maps with `s.[COL]`; `Drop table…` appears when every column of a table is unmapped.
- [ ] Clicking a blocker jumps to (and expands) its table, or scrolls to the unmapped panel for source blockers.
- [ ] Save as new version → toast `Saved as vN+1`, the view reloads the new version, the version picker lists it with author `human`, and the diff view shows the change.
- [ ] Put `dbo.CUST.NOPE` in a Source columns field and Save → an error toast names `unknown source column 'dbo.CUST.NOPE'` and the edits stay on screen.
- [ ] The comment button appears on target table cells (`tablemap:`), column-name cells (`colmap:`), unmapped source columns and drops (`column:src:` / `table:src:`); a draft comment lands in the feedback drawer with that anchor.
- [ ] Selecting an older version in the version picker shows the mapping read-only (expressions as code, no inputs, no save bar, the "Direct edits are available…" note).
- [ ] At 400 px: the page never scrolls horizontally (only the tables scroll inside their cards), KPI tiles wrap, the column table stacks into labelled fields, the popover stays inside the screen, the save bar stays reachable at the bottom.
- [ ] Dark theme: all text, bars, pills, popover and save bar use theme colours (no white panels or unreadable text).

Clean up: `sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "DROP DATABASE LegacyShopM3; DROP DATABASE ShopV2M3;"`, `dbm stop`, delete `%TEMP%\dbm-m3-ui`.

- [ ] **Step 11: Run all M3 tests**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`
Expected: PASS, 0 failures.

Run: `node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"` (Node 24 needs the glob form)
Expected: PASS, 0 failures (includes the earlier milestones' JS tests).

- [ ] **Step 12: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Web/Endpoints/MappingEndpoints.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs plugins/db-migrate/engine/Dbm/wwwroot/js/views/mapping.js plugins/db-migrate/engine/Dbm/wwwroot/index.html plugins/db-migrate/engine/Dbm/wwwroot/css/app.css plugins/db-migrate/engine/Dbm.Tests/Unit/Web/MappingEndpointsTests.cs plugins/db-migrate/engine/Dbm.Tests/js/mapping-ops.test.cjs
git commit -F - <<'EOF'
feat(ui): mapping screen with direct edits and mapping context endpoint (T3.5)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

## Contract notes

**Additions to the shared contracts (allowed: new members/types, no renames).**
- `MatchOptions` (C11) lives in its own file `Dbm/Core/Matching/MatchOptions.cs`, created in T3.2 because `MappingValidator.Attention` needs it before T3.3.
- New public members: `ColumnType.Parse`, `TypeCompat.Family` (T3.1); `MappingValidator.Kinds/NeedsReview/UncoveredSourceColumns/ResolveSourceColumn/FindTableMap/FindColumnMap` (T3.2); `AutoMapper.MinNameForAccept/MinNameForCandidate/MinTableCandidateScore` (T3.3); `MappingEndpoints.BuildContext` (T3.5).
- New types/files beyond the overview file map: `Dbm/Core/Matching/MatchContext.cs`, `ColumnScorer.cs` (`ColumnScore`), `TableScorer.cs` (`TableScore`), `MappingCarryOver.cs`, `MatchOptions.cs`; `Dbm/Core/Mapping/MappingPacket.cs`; test support `MappingTestData.cs` (extensions on `TempProject.Services`), `AutoMapperBaseline.cs`, `SampleExtract.cs`, `SampleCatalogs.cs`, `SampleMappings.cs`; JS `DBM.mappingOps` inside `views/mapping.js`.
- `GET /api/mapping/context` returns, besides the fields named in the task brief, `version`, `autoAccept`, `candidate`, `uncovered`, `source[].rows` and `target[].columns[].rowversion`.
- Summaries: `automap` job → `"N tables mapped, A need attention, B blockers"`; `MappingModule.Summarize` → `"N tables, M columns mapped, D drops, A attention, B blockers"`.

**Behavioural decisions (refinements of the brief, all tested).**
- A column pair is accepted only when its score ≥ `Candidate` **and** its name cosine ≥ 0.3. Without this rule, type + structure + profile compatibility alone reached 0.53, and junk pairs would fill every leftover target column.
- Candidates are listed only with name ≥ 0.1 or score ≥ `Candidate`; table candidates only with score ≥ 0.2, which keeps packets small. Identity target columns only accept columns from the source table's `BestKey()`; when none match, the column gets no `ColumnMap` entry.
- Extra validator rules: a non-`skip` map without `Sources` is a **blocker**. **Errors** cover keys in the wrong case, writes to computed/rowversion columns, blank `expr`, and drop keys that don't resolve or have no reason. Attention and blocker messages are exact strings (table in T3.2) that the UI mirrors.
- Carry-over also keeps a table's structure (`Kind`/`Sources`/`From`/`Filter`) when the table has kept columns, because agent expressions depend on the `From` aliases. A carried table's remaining columns are auto-assigned.
- TypeCompat returns Incompatible only for pairs SQL Server cannot convert at all; implicit or `CAST`-able pairs are Widening or Risky (int→bit is Risky). M4 can therefore map Incompatible → validation error and Risky → warning.
- Coverage convention: join keys used only in `From` must appear in the `SourceColumns` of a dependent column (the ground truth lists `dbo.ORD_HDR.STATUS_ID` under `Orders.StatusCode`). **M4 must not treat `SourceColumns` as a SELECT list.** It should use `TableMap.From` when present, else `[schema].[table] AS s`.

**Integration facts (verified by executing this plan on top of the M0–M2 code).**
- No upstream plan or code changes were needed; M3 only appends its lines to the M1/M2 registries, `EndpointRegistry`, `index.html` and `app.css`.
- `Json.Options` has `DictionaryKeyPolicy = null` (keys stay `app.Customers` / `FirstName`, which the patch paths need) and `UnsafeRelaxedJsonEscaping` (packets keep T-SQL quotes readable).
- M2's embedded `synonyms.json` covers the sample vocabulary; `ts` reaches `time` through `ts → timestamp` plus the `time`/`timestamp` group. `AutomapJob` loads `Synonyms.ForProject(ws)` (defaults + team + project files).
- `SampleCatalogs` matches `CatalogExtractor` exactly, and the fidelity test proves it. Conventions: rowversion `DataType` = `timestamp`; raw `sys.columns` precision/scale; `MaxLength` 0 for `text`; collation only on character columns; `TemporalType` null. It also carries the M2 additions `ColumnInfo.ComputedDefinition` and `TableInfo.TriggerNames`, and ORD_HDR has 3005 rows. `Fingerprint` excludes server metadata, rows, sizes, profiles and descriptions.
- `WorkflowEngine.RetryJob` accepts `running` and `drafting` (drafting goes back to running) and throws while a job for the phase is queued or running. `map auto` therefore reuses an active automap job instead of retrying. Commands open services with `ctx.OpenProject()` (`no_project` when there is none).
- Registries: `yield return` lines below the `// milestone registrations below` marker (Job/Module); `CommandRegistry` collection entry after `ShowCommand`; `EndpointRegistry` placeholder comment replaced.
- Tests use the M1/M2 helpers: `TempProject` (+ `MappingTestData` extensions), `TestWorkspace`, `CliRunner.RunAsync(ws, null, args)`, `WebTestServer.StartAsync(ws, ws => DbmServices.Open(ws))` (a request without the token gets 401), and `SamplePairFixture` for the two integration tests.
- UI: `ctx.artifact` is the shown version `{version, author, summary, createdAt, payload}`; `ctx.readOnly` is true unless the phase awaits review and the latest version is shown; toast kinds `ok | err | warn | info`; `modal({body})` takes a node; `DBM.h` handles `aria-*`, `data`, arrays and `--custom` style properties. `app.css` has no global `[hidden]` rule, so the view CSS adds `.map-pop[hidden]`.
- Standalone export (`window.DBM_EXPORT`) renders the mapping read-only from the payload alone, with no catalogs. M6 may add the context to the export to show column types.

## Self-review

- **Brief coverage.**
  - T3.1: every listed family and rule, with its risk text, is in the table-driven theory (182 cases). This includes the pairs M4 depends on: int→bit Risky, money→decimal(19,4) Widening, smallint→smallint Exact, datetime→datetime2(0) Risky, varchar(500)→nvarchar(200) Risky, datetime→date Risky, text→nvarchar(max) Widening, tinyint→int Widening.
  - T3.2: C12 is implemented verbatim. Errors, blockers and attention have exact messages. `SampleCatalogs` reproduces the C15 schemas (checked against a real LocalDB copy of the DDL: types, lengths, precision/scale, nullability, identity, computed, rowversion, defaults, PKs, FKs incl. the untrusted `FK_ORD_CUST`, trigger count, heaps). `SampleMappings.Approved()` is the C15 ground truth. The fidelity integration test is present.
  - T3.3: table pairing, empty-table drops, column scoring (all four components), greedy one-to-one assignment, method/typeRisk/candidates, computed/rowversion omission, identity key rule, carry-over, `automap` job, registration, `map auto` (`wrong_phase`, `--inline`). The C15 baseline runs as a unit test and as an integration test.
  - T3.4: module contract, draft/rework packets with anchor resolution, validation split into errors vs warnings, registration, and the agent file with frontmatter, the full playbook, worked examples and a complete example patch that a test applies with `JsonPatch`.
  - T3.5: context endpoint (unit and in-process HTTP tests), the full view (review bar, KPIs, blockers, table grid with expandable column editor, candidates popover, unmapped panel with Map to…/Drop…, drops card, save bar), anchors, ops builder with 18 node tests, CSS, script tag, and the manual checklist.
- **Placeholder scan.** No TBD/TODO. "…" appears only inside UI label strings (`Drop…`, `Loading mapping…`); none remain in code or JSON examples.
- **Name consistency.** Every type used by a later task is defined in an earlier one: `SampleExtract` (T3.2) is used in T3.3; `MappingTestData`/`AutoMapperBaseline` (T3.3) are used in T3.4/T3.5; `MappingPacket` (T3.4) is used by `MappingModule`. Namespaces follow folders.
- **Executed.** Every task was run in order on top of the M0–M2 integration build. The code blocks in this file are byte-identical to the files that passed.
  - Unit: 597 passed, 0 failed (335 from M0–M2 plus 262 for M3). This includes the baseline with the real M2 synonyms and vectoriser, the CLI tests, the workflow flow tests, and the in-process HTTP test.
  - LocalDB integration: 35 passed, 0 failed (33 upstream plus the SampleCatalogs fidelity test and the profiled-pair baseline).
  - Node: 36 passed (18 upstream plus 18 mapping-ops).
  - The agent's complete example patch applies with `JsonPatch` and leaves no errors or blockers.
- **Browser check.** `views/mapping.js` was rendered in a harness with stubbed `DBM` core at 1280 px and 400 px, in light and dark. It found two bugs, both fixed here: popovers ignored `hidden`, and the page overflowed at 400 px. The save path and read-only mode were exercised. The full-app manual checklist (Step 10) still needs a human.
- **Residual risks.** Real data beyond the sample may need more synonyms; teams extend `~/.dbmigrate/synonyms.json`, and the auto-mapper only proposes — the agent and the human decide.

---

## Task 3.5 — AMENDMENT (fix round 1): defects found in review

These are defects in the plan text above, not in the implementation. The committed code matches the original brief byte-for-byte; the brief was wrong. Everything below replaces or adds to the corresponding part of Task 3.5.

### A1 — The shell must never destroy unsaved human edits (replaces the dirty-guard design)

**INVARIANT (this is the spec; the triggers listed afterwards are examples, not an exhaustive list):**

> No code path may replace a rendered view whose `isDirty()` reports true, unless the user explicitly asked for it. If you find a path that reaches `renderView` and is not listed below, the invariant covers it too — fix it and say so.

The original text made `views/mapping.js` `onEvent` toast "Save or discard your edits, then reload the view." That guard cannot work: `app.js` `onEvent` runs the view's handler first and then calls `scheduleRefresh()` unconditionally, and `refresh()` re-renders whenever `viewKey` changes. `render()` rebuilds `view.work` from `ctx.artifact.payload` every time, so the edits are gone 150 ms after the toast.

Known triggers (again: examples): posting or deleting a comment (`components/review.js` calls `ctx.refresh()`, and a new feedback id changes `viewKey`); `artifact_created`; `agent_presence` flipping `agentOnline`; any job status change; any phase status change.

**`wwwroot/js/app.js`** — add the helper and use it in `refresh`:

```js
  function viewDirty() {
    var v = S.view;
    if (!v || typeof v.isDirty !== 'function') return false;
    try { return !!v.isDirty(S.ctx); } catch (err) { return false; }
  }
```

In `refresh`, replace the re-render block with:

```js
        var key = viewKey(row);
        if (force === true || key !== S.viewKey) {
          if (force !== true && viewDirty()) {
            // Keep the human's unsaved work. Do NOT advance S.viewKey: the render
            // happens on the next refresh after the edits are saved or discarded.
          } else {
            S.viewKey = key;
            renderView(row);
          }
        }
```

`renderChrome()` and `DBM.review.refresh(S.ctx)` continue to run in both branches — only the view re-render is skipped.

**`wwwroot/js/views/mapping.js`** — three changes that make the hook work:

1. Export the hook:

```js
  DBM.views.mapping = { title: 'Mapping', render: render, onEvent: onEvent, isDirty: function () { return isDirty(current); } };
```

2. **Save must clear dirty BEFORE refreshing.** In `saveEdits`, on success, set `view.before = clone(view.work);` before `view.ctx.refresh();`. Without this the new guard skips the post-save re-render and the user sits on a stale version after saving — the save path is the one case where dirty state must be cleared rather than protected.

3. **Discard must refresh.** The Discard handler becomes:

```js
  function () { view.work = clone(view.before); redraw(view); view.ctx.refresh(); }
```

so the view picks up any version that arrived while the user was editing.

`onEvent`'s toast stays: it is now true advice rather than dead code.

### A2 — A column carrying a `typeRisk` needs review (C# and JS must agree)

`needsReview` only flags `method === 'fuzzy' || 'vector'` below the auto-accept score, so an `exact` — or `human` — match carrying a data-loss hazard shows as OK, counts 0 in the KPI, and is hidden by the "Needs review" filter. The amber tag exists only inside the expanded row, which a reviewer triaging by filter never opens. The C# mirror has the identical gap.

- `wwwroot/js/views/mapping.js`: `columnStatus` returns `'attention'` when `cm && cm.typeRisk` is a non-empty string, in addition to the existing `needsReview` test. `attentionCount` counts such columns. `tableStatus` returns `'attention'` when any of its columns does.
- `Dbm/Core/Mapping/MappingValidator.cs`: `Attention` and `NeedsReview` gain the same rule, so the two implementations stay in step.
- Add a test on **both** sides asserting that a column with `method: 'exact'`, `confidence: 1` and a non-empty `typeRisk` is attention, not ok.

### A3 — Column names that collide with Object.prototype (scope: column level only)

`editColumn` (`t.columns[column] || (t.columns[column] = {...})`) and `restoreIfUnchanged` (`bt.columns[column]`, `at.columns[column]`) use raw bracket access. A target column named `__proto__` returns `Object.prototype` — truthy — so the edit writes onto the prototype, `diff` returns `[]` so the edit cannot be saved, and every object on the page then carries `expr`/`method`/`confidence`, corrupting `blockers()` and `needsReview()`. `toString`, `valueOf` and `constructor` yield built-ins and the edit is silently dropped. These are legal SQL Server identifiers and the schema is foreign input.

Fix with the idiom this file already uses: guard every column-map lookup with `Object.prototype.hasOwnProperty.call(...)` before treating a hit as a real entry.

**Do not touch `findKey`.** It already opens with `Object.prototype.hasOwnProperty.call(obj, key)` and then iterates `Object.keys`, so table-level lookup is prototype-safe. The defect is confined to the column level.

### A4 — mentions() must not match across a dot boundary (table level only)

`mentions(line, key)` tests `l.indexOf(k + '.') === 0`, so the blocker `"app.Customers.Old: no table mapping"` marks the unrelated, clean `app.Customers` as Blocker and `blockerTarget` can jump to the wrong row. Table names may legally contain dots. Match only when the character after the key begins a message (`':'`) or when the remainder is a single further segment that the line actually attributes to this table.

`columnStatus` is **not** affected — its prefix is `tableKey + '.' + column + ':'`, with the trailing colon, so `Name` cannot collide with `NameOld`. Leave it alone.

### A5 — /api/mapping/context must honour the error contract

- `?version=abc`: the handler binds `(int? version)`, so binding fails before the handler runs and the caller gets a bare 400, or a 500 `internal` through the error boundary in Development. Accept the raw query value and return `{"error":"bad_request","message":"..."}` — the exact shape the Global Constraints require.
- `?version=99` where no such version exists: currently 200 with `"version": null` and an empty `blockers` array, which reads as "nothing blocks approval". Return 404 `{"error":"not_found","message":"..."}`, matching the sibling `GET /api/artifact/{phase}/{version}`.
- Use `ApiResults.Json` with `Json.Options` rather than `Results.Content(node.ToJsonString(), "application/json")`, so this endpoint serialises the way every other one does.
- Add tests for both failure paths; neither is covered today.

### A6 — Two smaller corrections

- `contextFromPayload` hard-codes `autoAccept` as `0.85`. An exported page must use the project's real setting, or its statuses and Needs-review count disagree with the live screen whenever the setting differs.
- The context endpoint computes an `attention` value the view never reads. Either render it or stop computing it; do not ship a payload field with no consumer.

### Test-coverage requirements added by this amendment

- A test that fails if `typeRisk` stops rendering. There is none today.
- A test that fails if a dirty view is destroyed by a shell re-render — drive it through the comment-post path, which is the real workflow.
- The "blockers mirror MappingValidator" test currently compares hand-copied strings and never runs the C# validator and the JS against one fixture, so C#/JS drift goes unnoticed. Pin the two against a shared fixture.

### AMENDMENT CORRECTIONS (fix round 1, second pass)

These replace the corresponding parts of A1, A2, A3 and A4 above. They came from the reviewer checking the first pass at source. A5 and A6 are unchanged.

#### A3 corrected — the reason, and a missing site

The first pass credited `findKey` with making table level safe. That is wrong. `findKey` protects column **reads** only. Table keys are written with plain bracket access in `setSource` (~:748), `skipTable` (~:757) and `diff` (~:570-571, `t in at` / `t in bt`).

What actually protects them is the **dotted-key invariant**: a table key is always `schema.table`, so it always contains a dot and can never equal `__proto__`, `toString` or `constructor`. The same invariant protects `drops`, `covered`/`dropped`, `byTable`/`missing` and `view.expanded`. Cite the invariant, not `findKey`.

**Missing site, now in scope:** `diff` (~:578-579), `c in ac` / `c in bc`. For a removed column named `toString`, `in` is true through the prototype, so no remove op is emitted and the deletion silently fails to save. A3 therefore covers `editColumn`, `restoreIfUnchanged` **and** `diff`.

**Test design:** a prototype-key test must use a dotless **column** name. A test using a dotless **table** key would exercise a state that cannot occur; do not write one.

#### A4 corrected — concrete matching rule, and the regression trap

`tableStatus` must match `key + ':'` **or** `key + '.' + <a column of that target table> + ':'`, using the target column list from the context.

**Trap:** column blockers read `"app.Customers.FirstName: ..."`, and a table row must still turn red for its own column blockers. A fix that tests only `key + ':'` regresses that and breaks the existing passing test `statuses and attention follow the auto-accept band`. Deleting the `key + '.'` branch outright is the wrong fix.

**Required test pair:** (1) a blocker on `app.Customers.Old` must not mark `app.Customers`; (2) a genuine column blocker still does mark its table.

`columnStatus` stays out of this round but is **not** proven safe: a target table literally named `app.Customers.Name` yields a blocker that `columnStatus('app.Customers', 'Name')` matches. Known residual, deliberately deferred.

#### A2 corrected — the predicate must ignore method and confidence

The `typeRisk` check keys on a non-empty `typeRisk` **alone**. It must not be routed through `needsReview`, and must not carry any fuzzy/vector or confidence test. `needsReview` gates on `method` being `fuzzy` or `vector`, so reusing it leaves `exact` matches and `human` edits invisible — precisely the rows the deferred stale-risk finding produces.

Parity tests must cover **both** an `exact` row carrying a risk and a `human` row carrying a risk, on the C# and JS sides.

#### A1 corrected — two further paths

- A **failed** save (`res.ok === false`, or the catch path) must leave the view dirty. Clear `before` only on success. The guard must still block re-render while the failure toast is up, or the user loses the edits the save just failed to persist.
- **Approve** and **Request changes** from the review bar while dirty have no defined behaviour today, and the guard would strand the view on the old version after the phase moves on. While dirty, both must first make the user resolve the edits — save or discard — and after the action succeeds the view must re-render even though the guard would otherwise block it, because the phase has moved and the editable view is no longer valid. If `ctx.refresh` has no way to force a render, add one.

The A1 invariant is unchanged and still governs. These two paths are further examples, not a new exhaustive list.

---

## Task 3.4 — AMENDMENT (fix round 1): defects found in review

These are defects in the plan text above, not in the implementation. `MappingPacket.cs`, `MappingModule.cs`, `mapping-architect.md` and all four test files are byte-identical to the brief; the registry line is exactly as specified. The brief was wrong.

### B1 — A table carrying a type risk is NOT confident (highest priority)

**INVARIANT (this is the spec; everything after it is explanation):**

> A column's `TypeRisk` must reach BOTH the human and the AI agent, on every path, whenever it is present. No summarisation, confidence score, compatibility level or match method may cause it to be omitted.

`IsConfident` currently tests blockers, sources and `NeedsReview(method, confidence)` and never looks at `TypeRisk`. A confident table is summarised as `{target, source, confidence, columns}`, and the playbook tells the agent to skip confident tables. Only *detail* tables carry `columns[*].typeRisk`.

The auto-mapper produces the dangerous case routinely: a column matching by name with a Widening or Risky type conversion scores `method: fuzzy` (Exact requires `Level == Exact`) at a total at or above the 0.85 auto-accept threshold, so it is not "needs review". Example: `varchar(300) → nvarchar(200)` at fuzzy 0.9 carrying `"may truncate (source max 300)"`. Its table is confident, the agent skips it, no `LEFT`/`CAST` is added, and rows truncate at transfer.

**Fix, both halves:**

1. `IsConfident` returns false when any column of the table has a non-null, non-empty `TypeRisk`. Such a table is therefore detailed, and the existing detail path already carries the risk — no new packet field, no playbook change.
2. `NeedsAgent` returns true when any column carries a `TypeRisk` and has not already been decided by the agent or a human. Without this, a project whose *only* problem is type risk never invokes the agent at all.

Chosen deliberately over the cheaper alternative of adding a `typeRisks` map to summarised entries plus a playbook instruction to stop skipping them: that leaves a seam where a risk can be forgotten again, and this milestone's entire failure history is risks disappearing at seams. Detailing a table that carries a genuine data-loss hazard is the correct place to spend packet tokens.

**Rework contexts:** the `column:src:`, `table:src:` and `general` contexts omit the risk today, and `usedBy` lists bare target column names. Carry the risk in these too — the invariant above is not limited to the draft packet.

### B2 — Validate must REJECT what the packet builder cannot process

**INVARIANT (this is the spec; the list beneath is EXAMPLES, not the enumeration):**

> Any payload `Validate` accepts must be one that `BuildPacket` (both modes) and `Summarize` can process without throwing. If you find a malformed shape that Validate accepts and either of those two cannot handle, it is covered by this invariant even though it is not listed below — fix it and say so.

Task 3.2 made `MappingValidator` null-tolerant, meaning it no longer *throws* on malformed input. That was necessary and insufficient: not throwing and not reporting are different things. The validator now silently **accepts** shapes that `MappingPacket` dereferences directly, so `Validate` returns ok, the malformed artifact is stored, and the phase wedges — human edits need `awaiting_review`, `RetryJob` needs `running` or `drafting`, `Reopen` needs `approved`, so the only exit is editing `state.db` by hand.

Known-accepted malformed shapes (examples): `tables` null; `drops` null; a table's `columns` null; a table's `sources` null; `candidates` containing a null element at table or column level; a column's `sourceColumns` null.

`Validate` must report each such shape as an **Error** (`"tables must be an object"` and so on), so the patch is rejected rather than stored. Normalising during parse does not help: the engine stores the original `JsonNode`, not the deserialised object.

**Required test:** run the same malformed-payload matrix Task 3.2 used, but through the full chain — `Validate` → `Summarize` → `BuildPacket(Draft)` → `BuildPacket(Rework)`. A matrix that stops at `Validate` is what let this through.

### B3 — Null guards at public boundaries, and the brief's own test is wrong

`MappingModule`'s constructor and the public `MappingPacket` statics have no null guards, contrary to the global constraint (`RespectNullableAnnotations` is .NET 9+ and unavailable here). `new MappingModule(null!)` fails later with an NRE inside `Options()`/`Catalogs()`.

The brief's `Identity_and_registration` test calls `new MappingModule(null!)`, so adding the guard breaks it. **Change the test** to construct with real services from `TestWorkspace.OpenServices()`. A test asserting that a null-services module constructs successfully is pinning the wrong behaviour.

### B4 — Drop the undocumented `map` key

`TableDetail` emits `"map": null` for a target table with no map. That key appears in neither the normative Draft shape nor the playbook, so the agent receives an undocumented field. Omit the key when there is no map.

### B5 — Tests that would actually catch B1

Deleting the line that writes `typeRisk` into the packet leaves all of this task's tests passing — verified by mutation. The root cause is that `SampleMappings.Approved()` contains **zero** columns with a `TypeRisk`.

**Do NOT add a `TypeRisk` to `SampleMappings.Approved()`.** That fixture is pinned across milestones: Milestone 4's SQL generator tests assert exact generated SQL and the exact summary string `"6 tasks, 0 errors, 9 warnings"` derived from it, and a new risk would add a warning and break them before that milestone starts. New tests must build their own payload carrying a risk and leave the shared fixture untouched.

Required tests:
- an auto draft's risky *detail* column carries `typeRisk`;
- the B1 case: a high-confidence risky column in an otherwise clean table still reaches the agent (the table is no longer confident);
- the `colmap:` rework context carries the risk.

### B6 — Note on `ctx.Services`

The module captures `DbmServices` through its constructor and ignores `ctx.Services` everywhere. This is harmless today because the registry passes the same instance, but a test constructing a `ModuleContext` with different services would not notice. Not a required fix; recorded so it is a deliberate choice rather than an accident.

### B7 — A human remap must re-evaluate its own type risk

This is the seventh defect, found in Task 3.5's review but fixed here because this is where the mapping payload is validated.

**INVARIANT (this is the spec; the mechanics beneath it are how, not what):**

> A stored `typeRisk` must always describe the conversion the payload currently specifies. It must never survive a change to the column's source, and it must never be absent from a conversion that carries a hazard.

`editColumn`, `useCandidate` and `mapToTarget` change `expr`, `sourceColumns` and `default`, then set `method: human`, `confidence: 1` — and leave the auto-mapper's `typeRisk` untouched. Nothing recomputes it. Both directions are wrong: a risk from the *old* source pair is now asserted against a *new* conversion, and — worse — remapping onto a genuinely lossy source shows **no** risk at all, and the saved version carries none.

#### Where it goes, and the constraint that decides it

`MappingModule.Validate`, after the parse-error return and before blockers and attention are computed, so the stored warnings match the stored payload.

**It must write into the `JsonObject` that the engine stores, not into the deserialised object.** `WorkflowEngine.Apply` passes the patched node to `Validate` and then stores *that same node* by reference. Assigning to a parsed POCO's property persists nothing — the finding would close while the bug remained, which is worse than not fixing it.

Normalising the node in place does **not** break the `IPhaseModule` contract. It is already the established pattern in this design: Milestone 4's `SqlModule` specifies the same thing in its own words, "the payload node is normalised in place … so the flags reach the new version". Normalisation-on-accept and error-rejection are different operations.

#### Rules

1. **Only recompute columns that changed.** Diff each column's `expr` and `sourceColumns` against `ctx.Current`. A column whose source is untouched keeps its existing `typeRisk` verbatim — otherwise an agent's deliberate risk wording gets overwritten on every unrelated save.
2. **Bare single-source expressions are recomputed.** When the column resolves to exactly one source column and the expression is empty or is exactly a bare reference to that column, resolve both column types from the catalogs, call `TypeCompat`, and write the resulting risk string to `typeRisk` — or remove the property when there is no risk.
3. **Anything else clears the risk and is flagged.** A `CAST`, `CASE`, `LEFT`, literal, or multi-source expression cannot have its risk inferred, and the old risk is provably stale because the source changed. Remove `typeRisk` and let the column be caught by the new attention rule below. Do **not** re-flag such a column with the old text: the agent may have written that expression specifically to fix the risk, and re-flagging its own fix is how a correct change gets reverted.
4. **New attention rule, payload-only, both languages.** A column whose expression is custom (not a bare single-source reference) counts as needing review, with the reason "custom expression: type risk not evaluated". It must be computable from the payload alone so the C# and JS implementations can stay in parity, exactly like the `typeRisk` rule added in B-series amendment A2.

#### Deliberately out of scope, recorded as a residual

The candidates popover shows source, score and reason, but no per-candidate risk, so a human chooses without seeing it. Adding a risk to the candidate record would reach back into the auto-mapper and the payload model, both of which are closed work, this late in the milestone. With rules 1–3 in place the human sees the true risk immediately *after* choosing rather than before. Recorded as a known residual rather than fixed.

#### Tests

- A remap onto a lossy source produces a risk that was not there before.
- A remap away from a lossy source removes the risk that was there before.
- A column whose source did not change keeps its existing risk text byte-for-byte, including text an agent wrote.
- A `CAST` expression clears the risk and produces the custom-expression attention instead of re-flagging.
- The recompute survives a round trip: validate, store, re-read, and the stored artifact carries the new risk.

Build the payloads for these tests inline. Do **not** modify the shared approved-mapping fixture — see B5.

### AMENDMENT CORRECTIONS (Task 3.4, second pass)

From the reviewer checking the first pass at source. B1's `NeedsAgent` half and B1's rework clause were already correct and are unchanged — they are restated here only because they are easy to drop while fixing the summary path.

#### B1 corrected — where the risk check goes, and one predicate everywhere

**Skip tables are exempt, deliberately.** `IsConfident` returns true for `kind: "skip"` before it looks at columns. A skipped table loads no data, so a carried risk on one of its columns is not a hazard. Leave the skip return first and put the risk check after it. This is the one exemption to B1's "unconditional" wording, and it is an exemption because there is no conversion to be risky about — not because the risk is unimportant.

**Use one predicate in both places.** `ColumnMapNode` currently tests `TypeRisk is not null` while B1 asks `IsConfident` for "non-null, non-empty". Mixed, an empty-string risk would render in a detailed table without being the thing that made it detailed. Both sites use the same test: present and not whitespace.

**Do not exempt agent-, human- or carry-decided columns from `IsConfident`.** It is tempting, because carried risks keep a table detailed on every later draft even after a `CAST` fixed it, which costs packet tokens on re-runs. Accept that cost. Exempting them reintroduces exactly the stale-risk seam B7 exists to close; the right relief is B7's recompute clearing risks it can prove are handled.

#### B8 — the agent's own patch must not delete the risk (new)

The playbook tells the agent to replace the whole column object, and the Mapping-model block describes `typeRisk` as "risk text from the auto-mapper" without saying whether to carry it forward. The worked example patch replaces `Customers.CreatedAt` — whose risk is `"fractional seconds rounded to 0 digits"` — with an object containing no `typeRisk`. So an applied agent patch **deletes the hazard from the stored artifact**, and the UI stops showing it from that version on.

This is the packet-to-patch return leg. B1 fixes what the agent is *shown*; nothing yet governs what it is allowed to *remove*.

- Add a playbook rule: when replacing a column object, copy `typeRisk` across unchanged, unless the new expression handles the hazard — and when it does, say so in `rationale`.
- Fix the worked example patch to follow that rule, since an example that contradicts the rule is what the agent will actually imitate.
- B7's recompute will later restore the risk for bare single-source expressions, but it cannot infer one for a `CAST`, so the playbook rule is the real protection and remains necessary after B7 lands.

#### B5 corrected — copy the fixture, do not rebuild it

The earlier wording, "new tests must build their own payload", is misleading. `SampleMappings.Approved()` constructs a fresh object on every call and holds no static cache, so **taking a copy and setting a `TypeRisk` on one column inside a test is not a fixture change** — existing tests in this milestone already do exactly that. Nobody should hand-build six tables.

The rule is narrower than it sounded: **do not edit `SampleMappings.cs`.** Everything else is allowed.

Three tests are required, because no single one kills every mutation:

1. **Draft packet.** An `Approved()` copy with a `TypeRisk` set on a high-confidence column. Assert the table is absent from `confident` (six become five) **and** that the detail entry's column carries the exact risk text. This kills both the `IsConfident` regression and the line that writes `typeRisk` into the column node.
2. **Rework packet.** The same payload with a `colmap:` anchor; assert the context's map carries the risk. The rework path reaches the column node through a different caller, so test 1 does not cover it.
3. **Through `MappingModule.BuildPacket`** with `ctx.Current` holding the serialised payload. Nothing in this milestone currently round-trips a non-null `TypeRisk` through the JSON options, because the shared fixture has none — so a rename or a `[JsonIgnore]` on the property would pass tests 1 and 2 and still ship the bug.

Optionally, and free: assert that a real auto-mapper draft's known risky columns carry `typeRisk` in the packet. That uses genuine scorer output with no fixture change, but it cannot catch the `IsConfident` gap, because those tables are detailed for other reasons anyway.

#### B7 residual, extended

`Candidate.Why` carries only a coarse marker such as "type risky" or "widening", never the risk text. An agent switching a column to a candidate source cannot see that candidate's hazard, and the column also keeps the previous pairing's `TypeRisk`, which is stale in the other direction. The recompute closes the second half. The first half stays on the deferred list with the popover display.

### AMENDMENT CORRECTIONS (Task 3.4, third pass)

B7 rule 4 is **withdrawn**: it contradicted B7 rule 3 and would have broken the existing suite. B8 gains the acknowledgement it was missing. B1 point 2 loses a clause that excluded nothing. Everything not mentioned here stands as written.

#### C1 — B7 rule 4 is withdrawn; rule 3 writes a sentinel instead of deleting

Rule 4 made a custom expression count as needing review, computed from the payload alone. Two things are wrong with that.

It **breaks the existing suite without anyone touching the fixture.** The approved sample mapping is full of non-bare expressions — `LEFT`/`SUBSTRING` on the name columns, `CAST` on the date and notes columns, a subquery for the primary address, a `CASE` for the active flag, and a two-source column. A payload-only rule cannot distinguish a `CAST` written five minutes ago from one approved long ago, so it flags every one of them. That breaks the pinned `"… 0 attention, 0 blockers"` summary string in three tests, the assertion that the approved mapping does not need the agent, and an empty-warnings assertion in the validator tests.

It **contradicts rule 3 in the same section.** The playbook requires the agent to resolve every attention item, and the only way to clear "custom expression: type risk not evaluated" is to rewrite the expression as a bare column reference — that is, to revert the `CAST` that fixed the risk. Rule 3 exists precisely to stop that, and rule 4 reintroduced it by another route.

**Replacement.** Rule 3 no longer deletes `typeRisk` for a changed non-bare expression. It **writes a sentinel** instead:

    typeRisk: "not evaluated: custom expression"

Only columns whose `expr` or `sourceColumns` actually changed are touched, so long-standing expressions in any existing mapping are left exactly as they are. The sentinel travels through the existing risk channel — B1 carries it into the packet, A2 surfaces it as attention, the UI shows it in the same place — so no second predicate is needed anywhere, in either language. It is cleared the same way any other risk is, under C2.

**Rule 4 is deleted.** There is no custom-expression attention predicate, in C# or in JavaScript.

#### C2 — A risk can be acknowledged (resolves A2 against B8)

A2 makes any column carrying a `typeRisk` count as needing review. B8 tells the agent to keep a risk it has accepted rather than deleting it. Together those trap an accepted risk in attention forever: the attention count never reaches zero, dry-run warnings never clear, and `NeedsAgent` fires on every later automap run. The only escape is deleting the risk text — the exact incentive B8 exists to remove.

`ColumnMap` gains one field:

    riskAck: string   // why this risk is acceptable; absent or blank means unacknowledged

The attention predicate becomes: **a column needs review when `typeRisk` is present and `riskAck` is absent or blank.** This stays payload-only, so the C# and JavaScript implementations remain mirrorable. Note it cannot be expressed as a test on `method` — A2's correction forbids method gating, and an acknowledgement by a human and by the agent must behave identically.

`riskAck` must survive into the packet alongside `typeRisk`, must be shown in the UI next to the risk it acknowledges, and must be preserved by carry-over exactly as `typeRisk` is.

B8's playbook rule becomes: when replacing a column object, copy `typeRisk` across unchanged; if the new expression handles the hazard, remove the risk and say so in `rationale`; if the hazard is real but acceptable, keep the risk and set `riskAck` to the reason. The worked example must demonstrate the acknowledgement path, since that is the case it actually represents.

#### C3 — B1 point 2 loses its exclusion

B1 point 2 said `NeedsAgent` returns true for a risky column "not already decided by the agent or a human". That clause excludes nothing, and my stated reason for it was wrong at source: carry-over rewrites every kept column's method to `Carried`, and only agent-, human- and carry-decided columns are kept at all, so a fuzzy column is never carried. Read literally the clause therefore does not exclude carried decisions either.

It is also redundant once A2 lands, because an unacknowledged risk puts the column in attention and `NeedsAgent` already fires on a non-empty attention list.

**Delete the exclusion.** `NeedsAgent` needs no risk clause of its own: an unacknowledged risk reaches it through attention. Do not add one.

#### C4 — Carry-over must not preserve a risk across a source type change

B7's invariant says a stored `typeRisk` must never survive a change to the column's source. A source column's **type** can change under the same name between discoveries — `varchar(100)` becoming `varchar(400)` — and carry-over copies `TypeRisk` verbatim, checking only that the column still exists. Job drafts are stored without passing through `Validate`, so the recompute never runs on that path, and B7 rule 1 then sees no change on every later save and preserves the stale text permanently. Both directions fail: a newly-created hazard shows nothing, and a hazard that has gone away keeps its old text.

Carry-over therefore recomputes, for each kept column: if the mapping is a bare single-source reference, recompute the risk from the current catalogs; otherwise write the C1 sentinel. A `riskAck` is preserved only when the recomputed risk is unchanged — an acknowledgement is of a specific hazard, not a blanket permission.

#### C5 — B7 rule 1 restores a risk that was dropped

Rule 1 says an unchanged column keeps its existing `typeRisk` verbatim, without saying whether "existing" means the incoming payload or `ctx.Current`. It means `ctx.Current`: when a column's `expr` and `sourceColumns` are unchanged but the incoming payload has **lost** its `typeRisk` — an agent replacing the whole column object and omitting it — restore it from `ctx.Current`. With the expression unchanged, a bare-reference hazard cannot have been handled, so restoring is always correct. This makes B8 self-healing for the common case and leaves B8 load-bearing only for changed and custom expressions.

#### C6 — Two details for B7 rule 2

- Pass the source column's profile to the type-compatibility check, exactly as the auto-mapper's column scorer does. Without it the recomputed text differs from the auto-mapper's for the same pair — the sample-supported softening disappears — so a remap back to the original source would produce a different string than the original.
- An empty expression with a single source column means unmapped or default-only. There is no conversion, so compute no risk.

#### C7 — Wording

B7's test paragraph says "build the payloads for these tests inline", repeating the phrasing already corrected in B5. It means the same narrow thing: **do not edit `SampleMappings.cs`.** Copying the approved fixture and setting a risk on the copy inside a test is expected and is not a fixture change.

### Task 3.5 — JavaScript scope correction (follows the Task 3.4 third pass)

The Task 3.4 third-pass corrections change what the Task 3.5 round must implement in JavaScript. This section overrides the earlier A2 text wherever they differ.

**There is no custom-expression attention predicate, in either language.** An earlier draft added one (a column whose expression is not a bare single-source reference counts as needing review). It is withdrawn — it would have forced the agent to revert the very expressions written to fix a risk, and it broke the existing suite against the shared fixture. Do not implement a JavaScript mirror of it. If you find one already written, delete it.

**The attention predicate gains an acknowledgement.** A column needs review when it carries a `typeRisk` **and** its `riskAck` is absent or blank. Both halves are payload-only, so the C# and JavaScript implementations stay mirrorable.

**Mirror, do not invent.** The C# predicate lands first, in the Task 3.4 round. Read the committed C# and mirror it. Do not write a parallel implementation from this description — two implementations of one rule, written independently in two languages by two agents, is precisely the drift this project has already flagged as a hazard elsewhere.

**The sentinel is an ordinary risk.** A changed column whose expression is not a bare single-source reference carries the risk string `"not evaluated: custom expression"`. It is a `typeRisk` like any other: it flows through the same predicate, renders in the same place, and needs no special-casing. If the UI branches on that text anywhere, something is wrong with the design rather than with the UI.

**`riskAck` must render.** Show it next to the risk it acknowledges, so a reviewer can see both that a hazard exists and why it was accepted. A risk shown without its acknowledgement reads as an unresolved problem; an acknowledgement shown without its risk hides the hazard.

### AMENDMENT CORRECTION (Task 3.4, fourth pass) — C1's rule 3 reintroduced the loop

C1 withdrew rule 4 because it forced the agent to revert its own fixes. C1's replacement then did the same thing by a shorter route, and this corrects it. Everything else in C1 through C7 stands.

#### The defect

C1 made rule 3 write a sentinel risk on **every** changed non-bare expression. Trace the agent through that:

1. The agent writes a `CAST` to handle a truncation hazard and removes the risk, exactly as B8 instructs.
2. Validate runs rule 3, sees a changed custom expression, and overwrites that deliberate removal with the sentinel.
3. The column is now attention.
4. The playbook requires the agent to resolve every attention item — which it already did, in step 1.

That is the rule-3-versus-rule-4 loop, reintroduced inside the fix for it. The same trap catches a human who writes a `CAST` and explains it.

#### The correction

Rule 3, for a changed column whose expression is **not** a bare single-source reference:

- If the incoming payload supplies a **non-blank `riskAck`** for that column, write no sentinel and leave the risk text exactly as supplied. The author has taken responsibility and stated why.
- Otherwise, replace whatever risk text is present with the sentinel `"not evaluated: custom expression"`.

**`method` is never consulted.** An acknowledgement written by a human and by the agent must behave identically, and C2 already forbids method gating for the attention predicate — a method gate here would reintroduce one there by the back door.

This stays payload-only, so the JavaScript mirror remains trivial, and there is still exactly **one** attention predicate: a non-blank `typeRisk` with a blank `riskAck`.

#### Consequence for B8 — one channel, not two

B8 previously offered two ways to record that a new expression handles a hazard: remove the risk and explain in `rationale`, or keep the risk and set `riskAck`. **The `rationale` route is closed.** `rationale` is free text that nothing can check, and it leaves rule 3 unable to distinguish a considered removal from an accidental one — which is exactly how the loop above arises.

The single way to record that an expression handles a hazard is to **set `riskAck`** saying so. The worked example patch must demonstrate that, and the playbook must say it plainly.

#### Why the fixture stays green

None of the seven custom expressions in the shared approved fixture — the `LEFT`, the `SUBSTRING`, the two `CAST`s, the subquery, the `CASE`, the two-source column — has **changed**. Rule 3 only touches columns whose `expr` or `sourceColumns` differ from `ctx.Current`, so none of them is sentinelled, and the pinned zero-attention summary string, the assertion that the approved mapping does not need the agent, and the empty-warnings assertion all continue to pass with the fixture untouched.

### AMENDMENT CORRECTION (Task 3.4, fifth pass) — typeRisk becomes server-owned

Three passes have now tried to police what an author may write into `typeRisk`, and each produced a new seam: rule 4 forced the agent to revert its own fixes; C1's sentinel overwrote the agent's deliberate removal; C1's fourth-pass version made the acknowledgement into permission to erase the hazard. The mistake is shared and structural — the author was allowed to write the field at all.

**`typeRisk` is computed by the engine and only by the engine.** No agent patch and no human edit ever writes or removes it. Authors express themselves through `riskAck` alone. This section replaces the parts of B7, B8, C1 and C4 that conflict with it.

#### D1 — The ownership rule

- On every `Validate`, the engine sets each column's `typeRisk` itself: recomputed for a changed bare single-source reference, the sentinel for a changed non-bare expression, and otherwise restored verbatim from `ctx.Current` (C5).
- An incoming `typeRisk` in a patch is **ignored**, not merged and not trusted. If a patch supplies one that differs from what the engine computes, record a warning naming the column so the behaviour is visible rather than silent.
- `riskAck` is the only risk-related field an author writes.

This closes, in one rule, the erase seam, the remove-then-restore loop, and the question of whose text wins.

#### D2 — B8 and the playbook shorten

The playbook line becomes: **`typeRisk` is computed by dbm. Never write it and never remove it. If the hazard is handled by your expression, or is real but acceptable, set `riskAck` and say why.**

Delete B8's earlier instruction to copy the risk forward or to remove it with a `rationale`. Both routes are now impossible, and an instruction to do something the engine overrides teaches the agent a move that silently fails. The worked example must set `riskAck` and must not carry a `typeRisk`.

The `rationale` field keeps its existing purpose and is not a risk channel.

#### D3 — C4 narrows to bare columns; two tests change on purpose

Carry-over recomputes **only** columns that are a bare single-source reference. A kept column with a custom expression retains its stored `typeRisk` and `riskAck` verbatim.

This is not a compromise. If a custom expression's risk cannot be evaluated — which is exactly why the sentinel exists — then detecting a type change beneath it gains nothing, because the engine still could not say what the risk became. **Residual, recorded deliberately:** a source type change under a custom expression is not re-evaluated.

Recomputing bare columns correctly surfaces three hazards the hand-written approved fixture never recorded: `Customers.CreatedAt` and `Orders.OrderDate` round fractional seconds, and `Orders.Comment` may truncate. Two committed tests therefore change **on purpose** — the one asserting the carried approved mapping has no attention, and the one pinning a zero-attention summary after carry-over. Change them deliberately, with a comment recording that the risks are genuine and were previously invisible. Do not tune numbers until they pass.

#### D4 — An acknowledgement dies with the hazard it acknowledged

`riskAck` is an acknowledgement of a **specific** hazard, never a standing permission.

In `Validate`: when the engine's computed `typeRisk` for a column differs from the one in `ctx.Current`, clear that column's `riskAck` — unless the incoming patch supplies a `riskAck` that itself differs from `ctx.Current`'s, which is a deliberate new acknowledgement made in the same change.

The same rule decides whether an acknowledgement counts as "made in this change" for D1: an incoming `riskAck` equal to `ctx.Current`'s is the old one being carried along, not a new act.

**The sentinel is never "the same risk".** Comparing risk text by equality makes sentinel equal sentinel, which would let one acknowledgement silence every future hazard on that column. Treat a sentinel on either side as *unknown*, never as unchanged, so an acknowledgement never survives across it.

#### D5 — Acknowledged risks must reach a human

An acknowledgement removes a column from attention, and attention is the mechanism that reaches a human. Something must therefore surface acknowledgements before approval, or the acknowledgement quietly defeats the review it is part of. Nothing in the payload can distinguish a considered acknowledgement from a reflexive one, so the answer is exposure rather than detection:

- Setting `riskAck` on a column carrying **no** risk is meaningless and is the signature of blanket-acknowledging. Reject it with a validation error naming the column.
- Report acknowledged risks as their own count, separate from open attention, and list them in the review screen and in the agent work packet.
- The dry-run output lists newly acknowledged risks, so an orchestrator summary shows them.

A human approver signs off on the acknowledgements as a set. They are not hidden merely because they are not blocking.

#### D6 — Humans must be able to acknowledge

`riskAck` is settable from the mapping screen, next to the risk it acknowledges, through the ordinary human-edit path. Without it, a human-only review — no agent run, or a reviewer who never requests rework — can never clear attention, and the signal this whole mechanism exists to protect is lost anyway. This belongs to the Task 3.5 JavaScript round.

#### D7 — Two consequences in Milestone 4, to be fixed there

Recorded here because they are caused by this milestone's change and must not be discovered mid-M4.

1. **Custom SQL is silently discarded.** M4 keeps a human's hand-written SQL for a table only when a hash of the whole table mapping matches, and that hash covers `typeRisk`, `riskAck`, `candidates`, `rationale` and `confidence` — none of which affect the generated SQL. Acknowledging a risk, a carry-over recompute, or a sentinel write each change the hash and throw the custom SQL away. The M4 hash must cover only SQL-relevant fields: the table's kind, sources, from and filter, and per column the expression, source columns and default.
2. **Sentinels and acknowledged risks become SQL-plan warnings.** M4 emits a warning for every non-empty `typeRisk` and does not consult `riskAck`. A DBA reading the SQL review would see `"not evaluated: custom expression"` as a plan warning. M4 must skip acknowledged risks and must not render a sentinel as a data-loss warning.

### AMENDMENT CORRECTION (Task 3.4, sixth pass) — D5 drops rather than rejects

D5 opened by saying that setting `riskAck` on a column carrying no risk should be rejected with a validation error. That punishes caution. Consider the honest case: an author remaps a column to a safer source and sets an acknowledgement out of prudence, the engine then computes no risk for it, and a well-meaning patch fails validation for being careful.

An acknowledgement on a column with no risk is harmless noise. Acknowledgement-spraying only matters where a risk actually exists, and the remaining bullets of D5 already expose that case by counting and listing acknowledged risks where a human reads them before approval.

Corrected: **drop** a `riskAck` whose column has an empty computed `typeRisk`, and record a warning naming the column. Never reject the patch for it.

### Task 3.5 — JavaScript scope, second addition (follows the Task 3.4 fifth pass)

The Task 3.4 fifth pass makes `typeRisk` engine-owned and introduces `riskAck`. Three consequences land in this round. They are written here because an implementer reading only this section would otherwise never see them.

**Never write `typeRisk` from the browser.** The engine computes, recomputes and restores it. The screen displays it and sends only `riskAck`. A patch from the browser carrying a risk value is ignored by the engine and earns a warning.

**A human must be able to acknowledge a risk.** `riskAck` is settable from the mapping screen, next to the risk it acknowledges, through the ordinary human-edit path. Without it, a human-only review — no agent run, or a reviewer who never requests rework — can never clear attention, and the signal the acknowledgement exists to protect is lost anyway.

**Acknowledged risks must be visible as a set.** An acknowledgement removes a column from attention, and attention is the mechanism that reaches a human. Show acknowledged risks as their own count, separate from open attention, with a filter that lists them, so an approver signs them off deliberately rather than never seeing them. An acknowledgement hidden from the approver defeats the review it belongs to.

### AMENDMENT CORRECTION (Task 3.4, seventh pass) — supersession, and an acknowledgement must name its hazard

Six passes of appending corrections have left older passages contradicting newer ones. This pass strikes the stale text explicitly, closes the last acknowledgement hole, and finishes the playbook rewrite that D2 started but did not scope fully.

**Reading rule for this document:** where any earlier passage conflicts with a later one, the later one wins. The strikes below are the conflicts found so far; if you find another, it resolves the same way — tell me, and do not pick one yourself.

#### E1 — Passages that are superseded and must not be implemented

- **B7 rule 3's original text** ("remove `typeRisk` and let the column be caught by the new attention rule below") — superseded by C1 and then by D1. The engine writes the risk; nothing "lets it be caught" by a rule that no longer exists.
- **B7's test bullet** ("a `CAST` expression clears the risk and produces the custom-expression attention instead of re-flagging") — **struck.** It is a test for withdrawn rule 4. C1 deleted the rule; this bullet survived by oversight. Do not write this test.
- **B8's original instruction** ("copy `typeRisk` across unchanged, unless the new expression handles the hazard — and when it does, say so in `rationale`") — superseded by D2. Copying is impossible now; the engine owns the field.
- **C2's sentence** ("if the new expression handles the hazard, remove the risk and say so in `rationale`") — **struck.** D2 closed the `rationale` route, but the fourth pass said "everything else in C1 through C7 stands", which kept this sentence alive and contradicting it. There is one channel: `riskAck`.

#### E2 — An acknowledgement must name the hazard it accepts

This closes the reflexive-acknowledgement hole. `riskAck` suppresses attention, so an acknowledgement written without knowing what it suppresses is a way to hide a hazard nobody evaluated — and an agent that sets `riskAck` on every column it writes, imitating the required-fields list, achieves exactly that with no bad intent.

**An incoming `riskAck` is honoured only if the incoming `typeRisk` equals the risk the engine computed for that column.** Otherwise the acknowledgement is dropped and the column remains in attention.

D1 is unchanged: the incoming `typeRisk` is never **stored**. Here it is read purely as a confirmation token — the author must state which hazard they are accepting. A blank acknowledgement, or one written before the hazard existed, therefore fails.

The dry-run loop converges without anyone guessing: the warning prints the computed risk text, the author copies it next to their acknowledgement, and the next dry-run passes.

This also supplies D4's invalidation directly — a changed hazard produces different computed text, so an old acknowledgement no longer matches and drops — and it applies equally in carry-over, which removes the problem of a sentinel comparing equal to itself forever.

#### E3 — The sentinel is too eager

D1 writes the sentinel for any changed non-bare expression. Narrower and more honest: when a column's expression changed but its **`sourceColumns` set did not**, the stored risk still describes that same source pair, so keep `ctx.Current`'s text. Write the sentinel only when the `sourceColumns` set itself changed and the expression is not a bare single-source reference.

The sentinel then appears rarely and means what it says — nothing could be evaluated — instead of appearing on every expression edit and teaching everyone to ignore it.

#### E4 — The playbook rewrite, fully scoped

D2 changed the main instruction and the worked example. Three further places in `mapping-architect.md` still teach the closed route, and an agent follows whichever it reads first:

- the **mapping-model block** lists `typeRisk` as a field the author writes, and has no `riskAck` field;
- the **required-column-fields rule** lists what every column map must set, without `riskAck`;
- the **packet legend** describes the `method` values but never says how a risk is cleared.

All three must say the same thing: the engine owns `typeRisk`; a risk is cleared only by `riskAck`, which must quote the risk text it accepts.

**The complete example patch must pass its own procedure.** As written it leaves six columns in attention while step 7 requires clearing them, and the surrounding text claims success. Rework it so the example satisfies the procedure it demonstrates. An example that contradicts the rules is what the agent imitates.

#### E5 — The test that hides all of this

The complete-example-patch test applies the patch with raw `JsonPatch.Apply` and calls the validator directly, so `MappingModule.Validate` never runs on it — rules 1 to 3, C1, C5 and the whole acknowledgement mechanism are untested against the playbook's own example. It also asserts only errors and blockers, never attention.

Apply it through the workflow's dry-run patch path instead, and assert there are no warnings — or at minimum no attention. The playbook-style flow test needs the same treatment. This is the only test that fails when the playbook teaches a route the code no longer honours.

#### E6 — Keep acknowledgement counts out of `Summarize`

D5's acknowledged-risk count belongs to the work packet and the UI context. It must **not** go into `Summarize`, whose output is pinned by assertions such as the zero-attention, zero-blockers summary string. A display concern must not move a load-bearing assertion.

### AMENDMENT CORRECTION (Task 3.4, eighth pass) — invalidation must not become a treadmill, and three paths store a hazard with no text

D4 as written clears a valid acknowledgement on the next unrelated save. Three separate paths let a real conversion reach the artifact with no risk text at all — the same failure class every pass of this amendment has produced in a new disguise. And D1's warning compares the wrong two values, firing on the main human workflow.

#### F1 — D4 applies only to columns that changed in this Validate

D1 restores an unchanged custom column's `typeRisk` verbatim, so a sentinel goes in and the same sentinel comes out. D4 then says a sentinel never counts as unchanged, so that column compares as "differs" on **every** Validate, and a carried-along `riskAck` equals the one in `ctx.Current` so it does not count as a deliberate new acknowledgement — and it is cleared.

An acknowledgement on a sentinel therefore cannot survive one unrelated save. The reviewer called this the treadmill in its purest form, and that is right: it would teach every user that acknowledging is pointless.

**Correction.** D4's comparison — including the rule that a sentinel is never "the same risk" — applies **only** to columns whose `expr` or `sourceColumns` changed in this Validate. An unchanged column keeps both its `typeRisk` and its `riskAck` from `ctx.Current`, with no comparison performed. D4's own premise is that the hazard changed; nothing about an unchanged column changed.

The same exemption holds in carry-over: a custom-expression column keeps its stored risk and acknowledgement verbatim, and the D4 comparison is not applied to it.

#### F2 — "Bare single-source reference" defined precisely

Nothing currently verifies that the expression reads the column its `sourceColumns` names. `expr: "s.[NOTES]"` with `sourceColumns: ["dbo.CUST.CUST_NM"]` makes the engine evaluate `CUST_NM` and store its risk — or no risk — while the load actually reads `NOTES`. A truncation ships silently.

A column is a **bare single-source reference** when all of these hold:

- the expression is exactly an alias, a dot, and a bracketed column name;
- the alias resolves to `sources[0]`, or to an alias declared in the table's `from`;
- the resulting table-and-column equals the single `sourceColumns` entry, compared case-insensitively.

Anything else is non-bare and takes the sentinel.

#### F3 — Table-level changes make every column changed

`sources` and `from` decide what the alias points at. A patch touching only `/tables/T/sources` or `/tables/T/from` rebinds every expression in that table while each column still looks unchanged, so every stale risk is restored verbatim — including the absence of one, where the new table's types are lossier.

A column counts as changed when its own `expr` or `sourceColumns` changed, **or** when its table's `sources` or `from` changed.

#### F4 — Defaults and literals are non-bare

C6 says an empty expression with a single source column computes no risk. But a null expression with `default: "'UNKNOWN'"` into `char(3)`, or `default: "GETDATE()"` into `date`, is a real conversion with no text — and so is a literal expression carrying zero source columns.

A non-null `default`, or an expression with zero source columns, is non-bare and takes the sentinel. C6's no-risk case narrows to: expression null **and** default null.

#### F5 — D1's warning compares against ctx.Current, not against the computed risk

`Validate` sees the post-patch node, never the patch. A field-level edit sends only the properties that changed, so the node still carries the **old** risk; a whole-object replace omits the field entirely. Comparing the node against what the engine computes therefore fires on nearly every edit of a risky column — noise on the main human workflow, in one reading, and dead code in the other.

An author **wrote** `typeRisk` only when the node's value is present **and** differs from `ctx.Current`'s value for that column. Absent means not written. Equal to `ctx.Current` means carried through. Compare against `ctx.Current`; never against the computed value.

The same definition governs D5's newly-acknowledged count, or the dry-run output lists every carried acknowledgement as new.

Add a test: an agent patch that sets a `typeRisk` differing from `ctx.Current` on an unchanged column is stored with the engine's value, and the result warns naming that column. Nothing in the current tests, examples or UI writes the field, so without this test the rule is untested.

#### F6 — Invalidation compares a hazard class, not risk text

The profiler samples without an `ORDER BY`, and the truncation text embeds the **observed** maximum length. Two discoveries of unchanged data can therefore produce `"may truncate (source max 300)"` and then `"(source max 297)"`, or flip the sampled-rows suffix. If invalidation compares text, every rediscovery clears every acknowledgement and humans re-acknowledge hazards that did not change.

`TypeCompat` exposes a stable **hazard class** alongside the risk text, and acknowledgement invalidation compares that class rather than the text. This is server-side only, so the JavaScript mirror is unaffected and parity holds.

Compare trimmed expressions when deciding whether one changed, so that reformatting alone does not invalidate an acknowledgement.

Pin it: same declared types with a different sampled maximum leaves the acknowledgement intact; a changed declared target type clears it.

#### F7 — The residual is wider than D3 says

D3 records that a source type change under a custom expression is not re-evaluated. The real residual is larger: **every custom-expression column stored before this amendment carries no risk text and no sentinel**, and D3 keeps it verbatim while it stays unchanged. A hand-written `LEFT(...)` that truncates therefore stays text-less indefinitely. The shared approved fixture's seven custom expressions are exactly this state.

They are not retro-sentinelled: doing so would flag every long-approved expression in every existing project at once, which is the withdrawn rule 4 by a fourth route. They take a sentinel the first time they are edited. State the residual in these terms.

#### F8 — The browser must stop diffing the risk field

The mapping view lists `typeRisk` among the column properties it diffs, so the client will emit an operation for it the moment anything rebuilds a column object — which the acknowledgement UI in the Task 3.5 round will do. Remove `typeRisk` from the diffed column properties. The engine owns it; the browser sends only `riskAck`.

---

## THE RISK MODEL — normative, and it replaces everything above it

**This section supersedes every earlier statement about `typeRisk`, `riskAck`, sentinels, recomputation, invalidation and carry-over of risks.** Sections B1 through F8 and all their corrections are **historical**. Read them for context if you like; do not implement them. Where anything above conflicts with this section, this section wins and the passage above is wrong, not merely older.

Nine passes of appending corrections produced a document containing twenty-seven conflicts, eight of which no reading rule can settle because they conflict with equally live text or with themselves. That is not implementable, so it is replaced rather than patched again.

Everything outside the risk model — B2's malformed-payload rejection, B3's null guards, B4, B5's tests, E5's test route, E6, the playbook scope in E4 — stands as written.

### 1. Ownership

`typeRisk` is computed by the engine and only by the engine. No agent patch and no human edit ever writes or removes it. An incoming `typeRisk` is ignored: never stored, never merged, never used as a token.

Warn, naming the column, when the incoming node carries a `typeRisk` that is **present and different from `ctx.Current`'s** for that column. Absent means not written; equal means carried through by a whole-object replace. Never compare against the value the engine just computed.

### 2. The acknowledgement

`riskAck` is author-owned, and it carries its own token:

```
riskAck: { "risk": "<the exact typeRisk text being accepted>", "reason": "<why it is acceptable>" }
```

It is taken from the incoming node as supplied. Absent means no acknowledgement. There is no rule anywhere that preserves, restores or invalidates an acknowledgement by comparing against `ctx.Current` — the predicate below does all of that work by itself.

Drop an acknowledgement, with a warning naming the column, when its column has no computed risk. Never reject the patch for it.

Clear an acknowledgement when the column's `expr` changed, unless the same change supplies a new one. An acknowledgement that cites a transform must not outlive that transform.

### 3. The one predicate

A column **needs review** when:

> `typeRisk` is present and non-blank, **and not** (`riskAck.reason` is non-blank **and** `HazardClass(riskAck.risk)` equals `HazardClass(typeRisk)`).

Identical in C# and JavaScript. Payload-only: it reads nothing but the column itself. This single predicate decides attention, and it is the only place an acknowledgement has any effect.

It closes the reflexive-acknowledgement hole by construction: the token lives in the field the author is writing, so an acknowledgement that names nothing matches nothing. And it makes invalidation automatic — a changed hazard produces a different class, so the old acknowledgement stops matching without any comparison against the previous version.

### 4. `HazardClass`

`TypeCompat.HazardClass(string? risk)` maps a risk text to a stable class, derived from the text alone:

- strip the observed length (`(source max N)`, with or without a `bytes` suffix);
- strip the sampled-rows suffix;
- the sentinel is its own class and matches nothing else;
- null or blank maps to no class, which matches nothing.

It must be derivable from stored text, because at carry-over time the catalog that produced the original risk is gone. This is also what makes the profiler's sampling harmless: `TOP n` without an `ORDER BY` means an observed maximum can differ between discoveries of unchanged data, and comparing classes rather than texts stops that re-prompting a human who already acknowledged the hazard.

### 5. When the engine computes a risk

A column is **changed** when its own `expr`, `sourceColumns` or `default` changed, or when its table's `sources` or `from` changed. Compare `expr` trimmed, and `sourceColumns` as a case-insensitive set.

A column is a **bare single-source reference** when all hold: the expression is exactly an alias, a dot and a bracketed column name; the alias resolves to `sources[0]` or to an alias declared in the table's `from`; and the resulting table-and-column equals the single `sourceColumns` entry, case-insensitively.

Then, for each column:

- **unchanged** → keep `ctx.Current`'s `typeRisk` verbatim;
- **changed and bare** → recompute through `TypeCompat`, passing the source column's profile, exactly as the auto-mapper does, so the texts match;
- **changed, with a non-null `default`, or with zero source columns, or otherwise not bare** → write the sentinel, `"not evaluated: custom expression"`;
- **changed, `expr` null and `default` null** → no risk.

Carry-over follows the same rules, recomputing bare columns and leaving custom-expression columns' stored text alone.

### 6. Ordering inside `Validate`

Parse → B2's structural errors, returning if any → risk normalisation → blockers and attention. Normalising before the structural checks would dereference the very nulls B2 exists to reject.

### 7. Where risks surface

`IsConfident` uses the **needs-review** predicate of section 3, so an acknowledged risk does not force a table into detail. Skip tables remain exempt. Acknowledged risks instead surface as their own count and list, in the work packet, in the review screen, and as dry-run warnings — a human approver signs them off as a set.

The attention message for a risk is pinned: `<table>.<column>: type risk: <typeRisk>`, with the risk text last and verbatim, so an author can copy it into an acknowledgement. Pin it with a test asserting the substring after the prefix equals the stored `typeRisk`. A column that is both below the confidence band and risky must still show the risk text; a message that omits it makes the acknowledgement unwritable.

### 8. The residual, and the invariant it qualifies

The invariant "a risk must never be absent from a conversion that carries a hazard" is qualified here, deliberately: **custom-expression columns stored before this model existed carry no risk text and no sentinel, and keep none while they stay unchanged.** They take a sentinel the first time they are edited.

They are not retro-sentinelled. Doing so would flag every long-approved expression in every existing project at once — the withdrawn "every custom expression needs review" rule arriving by another route. The approved sample fixture's seven custom expressions are exactly this state, and they must stay green.

### Task 3.5 — JavaScript risk scope, FINAL (follows the Risk model)

The two earlier JavaScript scope sections above are **historical**. They describe `riskAck` as a string and carry a sentinel rule the Risk model narrowed. Implement this section and the Risk model only.

**Read the Risk model section first.** It is normative for both languages. This section says only which parts land in the browser.

**Never write `typeRisk`.** The engine owns it absolutely. The screen displays it; the browser never sends it. A patch from the browser carrying a risk value is ignored by the engine and earns a warning.

**The predicate is mirrored, not reinvented.** Read the committed C# and mirror it exactly:

> a column needs review when `typeRisk` is present and non-blank, and not (`riskAck.reason` is non-blank and `HazardClass(riskAck.risk)` equals `HazardClass(typeRisk)`).

`HazardClass` is mirrored from the committed C# too, with the same stripping rules and the same treatment of the sentinel as a class matching nothing else. Two implementations of one rule written independently in two languages is the drift hazard this project has already flagged elsewhere; the C# lands first precisely so this side can copy it.

**`riskAck` is a structured value**, `{ risk, reason }`. The acknowledgement control must put the exact `typeRisk` text into `risk` — the user should never have to retype or reformat it — and the user's own words into `reason`. An acknowledgement whose `risk` does not match the current hazard is not an acknowledgement, so the UI must not let a stale one look valid.

**A human must be able to acknowledge.** The control sits next to the risk it acknowledges and goes through the ordinary human-edit path. Without it a human-only review — no agent run, or a reviewer who never requests rework — can never clear attention, and the signal the mechanism exists to protect is lost anyway.

**Acknowledged risks are visible as a set.** Their own count, separate from open attention, with a filter that lists them, so an approver signs them off deliberately. An acknowledgement removes a column from attention, and attention is the mechanism that reaches a human — one that nobody sees defeats the review it belongs to.

**Remove `typeRisk` from the diffed column properties.** The view currently lists it, so the client would emit an operation for it the moment the acknowledgement control rebuilds a column object.

### RISK MODEL AMENDMENT — `riskClass`, because a class derived from text cannot see a declared-type change

Section 4 said `HazardClass` is derived from the stored text alone. That cannot satisfy section 7's own pin, and the implementer proved it: a target widening from `nvarchar(200)` to `nvarchar(250)` against a `varchar(300)` source produces `"may truncate (source max 300)"` both times, because the **target** length never appears in the risk text. A real declared-type change is therefore invisible to any text-derived class, and an acknowledgement of the old hazard survives a change to the conversion it described.

This was a choice I got wrong. The old class can only come from parsing the stored text or from a stored field; both had been rejected earlier, I picked text, and text does not work.

#### `riskClass` — engine-owned, stored

Written alongside `typeRisk`, by the engine only, on the same occasions:

- omitted entirely when there is no risk;
- composed of the **hazard kind** plus the **declared** source and target types — for example `truncate|varchar(300)->nvarchar(200)`;
- **never** observed lengths and never sample counts. Those are exactly what made text comparison unstable, since the profiler samples without an `ORDER BY` and an observed maximum can differ between discoveries of unchanged data;
- the sentinel carries its own class value, which matches nothing else, including another sentinel.

The browser must not diff it, for the same reason it must not diff `typeRisk`: the engine owns both.

It is not part of Milestone 4's mapping hash, which is already restricted to SQL-relevant fields.

#### Invalidation by class change

When the engine computes a risk for a column and the newly computed `riskClass` differs from the stored one, it **clears `riskAck`**. The hazard has changed, so the acknowledgement was of a different hazard.

This applies wherever the engine computes a risk — in `Validate` and in carry-over alike. Carry-over is the case that forces the field to exist at all: after rediscovery the catalog that produced the original risk is gone, so the old class cannot be recomputed and can only be read back from the payload.

#### The predicate is unchanged, and the two mechanisms are distinct

Section 3's predicate stands as written. `HazardClass` survives, derived from text as before, and is used **only** for the acknowledgement-to-risk match inside that predicate, where stripping observed lengths and sample suffixes is exactly right.

The two mechanisms do different jobs and neither can do the other's:

- **normalised text matching** stops an acknowledgement that names nothing — the reflexive-acknowledgement defence;
- **class change** invalidates an acknowledgement whose hazard genuinely moved, including changes no text reveals.

An implementer who finds themselves collapsing the two into one comparison has lost one of those properties; say which, and stop.

### Task 3.5 JavaScript — addendum for `riskClass`

The FINAL JavaScript risk scope above was written before `riskClass` existed. One addition, and it is the same rule that already applies to `typeRisk`:

**`riskClass` is engine-owned. The browser never writes it and never diffs it.** It is a second engine-owned field on a column map, and the view must exclude it from the diffed column properties exactly as it excludes `typeRisk`. Otherwise the acknowledgement control emits an operation for it the moment it rebuilds a column object, and the engine warns about a field the browser had no business sending.

The browser does not display it either. It is an identity used to decide when an acknowledgement has outlived its hazard, not something a reviewer reads — the reviewer reads `typeRisk`.

### RISK MODEL AMENDMENT 2 — section 5 was neither ordered nor exhaustive, and three ways an acknowledgement outlives its hazard

Section 5 claimed its cases were exhaustive by construction. They were not: they overlapped, and they said nothing about a column absent from `ctx.Current`. Three further paths let an acknowledgement survive a hazard it never covered.

#### G1 — Section 5 becomes an ordered sequence, first match wins

```
(1) not changed                                  -> keep ctx.Current's typeRisk verbatim
(2) expr null AND default null                   -> no risk
(3) expr non-null AND bare single-source ref     -> recompute via TypeCompat, with the source profile
(4) everything else                              -> sentinel
```

Case (4) is a true catch-all, and only with the order does the exhaustiveness claim hold. As an unordered set the cases collided: a coverage-only listing (`expr` null, `default` null, one `sourceColumns` entry) matched both "not bare, so sentinel" and "expr and default null, so no risk"; a bare reference carrying a `default` matched both recompute and sentinel — and `default` applies only when `expr` is null, so bare must win.

An alias that cannot be resolved from `sources[0]` or from the table's `from` is **not bare**, and therefore falls to case (4). Parsing a `FROM` clause is best-effort: a parse miss yields the sentinel, never an error.

#### G2 — Absent from `ctx.Current` is changed

"Changed" was defined only as a comparison, which left a column — or a whole table — that does not appear in `ctx.Current` falling into case (1) and keeping a risk that does not exist. That stores a brand-new mapping with no risk text.

The paths are ordinary, not exotic: the auto-mapper skips identity columns with no key match and the playbook tells the agent to add them; a human removes and re-adds a table map; a patch adds a table the draft lacked.

**A column, or its table, absent from `ctx.Current` is changed.**

#### G3 — An acknowledgement dies on any change, not only an expression change

Section 2 cleared `riskAck` when `expr` changed. Every other change trigger in section 5 — `sourceColumns`, `default`, the table's `sources` or `from` — left it standing. Because a sentinel matches its own class, a new sentinel written after one of those changes is silenced by the old acknowledgement.

Concretely: a two-source column carrying a sentinel is acknowledged "the join yields the code verbatim"; later only `from` changes, so the alias now joins a different table whose column is `nvarchar(max)` into `varchar(10)`; the column is changed and non-bare, a fresh sentinel is written, the class matches, and an unevaluated truncation shows no attention.

**Clear `riskAck` whenever the column is changed by section 5's definition, unless the same patch supplies a new acknowledgement.**

#### G4 — Carry-over must copy `riskAck`

`MappingCarryOver` builds each kept column from an **explicit field list**. A field not named there is silently dropped, so every automap re-run — re-approval, rediscovery, retry — would strip every acknowledgement and return every acknowledged risk to attention.

**Carry-over copies `riskAck` verbatim**, exactly as it copies `typeRisk`; the predicate then decides whether it still matches. Test it: acknowledge a risk, re-run automap, and the acknowledgement is present and still matching.

#### G5 — The engine refreshes the token, so the predicate is plain equality in both languages

Section 3 requires the predicate to be identical in C# and JavaScript, while `HazardClass` is C#-only. That left the browser with no algorithm to mirror, and the obvious shortcut — exact text equality — makes the UI disagree with the server precisely when the class matters, after a rediscovery changes an observed length.

During normalisation, and in carry-over, **when an acknowledgement's class still matches the current risk but its text differs, the engine rewrites `riskAck.risk` to the current text.** `reason` is never touched. The shared predicate then becomes plain string equality in both languages, and the only fuzzy comparison lives in one C# function.

**Order matters:** check `riskClass` first and clear the acknowledgement if the class changed; only then, if the class still matches and the text has drifted, refresh the token. Clearing beats refreshing.

This is the engine touching an author-owned field, deliberately and narrowly: only the token half, and only to re-point an acknowledgement that is still valid at the current wording.

#### G6 — Corrections and precision

- **Attention is the confidence band OR the risk predicate.** Section 3's name collides with the existing confidence-band check and could be read as replacing it. It does not. `IsConfident` uses band-or-risk, which is what it already does.
- **Section 1's examples are swapped.** A field-level edit leaves the old value in the node; a whole-object replace omits it. The rule — present and different from `ctx.Current` means written — is unchanged.
- **Section 2's "no comparison against `ctx.Current`"** is overstated: deciding whether *this* change supplied the acknowledgement requires exactly that one comparison. Nothing else does.
- **The packet emits `riskAck` beside `typeRisk`** on detail columns, not only in the acknowledged list, or an agent in rework sees a risk with no sign it was accepted.
- **Malformed `riskAck` shapes** — a bare string from the old shape, null members, an array — belong in the malformed-payload matrix. It is an object now.
- **Carried artifacts skip `Validate`**, so a malformed `riskAck` can reach the automap job, which catches only deserialisation failures. The predicate must tolerate an odd but well-formed acknowledgement without throwing.
- **The invariant covers type-conversion hazards.** Nullability — a left join feeding a `NOT NULL` target — and join fan-out are real hazards outside this model, and nothing here should be read as covering them.
- **Milestone 4 calls this same predicate** rather than re-deriving "acknowledged", or the project grows a third definition of it. Recorded with the other M4 rulings.

### Task 3.5 JavaScript — correction for the token refresh

The FINAL JavaScript risk scope above says the hazard class is mirrored from the committed C#, with the same stripping rules. **That is superseded.** The engine now refreshes the acknowledgement token: when an acknowledgement still matches its hazard by class but the risk text has drifted, the engine rewrites the stored token to the current text before anything else reads it.

So the browser needs **no hazard class at all**, and must not attempt one. The predicate in JavaScript is plain string equality:

> a column needs review when `typeRisk` is present and non-blank, and not (`riskAck.reason` is non-blank and `riskAck.risk` equals `typeRisk` exactly).

Mirroring a stripping algorithm was the thing that would have made the UI disagree with the server precisely when it mattered — after a rediscovery changes an observed length. With the refresh, both sides compare the same two strings and agree by construction.

Attention remains the confidence band **or** this risk predicate. The risk predicate does not replace the band.

### RISK MODEL AMENDMENT 3 — `riskClass` corrected: no hazard kind, and the two sentinel rules are opposites

Four corrections to `riskClass` itself. Two of them are ways the text and the class end up disagreeing, which is a new failure this field introduced; one removes a component I specified without a source; one makes a sentinel acknowledgeable at all.

#### J1 — The two sentinel rules are deliberately opposite

Two rules mention the sentinel and they must point in opposite directions. I wrote near-identical wording for both, which invites an implementer to apply one rule to the other and make every sentinel column permanently unacknowledgeable.

- **In the predicate**, a sentinel acknowledgement **matches** a sentinel risk. The author named the hazard they accepted; that the hazard is "unevaluable" does not make naming it impossible. Under the plain-equality predicate this falls out naturally — identical text compares equal — and nothing may special-case the sentinel to defeat it.
- **In invalidation**, a newly written sentinel `riskClass` **never equals** the stored one. A changed custom column always loses its old acknowledgement, because nothing about the new expression was evaluated and the old acceptance cannot carry over.

Pin both with shared test vectors: an acknowledged sentinel column stays out of attention while unchanged, and loses the acknowledgement the moment it changes.

#### J2 — Carry-over copies all three fields

G4 required carry-over to copy `riskAck`. It must copy `riskClass` too, and the omission is worse than it looks. `MappingCarryOver` builds each kept column from an explicit field list, so a field not named there is silently dropped, and dropping the class produces two distinct failures:

- a kept custom-expression column emerges with risk **text but no class** — precisely the disagreement this field was introduced to prevent;
- a kept bare column is recomputed, and its fresh class is compared against a stored class the copy just discarded, so absent-versus-value reads as "changed" and the acknowledgement is cleared on **every** re-run.

**Carry-over copies `typeRisk`, `riskClass` and `riskAck` verbatim, then recomputes bare columns under the invalidation rule.**

#### J3 — The auto-mapper writes the class, and an absent class never invalidates

The auto-mapper sets the risk text and nothing else, so every script draft would carry text with no class. The first automap re-run then compares a computed class against a stored null, reads "changed", and clears the acknowledgement — every project re-acknowledging every auto-mapper risk once, with the first rediscovery looking like a regression.

Both halves are required:

- **the auto-mapper writes `riskClass`** alongside the risk, through the same helper the recompute uses, so the text and the class cannot drift apart;
- **an absent stored class counts as unknown and never clears an acknowledgement.** This is needed regardless, for artifacts stored before this model existed.

#### J4 — `riskClass` is declared types only; "hazard kind" is removed

I specified the class as a hazard kind plus the declared types. The kind has no source. `TypeCompat` has no notion of one: it returns a level and a text, and it joins several risk items into a single string. Deriving a kind from that text is the string surgery already rejected; deriving it from the level makes it flip with the unordered sample, which brings back the treadmill this field exists to prevent.

The declared types already identify the conversion completely for a bare column, since the check is a pure function of those types plus the profile.

**`riskClass` is the declared source and target type names for a bare column — for example `varchar(300)->nvarchar(200)` — and the sentinel value otherwise.** Nothing derived from observed data, nothing derived from the level, nothing parsed out of the risk text.

#### J5 — Residual: growing data that proves loss does not re-prompt

Acknowledge a soft truncation risk whose reason cites that the sampled rows fit. A later discovery samples a longer value, the risk hardens, and the text changes — but the declared types are identical, so the class is unchanged and the acknowledgement survives although the evidence it cited has been contradicted.

Noise here runs one way only: a sampled scan can miss long values but cannot invent them, so a soft-to-hard transition is evidence while hard-to-soft is sampling noise. Clearing on the hardening transition would be defensible and monotone. It is **not** adopted, because it reintroduces a level-derived comparison that J4 removes for good reasons, and because the acknowledgement remains visible in the acknowledged list where a human reviews it.

Recorded as a residual alongside section 8's pre-model columns.

#### J6 — Ownership and malformed shapes cover the new field

`riskClass` is engine-owned exactly as `typeRisk` is: an incoming value is ignored, and a value present and differing from `ctx.Current`'s earns a warning naming the column.

The malformed-payload matrix gains: `riskClass` that is not a string; `riskAck` in the old plain-string shape; `riskAck` with null members; `riskAck` as an array. Each must be rejected or tolerated without throwing, on the same full chain as the rest of the matrix.

### RISK MODEL AMENDMENT 4 — carry-over gets its own sequence, and the token refresh stops laundering

"Carry-over follows the same rules" was unimplementable. The token refresh, placed in `Validate`, did nothing but accept inexact acknowledgements. And J3's second half protected exactly the case the class exists to catch. Three corrections, one reversal, two smaller.

#### K1 — Carry-over has its own sequence; G1 and G3 govern `Validate` only

G1 compares against `ctx.Current`, and carry-over has neither a `ctx.Current` nor a patch — a kept column is copied from the previous artifact field for field. Neither literal reading works:

- if kept columns are "not changed", bare columns are **never** recomputed after rediscovery, which silently undoes the recompute and removes the only reason `riskClass` exists;
- if kept columns are "changed", every kept custom expression reaches the catch-all and takes a sentinel — retro-sentinelling every long-approved expression, contradicting section 8, failing the committed carry-over test with seven sentinels, and then having G3 clear every acknowledgement, contradicting G4.

**Carry-over sequence, first match wins:**

```
expr null AND default null   -> no risk
bare single-source reference -> recompute text and class;
                                clear riskAck on a class change, else refresh the token
everything else              -> keep stored typeRisk, riskClass and riskAck verbatim
                                (no sentinel; G3's clearing does not apply)
```

**G1 and G3 apply to `Validate` only.**

#### K2 — The unchanged row keeps the class too

G1's first row says an unchanged column keeps `ctx.Current`'s `typeRisk`. It keeps `riskClass` as well. `riskAck` continues to come from the incoming node, subject to G3. Without this an unchanged column loses its class inside `Validate` and lands in the text-without-class state this field exists to prevent.

#### K3 — The token refresh happens only in carry-over

Inside one `Validate` the catalog does not change, so a genuine acknowledgement's text cannot drift. The only thing a fuzzy refresh does there is **launder an inexact incoming token**: an acknowledgement of `{risk: "may truncate", reason: "fine"}` matches every truncation column by family, is rewritten to the exact current text, and is honoured. One generic phrase sprayed across a patch defeats the naming requirement, and the stored artifact then shows its author quoting texts they never wrote.

**An acknowledgement supplied in a patch must match `typeRisk` exactly.** That is easy rather than harsh, because section 7 pins the attention message so the exact text is there to copy.

**Refresh happens only in carry-over**, where text genuinely drifts because rediscovery re-sampled the data. `Validate` needs no fuzzy comparison at all, and `HazardClass` is therefore used in exactly one place.

#### K4 — Refresh must not cross the sampled boundary; this replaces J5

A soft truncation risk carries a sampled-rows marker; the hard form does not. The declared types are identical either way, so the class is unchanged and the family matches — and a refresh across that boundary rewrites a human's acknowledgement, whose stated reason was that the sampled rows fit, into one that appears to accept a truncation the data has since **proven**.

**Refresh only when the sampled marker is present on both sides, or absent on both sides. A soft-to-hard transition clears the acknowledgement.**

This is monotone and cannot oscillate: a sampled scan can miss long values but cannot invent them, so hardening is evidence while softening is sampling noise.

It replaces J5, which recorded this as a residual. That was decided before it was clear the engine would be actively **rewriting** the acknowledgement rather than merely leaving a stale one standing — a stale acknowledgement is a gap, while a rewritten one puts words in a person's mouth.

#### K5 — A stored risk with no stored class clears; this reverses J3's second half

J3 said an absent stored class counts as unknown and never invalidates. That protects exactly the case the class was introduced to catch: with no class, the check is skipped and refresh proceeds on the text family alone, so a target narrowed from `nvarchar(200)` to `nvarchar(50)` under an unchanged `"may truncate (source max 300)"` keeps its acknowledgement.

**A stored risk text with no stored class clears the acknowledgement. Never refresh it.**

The cost is a one-time re-acknowledgement for artifacts written before this model, and it stops entirely once the auto-mapper writes the class (J3's first half) and carry-over copies it (J2) — both unchanged.

#### K6 — Strike the stale JavaScript instruction

The JavaScript risk scope still tells its implementer to mirror `HazardClass`, "with the same treatment of the sentinel as a class matching nothing else". Both halves are now wrong: the browser needs no class function at all, and read literally that sentence makes a sentinel column impossible to acknowledge, because sentinel would never match sentinel in the predicate.

**That sentence is struck.** The dual rule, stated once: in the **predicate**, sentinel text equals sentinel text and the acknowledgement stands; in the **`riskClass` check**, a newly computed sentinel class never equals the stored one, so a changed custom column always loses its acknowledgement.

### RISK MODEL AMENDMENT 5 — deletions only

Three things in this model are now dead: a rule that cannot fire, a function with no caller, and an instruction that tells an implementer to stop at a decision already taken. This amendment removes them and adds nothing.

It is deliberately a deletion-only amendment. A stopping rule recorded earlier says that a fifth amendment means cutting the acknowledgement mechanism rather than amending again. That rule exists because repeated amendment signalled a wrong design — and removing vestigial parts is the opposite signal. **The rule now reads: if anything after this requires a new rule rather than a removal, the mechanism is cut rather than amended.**

#### L1 — `HazardClass` is deleted

It has no caller. The predicate became plain string equality; K3 moved the token refresh into carry-over only, and there the comparison is the stored `riskClass` against the recomputed `riskClass`. Nothing in either language calls a text-derived class.

Remove it from `TypeCompat`, and remove any JavaScript mirror. The normalised-text idea it embodied — stripping observed lengths and sample suffixes — is not needed once `riskClass` carries declared types and the refresh is confined to carry-over.

#### L2 — Section 3's formula and the stop-instruction are struck

Section 3 still writes the predicate in terms of `HazardClass`. **Struck.** The predicate is:

> a column needs review when `typeRisk` is present and non-blank, and not (`riskAck.reason` is non-blank and `riskAck.risk` equals `typeRisk` exactly).

Plain string equality, identical in C# and JavaScript, reading nothing but the column.

The `riskClass` amendment ends with an instruction to stop if the two comparisons are ever collapsed into one, and to say which property was lost. K3 collapsed them deliberately, and the property — tolerance of text drift inside a single validation — turned out not to exist, because a catalogue cannot change mid-validation. **That instruction is struck.** Nobody should halt on it.

#### L3 — The sentinel special case is deleted

J1 gave the sentinel `riskClass` a comparator that never equals anything, including another sentinel. That rule cannot fire. A sentinel class is computed only in Validate's catch-all, which only a **changed** column reaches, and G3 already clears the acknowledgement on any change — so it could only ever apply to a column whose acknowledgement was already gone. Carry-over never computes a sentinel at all, because K1 copies non-bare kept columns verbatim.

**The sentinel `riskClass` is a plain constant, compared by plain equality like any other class.** There is now no sentinel special case anywhere in the model, in either language.

This also removes a trap: applying that never-equal comparator to every column, rather than only to columns whose class was computed in the current pass, would have compared an unchanged sentinel column's stored class with itself, called it changed, and cleared the acknowledgement on every save.

#### L4 — One constant each, for a later milestone to reference

The sentinel risk text and the sentinel `riskClass` value each live in a single C# constant, placed where a later milestone can reference them. Milestone 4 must recognise the sentinel so it does not render one as a data-loss warning, and it must reference the constant rather than string-matching a copy of the text.
