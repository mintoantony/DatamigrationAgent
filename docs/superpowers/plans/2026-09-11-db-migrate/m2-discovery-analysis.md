# Milestone 2 — Discovery & Analysis

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** after both connections are saved, a server job extracts and profiles both catalogs, fingerprints them and builds the local vector index (DISCOVERY); a second job runs 16 deterministic rules and writes the v0 analysis draft; the `schema-analyst` subagent adds the narrative and per-finding commentary through the M1 patch loop; the human reviews everything on the Analysis screen (KPIs, findings, per-table drill-down with profiles, FK graph, anchored comments) and can export it as one offline HTML file. `dbm search` / `dbm show` give agents cheap, targeted context. Everything is tested against the LegacyShop → ShopV2 fixture pair defined here (C15).

**Conventions used in every task of this milestone**

- Paths are relative to the repository root. `ENGINE` = `plugins/db-migrate/engine`.
- Unit tests: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration&FullyQualifiedName~<Namespace>"`. Integration tests (SQL Server via `DBM_TEST_SQL`, default LocalDB): same command with `Category=Integration`.
- Test helpers introduced here (T2.1) and reused later: `SampleDatabases`/`SamplePair`/`SamplePairFixture` (seeded fixture pair), `TempProject` (temporary workspace + `DbmServices` with an initialised project), `SampleProject.CreateAsync(pair)` (TempProject whose connections point at a pair), and `TestCatalogs` (T2.2; in-code structural copies of both sample catalogs for unit tests).
- M1 test support used here: `TestWorkspace` (temporary folder + `OpenServices()`), `TempDatabase.CreateAsync(prefix)` (unique `<prefix>_<8 hex>` database, dropped on dispose), `CliRunner.RunAsync(ws, factory, args…)` (runs `CliApp` in-process with `--workspace`), `WebTestServer.StartAsync(ws, factory)` (in-process `WebHost` on a free port, client carries the token). Integration databases are never dropped by name pattern — each test disposes exactly the `TempDatabase` it created.
- CLI commands are exercised through `CliRunner` (no process, no server); commands open services with `CliContext.RequireProject()` / `OpenServices(ws)` so the `ServicesFactory` test seam and the `no_project` error behave like the M1 commands.
- Commit commands use Git Bash on branch `feat/db-migrate`.

## Task overview

| Task | Delivers |
|---|---|
| 2.1 | Sample SQL (C15) embedded in Dbm.dll, `SampleSql`, fixture/test-project helpers |
| 2.2 | `CatalogModel` (C10) + `CatalogExtractor`, `TestCatalogs` |
| 2.3 | `ValueSignature` + `Profiler` |
| 2.4 | `SparseVector`, `Synonyms` + `synonyms.json`, `NameNormalizer`, `NgramTfidfVectorizer`, `VectorIndex` |
| 2.5 | `Fingerprint`, `CatalogRepo`, `DriftChecker`, `DiscoverJob`, `CatalogText`, drift approval guard, `discover` / `search` / `show` |
| 2.6 | `Finding`, `AnalysisPayload`, 16 rules, `Analyzer`, `AnalyzeJob` |
| 2.7 | `AnalysisModule` + `schema-analyst` agent + end-to-end analysis loop test |
| 2.8 | Catalog/search/export endpoints, `WebExport`, `lib/graph.js`, Analysis view |

---

### Task 2.1: Sample databases + SQL test fixtures

**Files:**
- Create: `plugins/db-migrate/samples/legacyshop-to-shopv2/legacyshop.sql`, `shopv2.sql`, `seed.sql`, `README.md`
- Create: `ENGINE/Dbm/Core/Samples/SampleSql.cs`
- Modify: `ENGINE/Dbm/Dbm.csproj` (embed the three `.sql` files)
- Create: `ENGINE/Dbm.Tests/Support/SampleDatabases.cs`
- Test: `ENGINE/Dbm.Tests/Unit/Samples/SampleSqlTests.cs`, `ENGINE/Dbm.Tests/Integration/Samples/SampleDatabasesTests.cs`

**Interfaces:**
- Consumes: `TempDatabase.CreateAsync(string prefix)` / `ConnectionString` / `DisposeAsync()`, `TestWorkspace` (M1 test support), `Workspace`, `DbmServices.Open`, `ProjectRepo.Exists/Init`, `ConnectionRepo.Save`, `SqlConnect.ProbeAsync` (C1–C3).
- Produces:
  ```csharp
  namespace Dbm.Core.Samples;
  public static class SampleSql
  {
      public const string UntrustedForeignKey;                 // last statement of seed.sql (FK_ORD_CUST WITH NOCHECK)
      public static string LegacyShopSchema { get; }
      public static string ShopV2Schema { get; }
      public static string Seed(int scale);                     // "$(scale)" replaced; scale >= 1
      public static IReadOnlyList<string> SplitBatches(string sql);   // split on lines that contain only GO
      public static Task ExecuteAsync(SqlConnection conn, string script, CancellationToken ct);   // runs every batch (600 s timeout)
  }
  namespace Dbm.Tests.Support;
  public sealed class SamplePair : IAsyncDisposable { TempDatabase Source; TempDatabase Target; string SourceCs; string TargetCs; }
  public static class SampleDatabases { Task<SamplePair> CreateAsync(int scale = 1, bool seed = true); Task RunAsync(string cs, string script); }
  public sealed class SamplePairFixture : IAsyncLifetime { SamplePair Pair; }             // one seeded pair per test class
  public sealed class TempProject : IDisposable { string Root; DbmServices Services; Workspace Ws; static TempProject Create(string name = "test"); }
  public static class SampleProject { Task<TempProject> CreateAsync(SamplePair pair); }  // project "sample", both connections saved
  ```
- **Exact fixture counts** (normative for M3–M6 tests): at scale `s` — CUST 1000·s, ADDR 1500·s, PROD 200, ORD_STATUS 4, **ORD_HDR 3000·s + 5**, **ORD_LINE 9000·s + 2**, AUDIT_LOG 5000·s, TMP_IMPORT 0. The +5 orders are the fixed anomalies: ORD_ID 3000·s+1 and +2 are orphans (CUST_ID −1 / −2, one line each); ORD_ID 3000·s+3..+5 carry a 300-character CMNT and have **no lines** (so the C15 "8 rejected rows" outcome holds). The zero-quantity line is ORD_ID 1 / LINE_NO 1. EMAIL_ADDR is NULL for exactly 10 % of customers (every 10th), FAX_NO for 98 %.

- [ ] **Step 1: Write the failing unit test**

`ENGINE/Dbm.Tests/Unit/Samples/SampleSqlTests.cs`
```csharp
using Dbm.Core.Samples;

namespace Dbm.Tests.Unit.Samples;

public sealed class SampleSqlTests
{
    [Fact]
    public void SplitBatches_splits_on_lines_that_contain_only_GO()
    {
        var batches = SampleSql.SplitBatches("CREATE SCHEMA app;\r\nGO\nSELECT 'GO' AS x; -- GO\n  go  -- trailing comment\n\nGO\n");
        Assert.Equal(new[] { "CREATE SCHEMA app;", "SELECT 'GO' AS x; -- GO" }, batches);
    }

    [Fact]
    public void Embedded_scripts_are_present()
    {
        Assert.Contains("CREATE TABLE dbo.CUST", SampleSql.LegacyShopSchema);
        Assert.Contains("CREATE TABLE dbo.TMP_IMPORT", SampleSql.LegacyShopSchema);
        Assert.Contains("CREATE TRIGGER app.trg_Orders_Audit", SampleSql.ShopV2Schema);
        Assert.Equal(3, SampleSql.SplitBatches(SampleSql.ShopV2Schema).Count);
    }

    [Fact]
    public void Seed_replaces_the_scale_token_and_ends_with_the_untrusted_fk()
    {
        var seed = SampleSql.Seed(3);
        Assert.DoesNotContain("$(scale)", seed);
        Assert.Contains("DECLARE @scale int = 3;", seed);
        Assert.EndsWith(SampleSql.UntrustedForeignKey, seed.TrimEnd());
    }

    [Fact]
    public void Seed_rejects_scale_below_one() => Assert.Throws<ArgumentOutOfRangeException>(() => SampleSql.Seed(0));
}
```

- [ ] **Step 2: Write the fixture helpers and the failing integration test**

`ENGINE/Dbm.Tests/Support/SampleDatabases.cs`
```csharp
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>A LegacyShop (source) + ShopV2 (target) pair of temporary databases on the test server.</summary>
public sealed class SamplePair : IAsyncDisposable
{
    public required TempDatabase Source { get; init; }
    public required TempDatabase Target { get; init; }
    public string SourceCs => Source.ConnectionString;
    public string TargetCs => Target.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await Target.DisposeAsync();
    }
}

public static class SampleDatabases
{
    /// <summary>Creates both databases from the embedded scripts; seeds LegacyShop at <paramref name="scale"/> when <paramref name="seed"/>.</summary>
    public static async Task<SamplePair> CreateAsync(int scale = 1, bool seed = true)
    {
        var source = await TempDatabase.CreateAsync("legacyshop");
        TempDatabase? target = null;
        try
        {
            target = await TempDatabase.CreateAsync("shopv2");
            await RunAsync(source.ConnectionString, SampleSql.LegacyShopSchema);
            await RunAsync(source.ConnectionString, seed ? SampleSql.Seed(scale) : SampleSql.UntrustedForeignKey);
            await RunAsync(target.ConnectionString, SampleSql.ShopV2Schema);
            return new SamplePair { Source = source, Target = target };
        }
        catch
        {
            await source.DisposeAsync();
            if (target is not null) await target.DisposeAsync();
            throw;
        }
    }

    public static async Task RunAsync(string connectionString, string script)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await SampleSql.ExecuteAsync(conn, script, CancellationToken.None);
    }
}

/// <summary>xUnit class fixture: one seeded (scale 1) sample pair shared by all tests of a class.</summary>
public sealed class SamplePairFixture : IAsyncLifetime
{
    public SamplePair Pair { get; private set; } = null!;
    public async Task InitializeAsync() => Pair = await SampleDatabases.CreateAsync();
    public async Task DisposeAsync() => await Pair.DisposeAsync();
}

/// <summary>
/// A <see cref="TestWorkspace"/> with an initialised project named <c>name</c>, opened through DbmServices with the real
/// registries (M2 modules and jobs included). Disposing it disposes the services and deletes the folder.
/// </summary>
public sealed class TempProject : IDisposable
{
    private readonly TestWorkspace _workspace;

    private TempProject(TestWorkspace workspace, DbmServices services)
    {
        _workspace = workspace;
        Services = services;
    }

    public string Root => _workspace.Root;
    public Workspace Ws => _workspace.Ws;
    public DbmServices Services { get; }

    public static TempProject Create(string name = "test")
    {
        var workspace = new TestWorkspace();
        workspace.Ws.EnsureCreated();
        var services = DbmServices.Open(workspace.Ws);
        if (!services.Project.Exists()) services.Project.Init(name);
        return new TempProject(workspace, services);
    }

    public void Dispose()
    {
        Services.Dispose();
        _workspace.Dispose();
    }
}

public static class SampleProject
{
    /// <summary>TempProject named "sample" whose src/tgt connections point at <paramref name="pair"/> (probed + saved).</summary>
    public static async Task<TempProject> CreateAsync(SamplePair pair)
    {
        var project = TempProject.Create("sample");
        project.Services.Connections.Save(Side.Src, pair.SourceCs, await SqlConnect.ProbeAsync(pair.SourceCs, CancellationToken.None));
        project.Services.Connections.Save(Side.Tgt, pair.TargetCs, await SqlConnect.ProbeAsync(pair.TargetCs, CancellationToken.None));
        return project;
    }
}
```

`ENGINE/Dbm.Tests/Integration/Samples/SampleDatabasesTests.cs`
```csharp
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Samples;

[Trait("Category", "Integration")]
public sealed class SampleDatabasesTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("dbo.CUST", 1000)]
    [InlineData("dbo.ADDR", 1500)]
    [InlineData("dbo.PROD", 200)]
    [InlineData("dbo.ORD_STATUS", 4)]
    [InlineData("dbo.ORD_HDR", 3005)]
    [InlineData("dbo.ORD_LINE", 9002)]
    [InlineData("dbo.AUDIT_LOG", 5000)]
    [InlineData("dbo.TMP_IMPORT", 0)]
    public async Task Source_row_counts_at_scale_1(string table, long expected) =>
        Assert.Equal(expected, await ScalarAsync(fixture.Pair.SourceCs, $"SELECT COUNT_BIG(*) FROM {table}"));

    [Fact]
    public async Task Two_orphan_orders_each_with_one_line()
    {
        const string orphans = "FROM dbo.ORD_HDR AS h WHERE NOT EXISTS (SELECT 1 FROM dbo.CUST AS c WHERE c.CUST_ID = h.CUST_ID)";
        Assert.Equal(2, await ScalarAsync(fixture.Pair.SourceCs, $"SELECT COUNT_BIG(*) {orphans}"));
        Assert.Equal(2, await ScalarAsync(fixture.Pair.SourceCs,
            $"SELECT COUNT_BIG(*) FROM dbo.ORD_LINE AS l WHERE l.ORD_ID IN (SELECT h.ORD_ID {orphans})"));
    }

    [Fact]
    public async Task Three_orders_have_a_300_character_comment_and_no_lines()
    {
        Assert.Equal(3, await ScalarAsync(fixture.Pair.SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_HDR WHERE LEN(CMNT) = 300"));
        Assert.Equal(0, await ScalarAsync(fixture.Pair.SourceCs,
            "SELECT COUNT_BIG(*) FROM dbo.ORD_LINE AS l JOIN dbo.ORD_HDR AS h ON h.ORD_ID = l.ORD_ID WHERE LEN(h.CMNT) = 300"));
    }

    [Fact]
    public async Task Exactly_one_line_has_zero_quantity() =>
        Assert.Equal(1, await ScalarAsync(fixture.Pair.SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_LINE WHERE QTY = 0"));

    [Fact]
    public async Task Fk_ord_cust_is_not_trusted() =>
        Assert.Equal(1, await ScalarAsync(fixture.Pair.SourceCs,
            "SELECT CAST(is_not_trusted AS int) FROM sys.foreign_keys WHERE name = 'FK_ORD_CUST'"));

    [Fact]
    public async Task Ten_percent_of_emails_are_null() =>
        Assert.Equal(100, await ScalarAsync(fixture.Pair.SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST WHERE EMAIL_ADDR IS NULL"));

    [Fact]
    public async Task Target_is_empty_and_has_the_audit_trigger()
    {
        Assert.Equal(6, await ScalarAsync(fixture.Pair.TargetCs,
            "SELECT COUNT_BIG(*) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = 'app'"));
        Assert.Equal(0, await ScalarAsync(fixture.Pair.TargetCs, "SELECT COUNT_BIG(*) FROM app.Customers"));
        Assert.Equal(1, await ScalarAsync(fixture.Pair.TargetCs, "SELECT COUNT_BIG(*) FROM sys.triggers WHERE name = 'trg_Orders_Audit'"));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Samples"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Samples' does not exist in the namespace 'Dbm.Core'`.

- [ ] **Step 4: Write the sample scripts**

`plugins/db-migrate/samples/legacyshop-to-shopv2/legacyshop.sql`
```sql
-- LegacyShop: source side of the db-migrate sample pair (schema dbo).
-- Run against an existing, empty database. Batches are separated by GO.
SET NOCOUNT ON;

CREATE TABLE dbo.CUST (
  CUST_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CUST PRIMARY KEY,
  CUST_NM varchar(100) NOT NULL,
  EMAIL_ADDR varchar(120) NULL,
  PHONE_NO varchar(30) NULL,
  DOB datetime NULL,
  CRT_DT datetime NOT NULL CONSTRAINT DF_CUST_CRT_DT DEFAULT (getdate()),
  FAX_NO varchar(30) NULL,
  NOTES text NULL);

CREATE TABLE dbo.ADDR (
  ADDR_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ADDR PRIMARY KEY,
  CUST_ID int NOT NULL CONSTRAINT FK_ADDR_CUST REFERENCES dbo.CUST (CUST_ID),
  LINE1 varchar(200) NOT NULL,
  CITY varchar(80) NOT NULL,
  ZIP varchar(12) NULL,
  CTRY_CD char(2) NOT NULL);

CREATE TABLE dbo.PROD (
  PROD_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PROD PRIMARY KEY,
  PROD_NM varchar(150) NOT NULL,
  PROD_DESC varchar(500) NULL,
  UNIT_PRC money NOT NULL,
  ACTIVE_FLG char(1) NOT NULL);

CREATE TABLE dbo.ORD_STATUS (
  STATUS_ID tinyint NOT NULL CONSTRAINT PK_ORD_STATUS PRIMARY KEY,
  STATUS_CD varchar(10) NOT NULL,
  STATUS_DESC varchar(50) NOT NULL);

CREATE TABLE dbo.ORD_HDR (
  ORD_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ORD_HDR PRIMARY KEY,
  CUST_ID int NOT NULL,
  ORD_DT datetime NOT NULL,
  STATUS_ID tinyint NOT NULL CONSTRAINT FK_ORD_STATUS REFERENCES dbo.ORD_STATUS (STATUS_ID),
  SHIP_ADDR_ID int NULL,
  TOTAL_AMT money NOT NULL,
  CMNT varchar(500) NULL);

CREATE TABLE dbo.ORD_LINE (
  ORD_ID int NOT NULL CONSTRAINT FK_LINE_ORD REFERENCES dbo.ORD_HDR (ORD_ID),
  LINE_NO smallint NOT NULL,
  PROD_ID int NOT NULL CONSTRAINT FK_LINE_PROD REFERENCES dbo.PROD (PROD_ID),
  QTY int NOT NULL,
  UNIT_PRC money NOT NULL,
  CONSTRAINT PK_ORD_LINE PRIMARY KEY (ORD_ID, LINE_NO));

CREATE TABLE dbo.AUDIT_LOG (
  LOG_TS datetime NOT NULL,
  USR varchar(50) NOT NULL,
  ACTION_TXT varchar(200) NOT NULL);

CREATE TABLE dbo.TMP_IMPORT (X int NULL);
GO
```

`plugins/db-migrate/samples/legacyshop-to-shopv2/shopv2.sql`
```sql
-- ShopV2: target side of the db-migrate sample pair (schema app).
-- Run against an existing, empty database. Batches are separated by GO.
CREATE SCHEMA app;
GO
SET NOCOUNT ON;

CREATE TABLE app.Customers (
  CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
  FirstName nvarchar(50) NOT NULL,
  LastName nvarchar(50) NOT NULL,
  Email nvarchar(120) NULL,
  Phone nvarchar(30) NULL,
  BirthDate date NULL,
  CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (sysutcdatetime()),
  PrimaryAddressId int NULL,
  Notes nvarchar(max) NULL,
  DisplayName AS (FirstName + N' ' + LastName));

CREATE TABLE app.Addresses (
  AddressId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Addresses PRIMARY KEY,
  CustomerId int NOT NULL CONSTRAINT FK_Addresses_Customers REFERENCES app.Customers (CustomerId),
  Line1 nvarchar(200) NOT NULL,
  City nvarchar(80) NOT NULL,
  PostalCode nvarchar(12) NULL,
  CountryCode char(2) NOT NULL);

ALTER TABLE app.Customers ADD CONSTRAINT FK_Customers_PrimaryAddress
  FOREIGN KEY (PrimaryAddressId) REFERENCES app.Addresses (AddressId);

CREATE TABLE app.Products (
  ProductId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
  Name nvarchar(150) NOT NULL,
  Description nvarchar(500) NULL,
  UnitPrice decimal(19,4) NOT NULL,
  IsActive bit NOT NULL,
  RowVer rowversion NOT NULL);

CREATE TABLE app.Orders (
  OrderId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
  CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES app.Customers (CustomerId),
  OrderDate datetime2(0) NOT NULL,
  StatusCode varchar(10) NOT NULL,
  ShippingAddressId int NULL CONSTRAINT FK_Orders_Addresses REFERENCES app.Addresses (AddressId),
  TotalAmount decimal(19,4) NOT NULL,
  Comment nvarchar(200) NULL);

CREATE TABLE app.OrderLines (
  OrderId int NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES app.Orders (OrderId),
  LineNumber smallint NOT NULL,
  ProductId int NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES app.Products (ProductId),
  Quantity int NOT NULL CONSTRAINT CK_OrderLines_Quantity CHECK (Quantity > 0),
  UnitPrice decimal(19,4) NOT NULL,
  CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));

CREATE TABLE app.AuditEvents (
  EventTime datetime2(3) NOT NULL,
  UserName nvarchar(50) NOT NULL,
  Action nvarchar(200) NOT NULL);
GO
CREATE TRIGGER app.trg_Orders_Audit ON app.Orders AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
GO
```

`plugins/db-migrate/samples/legacyshop-to-shopv2/seed.sql` (one batch; the last line must stay exactly equal to `SampleSql.UntrustedForeignKey`)
```sql
-- LegacyShop seed data. Deterministic and set-based; $(scale) is replaced by SampleSql.Seed(scale)
-- (or by sqlcmd -v scale=N). Row counts: CUST 1000*scale, ADDR 1500*scale, PROD 200, ORD_STATUS 4,
-- ORD_HDR 3000*scale + 5, ORD_LINE 9000*scale + 2, AUDIT_LOG 5000*scale, TMP_IMPORT 0.
-- Fixed anomalies (independent of scale):
--   * 2 orphan orders (CUST_ID -1 and -2, no such customer), 1 line each;
--   * 3 orders whose CMNT is exactly 300 characters (valid customers, no lines);
--   * 1 order line with QTY = 0 (ORD_ID 1, LINE_NO 1);
--   * FK_ORD_CUST added WITH NOCHECK at the end (untrusted).
SET NOCOUNT ON;
DECLARE @scale int = $(scale);
DECLARE @cust int = 1000 * @scale, @addr int = 1500 * @scale, @ord int = 3000 * @scale,
        @lines int = 9000 * @scale, @audit int = 5000 * @scale;
DECLARE @max int = 9000 * @scale;

CREATE TABLE #n (i int NOT NULL PRIMARY KEY);
WITH nums AS (
  SELECT TOP (@max) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
  FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c)
INSERT INTO #n (i) SELECT CAST(i AS int) FROM nums;

INSERT INTO dbo.ORD_STATUS (STATUS_ID, STATUS_CD, STATUS_DESC)
VALUES (1, 'NEW', 'New order'), (2, 'PAID', 'Paid'), (3, 'SHIPPED', 'Shipped'), (4, 'CANCELLED', 'Cancelled');

SET IDENTITY_INSERT dbo.CUST ON;
INSERT INTO dbo.CUST (CUST_ID, CUST_NM, EMAIL_ADDR, PHONE_NO, DOB, CRT_DT, FAX_NO, NOTES)
SELECT n.i,
       CONCAT('First', n.i, ' Last', n.i),
       CASE WHEN n.i % 10 = 0 THEN NULL ELSE CONCAT('first', n.i, '.last', n.i, '@example.com') END,
       CASE WHEN n.i % 7 = 0 THEN NULL ELSE CONCAT('+1-555-', RIGHT(CONCAT('0000', n.i % 10000), 4)) END,
       CASE WHEN n.i % 5 = 0 THEN NULL ELSE DATEADD(DAY, (n.i * 37) % 18000, CAST('1950-01-01' AS datetime)) END,
       DATEADD(MINUTE, n.i * 17, CAST('2015-01-01' AS datetime)),
       CASE WHEN n.i % 50 = 0 THEN CONCAT('+1-555-9', RIGHT(CONCAT('000', n.i % 1000), 3)) ELSE NULL END,
       CASE WHEN n.i % 4 = 0 THEN CONCAT('Customer note ', n.i) ELSE NULL END
FROM #n AS n WHERE n.i <= @cust ORDER BY n.i;
SET IDENTITY_INSERT dbo.CUST OFF;

SET IDENTITY_INSERT dbo.ADDR ON;
INSERT INTO dbo.ADDR (ADDR_ID, CUST_ID, LINE1, CITY, ZIP, CTRY_CD)
SELECT n.i,
       CASE WHEN n.i <= @cust THEN n.i ELSE (n.i - @cust) * 2 - 1 END,
       CONCAT((n.i * 7) % 999 + 1, ' ', CHOOSE(n.i % 5 + 1, 'Main Street', 'High Street', 'Park Avenue', 'Oak Road', 'Mill Lane')),
       CHOOSE(n.i % 8 + 1, 'London', 'Paris', 'Berlin', 'Madrid', 'Rome', 'Dublin', 'Lisbon', 'Vienna'),
       CASE WHEN n.i % 20 = 0 THEN NULL ELSE RIGHT(CONCAT('00000', (n.i * 37) % 99999), 5) END,
       CHOOSE(n.i % 8 + 1, 'GB', 'FR', 'DE', 'ES', 'IT', 'IE', 'PT', 'AT')
FROM #n AS n WHERE n.i <= @addr ORDER BY n.i;
SET IDENTITY_INSERT dbo.ADDR OFF;

SET IDENTITY_INSERT dbo.PROD ON;
INSERT INTO dbo.PROD (PROD_ID, PROD_NM, PROD_DESC, UNIT_PRC, ACTIVE_FLG)
SELECT n.i,
       CONCAT('Product ', n.i),
       CASE WHEN n.i % 3 = 0 THEN NULL ELSE CONCAT('Description of product ', n.i) END,
       CAST(((n.i * 725) % 50000) / 100.0 + 0.99 AS money),
       CASE WHEN n.i % 10 = 0 THEN 'N' ELSE 'Y' END
FROM #n AS n WHERE n.i <= 200 ORDER BY n.i;
SET IDENTITY_INSERT dbo.PROD OFF;

SET IDENTITY_INSERT dbo.ORD_HDR ON;
INSERT INTO dbo.ORD_HDR (ORD_ID, CUST_ID, ORD_DT, STATUS_ID, SHIP_ADDR_ID, TOTAL_AMT, CMNT)
SELECT n.i,
       (n.i * 7) % @cust + 1,
       DATEADD(MINUTE, n.i * 53, CAST('2020-01-01' AS datetime)),
       n.i % 4 + 1,
       CASE WHEN n.i % 6 = 0 THEN NULL ELSE (n.i * 7) % @cust + 1 END,
       0,
       CASE WHEN n.i % 9 = 0 THEN 'Leave at reception' WHEN n.i % 11 = 0 THEN 'Gift wrap' ELSE NULL END
FROM #n AS n WHERE n.i <= @ord ORDER BY n.i;
-- anomalies: 2 orphan orders, 3 orders with a 300-character comment
INSERT INTO dbo.ORD_HDR (ORD_ID, CUST_ID, ORD_DT, STATUS_ID, SHIP_ADDR_ID, TOTAL_AMT, CMNT)
VALUES (@ord + 1, -1, CAST('2024-06-01T10:00:00' AS datetime), 1, NULL, 10.00, 'Orphan order 1'),
       (@ord + 2, -2, CAST('2024-06-02T10:00:00' AS datetime), 1, NULL, 10.00, 'Orphan order 2'),
       (@ord + 3, 1, CAST('2024-06-03T10:00:00' AS datetime), 2, 1, 10.00, LEFT(REPLICATE(CAST('Long comment. ' AS varchar(500)), 25), 300)),
       (@ord + 4, 2, CAST('2024-06-04T10:00:00' AS datetime), 2, 2, 10.00, LEFT(REPLICATE(CAST('Long comment. ' AS varchar(500)), 25), 300)),
       (@ord + 5, 3, CAST('2024-06-05T10:00:00' AS datetime), 2, 3, 10.00, LEFT(REPLICATE(CAST('Long comment. ' AS varchar(500)), 25), 300));
SET IDENTITY_INSERT dbo.ORD_HDR OFF;

INSERT INTO dbo.ORD_LINE (ORD_ID, LINE_NO, PROD_ID, QTY, UNIT_PRC)
SELECT (n.i - 1) / 3 + 1, (n.i - 1) % 3 + 1, p.PROD_ID, n.i % 5 + 1, p.UNIT_PRC
FROM #n AS n JOIN dbo.PROD AS p ON p.PROD_ID = (n.i * 13) % 200 + 1
WHERE n.i <= @lines;
INSERT INTO dbo.ORD_LINE (ORD_ID, LINE_NO, PROD_ID, QTY, UNIT_PRC)
SELECT @ord + 1, 1, PROD_ID, 1, UNIT_PRC FROM dbo.PROD WHERE PROD_ID = 1
UNION ALL
SELECT @ord + 2, 1, PROD_ID, 1, UNIT_PRC FROM dbo.PROD WHERE PROD_ID = 2;
UPDATE dbo.ORD_LINE SET QTY = 0 WHERE ORD_ID = 1 AND LINE_NO = 1;

UPDATE h SET TOTAL_AMT = t.AMT
FROM dbo.ORD_HDR AS h
JOIN (SELECT ORD_ID, SUM(QTY * UNIT_PRC) AS AMT FROM dbo.ORD_LINE GROUP BY ORD_ID) AS t ON t.ORD_ID = h.ORD_ID
WHERE h.ORD_ID <= @ord;

INSERT INTO dbo.AUDIT_LOG (LOG_TS, USR, ACTION_TXT)
SELECT DATEADD(SECOND, n.i * 37, CAST('2021-01-01' AS datetime)),
       CHOOSE(n.i % 5 + 1, 'admin', 'jsmith', 'mlee', 'svc_import', 'kpatel'),
       CHOOSE(n.i % 4 + 1, 'LOGIN', CONCAT('UPDATE CUST ', n.i % @cust + 1), CONCAT('CREATE ORDER ', n.i % @ord + 1), 'LOGOUT')
FROM #n AS n WHERE n.i <= @audit;

DROP TABLE #n;

ALTER TABLE dbo.ORD_HDR WITH NOCHECK ADD CONSTRAINT FK_ORD_CUST FOREIGN KEY (CUST_ID) REFERENCES dbo.CUST (CUST_ID);
```

`plugins/db-migrate/samples/legacyshop-to-shopv2/README.md`
```markdown
# LegacyShop → ShopV2 sample pair

A small, deterministic source/target pair used by the db-migrate integration tests and by `dbm demo`.

| File | Run against | Purpose |
|---|---|---|
| `legacyshop.sql` | empty source database | LegacyShop schema (`dbo`): 8 tables, one heap (`AUDIT_LOG`), one empty table (`TMP_IMPORT`) |
| `seed.sql` | LegacyShop, after `legacyshop.sql` | Data for scale `$(scale)`, then `FK_ORD_CUST` added `WITH NOCHECK` |
| `shopv2.sql` | empty target database | ShopV2 schema (`app`): renamed/split columns, FK cycle Customers ↔ Addresses, computed column, rowversion, trigger |

Row counts at scale `s`: CUST 1000·s, ADDR 1500·s, PROD 200, ORD_STATUS 4, ORD_HDR 3000·s + 5, ORD_LINE 9000·s + 2,
AUDIT_LOG 5000·s, TMP_IMPORT 0.

Deliberate anomalies (same at every scale):

- 2 orphan orders (`CUST_ID` −1 and −2) with one line each — rejected by the target FK;
- 3 orders with a 300-character `CMNT` (target `Comment` is `nvarchar(200)`) and no lines — rejected as truncation;
- 1 order line with `QTY = 0` — rejected by `CK_OrderLines_Quantity`;
- `FK_ORD_CUST` is untrusted, `EMAIL_ADDR` is 10 % NULL, `FAX_NO` 98 % NULL, `NOTES` uses the deprecated `text` type.

With the approved ground-truth mapping and "skip and log", a transfer rejects exactly 8 rows.

Manual setup with sqlcmd (integrated auth, LocalDB shown):

    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "CREATE DATABASE LegacyShop; CREATE DATABASE ShopV2;"
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d LegacyShop -b -i legacyshop.sql
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d LegacyShop -b -v scale=1 -i seed.sql
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d ShopV2 -b -i shopv2.sql

The scripts are embedded in `Dbm.dll` (`Dbm.Core.Samples.SampleSql`), so `dbm demo` needs no files on disk.
```

- [ ] **Step 5: Embed the scripts in Dbm.dll**

In `ENGINE/Dbm/Dbm.csproj`, add this `ItemGroup` directly before the closing `</Project>` tag (the path is relative to `ENGINE/Dbm/`, i.e. it resolves to `plugins/db-migrate/samples/legacyshop-to-shopv2/`):
```xml
  <ItemGroup>
    <EmbeddedResource Include="..\..\samples\legacyshop-to-shopv2\*.sql" LogicalName="samples/legacyshop-to-shopv2/%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 6: Implement SampleSql**

`ENGINE/Dbm/Core/Samples/SampleSql.cs`
```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Samples;

/// <summary>The LegacyShop → ShopV2 sample scripts (samples/legacyshop-to-shopv2/*.sql), embedded into Dbm.dll.</summary>
public static class SampleSql
{
    private const string ResourcePrefix = "samples/legacyshop-to-shopv2/";

    /// <summary>The last statement of seed.sql; run on its own when the source is created without data.</summary>
    public const string UntrustedForeignKey =
        "ALTER TABLE dbo.ORD_HDR WITH NOCHECK ADD CONSTRAINT FK_ORD_CUST FOREIGN KEY (CUST_ID) REFERENCES dbo.CUST (CUST_ID);";

    private static readonly Regex GoLine = new(@"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string LegacyShopSchema => Load("legacyshop.sql");

    public static string ShopV2Schema => Load("shopv2.sql");

    /// <summary>seed.sql with every <c>$(scale)</c> token replaced by <paramref name="scale"/>.</summary>
    public static string Seed(int scale)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be >= 1");
        return Load("seed.sql").Replace("$(scale)", scale.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Splits a script on lines that contain only <c>GO</c> (case-insensitive, optional trailing comment). Empty batches are dropped.</summary>
    public static IReadOnlyList<string> SplitBatches(string sql)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (GoLine.IsMatch(line))
            {
                Flush();
                continue;
            }
            current.Append(line).Append('\n');
        }
        Flush();
        return batches;

        void Flush()
        {
            var batch = current.ToString().Trim();
            if (batch.Length > 0) batches.Add(batch);
            current.Clear();
        }
    }

    /// <summary>Runs every batch of <paramref name="script"/> on an open connection (10 minute timeout per batch).</summary>
    public static async Task ExecuteAsync(SqlConnection conn, string script, CancellationToken ct)
    {
        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string Load(string fileName)
    {
        using var stream = typeof(SampleSql).Assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"Embedded sample script '{ResourcePrefix}{fileName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Samples"`
Expected: PASS (4 tests).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~Dbm.Tests.Integration.Samples"`
Expected: PASS (14 tests; the fixture creates and seeds both databases in about a second on LocalDB).

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/samples plugins/db-migrate/engine/Dbm/Core/Samples plugins/db-migrate/engine/Dbm/Dbm.csproj \
  plugins/db-migrate/engine/Dbm.Tests/Support/SampleDatabases.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Samples \
  plugins/db-migrate/engine/Dbm.Tests/Integration/Samples
git commit -F - <<'EOF'
feat(samples): LegacyShop/ShopV2 fixture pair embedded in Dbm.dll

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.2: Catalog model + extractor

**Files:**
- Create: `ENGINE/Dbm/Core/Catalog/CatalogModel.cs`, `ENGINE/Dbm/Core/Catalog/CatalogExtractor.cs`
- Create: `ENGINE/Dbm.Tests/Support/TestCatalogs.cs`
- Test: `ENGINE/Dbm.Tests/Unit/Catalog/CatalogModelTests.cs`, `ENGINE/Dbm.Tests/Integration/Catalog/CatalogExtractorTests.cs`

**Interfaces:**
- Consumes: `ServerMeta`, `SqlConnect` (C3), `Clock`, `Json` (C1), `SamplePairFixture` (T2.1).
- Produces: every C10 catalog type exactly as specified (`CatalogSnapshot`, `ObjectCounts`, `TableInfo`, `ColumnInfo`, `IndexInfo`, `ForeignKeyInfo`, `ColumnProfile`, `CatalogExtractor.ExtractAsync`) plus these additions:
  ```csharp
  // TableInfo:  public List<string> TriggerNames { get; init; }       // DML trigger names (TriggerCount == TriggerNames.Count)
  // ColumnInfo: public string? ComputedDefinition { get; init; }      // sys.computed_columns.definition
  public static class TypeTraits
  {
      public static bool IsString(string dataType);        // char varchar nchar nvarchar text ntext
      public static bool IsDeprecated(string dataType);    // text ntext image
      public static bool IsModernLob(ColumnInfo c);        // (max) or xml
      public static bool IsLob(ColumnInfo c);
      public static bool IsComparable(ColumnInfo c);       // supports COUNT(DISTINCT)/MIN/MAX in the profiler
      public static string? TypeClass(string dataType);    // semantic class implied by a non-string type
  }
  // CatalogExtractor: public static int NormalizeMaxLength(string dataType, int rawMaxLength);
  namespace Dbm.Tests.Support;
  public static class TestCatalogs   // Meta, Col, Table, Fk, Profile, Snapshot builders + LegacyShop() / ShopV2() structural copies of C15
  ```
- Extraction rules: user tables only (`is_ms_shipped = 0`, excluding `sysdiagrams` and dbm's own `__dbm_%` control tables); `DataType` = base system type (`sys.types` by `user_type_id`, alias types resolved to their base type, CLR types keep their name); `MaxLength` in characters for n-types, `-1` = max, `0` for non-character/binary types; `Ordinal` = 1..n in `column_id` order; index key columns in `key_ordinal` order, included columns excluded; `TemporalType` = `"system_versioned"` / `"history"` / null (only queried when `MajorVersion >= 13`); rows/size from `sys.dm_db_partition_stats` (index 0/1), falling back to `sys.partitions` rows and `SizeMb = 0` when VIEW DATABASE STATE is denied.

- [ ] **Step 1: Write the in-code test catalogs**

`ENGINE/Dbm.Tests/Support/TestCatalogs.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Sql;

namespace Dbm.Tests.Support;

/// <summary>In-code catalogs for unit tests: builders plus structural copies of the C15 LegacyShop / ShopV2 pair (no server needed).</summary>
public static class TestCatalogs
{
    public const string Collation = "SQL_Latin1_General_CP1_CI_AS";

    public static ServerMeta Meta(string database, int majorVersion = 17, int compatLevel = 170, string collation = Collation) =>
        new("localhost", database, "Microsoft SQL Server 2025 (RTM)", "17.0.1000.7", majorVersion, "Developer Edition (64-bit)",
            collation, collation, compatLevel, "integrated");

    public static ColumnInfo Col(string name, string type, int maxLength = 0, bool nullable = true, bool identity = false,
        bool computed = false, int precision = 0, int scale = 0, string? defaultDefinition = null, ColumnProfile? profile = null,
        string? collation = null, string? description = null) =>
        new(name, 0, type, maxLength, precision, scale, nullable, identity, computed, type == "timestamp", defaultDefinition,
            collation ?? (TypeTraits.IsString(type) ? Collation : null), description) { Profile = profile };

    public static TableInfo Table(string key, long rows, IEnumerable<ColumnInfo> columns, string[]? pk = null,
        IEnumerable<ForeignKeyInfo>? fks = null, string[]? triggers = null, IEnumerable<IndexInfo>? indexes = null,
        string? temporal = null)
    {
        var dot = key.IndexOf('.');
        var name = key[(dot + 1)..];
        var cols = columns.Select((c, i) => c with { Ordinal = i + 1 }).ToList();
        var ix = new List<IndexInfo>();
        if (pk is not null) ix.Add(new IndexInfo($"PK_{name}", true, true, true, pk.ToList()));
        if (indexes is not null) ix.AddRange(indexes);
        var triggerNames = triggers?.ToList() ?? new List<string>();
        return new TableInfo(key[..dot], name, rows, Math.Round(rows / 5000.0, 3), cols, ix, fks?.ToList() ?? new List<ForeignKeyInfo>(),
            triggerNames.Count, temporal, null) { TriggerNames = triggerNames };
    }

    public static ForeignKeyInfo Fk(string name, string column, string refKey, string refColumn, bool notTrusted = false, bool disabled = false)
    {
        var dot = refKey.IndexOf('.');
        return new ForeignKeyInfo(name, new List<string> { column }, refKey[..dot], refKey[(dot + 1)..], new List<string> { refColumn },
            disabled, notTrusted);
    }

    public static ColumnProfile Profile(long sampled, long nulls, long? distinct = null, string? semanticClass = null, params string[] samples) =>
        new(sampled, nulls, distinct, null, null, null, null, semanticClass, new List<string>(), samples.ToList());

    public static CatalogSnapshot Snapshot(ServerMeta meta, IEnumerable<TableInfo> tables, int triggers = 0) =>
        new(meta, tables.ToList(), new ObjectCounts(0, 0, 0, triggers, 0), new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

    public static CatalogSnapshot LegacyShop() => Snapshot(Meta("LegacyShop"), new[]
    {
        Table("dbo.CUST", 1000, new[]
        {
            Col("CUST_ID", "int", nullable: false, identity: true),
            Col("CUST_NM", "varchar", 100, nullable: false),
            Col("EMAIL_ADDR", "varchar", 120, profile: Profile(1000, 100, 900, "email", "first1.last1@example.com")),
            Col("PHONE_NO", "varchar", 30),
            Col("DOB", "datetime", precision: 23, scale: 3),
            Col("CRT_DT", "datetime", nullable: false, precision: 23, scale: 3, defaultDefinition: "(getdate())"),
            Col("FAX_NO", "varchar", 30, profile: Profile(1000, 980, 20, "phone")),
            Col("NOTES", "text", profile: Profile(1000, 750)),
        }, pk: new[] { "CUST_ID" }),
        Table("dbo.ADDR", 1500, new[]
        {
            Col("ADDR_ID", "int", nullable: false, identity: true),
            Col("CUST_ID", "int", nullable: false),
            Col("LINE1", "varchar", 200, nullable: false),
            Col("CITY", "varchar", 80, nullable: false),
            Col("ZIP", "varchar", 12),
            Col("CTRY_CD", "char", 2, nullable: false),
        }, pk: new[] { "ADDR_ID" }, fks: new[] { Fk("FK_ADDR_CUST", "CUST_ID", "dbo.CUST", "CUST_ID") }),
        Table("dbo.PROD", 200, new[]
        {
            Col("PROD_ID", "int", nullable: false, identity: true),
            Col("PROD_NM", "varchar", 150, nullable: false),
            Col("PROD_DESC", "varchar", 500),
            Col("UNIT_PRC", "money", nullable: false, precision: 19, scale: 4),
            Col("ACTIVE_FLG", "char", 1, nullable: false),
        }, pk: new[] { "PROD_ID" }),
        Table("dbo.ORD_STATUS", 4, new[]
        {
            Col("STATUS_ID", "tinyint", nullable: false),
            Col("STATUS_CD", "varchar", 10, nullable: false),
            Col("STATUS_DESC", "varchar", 50, nullable: false),
        }, pk: new[] { "STATUS_ID" }),
        Table("dbo.ORD_HDR", 3005, new[]
        {
            Col("ORD_ID", "int", nullable: false, identity: true),
            Col("CUST_ID", "int", nullable: false),
            Col("ORD_DT", "datetime", nullable: false, precision: 23, scale: 3),
            Col("STATUS_ID", "tinyint", nullable: false),
            Col("SHIP_ADDR_ID", "int"),
            Col("TOTAL_AMT", "money", nullable: false, precision: 19, scale: 4),
            Col("CMNT", "varchar", 500),
        }, pk: new[] { "ORD_ID" }, fks: new[]
        {
            Fk("FK_ORD_CUST", "CUST_ID", "dbo.CUST", "CUST_ID", notTrusted: true),
            Fk("FK_ORD_STATUS", "STATUS_ID", "dbo.ORD_STATUS", "STATUS_ID"),
        }),
        Table("dbo.ORD_LINE", 9002, new[]
        {
            Col("ORD_ID", "int", nullable: false),
            Col("LINE_NO", "smallint", nullable: false),
            Col("PROD_ID", "int", nullable: false),
            Col("QTY", "int", nullable: false),
            Col("UNIT_PRC", "money", nullable: false, precision: 19, scale: 4),
        }, pk: new[] { "ORD_ID", "LINE_NO" }, fks: new[]
        {
            Fk("FK_LINE_ORD", "ORD_ID", "dbo.ORD_HDR", "ORD_ID"),
            Fk("FK_LINE_PROD", "PROD_ID", "dbo.PROD", "PROD_ID"),
        }),
        Table("dbo.AUDIT_LOG", 5000, new[]
        {
            Col("LOG_TS", "datetime", nullable: false, precision: 23, scale: 3),
            Col("USR", "varchar", 50, nullable: false),
            Col("ACTION_TXT", "varchar", 200, nullable: false),
        }),
        Table("dbo.TMP_IMPORT", 0, new[] { Col("X", "int") }),
    });

    public static CatalogSnapshot ShopV2() => Snapshot(Meta("ShopV2"), new[]
    {
        Table("app.Customers", 0, new[]
        {
            Col("CustomerId", "int", nullable: false, identity: true),
            Col("FirstName", "nvarchar", 50, nullable: false),
            Col("LastName", "nvarchar", 50, nullable: false),
            Col("Email", "nvarchar", 120),
            Col("Phone", "nvarchar", 30),
            Col("BirthDate", "date", precision: 10),
            Col("CreatedAt", "datetime2", nullable: false, precision: 19, defaultDefinition: "(sysutcdatetime())"),
            Col("PrimaryAddressId", "int"),
            Col("Notes", "nvarchar", -1),
            Col("DisplayName", "nvarchar", 101, nullable: false, computed: true) with { ComputedDefinition = "(([FirstName]+N' ')+[LastName])" },
        }, pk: new[] { "CustomerId" }, fks: new[] { Fk("FK_Customers_PrimaryAddress", "PrimaryAddressId", "app.Addresses", "AddressId") }),
        Table("app.Addresses", 0, new[]
        {
            Col("AddressId", "int", nullable: false, identity: true),
            Col("CustomerId", "int", nullable: false),
            Col("Line1", "nvarchar", 200, nullable: false),
            Col("City", "nvarchar", 80, nullable: false),
            Col("PostalCode", "nvarchar", 12),
            Col("CountryCode", "char", 2, nullable: false),
        }, pk: new[] { "AddressId" }, fks: new[] { Fk("FK_Addresses_Customers", "CustomerId", "app.Customers", "CustomerId") }),
        Table("app.Products", 0, new[]
        {
            Col("ProductId", "int", nullable: false, identity: true),
            Col("Name", "nvarchar", 150, nullable: false),
            Col("Description", "nvarchar", 500),
            Col("UnitPrice", "decimal", nullable: false, precision: 19, scale: 4),
            Col("IsActive", "bit", nullable: false),
            Col("RowVer", "timestamp", nullable: false),
        }, pk: new[] { "ProductId" }),
        Table("app.Orders", 0, new[]
        {
            Col("OrderId", "int", nullable: false, identity: true),
            Col("CustomerId", "int", nullable: false),
            Col("OrderDate", "datetime2", nullable: false, precision: 19),
            Col("StatusCode", "varchar", 10, nullable: false),
            Col("ShippingAddressId", "int"),
            Col("TotalAmount", "decimal", nullable: false, precision: 19, scale: 4),
            Col("Comment", "nvarchar", 200),
        }, pk: new[] { "OrderId" }, fks: new[]
        {
            Fk("FK_Orders_Customers", "CustomerId", "app.Customers", "CustomerId"),
            Fk("FK_Orders_Addresses", "ShippingAddressId", "app.Addresses", "AddressId"),
        }, triggers: new[] { "trg_Orders_Audit" }),
        Table("app.OrderLines", 0, new[]
        {
            Col("OrderId", "int", nullable: false),
            Col("LineNumber", "smallint", nullable: false),
            Col("ProductId", "int", nullable: false),
            Col("Quantity", "int", nullable: false),
            Col("UnitPrice", "decimal", nullable: false, precision: 19, scale: 4),
        }, pk: new[] { "OrderId", "LineNumber" }, fks: new[]
        {
            Fk("FK_OrderLines_Orders", "OrderId", "app.Orders", "OrderId"),
            Fk("FK_OrderLines_Products", "ProductId", "app.Products", "ProductId"),
        }),
        Table("app.AuditEvents", 0, new[]
        {
            Col("EventTime", "datetime2", nullable: false, precision: 23, scale: 3),
            Col("UserName", "nvarchar", 50, nullable: false),
            Col("Action", "nvarchar", 200, nullable: false),
        }),
    }, triggers: 1);
}
```

- [ ] **Step 2: Write the failing unit test**

`ENGINE/Dbm.Tests/Unit/Catalog/CatalogModelTests.cs`
```csharp
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Tests.Support;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Unit.Catalog;

public sealed class CatalogModelTests
{
    [Theory]
    [InlineData("nvarchar", 50, 0, 0, "nvarchar(50)")]
    [InlineData("varchar", -1, 0, 0, "varchar(max)")]
    [InlineData("varbinary", 16, 0, 0, "varbinary(16)")]
    [InlineData("decimal", 0, 19, 4, "decimal(19,4)")]
    [InlineData("datetime2", 0, 19, 0, "datetime2(0)")]
    [InlineData("timestamp", 0, 0, 0, "rowversion")]
    [InlineData("int", 0, 10, 0, "int")]
    public void TypeDisplay(string type, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, Col("C", type, maxLength, precision: precision, scale: scale).TypeDisplay);

    [Theory]
    [InlineData("nvarchar", 100, 50)]
    [InlineData("nchar", -1, -1)]
    [InlineData("varchar", 120, 120)]
    [InlineData("varbinary", -1, -1)]
    [InlineData("int", 4, 0)]
    [InlineData("text", 16, 0)]
    public void NormalizeMaxLength(string type, int raw, int expected) =>
        Assert.Equal(expected, CatalogExtractor.NormalizeMaxLength(type, raw));

    [Fact]
    public void BestKey_prefers_the_primary_key()
    {
        var t = Table("dbo.ORD_LINE", 1, new[] { Col("ORD_ID", "int", nullable: false), Col("LINE_NO", "smallint", nullable: false) },
            pk: new[] { "ORD_ID", "LINE_NO" });
        Assert.Equal(new[] { "ORD_ID", "LINE_NO" }, t.BestKey());
    }

    [Fact]
    public void BestKey_falls_back_to_a_unique_index_on_not_null_columns()
    {
        var t = Table("dbo.X", 1, new[] { Col("A", "int"), Col("B", "int", nullable: false) }, indexes: new[]
        {
            new IndexInfo("UX_A", false, true, false, new List<string> { "A" }),
            new IndexInfo("UX_B", false, true, false, new List<string> { "B" }),
        });
        Assert.Equal(new[] { "B" }, t.BestKey());
        Assert.True(t.IsHeap);
    }

    [Fact]
    public void BestKey_is_null_for_a_heap_without_unique_keys()
    {
        var t = LegacyShop().FindTable("dbo.audit_log")!;
        Assert.Null(t.BestKey());
        Assert.True(t.IsHeap);
        Assert.Null(t.PrimaryKey);
    }

    [Fact]
    public void Find_methods_are_case_insensitive()
    {
        var snapshot = LegacyShop();
        Assert.Equal("CUST", snapshot.FindTable("DBO.cust")!.Name);
        Assert.Equal("EMAIL_ADDR", snapshot.FindTable("dbo.CUST")!.FindColumn("email_addr")!.Name);
        Assert.Null(snapshot.FindTable("dbo.NOPE"));
    }

    [Fact]
    public void Snapshot_round_trips_through_json_without_computed_members()
    {
        var snapshot = ShopV2();
        var json = Json.Serialize(snapshot);
        Assert.DoesNotContain("\"key\"", json);
        Assert.DoesNotContain("\"isHeap\"", json);
        Assert.DoesNotContain("\"typeDisplay\"", json);
        Assert.Contains("\"triggerNames\":[\"trg_Orders_Audit\"]", json);

        var back = Json.Deserialize<CatalogSnapshot>(json);
        Assert.Equal(6, back.Tables.Count);
        Assert.Equal("app.Orders", back.FindTable("app.Orders")!.Key);
        Assert.Equal(new[] { "trg_Orders_Audit" }, back.FindTable("app.Orders")!.TriggerNames);
        Assert.Equal("(([FirstName]+N' ')+[LastName])", back.FindTable("app.Customers")!.FindColumn("DisplayName")!.ComputedDefinition);
        var email = Json.Deserialize<CatalogSnapshot>(Json.Serialize(LegacyShop())).FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!;
        Assert.Equal(0.1, email.Profile!.NullRatio, 3);
    }
}
```

- [ ] **Step 3: Write the failing integration test**

`ENGINE/Dbm.Tests/Integration/Catalog/CatalogExtractorTests.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Catalog;

[Trait("Category", "Integration")]
public sealed class CatalogExtractorTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    public static async Task<CatalogSnapshot> ExtractAsync(string connectionString)
    {
        var meta = await SqlConnect.ProbeAsync(connectionString, CancellationToken.None);
        await using var conn = await SqlConnect.OpenAsync(connectionString, CancellationToken.None);
        return await CatalogExtractor.ExtractAsync(conn, meta, CancellationToken.None);
    }

    [Fact]
    public async Task Source_tables_keys_and_rows()
    {
        var src = await ExtractAsync(fixture.Pair.SourceCs);
        Assert.Equal(
            new[] { "dbo.ADDR", "dbo.AUDIT_LOG", "dbo.CUST", "dbo.ORD_HDR", "dbo.ORD_LINE", "dbo.ORD_STATUS", "dbo.PROD", "dbo.TMP_IMPORT" },
            src.Tables.Select(t => t.Key));

        var cust = src.FindTable("dbo.CUST")!;
        Assert.Equal(1000, cust.Rows);
        Assert.True(cust.SizeMb > 0);
        Assert.Equal(new[] { "CUST_ID" }, cust.BestKey());
        Assert.True(cust.FindColumn("CUST_ID")!.IsIdentity);
        Assert.Equal("varchar(120)", cust.FindColumn("EMAIL_ADDR")!.TypeDisplay);
        Assert.Equal("text", cust.FindColumn("NOTES")!.DataType);
        Assert.Equal("(getdate())", cust.FindColumn("CRT_DT")!.DefaultDefinition);
        Assert.Equal(Enumerable.Range(1, 8), cust.Columns.Select(c => c.Ordinal));

        Assert.Equal(new[] { "ORD_ID", "LINE_NO" }, src.FindTable("dbo.ORD_LINE")!.BestKey());

        var audit = src.FindTable("dbo.AUDIT_LOG")!;
        Assert.True(audit.IsHeap);
        Assert.Null(audit.BestKey());
        Assert.Equal(5000, audit.Rows);
    }

    [Fact]
    public async Task Source_untrusted_foreign_key()
    {
        var hdr = (await ExtractAsync(fixture.Pair.SourceCs)).FindTable("dbo.ORD_HDR")!;
        var ordCust = hdr.ForeignKeys.Single(f => f.Name == "FK_ORD_CUST");
        Assert.True(ordCust.IsNotTrusted);
        Assert.False(ordCust.IsDisabled);
        Assert.Equal("dbo.CUST", ordCust.RefKey);
        Assert.Equal(new[] { "CUST_ID" }, ordCust.Columns);
        Assert.False(hdr.ForeignKeys.Single(f => f.Name == "FK_ORD_STATUS").IsNotTrusted);
    }

    [Fact]
    public async Task Target_types_computed_rowversion_triggers_and_cycle()
    {
        var tgt = await ExtractAsync(fixture.Pair.TargetCs);
        Assert.Equal(6, tgt.Tables.Count);

        var customers = tgt.FindTable("app.Customers")!;
        Assert.Equal(120, customers.FindColumn("Email")!.MaxLength);
        Assert.Equal("nvarchar(max)", customers.FindColumn("Notes")!.TypeDisplay);
        Assert.Equal("datetime2(0)", customers.FindColumn("CreatedAt")!.TypeDisplay);
        var display = customers.FindColumn("DisplayName")!;
        Assert.True(display.IsComputed);
        Assert.Contains("[FirstName]", display.ComputedDefinition);

        var products = tgt.FindTable("app.Products")!;
        Assert.True(products.FindColumn("RowVer")!.IsRowVersion);
        Assert.Equal("decimal(19,4)", products.FindColumn("UnitPrice")!.TypeDisplay);

        var orders = tgt.FindTable("app.Orders")!;
        Assert.Equal(1, orders.TriggerCount);
        Assert.Equal(new[] { "trg_Orders_Audit" }, orders.TriggerNames);
        Assert.Equal(1, tgt.Objects.Triggers);

        Assert.True(tgt.FindTable("app.AuditEvents")!.IsHeap);
        Assert.Contains(customers.ForeignKeys, f => f.RefKey == "app.Addresses");
        Assert.Contains(tgt.FindTable("app.Addresses")!.ForeignKeys, f => f.RefKey == "app.Customers");
        Assert.All(tgt.Tables, t => Assert.Equal(0, t.Rows));
    }

    [Fact]
    public async Task Dbm_control_tables_are_not_part_of_the_catalog()
    {
        var before = await ExtractAsync(fixture.Pair.TargetCs);
        await SampleDatabases.RunAsync(fixture.Pair.TargetCs,
            "IF OBJECT_ID(N'dbo.__dbm_checkpoint') IS NULL CREATE TABLE dbo.__dbm_checkpoint (task_id nvarchar(20) NOT NULL PRIMARY KEY, last_key_json nvarchar(max) NULL);");
        var after = await ExtractAsync(fixture.Pair.TargetCs);
        Assert.DoesNotContain(after.Tables, t => t.Name.StartsWith("__dbm_", StringComparison.Ordinal));
        Assert.Equal(6, after.Tables.Count);
        Assert.Equal(Fingerprint.Compute(before), Fingerprint.Compute(after));
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Catalog"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Catalog' does not exist in the namespace 'Dbm.Core'`.

- [ ] **Step 5: Implement the catalog model**

`ENGINE/Dbm/Core/Catalog/CatalogModel.cs`
```csharp
using System.Globalization;
using System.Text.Json.Serialization;
using Dbm.Core.Sql;

namespace Dbm.Core.Catalog;

public sealed record CatalogSnapshot(ServerMeta Server, List<TableInfo> Tables, ObjectCounts Objects, DateTimeOffset ExtractedAt)
{
    /// <summary>Finds a table by "schema.name" (case-insensitive).</summary>
    public TableInfo? FindTable(string key) =>
        Tables.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}

public sealed record ObjectCounts(int Views, int Procedures, int Functions, int Triggers, int Synonyms);

public sealed record TableInfo(string Schema, string Name, long Rows, double SizeMb, List<ColumnInfo> Columns,
    List<IndexInfo> Indexes, List<ForeignKeyInfo> ForeignKeys, int TriggerCount, string? TemporalType, string? Description)
{
    /// <summary>Names of the table's DML triggers (M2 addition to C10; TriggerCount == TriggerNames.Count after extraction).</summary>
    public List<string> TriggerNames { get; init; } = new();

    [JsonIgnore] public string Key => $"{Schema}.{Name}";
    [JsonIgnore] public IndexInfo? PrimaryKey => Indexes.FirstOrDefault(i => i.IsPrimaryKey);
    [JsonIgnore] public bool IsHeap => !Indexes.Any(i => i.IsClustered);

    public ColumnInfo? FindColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>PK columns, else the first unique index whose columns are all NOT NULL, else null.</summary>
    public IReadOnlyList<string>? BestKey()
    {
        if (PrimaryKey is { Columns.Count: > 0 } pk) return pk.Columns;
        foreach (var index in Indexes)
        {
            if (!index.IsUnique || index.Columns.Count == 0) continue;
            if (index.Columns.All(name => FindColumn(name) is { IsNullable: false })) return index.Columns;
        }
        return null;
    }
}

// DataType = sys.types name, lower-case. MaxLength = characters for char/varchar/nchar/nvarchar, bytes for binary/varbinary,
// -1 = max, 0 for other types.
public sealed record ColumnInfo(string Name, int Ordinal, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable,
    bool IsIdentity, bool IsComputed, bool IsRowVersion, string? DefaultDefinition, string? Collation, string? Description)
{
    public ColumnProfile? Profile { get; init; }

    /// <summary>sys.computed_columns.definition for computed columns (M2 addition to C10).</summary>
    public string? ComputedDefinition { get; init; }

    [JsonIgnore]
    public string TypeDisplay => DataType switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
            $"{DataType}({(MaxLength == -1 ? "max" : MaxLength.ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{DataType}({Precision.ToString(CultureInfo.InvariantCulture)},{Scale.ToString(CultureInfo.InvariantCulture)})",
        "datetime2" or "datetimeoffset" or "time" => $"{DataType}({Scale.ToString(CultureInfo.InvariantCulture)})",
        "timestamp" => "rowversion",
        _ => DataType,
    };
}

public sealed record IndexInfo(string Name, bool IsPrimaryKey, bool IsUnique, bool IsClustered, List<string> Columns);

public sealed record ForeignKeyInfo(string Name, List<string> Columns, string RefSchema, string RefTable, List<string> RefColumns,
    bool IsDisabled, bool IsNotTrusted)
{
    [JsonIgnore] public string RefKey => $"{RefSchema}.{RefTable}";
}

public sealed record ColumnProfile(long SampledRows, long Nulls, long? Distinct, string? Min, string? Max, int? MaxLen, double? AvgLen,
    string? SemanticClass, List<string> TopPatterns, List<string> Samples)
{
    [JsonIgnore] public double NullRatio => SampledRows == 0 ? 0 : (double)Nulls / SampledRows;
    [JsonIgnore] public double? DistinctRatio => Distinct is null || SampledRows == 0 ? null : (double)Distinct / SampledRows;
}

/// <summary>Data-type predicates shared by the profiler, rules and text renderers.</summary>
public static class TypeTraits
{
    public static bool IsString(string dataType) => dataType is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

    public static bool IsDeprecated(string dataType) => dataType is "text" or "ntext" or "image";

    /// <summary>(max) types and xml; the deprecated text/ntext/image types are reported separately (R02).</summary>
    public static bool IsModernLob(ColumnInfo c) => c.MaxLength == -1 || c.DataType == "xml";

    public static bool IsLob(ColumnInfo c) => IsModernLob(c) || IsDeprecated(c.DataType);

    /// <summary>Types that support COUNT(DISTINCT), MIN and MAX in the profiler.</summary>
    public static bool IsComparable(ColumnInfo c) =>
        c.MaxLength != -1 &&
        c.DataType is not ("text" or "ntext" or "image" or "xml" or "geography" or "geometry" or "hierarchyid" or "sql_variant" or "timestamp");

    /// <summary>Semantic class implied by a non-string type (strings are classified from their values instead).</summary>
    public static string? TypeClass(string dataType) => dataType switch
    {
        "date" => "date",
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "datetime",
        "tinyint" or "smallint" or "int" or "bigint" => "integer",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "decimal",
        "uniqueidentifier" => "guid",
        "bit" => "flag",
        _ => null,
    };
}
```

- [ ] **Step 6: Implement the extractor**

`ENGINE/Dbm/Core/Catalog/CatalogExtractor.cs`
```csharp
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Catalog;

/// <summary>Reads the structure of every user table from the sys.* catalog views (no data access except row counts).</summary>
public static class CatalogExtractor
{
    private const int CommandTimeoutSeconds = 300;

    // Excludes dbm's own control table (dbo.__dbm_checkpoint, M5) and the SSMS diagram table.
    private const string UserTableFilter =
        "t.is_ms_shipped = 0 AND t.name <> N'sysdiagrams' AND t.name NOT LIKE N'[_][_]dbm[_]%'";

    private const string TablesSql = """
        SELECT t.object_id, s.name AS schema_name, t.name, CAST(ep.value AS nvarchar(4000)) AS description, {temporal} AS temporal_type
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.extended_properties AS ep
          ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE {filter}
        ORDER BY s.name, t.name;
        """;

    private const string ColumnsSql = """
        SELECT c.object_id, c.column_id, c.name,
          CASE WHEN ty.user_type_id <> ty.system_type_id AND ty.is_assembly_type = 0 THEN bt.name ELSE ty.name END AS type_name,
          CAST(c.max_length AS int) AS max_length, CAST(c.precision AS int) AS precision, CAST(c.scale AS int) AS scale,
          c.is_nullable, c.is_identity, c.is_computed, c.collation_name, dc.definition AS default_definition,
          cc.definition AS computed_definition, CAST(ep.value AS nvarchar(4000)) AS description
        FROM sys.columns AS c
        JOIN sys.tables AS t ON t.object_id = c.object_id
        JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.types AS bt ON bt.user_type_id = ty.system_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.extended_properties AS ep
          ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;

    private const string IndexesSql = """
        SELECT i.object_id, i.index_id, i.name, i.is_primary_key, i.is_unique,
          CAST(CASE WHEN i.type IN (1, 5) THEN 1 ELSE 0 END AS bit) AS is_clustered
        FROM sys.indexes AS i
        JOIN sys.tables AS t ON t.object_id = i.object_id
        WHERE t.is_ms_shipped = 0 AND i.type IN (1, 2, 5, 7) AND i.is_hypothetical = 0 AND i.name IS NOT NULL
        ORDER BY i.object_id, i.index_id;
        """;

    private const string IndexColumnsSql = """
        SELECT ic.object_id, ic.index_id, c.name
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables AS t ON t.object_id = ic.object_id
        WHERE t.is_ms_shipped = 0 AND ic.key_ordinal > 0 AND ic.is_included_column = 0
        ORDER BY ic.object_id, ic.index_id, ic.key_ordinal;
        """;

    private const string ForeignKeysSql = """
        SELECT fk.object_id, fk.parent_object_id, fk.name, rs.name AS ref_schema, rt.name AS ref_table, fk.is_disabled, fk.is_not_trusted
        FROM sys.foreign_keys AS fk
        JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
        JOIN sys.tables AS rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas AS rs ON rs.schema_id = rt.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name;
        """;

    private const string ForeignKeyColumnsSql = """
        SELECT fkc.constraint_object_id, pc.name AS column_name, rc.name AS ref_column_name
        FROM sys.foreign_key_columns AS fkc
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;
        """;

    private const string TriggersSql = """
        SELECT tr.parent_id, tr.name FROM sys.triggers AS tr WHERE tr.parent_class = 1 ORDER BY tr.parent_id, tr.name;
        """;

    private const string ObjectCountsSql = """
        SELECT COUNT(CASE WHEN o.type = 'V' THEN 1 END) AS views,
          COUNT(CASE WHEN o.type IN ('P', 'PC') THEN 1 END) AS procedures,
          COUNT(CASE WHEN o.type IN ('FN', 'IF', 'TF', 'FS', 'FT') THEN 1 END) AS functions,
          COUNT(CASE WHEN o.type = 'TR' THEN 1 END) AS triggers,
          COUNT(CASE WHEN o.type = 'SN' THEN 1 END) AS synonyms
        FROM sys.objects AS o WHERE o.is_ms_shipped = 0;
        """;

    // Needs VIEW DATABASE STATE; falls back to PartitionsSql (rows only, SizeMb = 0) when denied.
    private const string PartitionStatsSql = """
        SELECT ps.object_id, SUM(ps.row_count) AS row_count, CAST(SUM(ps.used_page_count) * 8 / 1024.0 AS float) AS size_mb
        FROM sys.dm_db_partition_stats AS ps
        JOIN sys.tables AS t ON t.object_id = ps.object_id
        WHERE t.is_ms_shipped = 0 AND ps.index_id IN (0, 1)
        GROUP BY ps.object_id;
        """;

    private const string PartitionsSql = """
        SELECT p.object_id, SUM(p.rows) AS row_count
        FROM sys.partitions AS p
        JOIN sys.tables AS t ON t.object_id = p.object_id
        WHERE t.is_ms_shipped = 0 AND p.index_id IN (0, 1)
        GROUP BY p.object_id;
        """;

    public static async Task<CatalogSnapshot> ExtractAsync(SqlConnection conn, ServerMeta meta, CancellationToken ct)
    {
        var temporal = meta.MajorVersion >= 13 ? "CAST(t.temporal_type AS int)" : "CAST(0 AS int)";
        var tables = new List<TableBuilder>();
        var byId = new Dictionary<int, TableBuilder>();

        var tablesSql = TablesSql.Replace("{temporal}", temporal, StringComparison.Ordinal)
            .Replace("{filter}", UserTableFilter, StringComparison.Ordinal);
        await ReadAsync(conn, tablesSql, r =>
        {
            var table = new TableBuilder(r.GetString(1), r.GetString(2), Str(r, 3), TemporalText(r.GetInt32(4)));
            tables.Add(table);
            byId[r.GetInt32(0)] = table;
        }, ct);

        await ReadAsync(conn, ColumnsSql, r =>
        {
            if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
            var type = r.GetString(3).ToLowerInvariant();
            table.Columns.Add(new ColumnInfo(
                Name: r.GetString(2),
                Ordinal: table.Columns.Count + 1,
                DataType: type,
                MaxLength: NormalizeMaxLength(type, r.GetInt32(4)),
                Precision: r.GetInt32(5),
                Scale: r.GetInt32(6),
                IsNullable: Bool(r, 7),
                IsIdentity: Bool(r, 8),
                IsComputed: Bool(r, 9),
                IsRowVersion: type == "timestamp",
                DefaultDefinition: Str(r, 11),
                Collation: Str(r, 10),
                Description: Str(r, 13))
            {
                ComputedDefinition = Str(r, 12),
            });
        }, ct);

        var indexes = new Dictionary<(int ObjectId, int IndexId), IndexBuilder>();
        await ReadAsync(conn, IndexesSql, r =>
        {
            if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
            var index = new IndexBuilder(r.GetString(2), Bool(r, 3), Bool(r, 4), Bool(r, 5));
            table.Indexes.Add(index);
            indexes[(r.GetInt32(0), r.GetInt32(1))] = index;
        }, ct);
        await ReadAsync(conn, IndexColumnsSql, r =>
        {
            if (indexes.TryGetValue((r.GetInt32(0), r.GetInt32(1)), out var index)) index.Columns.Add(r.GetString(2));
        }, ct);

        var foreignKeys = new Dictionary<int, ForeignKeyBuilder>();
        await ReadAsync(conn, ForeignKeysSql, r =>
        {
            if (!byId.TryGetValue(r.GetInt32(1), out var table)) return;
            var fk = new ForeignKeyBuilder(r.GetString(2), r.GetString(3), r.GetString(4), Bool(r, 5), Bool(r, 6));
            table.ForeignKeys.Add(fk);
            foreignKeys[r.GetInt32(0)] = fk;
        }, ct);
        await ReadAsync(conn, ForeignKeyColumnsSql, r =>
        {
            if (!foreignKeys.TryGetValue(r.GetInt32(0), out var fk)) return;
            fk.Columns.Add(r.GetString(1));
            fk.RefColumns.Add(r.GetString(2));
        }, ct);

        await ReadAsync(conn, TriggersSql, r =>
        {
            if (byId.TryGetValue(r.GetInt32(0), out var table)) table.TriggerNames.Add(r.GetString(1));
        }, ct);

        var objects = new ObjectCounts(0, 0, 0, 0, 0);
        await ReadAsync(conn, ObjectCountsSql, r =>
            objects = new ObjectCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)), ct);

        try
        {
            await ReadAsync(conn, PartitionStatsSql, r =>
            {
                if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
                table.Rows = r.GetInt64(1);
                table.SizeMb = r.GetDouble(2);
            }, ct);
        }
        catch (SqlException)
        {
            // VIEW DATABASE STATE denied: row counts from sys.partitions, size unknown.
            await ReadAsync(conn, PartitionsSql, r =>
            {
                if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
                table.Rows = r.GetInt64(1);
                table.SizeMb = 0;
            }, ct);
        }

        return new CatalogSnapshot(meta, tables.Select(t => t.Build()).ToList(), objects, Clock.Now());
    }

    /// <summary>nchar/nvarchar store 2 bytes per character; only character and binary types keep a length.</summary>
    public static int NormalizeMaxLength(string dataType, int rawMaxLength) => dataType switch
    {
        "nchar" or "nvarchar" => rawMaxLength == -1 ? -1 : rawMaxLength / 2,
        "char" or "varchar" or "binary" or "varbinary" => rawMaxLength,
        _ => 0,
    };

    private static string? TemporalText(int temporalType) => temporalType switch
    {
        1 => "history",
        2 => "system_versioned",
        _ => null,
    };

    private static async Task ReadAsync(SqlConnection conn, string sql, Action<SqlDataReader> onRow, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) onRow(reader);
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static bool Bool(SqlDataReader r, int i) => !r.IsDBNull(i) && r.GetBoolean(i);

    private sealed class TableBuilder(string schema, string name, string? description, string? temporalType)
    {
        public long Rows;
        public double SizeMb;
        public List<ColumnInfo> Columns { get; } = new();
        public List<IndexBuilder> Indexes { get; } = new();
        public List<ForeignKeyBuilder> ForeignKeys { get; } = new();
        public List<string> TriggerNames { get; } = new();

        public TableInfo Build() => new(schema, name, Rows, Math.Round(SizeMb, 3), Columns,
            Indexes.Select(i => i.Build()).ToList(), ForeignKeys.Select(f => f.Build()).ToList(),
            TriggerNames.Count, temporalType, description)
        {
            TriggerNames = TriggerNames.ToList(),
        };
    }

    private sealed class IndexBuilder(string name, bool isPrimaryKey, bool isUnique, bool isClustered)
    {
        public List<string> Columns { get; } = new();
        public IndexInfo Build() => new(name, isPrimaryKey, isUnique, isClustered, Columns.ToList());
    }

    private sealed class ForeignKeyBuilder(string name, string refSchema, string refTable, bool isDisabled, bool isNotTrusted)
    {
        public List<string> Columns { get; } = new();
        public List<string> RefColumns { get; } = new();
        public ForeignKeyInfo Build() => new(name, Columns.ToList(), refSchema, refTable, RefColumns.ToList(), isDisabled, isNotTrusted);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Catalog"`
Expected: PASS (18 tests).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~Dbm.Tests.Integration.Catalog.CatalogExtractorTests"`
Expected: PASS (4 tests; `Dbm_control_tables_are_not_part_of_the_catalog` creates `dbo.__dbm_checkpoint` in the target and proves it is neither extracted nor fingerprinted).

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Catalog plugins/db-migrate/engine/Dbm.Tests/Support/TestCatalogs.cs \
  plugins/db-migrate/engine/Dbm.Tests/Unit/Catalog plugins/db-migrate/engine/Dbm.Tests/Integration/Catalog
git commit -F - <<'EOF'
feat(catalog): catalog model and sys.* extractor

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.3: Value signatures + profiler

**Files:**
- Create: `ENGINE/Dbm/Core/Catalog/ValueSignature.cs`, `ENGINE/Dbm/Core/Catalog/Profiler.cs`
- Test: `ENGINE/Dbm.Tests/Unit/Catalog/ValueSignatureTests.cs`, `ENGINE/Dbm.Tests/Unit/Catalog/ProfilerSqlTests.cs`, `ENGINE/Dbm.Tests/Integration/Catalog/ProfilerTests.cs`

**Interfaces:**
- Consumes: C10 model + `TypeTraits` (T2.2), `SqlConnect` (C3), `TestCatalogs`, `SamplePairFixture`.
- Produces: `ValueSignature.Pattern/Classify`, `ProfileOptions`, `Profiler.ProfileAsync` exactly as in C10, plus:
  ```csharp
  public static class Profiler { public const int MaxColumnsPerQuery = 100; public const int SampleMaxChars = 40; public static string? Truncate(string? value); }
  public static class ProfilerSql      // SQL text builders, public for unit tests (also used by T2.6 for quoting)
  {
      public static string Quote(string name);                    // [name] with ] doubled
      public static string From(TableInfo table);                 // [schema].[name]
      public static IReadOnlyList<IReadOnlyList<ColumnInfo>> Batches(IReadOnlyList<ColumnInfo> columns, int size = 100);
      public static string Aggregate(TableInfo table, IReadOnlyList<ColumnInfo> columns);        // uses @sample
      public static string PatternFetch(TableInfo table, IReadOnlyList<ColumnInfo> stringColumns); // uses @patternRows
  }
  ```
- Profiling rules: per table one aggregate query per batch of ≤ 100 columns over `(SELECT TOP (@sample) * FROM t) AS x` returning `COUNT_BIG(*)`, per-column null counts, `COUNT_BIG(DISTINCT)` + `MIN`/`MAX` as `CONVERT(nvarchar(4000), …, 126)` (style 1 for binary; `bit` via `CAST AS tinyint`) for comparable types, and `MAX`/`AVG` of `LEN` (`DATALENGTH` for text, `/2` for ntext) for strings; then one `SELECT TOP (@patternRows)` over the string columns (cast to `nvarchar(200)`, trailing spaces trimmed) → `TopPatterns` (3 most frequent `Pattern` values), `SemanticClass` (`Classify`), `Samples` (first 3 distinct values truncated to 40 chars, only when `SampleValues`). Non-string columns get `SemanticClass` from `TypeTraits.TypeClass` when they have non-null values. When `SampleValues` is false, string `Min`/`Max` are also withheld. One log line per table: `profiled <key>: <n> rows sampled, <m> columns`; a table whose query fails keeps `Profile = null` and logs `profile skipped for <key>: <message>`. The `TOP` sample has no `ORDER BY` (cheap, arbitrary rows); a table split into several batches may sample different rows per batch — acceptable for profiling.

- [ ] **Step 1: Write the failing unit tests**

`ENGINE/Dbm.Tests/Unit/Catalog/ValueSignatureTests.cs`
```csharp
using Dbm.Core.Catalog;

namespace Dbm.Tests.Unit.Catalog;

public sealed class ValueSignatureTests
{
    [Theory]
    [InlineData("John.Smith@x.com", "Aa.Aa@a.a")]
    [InlineData("first12.last12@example.com", "a9.a9@a.a")]
    [InlineData("+1-555-0123", "+9-9-9")]
    [InlineData("AB12 3CD", "A9 9A")]
    [InlineData("2020-01-02", "9-9-9")]
    [InlineData("", "")]
    public void Pattern(string value, string expected) => Assert.Equal(expected, ValueSignature.Pattern(value));

    [Theory]
    [InlineData("email", new[] { "a@b.com", "first1.last1@example.com", "x.y@z.org" })]
    [InlineData("phone", new[] { "+1-555-0123", "(555) 123-4567", "555 123 4567", "+44 20 7946 0958" })]
    [InlineData("date", new[] { "2020-01-02", "1999-12-31", "31/12/1999" })]
    [InlineData("datetime", new[] { "2020-01-02T10:00:00", "2020-01-02 10:00", "2021-06-30T23:59:59.123Z" })]
    [InlineData("guid", new[] { "0f8fad5b-d9cb-469f-a165-70867728950e", "{7c9e6679-7425-40de-944b-e07fc1f90ae7}" })]
    [InlineData("postal_code", new[] { "12345", "02134-1234", "SW1A 1AA", "K1A 0B1" })]
    [InlineData("flag", new[] { "Y", "N", "Y", "y" })]
    [InlineData("flag", new[] { "true", "false" })]
    [InlineData("country_code", new[] { "GB", "FR", "DE", "IT" })]
    [InlineData("integer", new[] { "1", "22", "-333" })]
    [InlineData("decimal", new[] { "1.5", "2,25", "-10.125" })]
    [InlineData("url", new[] { "http://x.com/a", "https://example.org", "www.example.com" })]
    [InlineData("text", new[] { "hello world", "Leave at reception" })]
    public void Classify(string expected, string[] values) => Assert.Equal(expected, ValueSignature.Classify(values));

    [Fact]
    public void Classify_needs_80_percent()
    {
        Assert.Equal("email", ValueSignature.Classify(new[] { "a@b.com", "c@d.com", "e@f.com", "g@h.com", "junk" }));
        Assert.Equal("text", ValueSignature.Classify(new[] { "a@b.com", "c@d.com", "e@f.com", "junk", "more junk" }));
    }

    [Fact]
    public void Classify_returns_null_without_values()
    {
        Assert.Null(ValueSignature.Classify(Array.Empty<string>()));
        Assert.Null(ValueSignature.Classify(new[] { "", "   " }));
    }
}
```

`ENGINE/Dbm.Tests/Unit/Catalog/ProfilerSqlTests.cs`
```csharp
using Dbm.Core.Catalog;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Unit.Catalog;

public sealed class ProfilerSqlTests
{
    [Fact]
    public void Batches_split_beyond_100_columns()
    {
        var columns = Enumerable.Range(1, 150).Select(i => Col($"C{i}", "int")).ToList();
        var batches = ProfilerSql.Batches(columns);
        Assert.Equal(new[] { 100, 50 }, batches.Select(b => b.Count));
    }

    [Fact]
    public void Aggregate_uses_the_sample_subquery_and_per_type_expressions()
    {
        var table = Table("dbo.T]x", 10, new[]
        {
            Col("Id", "int", nullable: false),
            Col("Flag", "bit"),
            Col("Blob", "varbinary", 16),
            Col("Name", "nvarchar", 50),
            Col("Notes", "text"),
            Col("Body", "nvarchar", -1),
            Col("Ver", "timestamp"),
            Col("Born", "datetime2", scale: 0),
        });
        var sql = ProfilerSql.Aggregate(table, table.Columns);

        Assert.StartsWith("SELECT COUNT_BIG(*) AS [rows]", sql);
        Assert.EndsWith("FROM (SELECT TOP (@sample) * FROM [dbo].[T]]x]) AS x;", sql);
        Assert.Contains("ISNULL(SUM(CAST(CASE WHEN x.[Id] IS NULL THEN 1 ELSE 0 END AS bigint)), 0) AS [n0]", sql);
        Assert.Contains("COUNT_BIG(DISTINCT x.[Id]) AS [d0]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MIN(x.[Id]), 126) AS [mn0]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MIN(CAST(x.[Flag] AS tinyint)), 126) AS [mn1]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MAX(x.[Blob]), 1) AS [mx2]", sql);
        Assert.Contains("CAST(MAX(LEN(x.[Name])) AS int) AS [ml3]", sql);
        Assert.Contains("CAST(MAX(DATALENGTH(x.[Notes])) AS int) AS [ml4]", sql);
        Assert.DoesNotContain("[d4]", sql);   // text: no DISTINCT/MIN/MAX
        Assert.DoesNotContain("[d5]", sql);   // nvarchar(max)
        Assert.Contains("CAST(MAX(LEN(x.[Body])) AS int) AS [ml5]", sql);
        Assert.DoesNotContain("[d6]", sql);   // timestamp
        Assert.Contains("[n6]", sql);
        Assert.Contains("CONVERT(nvarchar(4000), MAX(x.[Born]), 126) AS [mx7]", sql);
    }

    [Fact]
    public void PatternFetch_casts_string_columns_to_nvarchar_200()
    {
        var table = Table("dbo.CUST", 10, new[] { Col("CUST_NM", "varchar", 100), Col("NOTES", "text") });
        Assert.Equal(
            "SELECT TOP (@patternRows) CAST(t.[CUST_NM] AS nvarchar(200)) AS [p0], CAST(t.[NOTES] AS nvarchar(200)) AS [p1] FROM [dbo].[CUST] AS t;",
            ProfilerSql.PatternFetch(table, table.Columns));
    }

    [Fact]
    public void Truncate_limits_samples_to_40_characters()
    {
        Assert.Equal("short", Profiler.Truncate("short"));
        var truncated = Profiler.Truncate(new string('x', 60))!;
        Assert.Equal(40, truncated.Length);
        Assert.EndsWith("…", truncated);
    }
}
```

- [ ] **Step 2: Write the failing integration test**

`ENGINE/Dbm.Tests/Integration/Catalog/ProfilerTests.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Catalog;

[Trait("Category", "Integration")]
public sealed class ProfilerTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    private static async Task<(CatalogSnapshot Snapshot, List<string> Log)> ProfileAsync(string connectionString, bool sampleValues = true)
    {
        var log = new List<string>();
        var meta = await SqlConnect.ProbeAsync(connectionString, CancellationToken.None);
        await using var conn = await SqlConnect.OpenAsync(connectionString, CancellationToken.None);
        var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, CancellationToken.None);
        var profiled = await Profiler.ProfileAsync(conn, snapshot, new ProfileOptions(100_000, sampleValues), log.Add, CancellationToken.None);
        return (profiled, log);
    }

    [Fact]
    public async Task Source_profiles()
    {
        var (src, log) = await ProfileAsync(fixture.Pair.SourceCs);
        Assert.Equal(8, log.Count(l => l.StartsWith("profiled ", StringComparison.Ordinal)));
        Assert.Contains("profiled dbo.CUST: 1000 rows sampled, 8 columns", log);

        var cust = src.FindTable("dbo.CUST")!;
        var email = cust.FindColumn("EMAIL_ADDR")!.Profile!;
        Assert.Equal(1000, email.SampledRows);
        Assert.Equal(0.1, email.NullRatio, 3);
        Assert.Equal("email", email.SemanticClass);
        Assert.Equal(new[] { "a9.a9@a.a" }, email.TopPatterns);
        Assert.Equal(3, email.Samples.Count);
        Assert.All(email.Samples, s => Assert.EndsWith("@example.com", s));
        Assert.Equal(900, email.Distinct);

        var id = cust.FindColumn("CUST_ID")!.Profile!;
        Assert.Equal(("1", "1000", 1000L), (id.Min, id.Max, id.Distinct!.Value));
        Assert.Equal("integer", id.SemanticClass);

        var notes = cust.FindColumn("NOTES")!.Profile!;
        Assert.Null(notes.Distinct);
        Assert.Null(notes.Min);
        Assert.NotNull(notes.MaxLen);
        Assert.Equal(0.75, notes.NullRatio, 3);

        Assert.Equal("phone", cust.FindColumn("PHONE_NO")!.Profile!.SemanticClass);
        Assert.Equal("datetime", cust.FindColumn("DOB")!.Profile!.SemanticClass);
        Assert.Equal(0.98, cust.FindColumn("FAX_NO")!.Profile!.NullRatio, 3);
        Assert.Equal("flag", src.FindTable("dbo.PROD")!.FindColumn("ACTIVE_FLG")!.Profile!.SemanticClass);
        var addr = src.FindTable("dbo.ADDR")!;
        Assert.Equal("country_code", addr.FindColumn("CTRY_CD")!.Profile!.SemanticClass);
        Assert.Equal("postal_code", addr.FindColumn("ZIP")!.Profile!.SemanticClass);
        Assert.Equal(300, src.FindTable("dbo.ORD_HDR")!.FindColumn("CMNT")!.Profile!.MaxLen);
        Assert.Equal(0, src.FindTable("dbo.TMP_IMPORT")!.FindColumn("X")!.Profile!.SampledRows);
    }

    [Fact]
    public async Task Target_profiles_of_empty_tables()
    {
        var (tgt, _) = await ProfileAsync(fixture.Pair.TargetCs);
        var email = tgt.FindTable("app.Customers")!.FindColumn("Email")!.Profile!;
        Assert.Equal(0, email.SampledRows);
        Assert.Null(email.SemanticClass);
        Assert.Empty(email.Samples);
        Assert.NotNull(tgt.FindTable("app.Products")!.FindColumn("RowVer")!.Profile);
    }

    [Fact]
    public async Task Sample_values_can_be_disabled()
    {
        var (src, _) = await ProfileAsync(fixture.Pair.SourceCs, sampleValues: false);
        var email = src.FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!.Profile!;
        Assert.Empty(email.Samples);
        Assert.Null(email.Min);
        Assert.Equal("email", email.SemanticClass);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Catalog"`
Expected: build FAILS with `error CS0103: The name 'ValueSignature' does not exist in the current context` (and the same for `ProfilerSql`/`Profiler`).

- [ ] **Step 4: Implement ValueSignature**

`ENGINE/Dbm/Core/Catalog/ValueSignature.cs`
```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Dbm.Core.Catalog;

/// <summary>Value shapes used by the profiler and the auto-mapper's profile score.</summary>
public static class ValueSignature
{
    private const RegexOptions Rx = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$", Rx);
    private static readonly Regex Url = new(@"^((https?|ftp)://|www\.)\S+$", Rx | RegexOptions.IgnoreCase);
    private static readonly Regex DateTimeValue = new(
        @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:?\d{2})?$|^\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}[ T]\d{1,2}:\d{2}(:\d{2})?(\s?[AaPp][Mm])?$", Rx);
    private static readonly Regex DateValue = new(@"^\d{4}-\d{2}-\d{2}$|^\d{4}/\d{2}/\d{2}$|^\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}$", Rx);
    private static readonly Regex Postal = new(
        @"^\d{5}(-\d{4})?$|^[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}$|^[A-Z]\d[A-Z] ?\d[A-Z]\d$|^\d{4} ?[A-Z]{2}$", Rx);
    private static readonly Regex Integer = new(@"^[-+]?\d{1,18}$", Rx);
    private static readonly Regex Decimal = new(@"^[-+]?\d{1,18}[.,]\d+$", Rx);
    private static readonly Regex Phone = new(@"^\+?[\d\s\-().]{7,24}$", Rx);
    private static readonly Regex Country = new(@"^[A-Z]{2}$", Rx);

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "n", "yes", "no", "t", "f", "true", "false", "0", "1",
    };

    // Order matters: the first class that matches >= 80 % of the values wins.
    private static readonly (string Name, Func<string, bool> Test)[] Classes =
    {
        ("guid", v => Guid.TryParseExact(v, "D", out _) || Guid.TryParseExact(v, "B", out _)),
        ("email", Email.IsMatch),
        ("url", Url.IsMatch),
        ("datetime", DateTimeValue.IsMatch),
        ("date", DateValue.IsMatch),
        ("flag", Flags.Contains),
        ("postal_code", Postal.IsMatch),
        ("integer", Integer.IsMatch),
        ("decimal", Decimal.IsMatch),
        ("phone", v => Phone.IsMatch(v) && v.Count(char.IsDigit) >= 7),
        ("country_code", Country.IsMatch),
    };

    /// <summary>Letters -> 'a'/'A', digits -> '9', other characters kept; runs of the same symbol collapse to one.</summary>
    public static string Pattern(string value)
    {
        var sb = new StringBuilder(value.Length);
        var last = '\0';
        foreach (var ch in value)
        {
            var symbol = char.IsDigit(ch) ? '9' : char.IsLetter(ch) ? (char.IsUpper(ch) ? 'A' : 'a') : ch;
            if (symbol == last) continue;
            sb.Append(symbol);
            last = symbol;
        }
        return sb.ToString();
    }

    /// <summary>
    /// email | phone | url | guid | date | datetime | integer | decimal | postal_code | flag | country_code when at least 80 %
    /// of the non-blank values match; "text" otherwise; null when there are no non-blank values.
    /// </summary>
    public static string? Classify(IReadOnlyCollection<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (list.Count == 0) return null;
        foreach (var (name, test) in Classes)
        {
            var hits = list.Count(test);
            if (hits * 5 >= list.Count * 4) return name;
        }
        return "text";
    }
}
```

- [ ] **Step 5: Implement the profiler**

`ENGINE/Dbm/Core/Catalog/Profiler.cs`
```csharp
using System.Text;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Catalog;

public sealed record ProfileOptions(int SampleRows, bool SampleValues, int PatternRows = 1000);

/// <summary>Samples each table (TOP n, no ORDER BY) and attaches a <see cref="ColumnProfile"/> to every column.</summary>
public static class Profiler
{
    public const int MaxColumnsPerQuery = 100;
    public const int SampleMaxChars = 40;
    private const int PatternInputChars = 64;
    private const int CommandTimeoutSeconds = 300;

    public static async Task<CatalogSnapshot> ProfileAsync(SqlConnection conn, CatalogSnapshot snapshot, ProfileOptions options,
        Action<string>? log, CancellationToken ct)
    {
        var tables = new List<TableInfo>(snapshot.Tables.Count);
        foreach (var table in snapshot.Tables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (profiled, sampled) = await ProfileTableAsync(conn, table, options, ct);
                tables.Add(profiled);
                log?.Invoke($"profiled {table.Key}: {sampled} rows sampled, {table.Columns.Count} columns");
            }
            catch (SqlException ex)
            {
                tables.Add(table);
                log?.Invoke($"profile skipped for {table.Key}: {ex.Message}");
            }
        }
        return snapshot with { Tables = tables };
    }

    private static async Task<(TableInfo Table, long Sampled)> ProfileTableAsync(SqlConnection conn, TableInfo table,
        ProfileOptions options, CancellationToken ct)
    {
        var stats = new Dictionary<string, RawStats>(StringComparer.Ordinal);
        long sampled = 0;
        foreach (var batch in ProfilerSql.Batches(table.Columns))
        {
            await using var cmd = new SqlCommand(ProfilerSql.Aggregate(table, batch), conn) { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@sample", Math.Max(1, options.SampleRows));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;
            sampled = r.GetInt64(0);
            for (var j = 0; j < batch.Count; j++)
            {
                var column = batch[j];
                var s = new RawStats { Nulls = r.GetInt64(r.GetOrdinal($"n{j}")) };
                if (TypeTraits.IsComparable(column))
                {
                    s.Distinct = r.GetInt64(r.GetOrdinal($"d{j}"));
                    s.Min = Str(r, $"mn{j}");
                    s.Max = Str(r, $"mx{j}");
                }
                if (TypeTraits.IsString(column.DataType))
                {
                    var ml = r.GetOrdinal($"ml{j}");
                    var al = r.GetOrdinal($"al{j}");
                    s.MaxLen = r.IsDBNull(ml) ? null : r.GetInt32(ml);
                    s.AvgLen = r.IsDBNull(al) ? null : r.GetDouble(al);
                }
                stats[column.Name] = s;
            }
        }

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var stringColumns = table.Columns.Where(c => TypeTraits.IsString(c.DataType)).ToList();
        if (stringColumns.Count > 0 && sampled > 0 && options.PatternRows > 0)
        {
            foreach (var batch in ProfilerSql.Batches(stringColumns))
            {
                await using var cmd = new SqlCommand(ProfilerSql.PatternFetch(table, batch), conn) { CommandTimeout = CommandTimeoutSeconds };
                cmd.Parameters.AddWithValue("@patternRows", options.PatternRows);
                var lists = new List<string>[batch.Count];
                for (var j = 0; j < batch.Count; j++) values[batch[j].Name] = lists[j] = new List<string>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    for (var j = 0; j < batch.Count; j++)
                    {
                        if (!r.IsDBNull(j)) lists[j].Add(r.GetString(j).TrimEnd());
                    }
                }
            }
        }

        var columns = table.Columns
            .Select(c => stats.TryGetValue(c.Name, out var s) ? c with { Profile = Build(c, s, sampled, values.GetValueOrDefault(c.Name), options) } : c)
            .ToList();
        return (table with { Columns = columns }, sampled);
    }

    private static ColumnProfile Build(ColumnInfo column, RawStats s, long sampled, List<string>? values, ProfileOptions options)
    {
        var isString = TypeTraits.IsString(column.DataType);
        var patterns = new List<string>();
        var samples = new List<string>();
        string? semanticClass;
        if (isString)
        {
            var list = values ?? new List<string>();
            patterns = list
                .GroupBy(v => ValueSignature.Pattern(v.Length > PatternInputChars ? v[..PatternInputChars] : v), StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(3).Select(g => g.Key).ToList();
            semanticClass = ValueSignature.Classify(list);
            if (options.SampleValues)
                samples = list.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Take(3).Select(v => Truncate(v)!).ToList();
        }
        else
        {
            semanticClass = sampled > s.Nulls ? TypeTraits.TypeClass(column.DataType) : null;
        }

        // String MIN/MAX are real values: hide them when the project disables sample values.
        var hide = isString && !options.SampleValues;
        return new ColumnProfile(sampled, s.Nulls, s.Distinct, hide ? null : Truncate(s.Min), hide ? null : Truncate(s.Max),
            s.MaxLen, s.AvgLen is null ? null : Math.Round(s.AvgLen.Value, 2), semanticClass, patterns, samples);
    }

    public static string? Truncate(string? value) =>
        value is null ? null : value.Length <= SampleMaxChars ? value : value[..(SampleMaxChars - 1)] + "…";

    private static string? Str(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private sealed class RawStats
    {
        public long Nulls;
        public long? Distinct;
        public string? Min;
        public string? Max;
        public int? MaxLen;
        public double? AvgLen;
    }
}

/// <summary>SQL text used by <see cref="Profiler"/>; public so it can be unit-tested without a server.</summary>
public static class ProfilerSql
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string From(TableInfo table) => $"{Quote(table.Schema)}.{Quote(table.Name)}";

    public static IReadOnlyList<IReadOnlyList<ColumnInfo>> Batches(IReadOnlyList<ColumnInfo> columns, int size = Profiler.MaxColumnsPerQuery)
    {
        var batches = new List<IReadOnlyList<ColumnInfo>>();
        for (var i = 0; i < columns.Count; i += size) batches.Add(columns.Skip(i).Take(size).ToList());
        return batches;
    }

    /// <summary>One row: [rows], then per column j: [n{j}] nulls; [d{j}] [mn{j}] [mx{j}] for comparable types; [ml{j}] [al{j}] for strings.</summary>
    public static string Aggregate(TableInfo table, IReadOnlyList<ColumnInfo> columns)
    {
        var sb = new StringBuilder("SELECT COUNT_BIG(*) AS [rows]");
        for (var j = 0; j < columns.Count; j++)
        {
            var c = columns[j];
            var q = "x." + Quote(c.Name);
            sb.Append($",\n  ISNULL(SUM(CAST(CASE WHEN {q} IS NULL THEN 1 ELSE 0 END AS bigint)), 0) AS [n{j}]");
            if (TypeTraits.IsComparable(c))
            {
                var v = c.DataType == "bit" ? $"CAST({q} AS tinyint)" : q;
                var style = c.DataType is "binary" or "varbinary" ? 1 : 126;
                sb.Append($",\n  COUNT_BIG(DISTINCT {q}) AS [d{j}]");
                sb.Append($",\n  CONVERT(nvarchar(4000), MIN({v}), {style}) AS [mn{j}]");
                sb.Append($",\n  CONVERT(nvarchar(4000), MAX({v}), {style}) AS [mx{j}]");
            }
            if (TypeTraits.IsString(c.DataType))
            {
                var len = c.DataType switch
                {
                    "text" => $"DATALENGTH({q})",
                    "ntext" => $"DATALENGTH({q}) / 2",
                    _ => $"LEN({q})",
                };
                sb.Append($",\n  CAST(MAX({len}) AS int) AS [ml{j}]");
                sb.Append($",\n  AVG(CAST({len} AS float)) AS [al{j}]");
            }
        }
        sb.Append($"\nFROM (SELECT TOP (@sample) * FROM {From(table)}) AS x;");
        return sb.ToString();
    }

    /// <summary>First @patternRows rows of the given string columns, each cast to nvarchar(200) as [p{j}].</summary>
    public static string PatternFetch(TableInfo table, IReadOnlyList<ColumnInfo> stringColumns)
    {
        var columns = string.Join(", ", stringColumns.Select((c, j) => $"CAST(t.{Quote(c.Name)} AS nvarchar(200)) AS [p{j}]"));
        return $"SELECT TOP (@patternRows) {columns} FROM {From(table)} AS t;";
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Catalog"`
Expected: PASS (all catalog unit tests, 43 in total so far).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~Dbm.Tests.Integration.Catalog.ProfilerTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Catalog/ValueSignature.cs plugins/db-migrate/engine/Dbm/Core/Catalog/Profiler.cs \
  plugins/db-migrate/engine/Dbm.Tests/Unit/Catalog plugins/db-migrate/engine/Dbm.Tests/Integration/Catalog/ProfilerTests.cs
git commit -F - <<'EOF'
feat(catalog): value signatures and sampling profiler

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.4: Name normalisation, vectoriser, vector index

**Files:**
- Create: `ENGINE/Dbm/Core/Matching/SparseVector.cs`, `Synonyms.cs`, `synonyms.json`, `NameNormalizer.cs`, `NgramTfidfVectorizer.cs`, `VectorIndex.cs`
- Create: `ENGINE/Dbm/Core/State/CatalogRepo.cs` (this task adds only the `VectorRow` record; T2.5 adds `CatalogRepo` to the same file)
- Modify: `ENGINE/Dbm/Dbm.csproj` (embed `synonyms.json`)
- Test: `ENGINE/Dbm.Tests/Unit/Matching/NameNormalizerTests.cs`, `ENGINE/Dbm.Tests/Unit/Matching/VectorIndexTests.cs`

**Interfaces:**
- Consumes: C10 model, `Json`, `Workspace`, `UserHome` (C1), `Side` (C2), `TestCatalogs`.
- Produces: C11 text layer exactly as specified (`SparseVector`, `Synonyms.Default/Load/Expand`, `NameNormalizer.Split/Tokens/Singular`, `IVectorizer`, `NgramTfidfVectorizer`, `SearchHit`, `VectorIndex.Build/FromRows/Search`) and `VectorRow` (C10), plus:
  ```csharp
  public sealed class Synonyms
  {
      public static Synonyms ForProject(Workspace ws);   // Load(UserHome.Dir/synonyms.json, ws.Dir/synonyms.json)
      public bool Knows(string token);                   // abbreviation or group member
  }
  public sealed class VectorIndex
  {
      public const string Separator = " — ";             // VectorRow.Text = "<key> — <tokens joined by space>"
      public const double ContextWeight = 0.5;
      public static SparseVector Blend(SparseVector a, SparseVector b, double weight);   // normalize(a + weight·b)
      public static string TextOf(string key, IReadOnlyList<string> tokens);
      public static IReadOnlyList<string> TokensOf(string text);
  }
  ```
- Semantics:
  - `Tokens`: Split → drop a leading noise token (`tbl tb vw fld col`) when others remain → each part is expanded (a part that is not a known abbreviation/group word is looked up in its singular form, so `Clients` → `customer`) → every expanded word is singularised → duplicates dropped → tokens contained in `contextTokens` are dropped when other tokens remain.
  - Synonym files merge: later files override abbreviations; a group that shares a word with an existing group absorbs it (all its words move to the new group's canonical).
  - Documents: table = `Tokens(table.Name)`; column = `Tokens(column.Name, context: table tokens)` + value-class tokens (`email`, `phone`, `url`, `guid`, `postal code`, `country code` from the profile's `SemanticClass`) + up to 8 description words (length ≥ 3, stop words removed). A column's **stored vector** is `Blend(columnVector, tableVector, 0.5)` so `"customer email"` prefers `app.Customers.Email` over `app.Orders.CustomerId`; `Text` holds only the column's own tokens. `FromRows` re-fits the vectorizer from the tokens in `Text` (same documents → same idf) and reuses the stored vectors.

- [ ] **Step 1: Write the failing tests**

`ENGINE/Dbm.Tests/Unit/Matching/NameNormalizerTests.cs`
```csharp
using Dbm.Core.Matching;

namespace Dbm.Tests.Unit.Matching;

public sealed class NameNormalizerTests
{
    private static readonly Synonyms Syn = Synonyms.Default();

    [Theory]
    [InlineData("CUST_NM", new[] { "cust", "nm" })]
    [InlineData("EmailAddress", new[] { "email", "address" })]
    [InlineData("Line1", new[] { "line", "1" })]
    [InlineData("HTTPServer", new[] { "http", "server" })]
    [InlineData("tbl_Customer", new[] { "tbl", "customer" })]
    [InlineData("order-line.item id", new[] { "order", "line", "item", "id" })]
    public void Split(string identifier, string[] expected) => Assert.Equal(expected, NameNormalizer.Split(identifier));

    [Theory]
    [InlineData("EmailAddress", new[] { "email", "address" })]
    [InlineData("tbl_Customer", new[] { "customer" })]
    [InlineData("DOB", new[] { "birth", "date" })]
    [InlineData("CRT_DT", new[] { "created", "date" })]
    [InlineData("CreatedAt", new[] { "created", "date" })]
    [InlineData("Clients", new[] { "customer" })]
    [InlineData("ZIP", new[] { "postal", "code" })]
    [InlineData("PHONE_NO", new[] { "phone", "number" })]
    [InlineData("LastModified", new[] { "last", "updated" })]
    [InlineData("Addresses", new[] { "address" })]
    [InlineData("ORD_HDR", new[] { "order", "header" })]
    public void Tokens(string identifier, string[] expected) => Assert.Equal(expected, NameNormalizer.Tokens(identifier, Syn));

    [Fact]
    public void Tokens_drop_context_tokens_when_others_remain()
    {
        var context = NameNormalizer.Tokens("CUST", Syn);
        Assert.Equal(new[] { "customer" }, context);
        Assert.Equal(new[] { "name" }, NameNormalizer.Tokens("CUST_NM", Syn, context));
        Assert.Equal(new[] { "customer" }, NameNormalizer.Tokens("CUST", Syn, context));   // nothing else would remain
        Assert.Equal(new[] { "id" }, NameNormalizer.Tokens("CustomerId", Syn, NameNormalizer.Tokens("Customers", Syn)));
    }

    [Theory]
    [InlineData("addresses", "address")]
    [InlineData("categories", "category")]
    [InlineData("statuses", "status")]
    [InlineData("orders", "order")]
    [InlineData("address", "address")]
    [InlineData("status", "status")]
    [InlineData("analysis", "analysis")]
    [InlineData("boxes", "box")]
    [InlineData("ids", "id")]
    [InlineData("is", "is")]
    [InlineData("1", "1")]
    public void Singular(string token, string expected) => Assert.Equal(expected, NameNormalizer.Singular(token));

    [Fact]
    public void Synonyms_expand_abbreviations_then_canonicalise_groups()
    {
        Assert.Equal(new[] { "customer" }, Syn.Expand("CUST"));
        Assert.Equal(new[] { "customer" }, Syn.Expand("client"));
        Assert.Equal(new[] { "birth", "date" }, Syn.Expand("dob"));
        Assert.Equal(new[] { "time" }, Syn.Expand("ts"));
        Assert.Equal(new[] { "updated" }, Syn.Expand("modified"));
        Assert.Equal(new[] { "zebra" }, Syn.Expand("zebra"));
    }

    [Fact]
    public void Synonyms_load_merges_extra_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbm-syn-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"abbreviations":{"kd":["kunde"],"cust":["patron"]},"groups":[["kunde","customer"]]}""");
        try
        {
            var syn = Synonyms.Load(path, Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
            Assert.Equal(new[] { "kunde" }, syn.Expand("KD"));
            Assert.Equal(new[] { "patron" }, syn.Expand("cust"));
            Assert.Equal(new[] { "kunde" }, syn.Expand("client"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Synonyms_load_reports_invalid_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbm-syn-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => Synonyms.Load(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

`ENGINE/Dbm.Tests/Unit/Matching/VectorIndexTests.cs`
```csharp
using Dbm.Core;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Matching;

public sealed class VectorIndexTests
{
    private static readonly Synonyms Syn = Synonyms.Default();

    [Fact]
    public void SparseVector_normalises_and_dots()
    {
        var a = SparseVector.Normalized(new Dictionary<string, double> { ["x"] = 3, ["y"] = 4 });
        Assert.Equal(0.6, a.W["x"], 6);
        Assert.Equal(1.0, a.Dot(a), 6);
        var b = SparseVector.Normalized(new Dictionary<string, double> { ["y"] = 1 });
        Assert.Equal(0.8, a.Dot(b), 6);
        Assert.True(SparseVector.Normalized(new Dictionary<string, double> { ["z"] = 0 }).IsEmpty);
        Assert.Equal("{\"w\":{\"y\":1}}", Json.Serialize(b));
    }

    [Fact]
    public void Vectorizer_cosine_sanity()
    {
        var v = new NgramTfidfVectorizer();
        var docs = new[] { new[] { "customer", "name" }, new[] { "customer", "email" }, new[] { "product", "price" }, new[] { "order", "date" } };
        v.Fit(docs);
        var same = v.Transform(docs[0]).Dot(v.Transform(new[] { "customer", "name" }));
        var related = v.Transform(docs[0]).Dot(v.Transform(docs[1]));
        var unrelated = v.Transform(docs[0]).Dot(v.Transform(docs[2]));
        Assert.Equal(1.0, same, 6);
        Assert.True(related > unrelated);
        Assert.True(v.Transform(Array.Empty<string>()).IsEmpty);
    }

    [Fact]
    public void Build_creates_one_row_per_table_and_column()
    {
        var src = TestCatalogs.LegacyShop();
        var tgt = TestCatalogs.ShopV2();
        var (rows, _) = VectorIndex.Build(src, tgt, Syn);
        var expected = src.Tables.Count + src.Tables.Sum(t => t.Columns.Count) + tgt.Tables.Count + tgt.Tables.Sum(t => t.Columns.Count);
        Assert.Equal(expected, rows.Count);
        Assert.Contains(rows, r => r is { Side: Side.Tgt, Kind: "column", Key: "app.Customers.Email", Text: "app.Customers.Email — email" });
        Assert.Contains(rows, r => r is { Side: Side.Src, Kind: "column", Key: "dbo.CUST.CUST_NM", Text: "dbo.CUST.CUST_NM — name" });
        Assert.Contains(rows, r => r is { Side: Side.Src, Kind: "table", Key: "dbo.ORD_HDR", Text: "dbo.ORD_HDR — order header" });
    }

    [Fact]
    public void Search_customer_email_finds_both_email_columns_in_the_top_3()
    {
        var (_, index) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Syn);
        var top = index.Search("customer email", k: 3).Select(h => h.Key).ToList();
        Assert.Contains("app.Customers.Email", top);
        Assert.Contains("dbo.CUST.EMAIL_ADDR", top);
    }

    [Fact]
    public void Search_filters_by_side_and_kind_and_from_rows_matches_build()
    {
        var (rows, built) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Syn);
        var hits = built.Search("cust email", Side.Tgt, "column", 5);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal((Side.Tgt, "column"), (h.Side, h.Kind)));
        Assert.Equal("app.Customers.Email", hits[0].Key);

        var loaded = VectorIndex.FromRows(rows, Syn).Search("cust email", Side.Tgt, "column", 5);
        Assert.Equal(hits.Select(h => (h.Key, Math.Round(h.Score, 9))), loaded.Select(h => (h.Key, Math.Round(h.Score, 9))));
        Assert.Empty(built.Search("   "));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Matching"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Matching' does not exist in the namespace 'Dbm.Core'`.

- [ ] **Step 3: Implement SparseVector**

`ENGINE/Dbm/Core/Matching/SparseVector.cs`
```csharp
using System.Text.Json.Serialization;

namespace Dbm.Core.Matching;

/// <summary>Sparse feature vector (feature name -> weight). Vectors produced by the vectorizer are L2-normalised, so Dot = cosine.</summary>
public sealed class SparseVector
{
    public Dictionary<string, double> W { get; init; } = new();

    [JsonIgnore] public bool IsEmpty => W.Count == 0;

    public double Dot(SparseVector other)
    {
        var (small, large) = W.Count <= other.W.Count ? (W, other.W) : (other.W, W);
        double sum = 0;
        foreach (var (key, value) in small)
        {
            if (large.TryGetValue(key, out var o)) sum += value * o;
        }
        return sum;
    }

    /// <summary>L2-normalised copy; zero weights are dropped; an all-zero input gives an empty vector.</summary>
    public static SparseVector Normalized(Dictionary<string, double> weights)
    {
        var norm = Math.Sqrt(weights.Values.Sum(v => v * v));
        if (norm == 0) return new SparseVector();
        var w = new Dictionary<string, double>(weights.Count, StringComparer.Ordinal);
        foreach (var (key, value) in weights)
        {
            if (value != 0) w[key] = value / norm;
        }
        return new SparseVector { W = w };
    }
}
```

- [ ] **Step 4: Add and embed the synonym dictionary**

`ENGINE/Dbm/Core/Matching/synonyms.json`
```json
{
  "abbreviations": {
    "acct": ["account"],
    "addr": ["address"],
    "amt": ["amount"],
    "at": ["date"],
    "avg": ["average"],
    "bal": ["balance"],
    "cat": ["category"],
    "ccy": ["currency"],
    "cd": ["code"],
    "clnt": ["client"],
    "cmnt": ["comment"],
    "cmt": ["comment"],
    "cnt": ["count"],
    "cntry": ["country"],
    "cre": ["created"],
    "crt": ["created"],
    "ctry": ["country"],
    "curr": ["currency"],
    "cust": ["customer"],
    "del": ["deleted"],
    "dept": ["department"],
    "desc": ["description"],
    "descr": ["description"],
    "dob": ["birth", "date"],
    "dst": ["destination"],
    "dt": ["date"],
    "emp": ["employee"],
    "eml": ["email"],
    "flg": ["flag"],
    "fname": ["first", "name"],
    "hdr": ["header"],
    "img": ["image"],
    "inv": ["invoice"],
    "lat": ["latitude"],
    "lname": ["last", "name"],
    "ln": ["line"],
    "lng": ["longitude"],
    "loc": ["location"],
    "lon": ["longitude"],
    "lvl": ["level"],
    "mgr": ["manager"],
    "mob": ["mobile"],
    "msg": ["message"],
    "mth": ["month"],
    "nbr": ["number"],
    "nm": ["name"],
    "no": ["number"],
    "num": ["number"],
    "on": ["date"],
    "ord": ["order"],
    "org": ["organization"],
    "pct": ["percent"],
    "ph": ["phone"],
    "postcode": ["postal", "code"],
    "prc": ["price"],
    "prod": ["product"],
    "pwd": ["password"],
    "qty": ["quantity"],
    "ref": ["reference"],
    "seq": ["sequence"],
    "src": ["source"],
    "stat": ["status"],
    "sts": ["status"],
    "tel": ["phone"],
    "tgt": ["target"],
    "tot": ["total"],
    "ts": ["timestamp"],
    "ttl": ["total"],
    "txt": ["text"],
    "typ": ["type"],
    "upd": ["updated"],
    "usr": ["user"],
    "yr": ["year"],
    "zip": ["postal", "code"]
  },
  "groups": [
    ["customer", "client"],
    ["product", "item"],
    ["last", "surname"],
    ["first", "forename", "given"],
    ["phone", "telephone"],
    ["mobile", "cell"],
    ["email", "mail"],
    ["created", "inserted"],
    ["updated", "modified", "changed"],
    ["deleted", "removed"],
    ["active", "enabled"],
    ["time", "timestamp"],
    ["comment", "remark"],
    ["organization", "organisation"]
  ]
}
```
(`at`/`on` → `date` make `CreatedAt`/`CreatedOn` tokenise like `CRT_DT`.)

In `ENGINE/Dbm/Dbm.csproj`, add this `ItemGroup` directly before the closing `</Project>` tag:
```xml
  <ItemGroup>
    <EmbeddedResource Include="Core\Matching\synonyms.json" LogicalName="Dbm.Core.Matching.synonyms.json" />
  </ItemGroup>
```

- [ ] **Step 5: Implement Synonyms**

`ENGINE/Dbm/Core/Matching/Synonyms.cs`
```csharp
using System.Text.Json;

namespace Dbm.Core.Matching;

/// <summary>Abbreviation dictionary + synonym groups (canonical = first group member).</summary>
public sealed class Synonyms
{
    private const string ResourceName = "Dbm.Core.Matching.synonyms.json";
    private static readonly Lazy<string> DefaultJson = new(LoadDefaultJson);

    private readonly Dictionary<string, string[]> _abbreviations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);

    private Synonyms() { }

    /// <summary>The embedded Dbm/Core/Matching/synonyms.json.</summary>
    public static Synonyms Default()
    {
        var synonyms = new Synonyms();
        synonyms.Merge(DefaultJson.Value, ResourceName);
        return synonyms;
    }

    /// <summary>Default merged with each existing file (later files win for abbreviations; groups accumulate). Missing paths are skipped.</summary>
    public static Synonyms Load(params string?[] extraJsonPaths)
    {
        var synonyms = Default();
        foreach (var path in extraJsonPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) synonyms.Merge(File.ReadAllText(path), path);
        }
        return synonyms;
    }

    /// <summary>Default + ~/.dbmigrate/synonyms.json (team) + &lt;workspace&gt;/.dbmigrate/synonyms.json (project).</summary>
    public static Synonyms ForProject(Workspace ws) =>
        Load(Path.Combine(UserHome.Dir, "synonyms.json"), Path.Combine(ws.Dir, "synonyms.json"));

    /// <summary>Abbreviation expansion then group canonicalisation; unknown token -> [token]. Output is lower-case.</summary>
    public IReadOnlyList<string> Expand(string token)
    {
        var t = token.ToLowerInvariant();
        IEnumerable<string> words = _abbreviations.TryGetValue(t, out var expansion) ? expansion : new[] { t };
        return words.Select(w => _canonical.TryGetValue(w, out var canonical) ? canonical : w).ToList();
    }

    /// <summary>True when the token is a known abbreviation or group member.</summary>
    public bool Knows(string token)
    {
        var t = token.ToLowerInvariant();
        return _abbreviations.ContainsKey(t) || _canonical.ContainsKey(t);
    }

    private void Merge(string json, string source)
    {
        SynonymsFile file;
        try
        {
            file = JsonSerializer.Deserialize<SynonymsFile>(json, Json.Options) ?? new SynonymsFile(null, null);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid synonyms file '{source}': {ex.Message}", ex);
        }
        foreach (var (key, value) in file.Abbreviations ?? new Dictionary<string, List<string>>())
            _abbreviations[key.ToLowerInvariant()] = value.Select(v => v.ToLowerInvariant()).ToArray();
        foreach (var group in file.Groups ?? new List<List<string>>())
        {
            if (group.Count < 2) continue;
            var members = group.Select(m => m.ToLowerInvariant()).ToList();
            var canonical = members[0];
            // A group that overlaps an existing one absorbs it: every word of the old group moves to the new canonical.
            var affected = new HashSet<string>(members, StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!_canonical.TryGetValue(member, out var previous)) continue;
                foreach (var (word, c) in _canonical)
                    if (c == previous) affected.Add(word);
            }
            foreach (var word in affected) _canonical[word] = canonical;
        }
    }

    private static string LoadDefaultJson()
    {
        using var stream = typeof(Synonyms).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record SynonymsFile(Dictionary<string, List<string>>? Abbreviations, List<List<string>>? Groups);
}
```

- [ ] **Step 6: Implement NameNormalizer**

`ENGINE/Dbm/Core/Matching/NameNormalizer.cs`
```csharp
using System.Text;

namespace Dbm.Core.Matching;

/// <summary>Turns identifiers ("CUST_NM", "EmailAddress", "tbl_Customer") into normalised word tokens.</summary>
public static class NameNormalizer
{
    private static readonly HashSet<string> NoisePrefixes = new(StringComparer.Ordinal) { "tbl", "tb", "vw", "fld", "col" };

    /// <summary>Splits on non-alphanumerics, lower→upper, acronym→word ("HTTPServer") and letter↔digit boundaries; lower-case output.</summary>
    public static IReadOnlyList<string> Split(string identifier)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            if (current.Length > 0)
            {
                var p = identifier[i - 1];
                var boundary =
                    (char.IsLower(p) && char.IsUpper(c)) ||
                    (char.IsLetter(p) && char.IsDigit(c)) ||
                    (char.IsDigit(p) && char.IsLetter(c)) ||
                    (char.IsUpper(p) && char.IsUpper(c) && i + 1 < identifier.Length && char.IsLower(identifier[i + 1]));
                if (boundary) Flush();
            }
            current.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>
    /// Split → drop a noise prefix (tbl, tb, vw, fld, col) when other tokens remain → Expand (a plural that is unknown as written is
    /// looked up in its singular form) → Singular → de-duplicate → drop tokens contained in <paramref name="contextTokens"/> when
    /// other tokens remain.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string identifier, Synonyms synonyms, IReadOnlyCollection<string>? contextTokens = null)
    {
        var parts = Split(identifier).ToList();
        if (parts.Count > 1 && NoisePrefixes.Contains(parts[0])) parts.RemoveAt(0);
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            var lookup = synonyms.Knows(part) ? part : Singular(part);
            foreach (var expanded in synonyms.Expand(lookup))
            {
                var token = Singular(expanded);
                if (!tokens.Contains(token)) tokens.Add(token);
            }
        }
        if (contextTokens is { Count: > 0 })
        {
            var kept = tokens.Where(t => !contextTokens.Contains(t)).ToList();
            if (kept.Count > 0) return kept;
        }
        return tokens;
    }

    /// <summary>addresses→address, categories→category, statuses→status, orders→order; "ss"/"us"/"is" endings and tokens shorter than 3 chars unchanged.</summary>
    public static string Singular(string token)
    {
        if (token.Length < 3 || !char.IsLetter(token[^1])) return token;
        if (token.EndsWith("ss", StringComparison.Ordinal) || token.EndsWith("us", StringComparison.Ordinal) ||
            token.EndsWith("is", StringComparison.Ordinal)) return token;
        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4) return token[..^3] + "y";
        if (token.EndsWith("es", StringComparison.Ordinal))
        {
            var stem = token[..^2];
            if (stem.EndsWith("ss", StringComparison.Ordinal) || stem.EndsWith("us", StringComparison.Ordinal) ||
                stem.EndsWith("x", StringComparison.Ordinal) || stem.EndsWith("ch", StringComparison.Ordinal) ||
                stem.EndsWith("sh", StringComparison.Ordinal)) return stem;
        }
        return token.EndsWith('s') ? token[..^1] : token;
    }
}
```

- [ ] **Step 7: Implement the vectorizer**

`ENGINE/Dbm/Core/Matching/NgramTfidfVectorizer.cs`
```csharp
namespace Dbm.Core.Matching;

public interface IVectorizer
{
    void Fit(IEnumerable<IReadOnlyList<string>> documents);
    SparseVector Transform(IReadOnlyList<string> tokens);
}

/// <summary>
/// Word + character n-gram TF-IDF. Features: "w:&lt;token&gt;" (weight wordWeight·idf) and "g:&lt;n-gram of '#' + tokens joined by '#' + '#'&gt;"
/// (weight ngramWeight·idf), multiplied by the raw term count; idf = ln((1+N)/(1+df))+1; output L2-normalised.
/// </summary>
public sealed class NgramTfidfVectorizer(int n = 3, double wordWeight = 1.0, double ngramWeight = 0.5) : IVectorizer
{
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private int _documents;

    public void Fit(IEnumerable<IReadOnlyList<string>> documents)
    {
        _documentFrequency.Clear();
        _documents = 0;
        foreach (var document in documents)
        {
            _documents++;
            foreach (var feature in Features(document).Keys)
                _documentFrequency[feature] = _documentFrequency.GetValueOrDefault(feature) + 1;
        }
    }

    public SparseVector Transform(IReadOnlyList<string> tokens)
    {
        var counts = Features(tokens);
        var weights = new Dictionary<string, double>(counts.Count, StringComparer.Ordinal);
        foreach (var (feature, count) in counts)
        {
            var idf = Math.Log((1.0 + _documents) / (1.0 + _documentFrequency.GetValueOrDefault(feature))) + 1.0;
            weights[feature] = count * (feature[0] == 'w' ? wordWeight : ngramWeight) * idf;
        }
        return SparseVector.Normalized(weights);
    }

    private Dictionary<string, int> Features(IReadOnlyList<string> tokens)
    {
        var features = new Dictionary<string, int>(StringComparer.Ordinal);
        if (tokens.Count == 0) return features;
        foreach (var token in tokens) Add("w:" + token);
        var text = "#" + string.Join("#", tokens) + "#";
        for (var i = 0; i + n <= text.Length; i++) Add("g:" + text.Substring(i, n));
        return features;

        void Add(string key) => features[key] = features.GetValueOrDefault(key) + 1;
    }
}
```

- [ ] **Step 8: Add VectorRow and implement VectorIndex**

`ENGINE/Dbm/Core/State/CatalogRepo.cs` (T2.5 appends the `CatalogRepo` class to this file)
```csharp
using Dbm.Core.Matching;

namespace Dbm.Core.State;

public sealed record VectorRow(Side Side, string Kind, string Key, string Text, SparseVector Vec);   // Kind "table"|"column"
```

`ENGINE/Dbm/Core/Matching/VectorIndex.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.State;

namespace Dbm.Core.Matching;

public sealed record SearchHit(Side Side, string Kind, string Key, double Score, string Text);

/// <summary>
/// In-memory cosine search over table and column documents of both catalogs.
/// Table document = table-name tokens. Column document = column-name tokens (owning table's tokens as context, so "CUST_NM" in CUST
/// becomes [name]) + value-class tokens (email, phone, url, guid, postal code, country code) + up to 8 description tokens.
/// A column's stored vector is blended with its table's vector (weight 0.5) so "customer email" prefers Customers.Email.
/// Row text = "&lt;key&gt; — &lt;tokens&gt;"; FromRows re-fits the vectorizer from those tokens and reuses the stored vectors.
/// </summary>
public sealed class VectorIndex
{
    public const string Separator = " — ";
    public const double ContextWeight = 0.5;
    private const int MaxDescriptionTokens = 8;

    private static readonly Dictionary<string, string[]> ClassTokens = new(StringComparer.Ordinal)
    {
        ["email"] = new[] { "email" },
        ["phone"] = new[] { "phone" },
        ["url"] = new[] { "url" },
        ["guid"] = new[] { "guid" },
        ["postal_code"] = new[] { "postal", "code" },
        ["country_code"] = new[] { "country", "code" },
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "this", "that", "which", "are", "was", "not", "used", "value", "column", "table",
    };

    private readonly IReadOnlyList<VectorRow> _rows;
    private readonly NgramTfidfVectorizer _vectorizer;
    private readonly Synonyms _synonyms;

    private VectorIndex(IReadOnlyList<VectorRow> rows, NgramTfidfVectorizer vectorizer, Synonyms synonyms)
    {
        _rows = rows;
        _vectorizer = vectorizer;
        _synonyms = synonyms;
    }

    public static (List<VectorRow> Rows, VectorIndex Index) Build(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms)
    {
        var documents = new List<(Side Side, string Kind, string Key, IReadOnlyList<string> Tokens)>();
        foreach (var (side, snapshot) in new[] { (Side.Src, src), (Side.Tgt, tgt) })
        {
            foreach (var table in snapshot.Tables)
            {
                var tableTokens = NameNormalizer.Tokens(table.Name, synonyms);
                documents.Add((side, "table", table.Key, tableTokens));
                foreach (var column in table.Columns)
                    documents.Add((side, "column", $"{table.Key}.{column.Name}", ColumnTokens(column, tableTokens, synonyms)));
            }
        }
        var vectorizer = new NgramTfidfVectorizer();
        vectorizer.Fit(documents.Select(d => d.Tokens));
        var rows = new List<VectorRow>(documents.Count);
        SparseVector tableVector = new();
        foreach (var d in documents)
        {
            var vector = vectorizer.Transform(d.Tokens);
            if (d.Kind == "table") tableVector = vector;   // documents are ordered table, its columns, next table, …
            else vector = Blend(vector, tableVector, ContextWeight);
            rows.Add(new VectorRow(d.Side, d.Kind, d.Key, TextOf(d.Key, d.Tokens), vector));
        }
        return (rows, new VectorIndex(rows, vectorizer, synonyms));
    }

    /// <summary>normalize(a + weight·b): a column vector carries its owning table's vector as context.</summary>
    public static SparseVector Blend(SparseVector a, SparseVector b, double weight)
    {
        var sum = new Dictionary<string, double>(a.W, StringComparer.Ordinal);
        foreach (var (key, value) in b.W) sum[key] = sum.GetValueOrDefault(key) + weight * value;
        return SparseVector.Normalized(sum);
    }

    public static VectorIndex FromRows(IReadOnlyList<VectorRow> rows, Synonyms synonyms)
    {
        var vectorizer = new NgramTfidfVectorizer();
        vectorizer.Fit(rows.Select(r => TokensOf(r.Text)));
        return new VectorIndex(rows.ToList(), vectorizer, synonyms);
    }

    public IReadOnlyList<SearchHit> Search(string query, Side? side = null, string? kind = null, int k = 8)
    {
        var q = _vectorizer.Transform(NameNormalizer.Tokens(query, _synonyms));
        if (q.IsEmpty) return Array.Empty<SearchHit>();
        return _rows
            .Where(r => (side is null || r.Side == side) && (kind is null || string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            .Select(r => new SearchHit(r.Side, r.Kind, r.Key, q.Dot(r.Vec), r.Text))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Side)
            .ThenBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, k))
            .ToList();
    }

    public static string TextOf(string key, IReadOnlyList<string> tokens) => key + Separator + string.Join(' ', tokens);

    public static IReadOnlyList<string> TokensOf(string text)
    {
        var i = text.LastIndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? Array.Empty<string>() : text[(i + Separator.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<string> ColumnTokens(ColumnInfo column, IReadOnlyList<string> tableTokens, Synonyms synonyms)
    {
        var tokens = NameNormalizer.Tokens(column.Name, synonyms, tableTokens.ToList()).ToList();
        if (column.Profile?.SemanticClass is { } semanticClass && ClassTokens.TryGetValue(semanticClass, out var extra))
            foreach (var t in extra) if (!tokens.Contains(t)) tokens.Add(t);
        if (!string.IsNullOrWhiteSpace(column.Description))
        {
            var added = 0;
            foreach (var t in NameNormalizer.Tokens(column.Description, synonyms))
            {
                if (added == MaxDescriptionTokens) break;
                if (t.Length < 3 || StopWords.Contains(t) || tokens.Contains(t)) continue;
                tokens.Add(t);
                added++;
            }
        }
        return tokens;
    }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Matching"`
Expected: PASS (37 tests). With the default dictionary `Search("customer email")` ranks `app.Customers.Email` (≈ 0.92) then `dbo.CUST.EMAIL_ADDR` (≈ 0.75) above the two customer tables (≈ 0.65).

- [ ] **Step 10: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Matching plugins/db-migrate/engine/Dbm/Core/State/CatalogRepo.cs \
  plugins/db-migrate/engine/Dbm/Dbm.csproj plugins/db-migrate/engine/Dbm.Tests/Unit/Matching
git commit -F - <<'EOF'
feat(matching): name normaliser, synonyms, n-gram TF-IDF vector index

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.5: Fingerprint, drift, catalog repo, discover job, `discover` / `search` / `show`

**Files:**
- Create: `ENGINE/Dbm/Core/Catalog/Fingerprint.cs`, `DriftChecker.cs`, `DiscoverJob.cs`, `CatalogText.cs`
- Create: `ENGINE/Dbm/Cli/Commands/CatalogCommands.cs`
- Modify: `ENGINE/Dbm/Core/State/CatalogRepo.cs` (add `CatalogRepo`), `ENGINE/Dbm/Core/DbmServices.cs` (`Catalog`), `ENGINE/Dbm/Core/JobRegistry.cs` (`DiscoverJob`), `ENGINE/Dbm/Cli/CommandRegistry.cs` (3 commands), `ENGINE/Dbm/Web/ApprovalGuards.cs` (drift guard)
- Test: `ENGINE/Dbm.Tests/Unit/Catalog/FingerprintTests.cs`, `ENGINE/Dbm.Tests/Unit/State/CatalogRepoTests.cs`, `ENGINE/Dbm.Tests/Unit/Catalog/CatalogTextTests.cs`, `ENGINE/Dbm.Tests/Integration/Catalog/DiscoverJobTests.cs`

**Interfaces:**
- Consumes: `StateDb`, `ConnectionRepo`, `ProjectRepo.GetSettings`, `JobRepo`, `PhaseRepo`, `ArtifactRepo` (C2), `SqlConnect`, `Redactor` (C3), `WorkflowEngine.Rediscover`, `WorkflowException` (C5), `IJobHandler`, `JobContext`, `JobResult`, `JobRunner.RunPendingAsync` (C6), `WebState`, `ApprovalGuards.All`, `ServerControl.EnsureRunningAsync` (C7), `ICommand`, `Args`, `CliContext`, `Output` (C8), T2.2–T2.4 types.
- Produces: `Fingerprint.Compute`, `DriftResult`, `DriftChecker.CheckAsync`, `CatalogRepo` exactly as in C10; `DbmServices.Catalog` (C6); job kind `"discover"`; plus:
  ```csharp
  public static class CatalogText    // compact one-line renderings (CLI, packets)
  {
      public static string TableHeader(Side side, TableInfo t);      // "src table dbo.CUST rows=1000 size=0.14MB pk=CUST_ID triggers=0"
      public static string ColumnLine(TableInfo t, ColumnInfo c);    // "EMAIL_ADDR varchar(120) null=10% distinct=0.90 maxlen=28 class=email samples=[…]"
      public static string ProfileLine(ColumnProfile p);             // "profile sampled=1000 nulls=100 distinct=900 min=… max=… …"
      public static string Table(Side side, TableInfo t, int maxColumns = int.MaxValue);
      public static string SearchLine(SearchHit hit);                // "0.82 tgt column app.Customers.Email — email"
      public static string Percent(double ratio);
  }
  public sealed class DiscoverJob : IJobHandler { public string Kind => "discover"; }
  public sealed class DiscoverCommand : ICommand   // "discover [--inline]"
  public sealed class SearchCommand : ICommand     // "search <query> [--side src|tgt] [--kind table|column] [-k n] [--json]"
  public sealed class ShowCommand : ICommand       // "show <schema.table[.column] | table | F001> [--side src|tgt] [--json]"
  public static class ApprovalGuards { public static Task<string?> DriftGuardAsync(WebState state, PhaseName phase, CancellationToken ct); }
  ```
- Fingerprint covers tables, columns (type, length, precision, scale, nullability, identity, computed + definition, rowversion, default, collation), indexes, FK columns/targets and trigger names; it excludes rows, sizes, profiles, descriptions, `ExtractedAt`, server metadata and FK trust/disabled state (the M5 transfer toggles NOCHECK/WITH CHECK, which must not read as drift).

- [ ] **Step 1: Write the failing unit tests**

`ENGINE/Dbm.Tests/Unit/Catalog/FingerprintTests.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Catalog;

public sealed class FingerprintTests
{
    private static CatalogSnapshot WithTable(CatalogSnapshot s, string key, Func<TableInfo, TableInfo> change) =>
        s with { Tables = s.Tables.Select(t => t.Key == key ? change(t) : t).ToList() };

    [Fact]
    public void Is_lower_hex_sha256_and_deterministic()
    {
        var fp = Fingerprint.Compute(TestCatalogs.LegacyShop());
        Assert.Matches("^[0-9a-f]{64}$", fp);
        Assert.Equal(fp, Fingerprint.Compute(TestCatalogs.LegacyShop()));
        Assert.NotEqual(fp, Fingerprint.Compute(TestCatalogs.ShopV2()));
    }

    [Fact]
    public void Ignores_rows_sizes_profiles_descriptions_time_order_and_fk_trust()
    {
        var baseline = TestCatalogs.LegacyShop();
        var changed = WithTable(baseline, "dbo.CUST", t => t with
        {
            Rows = 99, SizeMb = 42, Description = "customers",
            Columns = t.Columns.Select(c => c with { Profile = null }).ToList(),
        });
        changed = WithTable(changed, "dbo.ORD_HDR", t => t with
        {
            ForeignKeys = t.ForeignKeys.Select(f => f with { IsNotTrusted = false, IsDisabled = true }).ToList(),
        });
        changed = changed with { ExtractedAt = DateTimeOffset.UnixEpoch, Tables = Enumerable.Reverse(changed.Tables).ToList() };
        Assert.Equal(Fingerprint.Compute(baseline), Fingerprint.Compute(changed));
    }

    [Fact]
    public void Changes_with_structure()
    {
        var baseline = TestCatalogs.ShopV2();
        var fp = Fingerprint.Compute(baseline);
        var widened = WithTable(baseline, "app.Customers", t => t with
        {
            Columns = t.Columns.Select(c => c.Name == "Email" ? c with { MaxLength = 200 } : c).ToList(),
        });
        var added = WithTable(baseline, "app.Orders", t => t with
        {
            Columns = t.Columns.Append(TestCatalogs.Col("Channel", "varchar", 10) with { Ordinal = 8 }).ToList(),
        });
        var trigger = WithTable(baseline, "app.Products", t => t with { TriggerCount = 1, TriggerNames = new List<string> { "trg_x" } });
        Assert.NotEqual(fp, Fingerprint.Compute(widened));
        Assert.NotEqual(fp, Fingerprint.Compute(added));
        Assert.NotEqual(fp, Fingerprint.Compute(trigger));
    }
}
```

`ENGINE/Dbm.Tests/Unit/State/CatalogRepoTests.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public sealed class CatalogRepoTests
{
    [Fact]
    public void Save_get_and_replace_snapshots()
    {
        using var project = TempProject.Create();
        var repo = project.Services.Catalog;
        Assert.Null(repo.Get(Side.Src));
        Assert.Null(repo.Fingerprint(Side.Src));

        repo.Save(Side.Src, TestCatalogs.LegacyShop(), "fp1");
        var back = repo.Get(Side.Src)!;
        Assert.Equal(8, back.Tables.Count);
        Assert.Equal(0.1, back.FindTable("dbo.CUST")!.FindColumn("EMAIL_ADDR")!.Profile!.NullRatio, 3);
        Assert.Equal("fp1", repo.Fingerprint(Side.Src));
        Assert.Null(repo.Get(Side.Tgt));

        repo.Save(Side.Src, TestCatalogs.ShopV2(), "fp2");
        Assert.Equal(6, repo.Get(Side.Src)!.Tables.Count);
        Assert.Equal("fp2", repo.Fingerprint(Side.Src));
    }

    [Fact]
    public void SaveVectors_replaces_rows_per_side_and_filters()
    {
        using var project = TempProject.Create();
        var repo = project.Services.Catalog;
        var (rows, _) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Synonyms.Default());
        repo.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Src));
        repo.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));
        repo.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));   // idempotent replace

        Assert.Equal(rows.Count, repo.Vectors().Count);
        Assert.Equal(6, repo.Vectors(Side.Tgt, "table").Count);
        var email = repo.Vectors(Side.Tgt, "column").Single(r => r.Key == "app.Customers.Email");
        Assert.Equal("app.Customers.Email — email", email.Text);
        Assert.Equal(1.0, email.Vec.Dot(email.Vec), 6);

        Assert.Throws<ArgumentException>(() => repo.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Tgt)));
    }
}
```

`ENGINE/Dbm.Tests/Unit/Catalog/CatalogTextTests.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Catalog;

public sealed class CatalogTextTests
{
    private static readonly CatalogSnapshot Src = TestCatalogs.LegacyShop();

    [Fact]
    public void TableHeader()
    {
        Assert.Equal("src table dbo.CUST rows=1000 size=0.2MB pk=CUST_ID triggers=0", CatalogText.TableHeader(Side.Src, Src.FindTable("dbo.CUST")!));
        Assert.Equal("src table dbo.AUDIT_LOG rows=5000 size=1MB pk=none heap triggers=0", CatalogText.TableHeader(Side.Src, Src.FindTable("dbo.AUDIT_LOG")!));
        Assert.Equal("src table dbo.ORD_HDR rows=3005 size=0.6MB pk=ORD_ID triggers=0 fk→dbo.CUST(untrusted) fk→dbo.ORD_STATUS",
            CatalogText.TableHeader(Side.Src, Src.FindTable("dbo.ORD_HDR")!));
    }

    [Fact]
    public void ColumnLine()
    {
        var cust = Src.FindTable("dbo.CUST")!;
        Assert.Equal("CUST_ID int NN id pk", CatalogText.ColumnLine(cust, cust.FindColumn("CUST_ID")!));
        Assert.Equal("EMAIL_ADDR varchar(120) null=10% distinct=0.90 class=email samples=[first1.last1@example.com]",
            CatalogText.ColumnLine(cust, cust.FindColumn("EMAIL_ADDR")!));
        Assert.Equal("CRT_DT datetime NN default=(getdate())", CatalogText.ColumnLine(cust, cust.FindColumn("CRT_DT")!));
        var addr = Src.FindTable("dbo.ADDR")!;
        Assert.Equal("CUST_ID int NN fk→dbo.CUST.CUST_ID", CatalogText.ColumnLine(addr, addr.FindColumn("CUST_ID")!));
    }

    [Fact]
    public void Table_caps_columns()
    {
        var text = CatalogText.Table(Side.Src, Src.FindTable("dbo.CUST")!, maxColumns: 2);
        Assert.Equal(4, text.Split('\n').Length);
        Assert.EndsWith("  … 6 more columns", text);
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.004, "<1%")]
    [InlineData(0.1, "10%")]
    [InlineData(0.985, "99%")]
    public void Percent(double ratio, string expected) => Assert.Equal(expected, CatalogText.Percent(ratio));

    [Fact]
    public void SearchLine() =>
        Assert.Equal("0.82 tgt column app.Customers.Email — email",
            CatalogText.SearchLine(new SearchHit(Side.Tgt, "column", "app.Customers.Email", 0.8219, "app.Customers.Email — email")));
}
```

- [ ] **Step 2: Write the failing integration test**

`ENGINE/Dbm.Tests/Integration/Catalog/DiscoverJobTests.cs`
```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Catalog;

[Trait("Category", "Integration")]
public sealed class DiscoverJobTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    public static async Task<List<string>> RunDiscoverAsync(TempProject project)
    {
        var log = new List<string>();
        var services = project.Services;
        var job = services.Jobs.Get(services.Jobs.Enqueue("discover", PhaseName.Discovery))!;
        var result = await new DiscoverJob().RunAsync(new JobContext { Services = services, Job = job, Log = log.Add }, CancellationToken.None);
        Assert.Null(result.DraftPayload);
        Assert.Equal("src: 8 tables, tgt: 6 tables", result.Summary);
        return log;
    }

    [Fact]
    public async Task Discover_saves_catalogs_fingerprints_and_vectors_then_search_and_show_work()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        var log = await RunDiscoverAsync(project);
        var catalog = project.Services.Catalog;

        Assert.Contains(log, l => l.StartsWith("src: profiled dbo.CUST", StringComparison.Ordinal));
        Assert.Equal(8, catalog.Get(Side.Src)!.Tables.Count);
        Assert.Equal(Fingerprint.Compute(catalog.Get(Side.Tgt)!), catalog.Fingerprint(Side.Tgt));
        Assert.Equal(8 + 6, catalog.Vectors(kind: "table").Count);

        var r = await CliRunner.RunAsync(project.Ws, null, "search", "customer", "email", "--side", "tgt", "-k", "3");
        Assert.Equal(0, r.Exit);
        Assert.Matches(@"^\d\.\d\d tgt column app\.Customers\.Email — email$", r.Out.Split('\n')[0].TrimEnd('\r'));

        r = await CliRunner.RunAsync(project.Ws, null, "search", "customer email", "--json");
        Assert.Equal(0, r.Exit);
        Assert.Contains(r.Json.AsArray(), h => (string?)h!["key"] == "dbo.CUST.EMAIL_ADDR");

        r = await CliRunner.RunAsync(project.Ws, null, "show", "dbo.CUST");
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("src table dbo.CUST rows=1000 ", r.Out);
        Assert.Contains("  EMAIL_ADDR varchar(120) null=10% distinct=0.90 maxlen=", r.Out);
        Assert.Contains("class=email samples=[", r.Out);

        r = await CliRunner.RunAsync(project.Ws, null, "show", "app.Customers.Email");
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("tgt column app.Customers.Email", r.Out);
        Assert.Contains("Email nvarchar(120) empty", r.Out);

        r = await CliRunner.RunAsync(project.Ws, null, "show", "CUST", "--json");
        Assert.Equal(0, r.Exit);
        Assert.Equal("src", (string?)r.Json["side"]);

        r = await CliRunner.RunAsync(project.Ws, null, "show", "dbo.NOPE");
        Assert.Equal(1, r.Exit);
        Assert.Equal("not_found", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Drift_checker_detects_structural_changes_only()
    {
        await using var pair = await SampleDatabases.CreateAsync(seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        await RunDiscoverAsync(project);
        Assert.False((await DriftChecker.CheckAsync(project.Services, CancellationToken.None)).Any);

        await using (var conn = new SqlConnection(pair.TargetCs))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("ALTER TABLE app.Orders ADD Channel varchar(10) NULL;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var drift = await DriftChecker.CheckAsync(project.Services, CancellationToken.None);
        Assert.False(drift.SrcChanged);
        Assert.True(drift.TgtChanged);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.State.CatalogRepoTests"`
Expected: build FAILS with `error CS1061: 'DbmServices' does not contain a definition for 'Catalog'` (plus `Fingerprint`/`CatalogText`/`DiscoverJob` not found).

- [ ] **Step 4: Implement Fingerprint**

`ENGINE/Dbm/Core/Catalog/Fingerprint.cs`
```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dbm.Core.Catalog;

/// <summary>
/// Lower-hex SHA-256 over the canonical structure: tables, columns (type, nullability, identity, computed, rowversion, default,
/// computed definition, collation), indexes, foreign keys (columns and target) and trigger names. Excludes rows, sizes, profiles,
/// descriptions, ExtractedAt, server metadata and FK trust/disabled state (the transfer toggles those with NOCHECK / WITH CHECK).
/// </summary>
public static class Fingerprint
{
    public static string Compute(CatalogSnapshot snapshot)
    {
        var sb = new StringBuilder();
        foreach (var t in snapshot.Tables.OrderBy(t => t.Key.ToLowerInvariant(), StringComparer.Ordinal))
        {
            Line(sb, "T", t.Schema, t.Name, t.TemporalType);
            foreach (var c in t.Columns.OrderBy(c => c.Ordinal))
                Line(sb, "C", c.Name, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsIdentity, c.IsComputed,
                    c.IsRowVersion, c.DefaultDefinition, c.ComputedDefinition, c.Collation);
            foreach (var i in t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
                Line(sb, "I", i.Name, i.IsPrimaryKey, i.IsUnique, i.IsClustered, string.Join(",", i.Columns));
            foreach (var f in t.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
                Line(sb, "F", f.Name, string.Join(",", f.Columns), f.RefKey, string.Join(",", f.RefColumns));
            Line(sb, "R", t.TriggerCount, string.Join(",", t.TriggerNames.OrderBy(n => n, StringComparer.Ordinal)));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static void Line(StringBuilder sb, string kind, params object?[] parts)
    {
        sb.Append(kind);
        foreach (var part in parts)
        {
            sb.Append('|').Append(part switch
            {
                null => "",
                bool b => b ? "1" : "0",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString(),
            });
        }
        sb.Append('\n');
    }
}
```

- [ ] **Step 5: Implement CatalogRepo and expose it on DbmServices**

Replace the whole of `ENGINE/Dbm/Core/State/CatalogRepo.cs` (created in T2.4) with:
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.State;

public sealed record VectorRow(Side Side, string Kind, string Key, string Text, SparseVector Vec);   // Kind "table"|"column"

/// <summary>One JSON catalog snapshot (profiles embedded) + fingerprint per side, and the search vectors.</summary>
public sealed class CatalogRepo(StateDb db)
{
    public void Save(Side side, CatalogSnapshot snapshot, string fingerprint) =>
        db.Execute("""
            INSERT INTO catalog (side, snapshot_json, fingerprint, extracted_at) VALUES ($side, $json, $fingerprint, $extractedAt)
            ON CONFLICT(side) DO UPDATE SET snapshot_json = excluded.snapshot_json, fingerprint = excluded.fingerprint,
              extracted_at = excluded.extracted_at
            """,
            new { side = EnumText.ToText(side), json = Json.Serialize(snapshot), fingerprint, extractedAt = snapshot.ExtractedAt.ToString("O") });

    public CatalogSnapshot? Get(Side side)
    {
        var json = db.Scalar<string>("SELECT snapshot_json FROM catalog WHERE side = $side", new { side = EnumText.ToText(side) });
        return json is null ? null : Json.Deserialize<CatalogSnapshot>(json);
    }

    public string? Fingerprint(Side side) =>
        db.Scalar<string>("SELECT fingerprint FROM catalog WHERE side = $side", new { side = EnumText.ToText(side) });

    /// <summary>Replaces all vector rows of <paramref name="side"/>; every row must belong to that side.</summary>
    public void SaveVectors(Side side, IEnumerable<VectorRow> rows)
    {
        var sideText = EnumText.ToText(side);
        db.InTransaction(() =>
        {
            db.Execute("DELETE FROM vector WHERE side = $side", new { side = sideText });
            foreach (var row in rows)
            {
                if (row.Side != side)
                    throw new ArgumentException($"Vector row '{row.Key}' belongs to side {EnumText.ToText(row.Side)}, not {sideText}.");
                db.Execute("INSERT INTO vector (side, kind, key, text, vec_json) VALUES ($side, $kind, $key, $text, $vec)",
                    new { side = sideText, kind = row.Kind, key = row.Key, text = row.Text, vec = Json.Serialize(row.Vec) });
            }
        });
    }

    public IReadOnlyList<VectorRow> Vectors(Side? side = null, string? kind = null) =>
        db.Query(
            "SELECT side, kind, key, text, vec_json FROM vector WHERE ($side IS NULL OR side = $side) AND ($kind IS NULL OR kind = $kind) ORDER BY side, kind, key",
            r => new VectorRow(EnumText.Parse<Side>(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                Json.Deserialize<SparseVector>(r.GetString(4))),
            new { side = side is null ? null : EnumText.ToText(side.Value), kind });
}
```

In `ENGINE/Dbm/Core/DbmServices.cs`, add these lines inside the `DbmServices` class directly below the `public ConnectionRepo Connections { get; }` property:
```csharp
    // T2.5: catalog snapshots + search vectors (a stateless wrapper over Db, created on first use)
    private CatalogRepo? _catalog;
    public CatalogRepo Catalog => _catalog ??= new CatalogRepo(Db);
```

- [ ] **Step 6: Implement CatalogText**

`ENGINE/Dbm/Core/Catalog/CatalogText.cs`
```csharp
using System.Globalization;
using Dbm.Core.Matching;
using Dbm.Core.State;

namespace Dbm.Core.Catalog;

/// <summary>Compact one-line renderings used by `dbm show`, `dbm search` and work packets.</summary>
public static class CatalogText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"src table dbo.CUST rows=1000 size=0.14MB pk=CUST_ID triggers=0" (+ " heap", " temporal=…", " fk→dbo.X").</summary>
    public static string TableHeader(Side side, TableInfo t)
    {
        var parts = new List<string>
        {
            $"{EnumText.ToText(side)} table {t.Key}",
            $"rows={t.Rows.ToString(Inv)}",
            $"size={t.SizeMb.ToString("0.##", Inv)}MB",
            t.PrimaryKey is { } pk ? $"pk={string.Join(",", pk.Columns)}" : t.BestKey() is { } key ? $"key={string.Join(",", key)}" : "pk=none",
        };
        if (t.IsHeap) parts.Add("heap");
        parts.Add($"triggers={t.TriggerCount.ToString(Inv)}");
        if (t.TemporalType is not null) parts.Add($"temporal={t.TemporalType}");
        foreach (var fk in t.ForeignKeys)
            parts.Add($"fk→{fk.RefKey}{(fk.IsNotTrusted ? "(untrusted)" : "")}{(fk.IsDisabled ? "(disabled)" : "")}");
        return string.Join(' ', parts);
    }

    /// <summary>"EMAIL_ADDR varchar(120) null=10% distinct=1.00 class=email samples=[a, b, c]".</summary>
    public static string ColumnLine(TableInfo t, ColumnInfo c)
    {
        var parts = new List<string> { c.Name, c.TypeDisplay };
        if (!c.IsNullable) parts.Add("NN");
        if (c.IsIdentity) parts.Add("id");
        if (t.PrimaryKey?.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase) == true) parts.Add("pk");
        foreach (var fk in t.ForeignKeys)
        {
            var i = fk.Columns.FindIndex(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            parts.Add(fk.Columns.Count == 1 ? $"fk→{fk.RefKey}.{fk.RefColumns[i]}" : $"fk→{fk.RefKey}({string.Join(",", fk.RefColumns)})");
        }
        if (c.IsComputed) parts.Add("computed");
        if (c.IsRowVersion) parts.Add("rowversion");
        if (c.DefaultDefinition is not null) parts.Add($"default={c.DefaultDefinition}");
        if (c.Profile is { } p)
        {
            if (p.SampledRows == 0) parts.Add("empty");
            else
            {
                parts.Add($"null={Percent(p.NullRatio)}");
                if (p.DistinctRatio is { } d) parts.Add($"distinct={d.ToString("0.00", Inv)}");
                if (p.MaxLen is { } ml) parts.Add($"maxlen={ml.ToString(Inv)}");
                if (p.SemanticClass is not null) parts.Add($"class={p.SemanticClass}");
                if (p.Samples.Count > 0) parts.Add($"samples=[{string.Join(", ", p.Samples)}]");
            }
        }
        return string.Join(' ', parts);
    }

    /// <summary>"profile sampled=1000 nulls=100 distinct=900 min=… max=… maxlen=28 avglen=25.9 patterns=[a9.a9@a.a]".</summary>
    public static string ProfileLine(ColumnProfile p)
    {
        var parts = new List<string> { "profile", $"sampled={p.SampledRows.ToString(Inv)}", $"nulls={p.Nulls.ToString(Inv)}" };
        if (p.Distinct is { } d) parts.Add($"distinct={d.ToString(Inv)}");
        if (p.Min is not null) parts.Add($"min={p.Min}");
        if (p.Max is not null) parts.Add($"max={p.Max}");
        if (p.MaxLen is { } ml) parts.Add($"maxlen={ml.ToString(Inv)}");
        if (p.AvgLen is { } al) parts.Add($"avglen={al.ToString("0.#", Inv)}");
        if (p.SemanticClass is not null) parts.Add($"class={p.SemanticClass}");
        if (p.TopPatterns.Count > 0) parts.Add($"patterns=[{string.Join(", ", p.TopPatterns)}]");
        return string.Join(' ', parts);
    }

    /// <summary>Header line + one indented line per column (at most <paramref name="maxColumns"/>, then "… n more columns").</summary>
    public static string Table(Side side, TableInfo t, int maxColumns = int.MaxValue)
    {
        var lines = new List<string> { TableHeader(side, t) };
        lines.AddRange(t.Columns.Take(maxColumns).Select(c => "  " + ColumnLine(t, c)));
        if (t.Columns.Count > maxColumns) lines.Add($"  … {t.Columns.Count - maxColumns} more columns");
        return string.Join('\n', lines);
    }

    /// <summary>"0.82 tgt column app.Customers.Email — email".</summary>
    public static string SearchLine(SearchHit hit) =>
        $"{hit.Score.ToString("0.00", Inv)} {EnumText.ToText(hit.Side)} {hit.Kind} {hit.Text}";

    public static string Percent(double ratio) =>
        ratio == 0 ? "0%" : ratio < 0.01 ? "<1%" : (ratio * 100).ToString("0", Inv) + "%";
}
```

- [ ] **Step 7: Run the unit tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration&(FullyQualifiedName~FingerprintTests|FullyQualifiedName~CatalogRepoTests|FullyQualifiedName~CatalogTextTests)"`
Expected: PASS (13 tests).

- [ ] **Step 8: Implement DriftChecker and DiscoverJob**

`ENGINE/Dbm/Core/Catalog/DriftChecker.cs`
```csharp
using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Core.Catalog;

public sealed record DriftResult(bool SrcChanged, bool TgtChanged)
{
    public bool Any => SrcChanged || TgtChanged;
}

/// <summary>Re-extracts structure (no profiling) and compares fingerprints with the stored catalog.</summary>
public static class DriftChecker
{
    public static async Task<DriftResult> CheckAsync(DbmServices services, CancellationToken ct)
    {
        var src = await ChangedAsync(services, Side.Src, ct);
        var tgt = await ChangedAsync(services, Side.Tgt, ct);
        return new DriftResult(src, tgt);
    }

    private static async Task<bool> ChangedAsync(DbmServices services, Side side, CancellationToken ct)
    {
        var saved = services.Catalog.Fingerprint(side);
        var connectionString = services.Connections.GetConnectionString(side);
        if (saved is null || connectionString is null) return false;   // nothing discovered yet: nothing to drift from
        var meta = services.Connections.GetMeta(side) ?? await SqlConnect.ProbeAsync(connectionString, ct);
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        var current = await CatalogExtractor.ExtractAsync(conn, meta, ct);
        return !string.Equals(Fingerprint.Compute(current), saved, StringComparison.Ordinal);
    }
}
```

`ENGINE/Dbm/Core/Catalog/DiscoverJob.cs`
```csharp
using Dbm.Core.Jobs;
using Dbm.Core.Matching;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Catalog;

/// <summary>Job "discover": extract + profile + fingerprint both catalogs, then build the vector index.</summary>
public sealed class DiscoverJob : IJobHandler
{
    public string Kind => "discover";

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var services = ctx.Services;
        var settings = services.Project.GetSettings();
        var options = new ProfileOptions(settings.ProfileSampleRows, settings.SampleValues);
        var snapshots = new Dictionary<Side, CatalogSnapshot>();

        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            var label = EnumText.ToText(side);
            var connectionString = services.Connections.GetConnectionString(side)
                ?? throw new InvalidOperationException($"No {label} connection saved; complete Setup first.");
            try
            {
                ctx.Log($"{label}: connecting to {Redactor.Describe(connectionString)}");
                var meta = await SqlConnect.ProbeAsync(connectionString, ct);
                await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
                var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, ct);
                ctx.Log($"{label}: {snapshot.Tables.Count} tables, {snapshot.Tables.Sum(t => t.Columns.Count)} columns extracted");
                snapshot = await Profiler.ProfileAsync(conn, snapshot, options, message => ctx.Log($"{label}: {message}"), ct);
                services.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
                snapshots[side] = snapshot;
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException($"{label}: {Redactor.Scrub(ex.Message, Redactor.SecretsOf(connectionString))}");
            }
        }

        var (rows, _) = VectorIndex.Build(snapshots[Side.Src], snapshots[Side.Tgt], Synonyms.ForProject(services.Ws));
        services.Catalog.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Src));
        services.Catalog.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));
        ctx.Log($"vector index: {rows.Count} entries");

        return new JobResult(null, $"src: {snapshots[Side.Src].Tables.Count} tables, tgt: {snapshots[Side.Tgt].Tables.Count} tables");
    }
}
```

Register it in `ENGINE/Dbm/Core/JobRegistry.cs`, directly below the marker line `        // milestone registrations below`:
```csharp
        yield return new Dbm.Core.Catalog.DiscoverJob();   // T2.5
```

- [ ] **Step 9: Add the drift approval guard**

In `ENGINE/Dbm/Web/ApprovalGuards.cs`, add `using Dbm.Core.Catalog;`, `using Dbm.Core.Sql;` and `using Dbm.Core.State;` to the usings (keep the existing ones), then replace the line
```csharp
    public static readonly List<Func<WebState, PhaseName, CancellationToken, Task<string?>>> All = new();
```
with
```csharp
    public static readonly List<Func<WebState, PhaseName, CancellationToken, Task<string?>>> All = new()
    {
        DriftGuardAsync,   // T2.5
    };

    // T2.5: Analysis/Mapping/Sql approval is refused while either database's structure differs from the discovered catalog.
    // A failing check (database unreachable) does not block approval; it is logged as a warning instead.
    public static async Task<string?> DriftGuardAsync(WebState state, PhaseName phase, CancellationToken ct)
    {
        if (phase is not (PhaseName.Analysis or PhaseName.Mapping or PhaseName.Sql)) return null;
        var services = state.Services;
        DriftResult drift;
        try
        {
            drift = await DriftChecker.CheckAsync(services, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var secrets = new[] { Side.Src, Side.Tgt }
                .Select(side => services.Connections.GetConnectionString(side))
                .Where(cs => cs is not null)
                .SelectMany(cs => Redactor.SecretsOf(cs!));
            services.Sink.Publish("log", new { level = "warn", message = $"Drift check skipped: {Redactor.Scrub(ex.Message, secrets)}" });
            return null;
        }
        if (!drift.Any) return null;
        var sides = new List<string>();
        if (drift.SrcChanged) sides.Add("src");
        if (drift.TgtChanged) sides.Add("tgt");
        foreach (var side in sides) services.Sink.Publish("drift_detected", new { side });
        return $"Schema changed since discovery on {string.Join(" and ", sides)}; re-run discovery";
    }
```

- [ ] **Step 10: Implement the catalog CLI commands**

`ENGINE/Dbm/Cli/Commands/CatalogCommands.cs`
```csharp
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm discover [--inline]`: re-run discovery (the server runs the job; --inline runs pending jobs in this process).</summary>
public sealed class DiscoverCommand : ICommand
{
    public string Name => "discover";
    public string Help => "Extract + profile both catalogs and rebuild the vector index (--inline: run the jobs here instead of in the server)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);
        if (!services.Connections.Has(Side.Src) || !services.Connections.Has(Side.Tgt))
            return Output.Fail(ctx, "no_connections", "Save both connection strings in the UI (Setup) first.");

        if (!services.Jobs.Active().Any(j => j.Kind == "discover"))
        {
            try
            {
                services.Workflow.Rediscover();
            }
            catch (WorkflowException ex)
            {
                return Output.Fail(ctx, "workflow", ex.Message);
            }
        }

        if (args.Flag("inline"))
        {
            var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
            var job = services.Jobs.LatestFor(PhaseName.Discovery);
            if (job is { Status: JobStatus.Failed }) return Output.Fail(ctx, "job_failed", job.Error ?? "discover failed");
            return Output.Ok(ctx, new
            {
                ok = true,
                ran,
                src = services.Catalog.Get(Side.Src)?.Tables.Count ?? 0,
                tgt = services.Catalog.Get(Side.Tgt)?.Tables.Count ?? 0,
            });
        }

        var info = await ServerControl.EnsureRunningAsync(ws);
        return Output.Ok(ctx, new { ok = true, queued = true, url = info.UiUrl });
    }
}

/// <summary>`dbm search &lt;query&gt; [--side src|tgt] [--kind table|column] [-k n] [--json]`.</summary>
public sealed class SearchCommand : ICommand
{
    public string Name => "search";
    public string Help => "Vector search over both catalogs: search <query> [--side src|tgt] [--kind table|column] [-k n] [--json]";

    public Task<int> RunAsync(Args args, CliContext ctx) => Task.FromResult(Run(args, ctx));

    private static int Run(Args args, CliContext ctx)
    {
        var query = string.Join(' ', args.Positionals).Trim();
        if (query.Length == 0) return Output.Fail(ctx, "usage", "usage: dbm search <query> [--side src|tgt] [--kind table|column] [-k n] [--json]");
        if (!CatalogCli.TryParseSide(args.Opt("side"), out var side)) return Output.Fail(ctx, "usage", "--side must be src or tgt");
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);
        var rows = services.Catalog.Vectors();
        if (rows.Count == 0) return Output.Fail(ctx, "no_catalog", CatalogCli.NoCatalog);

        var hits = VectorIndex.FromRows(rows, Synonyms.ForProject(ws))
            .Search(query, side, args.Opt("kind"), Math.Clamp(args.Int("k", 8), 1, 50));
        if (args.Flag("json"))
            return Output.Ok(ctx, hits.Select(h => new
            {
                side = EnumText.ToText(h.Side), kind = h.Kind, key = h.Key, score = Math.Round(h.Score, 3), text = h.Text,
            }).ToList());
        return Output.Text(ctx, hits.Count == 0 ? "no matches" : string.Join('\n', hits.Select(CatalogText.SearchLine)));
    }
}

/// <summary>`dbm show &lt;schema.table[.column] | table | F001&gt; [--side src|tgt] [--json]`.</summary>
public sealed class ShowCommand : ICommand
{
    private static readonly Regex FindingId = new(@"^F\d{3,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Name => "show";
    public string Help => "Compact view of a table, column or finding: show <schema.table[.column]|F001> [--side src|tgt] [--json]";

    public Task<int> RunAsync(Args args, CliContext ctx) => Task.FromResult(Run(args, ctx));

    private static int Run(Args args, CliContext ctx)
    {
        if (args.Positionals.Count == 0) return Output.Fail(ctx, "usage", "usage: dbm show <schema.table[.column]|F001> [--side src|tgt] [--json]");
        var target = args.Positionals[0];
        var json = args.Flag("json");
        if (!CatalogCli.TryParseSide(args.Opt("side"), out var only)) return Output.Fail(ctx, "usage", "--side must be src or tgt");
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);

        if (FindingId.IsMatch(target)) return ShowFinding(services, target.ToUpperInvariant(), json, ctx);

        var anyCatalog = false;
        foreach (var side in only is { } s ? new[] { s } : new[] { Side.Src, Side.Tgt })
        {
            var snapshot = services.Catalog.Get(side);
            if (snapshot is null) continue;
            anyCatalog = true;
            var (table, column) = Resolve(snapshot, target);
            if (table is null) continue;
            var sideText = EnumText.ToText(side);
            if (column is null)
                return json ? Output.Ok(ctx, new { side = sideText, table }) : Output.Text(ctx, CatalogText.Table(side, table));
            if (json) return Output.Ok(ctx, new { side = sideText, table = table.Key, column });
            var lines = new List<string> { $"{sideText} column {table.Key}.{column.Name}", CatalogText.ColumnLine(table, column) };
            if (column.Profile is { } profile) lines.Add(CatalogText.ProfileLine(profile));
            return Output.Text(ctx, string.Join('\n', lines));
        }
        return anyCatalog
            ? Output.Fail(ctx, "not_found", $"No table or column '{target}' in the catalog (try `dbm search {target}`).")
            : Output.Fail(ctx, "no_catalog", CatalogCli.NoCatalog);
    }

    /// <summary>"schema.table" → table; "schema.table.column" → column; a bare name → the only table with that name.</summary>
    public static (TableInfo? Table, ColumnInfo? Column) Resolve(CatalogSnapshot snapshot, string target)
    {
        if (snapshot.FindTable(target) is { } table) return (table, null);
        var dot = target.LastIndexOf('.');
        if (dot > 0 && snapshot.FindTable(target[..dot]) is { } owner && owner.FindColumn(target[(dot + 1)..]) is { } column)
            return (owner, column);
        if (dot < 0)
        {
            var byName = snapshot.Tables.Where(t => string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return (byName[0], null);
        }
        return (null, null);
    }

    private static int ShowFinding(DbmServices services, string id, bool json, CliContext ctx)
    {
        var version = services.Phases.Get(PhaseName.Analysis).CurrentVersion;
        var artifact = (version is { } v ? services.Artifacts.Get(PhaseName.Analysis, v) : null) ?? services.Artifacts.Latest(PhaseName.Analysis);
        if (artifact is null) return Output.Fail(ctx, "no_analysis", "No analysis artifact yet.");
        var finding = JsonNode.Parse(artifact.PayloadJson)?["findings"]?[id];
        if (finding is null) return Output.Fail(ctx, "not_found", $"No finding {id} in analysis v{artifact.Version}.");
        if (json) return Output.Ok(ctx, new { id, version = artifact.Version, finding });
        var line = $"{id} {finding["severity"]} {finding["rule"]} {finding["side"]} {finding["object"]} — {finding["message"]}";
        if (finding["count"] is { } count) line += $" (n={count})";
        if (finding["commentary"] is { } commentary) line += $"\ncommentary: {commentary}";
        return Output.Text(ctx, line);
    }
}

internal static class CatalogCli
{
    public const string NoCatalog = "No catalog yet; run discovery first (`dbm discover`).";

    public static bool TryParseSide(string? text, out Side? side)
    {
        side = null;
        if (string.IsNullOrEmpty(text)) return true;
        if (string.Equals(text, "src", StringComparison.OrdinalIgnoreCase)) side = Side.Src;
        else if (string.Equals(text, "tgt", StringComparison.OrdinalIgnoreCase)) side = Side.Tgt;
        else return false;
        return true;
    }
}
```

Register the commands in `ENGINE/Dbm/Cli/CommandRegistry.cs`, directly below the comment `        // Later milestones append their commands below this line.`:
```csharp
        new DiscoverCommand(),   // T2.5
        new SearchCommand(),     // T2.5
        new ShowCommand(),       // T2.5
```

- [ ] **Step 11: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`
Expected: PASS (all unit tests, including the 13 new ones).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~DiscoverJobTests"`
Expected: PASS (2 tests).

- [ ] **Step 12: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Catalog plugins/db-migrate/engine/Dbm/Core/State/CatalogRepo.cs \
  plugins/db-migrate/engine/Dbm/Core/DbmServices.cs plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs \
  plugins/db-migrate/engine/Dbm/Cli/Commands/CatalogCommands.cs plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs \
  plugins/db-migrate/engine/Dbm/Web/ApprovalGuards.cs plugins/db-migrate/engine/Dbm.Tests/Unit \
  plugins/db-migrate/engine/Dbm.Tests/Integration/Catalog/DiscoverJobTests.cs
git commit -F - <<'EOF'
feat(discovery): catalog repo, fingerprints, drift guard, discover job, search/show CLI

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.6: Rules, analyzer, analyze job

**Files:**
- Create: `ENGINE/Dbm/Core/Analysis/Finding.cs`, `AnalysisPayload.cs`, `Rules.cs`, `Analyzer.cs`, `AnalyzeJob.cs`
- Modify: `ENGINE/Dbm/Core/JobRegistry.cs` (`AnalyzeJob`)
- Test: `ENGINE/Dbm.Tests/Unit/Analysis/RulesTests.cs`, `ENGINE/Dbm.Tests/Integration/Analysis/AnalyzeJobTests.cs`

**Interfaces:**
- Consumes: C10 model, `CatalogRepo`, `ConnectionRepo`, `SqlConnect`, `Redactor`, `Json`, `IJobHandler`/`JobContext`/`JobResult`, `ProfilerSql.Quote` (T2.3), `TestCatalogs`, `DiscoverJobTests.RunDiscoverAsync` (T2.5).
- Produces (the analysis artifact; JSON camelCase, enums snake_case):
  ```csharp
  namespace Dbm.Core.Analysis;
  public enum Severity { Critical, High, Medium, Low, Info }
  public sealed record Finding { required string Rule; required string Title; required Severity Severity; required string Side /* src|tgt|both */;
      required string Object; required string Message; string? Anchor; long? Count; string? Commentary; }
  public sealed class AnalysisPayload { DbSummary Source; DbSummary Target; Dictionary<string, Finding> Findings /* F001… */; Estimates Estimates; Narrative? Narrative; }
  public sealed record DbSummary(string Server, string Database, string Version, string Edition, string Collation, int CompatLevel,
      int Tables, int Columns, long Rows, double SizeMb, int Views, int Procedures, int Functions, int Triggers)
      { int MajorVersion; string? ProductVersion; static DbSummary From(CatalogSnapshot s); }
  public sealed record Estimates(long TotalRows, double TotalSizeMb, double EstimatedMinutes, string Basis);
  public sealed class Narrative { string Summary; List<string> Risks; List<string> Recommendations; }
  public sealed class RuleContext { required CatalogSnapshot Src; required CatalogSnapshot Tgt; IReadOnlyDictionary<string,long> OrphanCounts; static string OrphanKey(string tableKey, string fkName); }
  public interface IRule { string Id { get; } string Title { get; } IEnumerable<Finding> Evaluate(RuleContext ctx); }
  public static class Rules { IReadOnlyList<IRule> All; }                       // R01..R16 in order
  public static class FkGraph { List<List<string>> Cycles(IReadOnlyList<TableInfo> tables); }   // Tarjan SCCs with ≥ 2 tables
  public static class Analyzer { AnalysisPayload Analyze(CatalogSnapshot src, CatalogSnapshot tgt, IReadOnlyDictionary<string,long>? orphanCounts = null);
      Estimates Estimate(CatalogSnapshot src); string Summary(AnalysisPayload p); /* "N findings (x critical, y high)" */ }
  public sealed class AnalyzeJob : IJobHandler { Kind "analyze"; static Task<Dictionary<string,long>> CountOrphansAsync(...); static string OrphanSql(TableInfo child, ForeignKeyInfo fk); }
  ```
- Rules (severity; side):

| Id | Title | Fires on |
|---|---|---|
| R01 | No primary or unique key | `BestKey() == null`: source with rows → **high** (no mid-table resume); empty source → low; target → low |
| R02 | Deprecated data type | `text`/`ntext`/`image` columns, both sides → medium |
| R03 | Large object column | `(max)` and `xml` columns, both sides → low |
| R04 | Collation difference | database collations differ → medium (`both`, object `database`); column collation ≠ its database default → info |
| R05 | Identity column in target | target tables with an identity column → info |
| R06 | Triggers on target table | `TriggerCount > 0` → medium (names listed; not fired by bulk load unless FireTriggers) |
| R07 | Computed column in target | info per column |
| R08 | Rowversion column in target | info per column |
| R09 | Untrusted or disabled foreign key | source FKs: orphans > 0 → **high** (`Count` = orphans); 0 → low; not counted → medium |
| R10 | Mostly NULL column | profiled columns with `NullRatio > 0.95`, both sides → low |
| R11 | Empty source table | source `Rows == 0` → low |
| R12 | Large table | source `Rows > 10 000 000` → info |
| R13 | Temporal table | `TemporalType != null`, both sides → medium |
| R14 | Foreign-key cycle in target | each SCC (object `A ↔ B`) and each self-reference → medium |
| R15 | Target table already has rows | target `Rows > 0` → **high** |
| R16 | Target older than source | target `MajorVersion` or `CompatLevel` lower → medium (`both`) |

  Finding ids: `F001`… in rule order, then side (src, tgt, both), then object (case-insensitive). Anchors: `table:<side>:<key>`, `column:<side>:<key>.<col>`, null for `database`. Estimates: `EstimatedMinutes = ceil(sourceRows / 50 000 / 60 × 10) / 10`.

- [ ] **Step 1: Write the failing unit test**

`ENGINE/Dbm.Tests/Unit/Analysis/RulesTests.cs`
```csharp
using Dbm.Core.Analysis;
using Dbm.Core.Catalog;
using Dbm.Tests.Support;
using static Dbm.Tests.Support.TestCatalogs;

namespace Dbm.Tests.Unit.Analysis;

public sealed class RulesTests
{
    private static readonly Dictionary<string, long> TwoOrphans = new() { [RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")] = 2 };

    private static List<Finding> Run(IRule rule, CatalogSnapshot? src = null, CatalogSnapshot? tgt = null, Dictionary<string, long>? orphans = null) =>
        rule.Evaluate(new RuleContext { Src = src ?? LegacyShop(), Tgt = tgt ?? ShopV2(), OrphanCounts = orphans ?? TwoOrphans }).ToList();

    private static CatalogSnapshot Change(CatalogSnapshot s, string key, Func<TableInfo, TableInfo> change) =>
        s with { Tables = s.Tables.Select(t => t.Key == key ? change(t) : t).ToList() };

    [Fact]
    public void Rule_ids_are_R01_to_R16_in_order() =>
        Assert.Equal(Enumerable.Range(1, 16).Select(i => $"R{i:00}"), Rules.All.Select(r => r.Id));

    [Fact]
    public void R01_no_primary_key()
    {
        var f = Run(new NoPrimaryKeyRule());
        Assert.Contains(f, x => x is { Object: "dbo.AUDIT_LOG", Severity: Severity.High, Side: "src", Anchor: "table:src:dbo.AUDIT_LOG" });
        Assert.Contains(f, x => x is { Object: "dbo.TMP_IMPORT", Severity: Severity.Low });
        Assert.Contains(f, x => x is { Object: "app.AuditEvents", Severity: Severity.Low, Side: "tgt" });
        Assert.Equal(3, f.Count);
    }

    [Fact]
    public void R02_deprecated_type() =>
        Assert.Equal(("dbo.CUST.NOTES", Severity.Medium, "column:src:dbo.CUST.NOTES"),
            Run(new DeprecatedTypeRule()).Select(x => (x.Object, x.Severity, x.Anchor)).Single());

    [Fact]
    public void R03_lob_columns() =>
        Assert.Equal("app.Customers.Notes", Run(new LobColumnsRule()).Single(x => x.Severity == Severity.Low).Object);

    [Fact]
    public void R04_collation_mismatch()
    {
        Assert.Empty(Run(new CollationMismatchRule()));
        var tgt = ShopV2() with { Server = Meta("ShopV2", collation: "Latin1_General_100_CI_AS_SC_UTF8") };
        var f = Run(new CollationMismatchRule(), tgt: tgt);
        Assert.Contains(f, x => x is { Object: "database", Severity: Severity.Medium, Side: "both" });
        Assert.Contains(f, x => x is { Object: "app.Customers.Email", Severity: Severity.Info });
    }

    [Fact]
    public void R05_to_R08_target_generated_values()
    {
        Assert.Equal(new[] { "app.Addresses", "app.Customers", "app.Orders", "app.Products" },
            Run(new TargetIdentityRule()).Select(x => x.Object).OrderBy(x => x, StringComparer.Ordinal));
        var trig = Run(new TargetTriggersRule()).Single();
        Assert.Equal(("app.Orders", Severity.Medium, 1L), (trig.Object, trig.Severity, trig.Count!.Value));
        Assert.Contains("trg_Orders_Audit", trig.Message);
        Assert.Equal("app.Customers.DisplayName", Run(new TargetComputedRule()).Single().Object);
        Assert.Equal("app.Products.RowVer", Run(new TargetRowVersionRule()).Single().Object);
    }

    [Fact]
    public void R09_untrusted_foreign_keys_depend_on_orphan_counts()
    {
        var high = Run(new UntrustedForeignKeysRule()).Single();
        Assert.Equal(("dbo.ORD_HDR", Severity.High, 2L), (high.Object, high.Severity, high.Count!.Value));
        Assert.Contains("FK_ORD_CUST", high.Message);
        Assert.Equal(Severity.Medium, Run(new UntrustedForeignKeysRule(), orphans: new()).Single().Severity);
        var none = new Dictionary<string, long> { [RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")] = 0 };
        Assert.Equal(Severity.Low, Run(new UntrustedForeignKeysRule(), orphans: none).Single().Severity);
    }

    [Fact]
    public void R10_to_R13_data_shape()
    {
        Assert.Equal("dbo.CUST.FAX_NO", Run(new NullHeavyColumnsRule()).Single().Object);
        Assert.Equal("dbo.TMP_IMPORT", Run(new EmptySourceTablesRule()).Single().Object);
        Assert.Empty(Run(new LargeTablesRule()));
        var big = Change(LegacyShop(), "dbo.AUDIT_LOG", t => t with { Rows = 12_000_000 });
        Assert.Equal(("dbo.AUDIT_LOG", Severity.Info), Run(new LargeTablesRule(), src: big).Select(x => (x.Object, x.Severity)).Single());
        Assert.Empty(Run(new TemporalTablesRule()));
        var temporal = Change(ShopV2(), "app.Orders", t => t with { TemporalType = "system_versioned" });
        Assert.Equal(("app.Orders", Severity.Medium, "tgt"), Run(new TemporalTablesRule(), tgt: temporal).Select(x => (x.Object, x.Severity, x.Side)).Single());
    }

    [Fact]
    public void R14_target_fk_cycles()
    {
        var f = Run(new TargetFkCyclesRule()).Single();
        Assert.Equal(("app.Addresses ↔ app.Customers", Severity.Medium, "table:tgt:app.Addresses"), (f.Object, f.Severity, f.Anchor));
        var selfRef = Change(ShopV2(), "app.Products", t => t with
        {
            ForeignKeys = new List<ForeignKeyInfo> { Fk("FK_Products_Parent", "ProductId", "app.Products", "ProductId") },
        });
        Assert.Contains(Run(new TargetFkCyclesRule(), tgt: selfRef), x => x.Object == "app.Products" && x.Message.Contains("FK_Products_Parent"));
    }

    [Fact]
    public void R15_and_R16_target_state_and_version()
    {
        Assert.Empty(Run(new TargetNotEmptyRule()));
        var filled = Change(ShopV2(), "app.Products", t => t with { Rows = 10 });
        Assert.Equal(("app.Products", Severity.High), Run(new TargetNotEmptyRule(), tgt: filled).Select(x => (x.Object, x.Severity)).Single());
        Assert.Empty(Run(new VersionDowngradeRule()));
        var older = ShopV2() with { Server = Meta("ShopV2", majorVersion: 15, compatLevel: 150) };
        Assert.Equal(Severity.Medium, Run(new VersionDowngradeRule(), tgt: older).Single().Severity);
    }

    [Fact]
    public void Analyzer_numbers_findings_in_rule_then_object_order()
    {
        var payload = Analyzer.Analyze(LegacyShop(), ShopV2(), TwoOrphans);
        var list = payload.Findings.ToList();
        Assert.Equal("F001", list[0].Key);
        Assert.Equal(("R01", "dbo.AUDIT_LOG"), (list[0].Value.Rule, list[0].Value.Object));
        Assert.Equal(("R01", "dbo.TMP_IMPORT"), (list[1].Value.Rule, list[1].Value.Object));
        Assert.Equal(("R01", "app.AuditEvents"), (list[2].Value.Rule, list[2].Value.Object));
        Assert.Equal("R02", list[3].Value.Rule);
        Assert.Equal(payload.Findings.Values.Select(f => f.Rule).OrderBy(r => r, StringComparer.Ordinal), payload.Findings.Values.Select(f => f.Rule));
        Assert.Matches(@"^\d+ findings \(0 critical, 2 high\)$", Analyzer.Summary(payload));
    }

    [Fact]
    public void Analyzer_summaries_and_estimates()
    {
        var payload = Analyzer.Analyze(LegacyShop(), ShopV2(), TwoOrphans);
        Assert.Equal(("LegacyShop", 8, 38), (payload.Source.Database, payload.Source.Tables, payload.Source.Columns));
        Assert.Equal(19_711, payload.Source.Rows);
        Assert.Equal((6, 1, 17), (payload.Target.Tables, payload.Target.Triggers, payload.Target.MajorVersion));
        Assert.Equal(19_711, payload.Estimates.TotalRows);
        Assert.Equal(0.1, payload.Estimates.EstimatedMinutes);
        Assert.Null(payload.Narrative);
        var big = Analyzer.Estimate(Change(LegacyShop(), "dbo.AUDIT_LOG", t => t with { Rows = 30_000_000 }));
        Assert.Equal(10.1, big.EstimatedMinutes);   // 30 014 711 rows / 50 000 / 60 = 10.005 → 10.1
    }

    [Fact]
    public void OrphanSql_checks_non_null_children_without_parent() =>
        Assert.Equal(
            "SELECT COUNT_BIG(*) FROM [dbo].[ORD_HDR] AS c WHERE c.[CUST_ID] IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [dbo].[CUST] AS p WHERE p.[CUST_ID] = c.[CUST_ID]);",
            AnalyzeJob.OrphanSql(LegacyShop().FindTable("dbo.ORD_HDR")!, LegacyShop().FindTable("dbo.ORD_HDR")!.ForeignKeys[0]));
}
```

- [ ] **Step 2: Write the failing integration test**

`ENGINE/Dbm.Tests/Integration/Analysis/AnalyzeJobTests.cs`
```csharp
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Tests.Integration.Catalog;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Analysis;

[Trait("Category", "Integration")]
public sealed class AnalyzeJobTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Analyze_job_reports_the_sample_anomalies()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        await DiscoverJobTests.RunDiscoverAsync(project);
        var services = project.Services;
        var log = new List<string>();
        var job = services.Jobs.Get(services.Jobs.Enqueue("analyze", PhaseName.Analysis))!;

        var result = await new AnalyzeJob().RunAsync(new JobContext { Services = services, Job = job, Log = log.Add }, CancellationToken.None);

        Assert.Contains("orphan check dbo.ORD_HDR FK_ORD_CUST: 2", log);
        var payload = Json.FromNode<AnalysisPayload>(result.DraftPayload!);
        var findings = payload.Findings.Values.ToList();
        Assert.Contains(findings, f => f is { Rule: "R01", Object: "dbo.AUDIT_LOG", Severity: Severity.High });
        Assert.Contains(findings, f => f is { Rule: "R02", Object: "dbo.CUST.NOTES" });
        Assert.Contains(findings, f => f.Rule == "R06" && f.Object == "app.Orders" && f.Message.Contains("trg_Orders_Audit"));
        Assert.Contains(findings, f => f is { Rule: "R09", Object: "dbo.ORD_HDR", Severity: Severity.High, Count: 2 });
        Assert.Contains(findings, f => f is { Rule: "R10", Object: "dbo.CUST.FAX_NO" });
        Assert.Contains(findings, f => f is { Rule: "R11", Object: "dbo.TMP_IMPORT" });
        Assert.Contains(findings, f => f is { Rule: "R14", Object: "app.Addresses ↔ app.Customers" });
        Assert.DoesNotContain(findings, f => f.Rule == "R15");
        Assert.Equal(Analyzer.Summary(payload), result.Summary);
        Assert.Equal(19_711, payload.Estimates.TotalRows);
        Assert.Null(result.DraftPayload!["narrative"]);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Analysis"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Analysis' does not exist in the namespace 'Dbm.Core'`.

- [ ] **Step 4: Implement the finding and payload types**

`ENGINE/Dbm/Core/Analysis/Finding.cs`
```csharp
namespace Dbm.Core.Analysis;

public enum Severity { Critical, High, Medium, Low, Info }   // JSON: "critical" | "high" | "medium" | "low" | "info"

/// <summary>One rule hit. Stored in AnalysisPayload.Findings under its id ("F001"…); only Commentary may be patched by the agent.</summary>
public sealed record Finding
{
    public required string Rule { get; init; }        // "R01".."R16"
    public required string Title { get; init; }       // rule title, e.g. "No primary or unique key"
    public required Severity Severity { get; init; }
    public required string Side { get; init; }        // "src" | "tgt" | "both"
    public required string Object { get; init; }      // "dbo.AUDIT_LOG", "dbo.CUST.NOTES", "app.Addresses ↔ app.Customers" or "database"
    public required string Message { get; init; }     // one or two sentences, evidence-based
    public string? Anchor { get; init; }              // "table:src:dbo.AUDIT_LOG" | "column:src:dbo.CUST.NOTES" | null
    public long? Count { get; init; }                 // e.g. orphan rows
    public string? Commentary { get; init; }          // written by the schema-analyst (critical/high only)
}
```

`ENGINE/Dbm/Core/Analysis/AnalysisPayload.cs`
```csharp
using Dbm.Core.Catalog;

namespace Dbm.Core.Analysis;

/// <summary>The Analysis artifact (phase "analysis"). v0 is written by the analyze job; the agent adds /narrative and commentary.</summary>
public sealed class AnalysisPayload
{
    public DbSummary Source { get; set; } = null!;
    public DbSummary Target { get; set; } = null!;
    public Dictionary<string, Finding> Findings { get; set; } = new();   // "F001", "F002", … in rule order, then object order
    public Estimates Estimates { get; set; } = null!;
    public Narrative? Narrative { get; set; }
}

public sealed record DbSummary(string Server, string Database, string Version, string Edition, string Collation, int CompatLevel,
    int Tables, int Columns, long Rows, double SizeMb, int Views, int Procedures, int Functions, int Triggers)
{
    public int MajorVersion { get; init; }
    public string? ProductVersion { get; init; }

    public static DbSummary From(CatalogSnapshot s) => new(
        s.Server.Server,
        s.Server.Database,
        FirstLine(s.Server.Version),
        s.Server.Edition,
        s.Server.DatabaseCollation,
        s.Server.CompatLevel,
        s.Tables.Count,
        s.Tables.Sum(t => t.Columns.Count),
        s.Tables.Sum(t => t.Rows),
        Math.Round(s.Tables.Sum(t => t.SizeMb), 2),
        s.Objects.Views,
        s.Objects.Procedures,
        s.Objects.Functions,
        s.Objects.Triggers)
    {
        MajorVersion = s.Server.MajorVersion,
        ProductVersion = s.Server.ProductVersion,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 80 ? line : line[..80];
    }
}

/// <summary>EstimatedMinutes = source rows / 50 000 rows per second, in minutes, rounded up to 0.1.</summary>
public sealed record Estimates(long TotalRows, double TotalSizeMb, double EstimatedMinutes, string Basis);

public sealed class Narrative
{
    public string Summary { get; set; } = "";                     // <= 150 words
    public List<string> Risks { get; set; } = new();              // <= 12 bullets
    public List<string> Recommendations { get; set; } = new();    // 1..8 imperative bullets
}
```

- [ ] **Step 5: Implement the rules**

`ENGINE/Dbm/Core/Analysis/Rules.cs`
```csharp
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
    /// <summary>Strongly connected components with two or more tables; members sorted, components sorted by first member.</summary>
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

        foreach (var key in keys)
            if (!indices.ContainsKey(key)) Visit(key);

        return result.OrderBy(c => c[0], StringComparer.OrdinalIgnoreCase).ToList();

        void Visit(string v)
        {
            indices[v] = low[v] = index++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in edges[v])
            {
                if (!indices.ContainsKey(w))
                {
                    Visit(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], indices[w]);
                }
            }
            if (low[v] != indices[v]) return;
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
    }
}
```

- [ ] **Step 6: Implement the analyzer and the analyze job**

`ENGINE/Dbm/Core/Analysis/Analyzer.cs`
```csharp
using System.Globalization;
using Dbm.Core.Catalog;

namespace Dbm.Core.Analysis;

/// <summary>Runs every rule and assembles the script draft (v0) of the analysis artifact.</summary>
public static class Analyzer
{
    public const double RowsPerSecond = 50_000;

    public static AnalysisPayload Analyze(CatalogSnapshot src, CatalogSnapshot tgt, IReadOnlyDictionary<string, long>? orphanCounts = null)
    {
        var ctx = new RuleContext { Src = src, Tgt = tgt, OrphanCounts = orphanCounts ?? new Dictionary<string, long>() };
        var findings = new Dictionary<string, Finding>(StringComparer.Ordinal);
        var n = 0;
        foreach (var rule in Rules.All)
        {
            var ordered = rule.Evaluate(ctx)
                .OrderBy(f => SideRank(f.Side))
                .ThenBy(f => f.Object, StringComparer.OrdinalIgnoreCase);
            foreach (var finding in ordered)
                findings[$"F{(++n).ToString("000", CultureInfo.InvariantCulture)}"] = finding;
        }
        return new AnalysisPayload
        {
            Source = DbSummary.From(src),
            Target = DbSummary.From(tgt),
            Findings = findings,
            Estimates = Estimate(src),
        };
    }

    public static Estimates Estimate(CatalogSnapshot src)
    {
        var rows = src.Tables.Sum(t => t.Rows);
        var sizeMb = Math.Round(src.Tables.Sum(t => t.SizeMb), 2);
        var minutes = Math.Ceiling(rows / RowsPerSecond / 60.0 * 10.0) / 10.0;
        return new Estimates(rows, sizeMb, minutes,
            "source rows ÷ 50,000 rows/s (nominal bulk-copy rate); network, row width and target indexes change the real figure");
    }

    /// <summary>"N findings (x critical, y high)".</summary>
    public static string Summary(AnalysisPayload payload)
    {
        var critical = payload.Findings.Values.Count(f => f.Severity == Severity.Critical);
        var high = payload.Findings.Values.Count(f => f.Severity == Severity.High);
        return $"{payload.Findings.Count} findings ({critical} critical, {high} high)";
    }

    private static int SideRank(string side) => side switch { "src" => 0, "tgt" => 1, _ => 2 };
}
```

`ENGINE/Dbm/Core/Analysis/AnalyzeJob.cs`
```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Jobs;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Analysis;

/// <summary>Job "analyze": counts orphans behind untrusted/disabled source FKs, runs the rules, returns the v0 draft.</summary>
public sealed class AnalyzeJob : IJobHandler
{
    public const int OrphanQueryTimeoutSeconds = 60;

    public string Kind => "analyze";

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var services = ctx.Services;
        var src = services.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("Source catalog missing; run discovery first.");
        var tgt = services.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("Target catalog missing; run discovery first.");
        var orphans = await CountOrphansAsync(services.Connections.GetConnectionString(Side.Src), src, ctx.Log, ct);
        var payload = Analyzer.Analyze(src, tgt, orphans);
        var summary = Analyzer.Summary(payload);
        ctx.Log($"analysis: {summary}");
        return new JobResult(Json.ToNode(payload), summary);
    }

    /// <summary>Counts child rows without a parent for every untrusted/disabled FK; a failing query is logged and skipped.</summary>
    public static async Task<Dictionary<string, long>> CountOrphansAsync(string? sourceConnectionString, CatalogSnapshot src,
        Action<string> log, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var candidates = src.Tables
            .SelectMany(t => t.ForeignKeys.Where(f => f.IsNotTrusted || f.IsDisabled).Select(f => (Table: t, Fk: f)))
            .ToList();
        if (candidates.Count == 0 || sourceConnectionString is null) return counts;

        SqlConnection conn;
        try
        {
            conn = await SqlConnect.OpenAsync(sourceConnectionString, ct);
        }
        catch (SqlException ex)
        {
            log($"orphan checks skipped: {Redactor.Scrub(ex.Message, Redactor.SecretsOf(sourceConnectionString))}");
            return counts;
        }

        await using (conn)
        {
            foreach (var (table, fk) in candidates)
            {
                try
                {
                    await using var cmd = new SqlCommand(OrphanSql(table, fk), conn) { CommandTimeout = OrphanQueryTimeoutSeconds };
                    var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                    counts[RuleContext.OrphanKey(table.Key, fk.Name)] = count;
                    log($"orphan check {table.Key} {fk.Name}: {count}");
                }
                catch (SqlException ex)
                {
                    log($"orphan check skipped for {table.Key} {fk.Name}: {ex.Message}");
                }
            }
        }
        return counts;
    }

    /// <summary>SELECT COUNT_BIG(*) of child rows whose FK columns are all non-NULL and have no matching parent row.</summary>
    public static string OrphanSql(TableInfo child, ForeignKeyInfo fk)
    {
        var notNull = string.Join(" AND ", fk.Columns.Select(c => $"c.{ProfilerSql.Quote(c)} IS NOT NULL"));
        var join = string.Join(" AND ", fk.Columns.Select((c, i) => $"p.{ProfilerSql.Quote(fk.RefColumns[i])} = c.{ProfilerSql.Quote(c)}"));
        return $"SELECT COUNT_BIG(*) FROM {ProfilerSql.Quote(child.Schema)}.{ProfilerSql.Quote(child.Name)} AS c " +
               $"WHERE {notNull} AND NOT EXISTS (SELECT 1 FROM {ProfilerSql.Quote(fk.RefSchema)}.{ProfilerSql.Quote(fk.RefTable)} AS p WHERE {join});";
    }
}
```

Register it in `ENGINE/Dbm/Core/JobRegistry.cs` directly after the `DiscoverJob` line added in T2.5:
```csharp
        yield return new Dbm.Core.Analysis.AnalyzeJob();   // T2.6
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Analysis"`
Expected: PASS (13 tests).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~AnalyzeJobTests"`
Expected: PASS (1 test; the log shows `orphan check dbo.ORD_HDR FK_ORD_CUST: 2`).

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Analysis plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs \
  plugins/db-migrate/engine/Dbm.Tests/Unit/Analysis plugins/db-migrate/engine/Dbm.Tests/Integration/Analysis
git commit -F - <<'EOF'
feat(analysis): 16 rules, analyzer and analyze job with orphan counts

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.7: Analysis module + schema-analyst agent

**Files:**
- Create: `ENGINE/Dbm/Core/Analysis/AnalysisModule.cs`
- Create: `plugins/db-migrate/agents/schema-analyst.md`
- Modify: `ENGINE/Dbm/Core/ModuleRegistry.cs` (`AnalysisModule`)
- Test: `ENGINE/Dbm.Tests/Unit/Analysis/AnalysisModuleTests.cs`, `ENGINE/Dbm.Tests/Integration/Analysis/AnalysisLoopTests.cs`

**Interfaces:**
- Consumes: `IPhaseModule`, `ModuleContext`, `PacketMode`, `PayloadCheck` (C5), `WorkflowEngine.OnConnectionsSaved/Next/ApplyPatch/HumanEdit`, `Patch`/`PatchOp`/`FeedbackResponse` (C4), `ArtifactRow`, `FeedbackRow`, `CatalogRepo`, T2.5 commands, T2.6 analysis types, `CatalogText`.
- Produces:
  ```csharp
  public sealed class AnalysisModule : IPhaseModule
  {
      public const int BriefCap = 60, LargestTables = 15, TargetTableCap = 100, MinSummaryChars = 40;
      public const string Hint = "use `dbm show <table>` / `dbm search <text>` for detail";
      // Phase = Analysis, Agent = "schema-analyst", JobKind = "analyze", NeedsAgent(draft) = draft["narrative"] is null
      public static JsonNode? ResolveAnchor(ModuleContext ctx, AnalysisPayload p, string? anchor);
  }
  ```
- Packet `data` (draft): `legend`, `source`, `target`, `estimates`, `totals` (per severity), `detail` (critical + high findings in full: `id rule title sev side obj msg n? commentary?`), `brief.medium` / `brief.low` (`total` + ≤ 60 `{id, rule, obj, msg}`), `info` (info findings grouped by rule: `rule title count examples[3]`), `largestSourceTables` (15: `key rows mb pk heap`), `targetTables` (≤ 100 keys), `targetTablesTotal`, `hint`. Packet `data` (rework): `legend`, `currentNarrative`, `totals`, `detail`, `feedbackContext` (`[{id, anchor, context}]`: `finding:<id>` → the finding, `table:<side>:<key>` → `CatalogText.Table` capped at 40 columns, `column:<side>:<key>.<col>` → header + column line, `narrative` → the narrative, general → null), `hint`.
- Validation: `/narrative` required with `summary` ≥ 40 characters and ≥ 1 non-blank recommendation (warnings: summary > 150 words, > 12 risks, > 8 recommendations); every top-level member except `narrative` and `findings` must equal the base payload; `findings` must keep exactly the base ids and each finding may differ only in `commentary`, which must be a string. Approval blocker: narrative missing. Summary line: `"N findings (x critical, y high) · narrative ready|pending"`.

- [ ] **Step 1: Write the failing unit test**

`ENGINE/Dbm.Tests/Unit/Analysis/AnalysisModuleTests.cs`
```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Analysis;

public sealed class AnalysisModuleTests : IDisposable
{
    private readonly TempProject _project = TempProject.Create();
    private readonly AnalysisModule _module = new();
    private static readonly Dictionary<string, long> TwoOrphans = new() { [RuleContext.OrphanKey("dbo.ORD_HDR", "FK_ORD_CUST")] = 2 };

    public AnalysisModuleTests()
    {
        _project.Services.Catalog.Save(Side.Src, TestCatalogs.LegacyShop(), "src-fp");
        _project.Services.Catalog.Save(Side.Tgt, TestCatalogs.ShopV2(), "tgt-fp");
    }

    public void Dispose() => _project.Dispose();

    private static AnalysisPayload Draft() => Analyzer.Analyze(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), TwoOrphans);

    private ModuleContext Ctx(AnalysisPayload payload, params FeedbackRow[] feedback) => new()
    {
        Services = _project.Services,
        Current = new ArtifactRow(1, PhaseName.Analysis, 0, Json.Serialize(payload), "script", null, DateTimeOffset.UnixEpoch),
        OpenFeedback = feedback,
    };

    private static FeedbackRow Feedback(long id, string? anchor, string text) =>
        new(id, PhaseName.Analysis, 1, anchor, text, FeedbackStatus.Open, null, null, DateTimeOffset.UnixEpoch);

    private static JsonObject WithNarrative(AnalysisPayload payload, string summary = "LegacyShop moves 19,711 rows into ShopV2; two orphan orders and a heap need attention.")
    {
        var doc = (JsonObject)Json.ToNode(payload);
        doc["narrative"] = new JsonObject
        {
            ["summary"] = summary,
            ["risks"] = new JsonArray("2 orphan orders will be rejected by FK_Orders_Customers."),
            ["recommendations"] = new JsonArray("Decide whether to drop or re-parent the orphan orders."),
        };
        return doc;
    }

    [Fact]
    public void Identity_and_needs_agent()
    {
        Assert.Equal((PhaseName.Analysis, "schema-analyst", "analyze"), (_module.Phase, _module.Agent, _module.JobKind));
        Assert.True(_module.NeedsAgent(Json.ToNode(Draft())));
        Assert.False(_module.NeedsAgent(WithNarrative(Draft())));
    }

    [Fact]
    public void Draft_packet_has_digest_sections()
    {
        var data = _module.BuildPacket(Ctx(Draft()), PacketMode.Draft).AsObject();
        Assert.Equal(new[] { "legend", "source", "target", "estimates", "totals", "detail", "brief", "info", "largestSourceTables", "targetTables", "targetTablesTotal", "hint" },
            data.Select(p => p.Key));
        Assert.Equal(2, (int)data["totals"]!["high"]!);
        Assert.All(data["detail"]!.AsArray(), d => Assert.Equal("high", (string?)d!["sev"]));
        Assert.Contains(data["detail"]!.AsArray(), d => (string?)d!["rule"] == "R09" && (long)d!["n"]! == 2);
        Assert.Equal("dbo.ORD_LINE", (string?)data["largestSourceTables"]![0]!["key"]);
        Assert.Equal(6, (int)data["targetTablesTotal"]!);
        Assert.Equal(AnalysisModule.Hint, (string?)data["hint"]);
    }

    [Fact]
    public void Draft_packet_caps_medium_and_low_lists()
    {
        var payload = Draft();
        for (var i = 0; i < 70; i++)
            payload.Findings[$"L{i:000}"] = new Finding { Rule = "R10", Title = "t", Severity = Severity.Low, Side = "src", Object = $"dbo.T.C{i}", Message = "m" };
        var brief = _module.BuildPacket(Ctx(payload), PacketMode.Draft)["brief"]!;
        var lowTotal = payload.Findings.Values.Count(f => f.Severity == Severity.Low);
        Assert.Equal(lowTotal, (int)brief["low"]!["total"]!);
        Assert.Equal(AnalysisModule.BriefCap, brief["low"]!["items"]!.AsArray().Count);
    }

    [Fact]
    public void Rework_packet_resolves_feedback_anchors()
    {
        var payload = Json.FromNode<AnalysisPayload>(WithNarrative(Draft()));
        var data = _module.BuildPacket(Ctx(payload,
            Feedback(11, "finding:F001", "why high?"),
            Feedback(12, "table:src:dbo.CUST", "PII?"),
            Feedback(13, "column:src:dbo.CUST.EMAIL_ADDR", "normalise"),
            Feedback(14, "narrative", "shorter"),
            Feedback(15, null, "general")), PacketMode.Rework).AsObject();

        Assert.NotNull(data["currentNarrative"]);
        var context = data["feedbackContext"]!.AsArray();
        Assert.Equal(5, context.Count);
        Assert.Equal("dbo.AUDIT_LOG", (string?)context[0]!["context"]!["obj"]);
        Assert.StartsWith("src table dbo.CUST rows=1000", (string?)context[1]!["context"]);
        Assert.Contains("EMAIL_ADDR varchar(120)", (string?)context[2]!["context"]);
        Assert.NotNull(context[3]!["context"]!["summary"]);
        Assert.Null(context[4]!["context"]);
    }

    [Fact]
    public void Validate_accepts_narrative_and_commentary()
    {
        var draft = Draft();
        var doc = WithNarrative(draft);
        var r09 = draft.Findings.First(f => f.Value.Rule == "R09").Key;
        doc["findings"]![r09]!["commentary"] = "Orphans come from deleted customers; filter or re-parent.";
        var check = _module.Validate(Ctx(draft), doc);
        Assert.True(check.Ok, string.Join("; ", check.Errors));
    }

    [Fact]
    public void Validate_rejects_bad_narratives()
    {
        var draft = Draft();
        Assert.Contains(_module.Validate(Ctx(draft), Json.ToNode(draft)).Errors, e => e.StartsWith("/narrative is required", StringComparison.Ordinal));
        Assert.Contains(_module.Validate(Ctx(draft), WithNarrative(draft, "Too short.")).Errors, e => e.Contains("at least 40 characters"));
        var noRecs = WithNarrative(draft);
        noRecs["narrative"]!["recommendations"] = new JsonArray();
        Assert.Contains(_module.Validate(Ctx(draft), noRecs).Errors, e => e.Contains("recommendations needs at least one item"));
    }

    [Fact]
    public void Validate_rejects_changes_outside_narrative_and_commentary()
    {
        var draft = Draft();
        var unknown = WithNarrative(draft);
        unknown["findings"]!["F999"] = new JsonObject { ["commentary"] = "x" };
        Assert.Contains(_module.Validate(Ctx(draft), unknown).Errors, e => e.Contains("unknown finding 'F999'"));

        var estimates = WithNarrative(draft);
        estimates["estimates"]!["estimatedMinutes"] = 99;
        Assert.Contains(_module.Validate(Ctx(draft), estimates).Errors, e => e.StartsWith("/estimates must not be changed", StringComparison.Ordinal));

        var message = WithNarrative(draft);
        message["findings"]!["F001"]!["message"] = "rewritten";
        Assert.Contains(_module.Validate(Ctx(draft), message).Errors, e => e == "/findings/F001: only commentary may change");

        var nonString = WithNarrative(draft);
        nonString["findings"]!["F001"]!["commentary"] = 5;
        Assert.Contains(_module.Validate(Ctx(draft), nonString).Errors, e => e == "/findings/F001/commentary must be a string");
    }

    [Fact]
    public void Blockers_and_summary()
    {
        var draft = Draft();
        Assert.Single(_module.ApprovalBlockers(Ctx(draft), Json.ToNode(draft)));
        Assert.Empty(_module.ApprovalBlockers(Ctx(draft), WithNarrative(draft)));
        Assert.EndsWith("(0 critical, 2 high) · narrative pending", _module.Summarize(Json.ToNode(draft)));
        Assert.EndsWith("· narrative ready", _module.Summarize(WithNarrative(draft)));
    }
}
```

- [ ] **Step 2: Write the failing end-to-end loop test**

This drives the real M1 engine: connections saved → `discover --inline` runs the discover and analyze jobs → the Analysis phase waits for the agent → the packet is written → a schema-analyst patch is applied → a human edit outside the allowed paths is rejected.

`ENGINE/Dbm.Tests/Integration/Analysis/AnalysisLoopTests.cs`
```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.Analysis;

[Trait("Category", "Integration")]
public sealed class AnalysisLoopTests(SamplePairFixture fixture) : IClassFixture<SamplePairFixture>
{
    [Fact]
    public async Task Discover_analyze_packet_agent_patch_and_validation()
    {
        using var project = await SampleProject.CreateAsync(fixture.Pair);
        var s = project.Services;
        s.Workflow.OnConnectionsSaved();                     // Setup approved, Discovery running, "discover" queued

        var r = await CliRunner.RunAsync(project.Ws, null, "discover", "--inline");
        Assert.True(r.Exit == 0, r.Out);
        Assert.Equal((8, 6), ((int)r.Json["src"]!, (int)r.Json["tgt"]!));
        Assert.True((int)r.Json["ran"]! >= 2);               // discover + analyze

        Assert.Equal(PhaseStatus.Approved, s.Phases.Get(PhaseName.Discovery).Status);
        Assert.Equal(PhaseStatus.Drafting, s.Phases.Get(PhaseName.Analysis).Status);
        var v0 = s.Artifacts.Latest(PhaseName.Analysis)!;
        Assert.Equal((0, "script"), (v0.Version, v0.Author));
        var r09 = Json.Deserialize<AnalysisPayload>(v0.PayloadJson).Findings.Single(f => f.Value.Rule == "R09").Key;

        var next = s.Workflow.Next();
        Assert.Equal(("agent", "schema-analyst", "analysis", "draft"), (next.Action, next.Agent, next.Phase, next.Mode));
        var packetText = File.ReadAllText(next.Packet!);
        Assert.DoesNotContain(fixture.Pair.SourceCs, packetText);
        var packet = JsonNode.Parse(packetText)!;
        Assert.Equal(0, (int)packet["baseVersion"]!);
        Assert.Contains(packet["data"]!["detail"]!.AsArray(), d => (string?)d!["id"] == r09);

        var patch = new Patch("analysis", 0, new List<PatchOp>
        {
            new("add", "/narrative", new JsonObject
            {
                ["summary"] = "LegacyShop moves about 19,700 rows into ShopV2 in under a minute; two orphan orders and one heap need decisions first.",
                ["risks"] = new JsonArray($"2 orphan orders in dbo.ORD_HDR will be rejected by the target foreign key ({r09})."),
                ["recommendations"] = new JsonArray("Decide whether to re-parent or drop the 2 orphan orders before mapping."),
            }),
            new("add", $"/findings/{r09}/commentary", JsonValue.Create("The orphans reference customers that no longer exist; filter them in the Orders source query.")),
        }, new List<FeedbackResponse>(), "Narrative and commentary drafted.");

        var dry = s.Workflow.ApplyPatch(patch, dryRun: true);
        Assert.True(dry.Ok, string.Join("; ", dry.Errors));
        var applied = s.Workflow.ApplyPatch(patch);
        Assert.True(applied.Ok, string.Join("; ", applied.Errors));
        Assert.Equal(1, applied.Version);
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Equal("agent", s.Artifacts.Latest(PhaseName.Analysis)!.Author);
        Assert.Equal("review", s.Workflow.Next().Reason);

        r = await CliRunner.RunAsync(project.Ws, null, "show", r09);
        Assert.Equal(0, r.Exit);
        Assert.Contains("commentary: The orphans reference customers", r.Out);

        var tampered = new Patch("analysis", 1, new List<PatchOp> { new("replace", "/estimates/estimatedMinutes", JsonValue.Create(99)) },
            new List<FeedbackResponse>());
        var rejected = s.Workflow.HumanEdit(tampered);
        Assert.False(rejected.Ok);
        Assert.Contains(rejected.Errors, e => e.Contains("/estimates must not be changed"));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AnalysisModuleTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'AnalysisModule' could not be found`.

- [ ] **Step 4: Implement the module**

`ENGINE/Dbm/Core/Analysis/AnalysisModule.cs`
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.Analysis;

/// <summary>Phase module for ANALYSIS: packets for the schema-analyst, patch validation, approval blockers.</summary>
public sealed class AnalysisModule : IPhaseModule
{
    public const int BriefCap = 60;
    public const int LargestTables = 15;
    public const int TargetTableCap = 100;
    public const int MinSummaryChars = 40;
    public const string Hint = "use `dbm show <table>` / `dbm search <text>` for detail";

    public PhaseName Phase => PhaseName.Analysis;
    public string Agent => "schema-analyst";
    public string JobKind => "analyze";

    public bool NeedsAgent(JsonNode draft) => draft["narrative"] is null;

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        var payload = Json.Deserialize<AnalysisPayload>(ctx.Current.PayloadJson);
        return mode == PacketMode.Draft ? DraftPacket(ctx, payload) : ReworkPacket(ctx, payload);
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (payload is not JsonObject doc) return new PayloadCheck(["payload must be a JSON object"], []);

        ValidateNarrative(doc["narrative"], errors, warnings);

        if (JsonNode.Parse(ctx.Current.PayloadJson) is JsonObject baseDoc)
        {
            var keys = doc.Select(p => p.Key).Union(baseDoc.Select(p => p.Key), StringComparer.Ordinal);
            foreach (var key in keys.Where(k => k is not ("narrative" or "findings")))
                if (!JsonNode.DeepEquals(doc[key], baseDoc[key])) errors.Add($"/{key} must not be changed (only /narrative and /findings/<id>/commentary are editable)");
            ValidateFindings(doc["findings"] as JsonObject, baseDoc["findings"] as JsonObject, errors);
        }
        return new PayloadCheck(errors, warnings);
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload) =>
        payload["narrative"] is null
            ? ["Narrative missing: the schema-analyst must write the summary, risks and recommendations before approval."]
            : [];

    public string Summarize(JsonNode payload)
    {
        var p = Json.FromNode<AnalysisPayload>(payload);
        return Analyzer.Summary(p) + (p.Narrative is null ? " · narrative pending" : " · narrative ready");
    }

    // ---- packets -------------------------------------------------------------------------------------------------

    private static JsonObject DraftPacket(ModuleContext ctx, AnalysisPayload p)
    {
        var src = ctx.Services.Catalog.Get(Side.Src);
        var tgt = ctx.Services.Catalog.Get(Side.Tgt);
        var findings = p.Findings.ToList();
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["source"] = Json.ToNode(p.Source),
            ["target"] = Json.ToNode(p.Target),
            ["estimates"] = Json.ToNode(p.Estimates),
            ["totals"] = Totals(p),
            ["detail"] = Detailed(findings),
            ["brief"] = new JsonObject
            {
                ["medium"] = Brief(findings, Severity.Medium),
                ["low"] = Brief(findings, Severity.Low),
            },
            ["info"] = InfoByRule(findings),
            ["largestSourceTables"] = new JsonArray((src?.Tables ?? new List<TableInfo>())
                .OrderByDescending(t => t.Rows).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Take(LargestTables)
                .Select(t => (JsonNode?)new JsonObject
                {
                    ["key"] = t.Key, ["rows"] = t.Rows, ["mb"] = Math.Round(t.SizeMb, 2),
                    ["pk"] = t.PrimaryKey is not null, ["heap"] = t.IsHeap,
                }).ToArray()),
            ["targetTables"] = new JsonArray((tgt?.Tables ?? new List<TableInfo>())
                .Select(t => t.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Take(TargetTableCap).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["targetTablesTotal"] = tgt?.Tables.Count ?? 0,
            ["hint"] = Hint,
        };
    }

    private static JsonObject ReworkPacket(ModuleContext ctx, AnalysisPayload p)
    {
        var context = new JsonArray();
        foreach (var feedback in ctx.OpenFeedback)
            context.Add(new JsonObject
            {
                ["id"] = feedback.Id,
                ["anchor"] = feedback.Anchor,
                ["context"] = ResolveAnchor(ctx, p, feedback.Anchor),
            });
        return new JsonObject
        {
            ["legend"] = Legend(),
            ["currentNarrative"] = p.Narrative is null ? null : Json.ToNode(p.Narrative),
            ["totals"] = Totals(p),
            ["detail"] = Detailed(p.Findings.ToList()),
            ["feedbackContext"] = context,
            ["hint"] = Hint,
        };
    }

    /// <summary>finding:&lt;id&gt; → the finding; table:&lt;side&gt;:&lt;key&gt; → compact table text; column:… → column line; narrative → narrative.</summary>
    public static JsonNode? ResolveAnchor(ModuleContext ctx, AnalysisPayload p, string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return null;
        if (anchor == "narrative") return p.Narrative is null ? null : Json.ToNode(p.Narrative);
        var parts = anchor.Split(':', 3);
        if (parts[0] == "finding" && parts.Length == 2)
            return p.Findings.TryGetValue(parts[1], out var finding) ? Detail(parts[1], finding) : JsonValue.Create($"unknown finding {parts[1]}");
        if (parts.Length == 3 && parts[0] is "table" or "column")
        {
            var side = parts[1] == "tgt" ? Side.Tgt : Side.Src;
            var snapshot = ctx.Services.Catalog.Get(side);
            if (snapshot is null) return null;
            if (parts[0] == "table")
                return snapshot.FindTable(parts[2]) is { } t ? JsonValue.Create(CatalogText.Table(side, t, 40)) : JsonValue.Create($"unknown table {parts[2]}");
            var dot = parts[2].LastIndexOf('.');
            if (dot > 0 && snapshot.FindTable(parts[2][..dot]) is { } table && table.FindColumn(parts[2][(dot + 1)..]) is { } column)
                return JsonValue.Create(CatalogText.TableHeader(side, table) + "\n  " + CatalogText.ColumnLine(table, column));
            return JsonValue.Create($"unknown column {parts[2]}");
        }
        return null;
    }

    private static JsonObject Legend() => new()
    {
        ["id"] = "finding id; feedback anchor finding:<id>; patch path /findings/<id>/commentary",
        ["sev"] = "severity: critical|high|medium|low|info",
        ["obj"] = "object: schema.table, schema.table.column, 'A ↔ B' cycle, or database",
        ["msg"] = "script-generated message",
        ["n"] = "count (orphan rows, trigger count, rows)",
        ["rows"] = "row count",
        ["mb"] = "size in MB",
        ["pk"] = "has a primary key",
        ["heap"] = "no clustered index",
    };

    private static JsonObject Totals(AnalysisPayload p)
    {
        var totals = new JsonObject();
        foreach (var severity in Enum.GetValues<Severity>())
            totals[EnumText.ToText(severity)] = p.Findings.Values.Count(f => f.Severity == severity);
        return totals;
    }

    private static JsonArray Detailed(List<KeyValuePair<string, Finding>> findings) =>
        new(findings.Where(f => f.Value.Severity is Severity.Critical or Severity.High)
            .Select(f => (JsonNode?)Detail(f.Key, f.Value)).ToArray());

    private static JsonObject Detail(string id, Finding f)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["rule"] = f.Rule, ["title"] = f.Title, ["sev"] = EnumText.ToText(f.Severity),
            ["side"] = f.Side, ["obj"] = f.Object, ["msg"] = f.Message,
        };
        if (f.Count is { } n) o["n"] = n;
        if (f.Commentary is { } c) o["commentary"] = c;
        return o;
    }

    private static JsonObject Brief(List<KeyValuePair<string, Finding>> findings, Severity severity)
    {
        var matching = findings.Where(f => f.Value.Severity == severity).ToList();
        return new JsonObject
        {
            ["total"] = matching.Count,
            ["items"] = new JsonArray(matching.Take(BriefCap).Select(f => (JsonNode?)new JsonObject
            {
                ["id"] = f.Key, ["rule"] = f.Value.Rule, ["obj"] = f.Value.Object, ["msg"] = f.Value.Message,
            }).ToArray()),
        };
    }

    private static JsonArray InfoByRule(List<KeyValuePair<string, Finding>> findings) =>
        new(findings.Where(f => f.Value.Severity == Severity.Info)
            .GroupBy(f => f.Value.Rule)
            .Select(g => (JsonNode?)new JsonObject
            {
                ["rule"] = g.Key,
                ["title"] = g.First().Value.Title,
                ["count"] = g.Count(),
                ["examples"] = new JsonArray(g.Take(3).Select(f => (JsonNode?)JsonValue.Create(f.Value.Object)).ToArray()),
            }).ToArray());

    // ---- validation ----------------------------------------------------------------------------------------------

    private static void ValidateNarrative(JsonNode? node, List<string> errors, List<string> warnings)
    {
        if (node is null)
        {
            errors.Add("/narrative is required: {summary, risks[], recommendations[]}");
            return;
        }
        Narrative? narrative;
        try
        {
            narrative = node.Deserialize<Narrative>(Json.Options);
        }
        catch (JsonException ex)
        {
            errors.Add($"/narrative is malformed: {ex.Message}");
            return;
        }
        if (narrative is null)
        {
            errors.Add("/narrative is required: {summary, risks[], recommendations[]}");
            return;
        }
        var summary = narrative.Summary?.Trim() ?? "";
        if (summary.Length < MinSummaryChars) errors.Add($"/narrative/summary must be at least {MinSummaryChars} characters");
        var recommendations = (narrative.Recommendations ?? new List<string>()).Count(r => !string.IsNullOrWhiteSpace(r));
        if (recommendations == 0) errors.Add("/narrative/recommendations needs at least one item");
        var words = summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 150) warnings.Add($"/narrative/summary has {words} words (target ≤ 150)");
        if ((narrative.Risks?.Count ?? 0) > 12) warnings.Add("/narrative/risks has more than 12 items");
        if (recommendations > 8) warnings.Add("/narrative/recommendations has more than 8 items");
    }

    private static void ValidateFindings(JsonObject? findings, JsonObject? baseFindings, List<string> errors)
    {
        if (findings is null)
        {
            errors.Add("/findings must not be removed");
            return;
        }
        baseFindings ??= new JsonObject();
        foreach (var (id, _) in findings)
            if (!baseFindings.ContainsKey(id)) errors.Add($"commentary targets unknown finding '{id}' (only existing ids may be annotated)");
        foreach (var (id, baseFinding) in baseFindings)
        {
            if (!findings.TryGetPropertyValue(id, out var finding) || finding is null)
            {
                errors.Add($"/findings/{id} must not be removed");
                continue;
            }
            if (!JsonNode.DeepEquals(WithoutCommentary(finding), WithoutCommentary(baseFinding)))
                errors.Add($"/findings/{id}: only commentary may change");
            if (finding["commentary"] is { } commentary && !(commentary is JsonValue v && v.TryGetValue<string>(out _)))
                errors.Add($"/findings/{id}/commentary must be a string");
        }
    }

    private static JsonNode? WithoutCommentary(JsonNode? node)
    {
        if (node is not JsonObject o) return node;
        var copy = (JsonObject)o.DeepClone();
        copy.Remove("commentary");
        return copy;
    }
}
```

Register it in `ENGINE/Dbm/Core/ModuleRegistry.cs`, directly below the marker line `        // milestone registrations below` (the module takes no constructor argument; it reads services from `ModuleContext.Services`):
```csharp
        yield return new Dbm.Core.Analysis.AnalysisModule();   // T2.7
```

- [ ] **Step 5: Run the unit tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~AnalysisModuleTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Write the schema-analyst agent**

`plugins/db-migrate/agents/schema-analyst.md`
````markdown
---
name: schema-analyst
description: Writes the executive summary, risk narrative, recommendations and per-finding commentary for the db-migrate ANALYSIS phase from a compact work packet, and answers human feedback in rework rounds. Dispatched by the /db-migrate orchestrator with a packet path; not for general use.
tools: Bash, Read, Write
model: inherit
---

# schema-analyst

You turn the script-computed analysis of a SQL Server → SQL Server migration into a short, evidence-based narrative that a DBA can approve. The engine has already extracted both schemas, profiled the data and run 16 rule checks. You add judgement, not new facts.

## 1. Read the packet

The orchestrator gives you one absolute path: the work packet (JSON). Read it with the Read tool.

- Envelope: `phase` ("analysis"), `mode` ("draft" or "rework"), `baseVersion`, `patchPath` (where you write the patch), `feedback` (rework only: `{id, anchor, text}` items), `rules`, `data`.
- Draft `data`: `legend` (meaning of the short keys), `source` / `target` (server, database, version, edition, collation, compatLevel, tables, columns, rows, sizeMb, views, procedures, functions, triggers), `estimates` (totalRows, totalSizeMb, estimatedMinutes, basis), `totals` (findings per severity), `detail` (every critical and high finding: id, rule, title, sev, side, obj, msg, n, commentary), `brief.medium` / `brief.low` (`total` plus up to 60 `{id, rule, obj, msg}`), `info` (info findings grouped by rule with a count and three examples), `largestSourceTables` (key, rows, mb, pk, heap), `targetTables` + `targetTablesTotal`, `hint`.
- Rework `data`: `currentNarrative`, `totals`, `detail`, `feedbackContext` (for each feedback id: the finding, the compact table or column summary, or the narrative the comment is anchored to), `hint`.

## 2. Get more context only when needed

- `dbm show <schema.table>` — table header plus one line per column with profile (null %, distinct ratio, class, samples).
- `dbm show <schema.table.column>` — one column with its full profile.
- `dbm show F007` — one finding with its current commentary.
- `dbm search "<words>" --side src|tgt -k 5` — find tables or columns by meaning, e.g. `dbm search "customer email" --side tgt`.

Use at most about eight of these calls per run. The packet already holds everything important.

## 3. Write the narrative and commentary

1. `narrative.summary` — at least 40 characters and at most 150 words: what moves (tables, rows, size, estimated time), overall readiness, and the two or three issues that matter most. Use the objects and numbers from the packet.
2. `narrative.risks` — at most 12 bullets, most severe first. Each names the object(s), the consequence for the transfer and the finding id in parentheses, e.g. `2 orphan orders in dbo.ORD_HDR will be rejected by the target foreign key (F012).`
3. `narrative.recommendations` — 1 to 8 imperative bullets a DBA can act on before or during mapping, e.g. `Decide whether to re-parent or drop the 2 orphan orders in dbo.ORD_HDR.`
4. `findings/<id>/commentary` — only for critical and high findings: one or two sentences on why it matters in this migration and what to do. Skip a finding when you have nothing useful to add.

Be factual: never invent tables, columns, counts or behaviour that are not in the packet or in `dbm show` / `dbm search` output. Plain sentences only — inline `code` and **bold** are fine, headings and tables are not. Do not restate every finding.

## 4. In rework mode, answer every feedback item

For each feedback id in the packet add one response: `addressed` with a note saying what changed, or `declined` with the reason. Change only what the feedback asks for.

## 5. Write the patch

Write exactly one JSON file to `patchPath` with the Write tool:

```json
{"phase":"analysis","baseVersion":0,
 "ops":[
   {"op":"add","path":"/narrative","value":{"summary":"…","risks":["…"],"recommendations":["…"]}},
   {"op":"add","path":"/findings/F012/commentary","value":"…"}
 ],
 "responses":[],
 "summary":"Narrative and commentary for 2 high findings."}
```

- `baseVersion` is the packet's `baseVersion` (0 in the example).
- Draft mode: `add` `/narrative` and `add` each commentary.
- Rework mode: `replace` `/narrative` when you change it (send the whole object); `add` a commentary that does not exist yet, `replace` one that does; `responses` holds `{"feedbackId": 12, "status": "addressed", "note": "Shortened the summary to 90 words."}` or `{"feedbackId": 13, "status": "declined", "note": "The count comes from the source catalog and is correct."}` for every feedback id.
- Only `/narrative` and `/findings/<existing id>/commentary` may change; anything else is rejected.

## 6. Validate

Run `dbm apply <patchPath> --dry-run`. If it reports errors, fix the patch and run it again until the output contains `"ok":true`. Never run `dbm apply` without `--dry-run`; the orchestrator applies the patch.

## 7. Reply

Reply with exactly one line: `patch: <patchPath> ops=<number of ops> responses=<number of responses>`

## Never

- print, echo or ask for connection strings — they are not in the packet and must never appear in your output;
- read `.dbmigrate/state.db` or any other file under `.dbmigrate/` except the packet and your patch;
- connect to the databases (no sqlcmd, no scripts) — use `dbm show` and `dbm search` only.
````

- [ ] **Step 7: Run the end-to-end loop test**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~AnalysisLoopTests"`
Expected: PASS (1 test). If `ran` is 1, the analyze handler or the module registration is missing — check `JobRegistry` (T2.6) and `ModuleRegistry` (Step 4).

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Analysis/AnalysisModule.cs plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs \
  plugins/db-migrate/agents/schema-analyst.md plugins/db-migrate/engine/Dbm.Tests/Unit/Analysis/AnalysisModuleTests.cs \
  plugins/db-migrate/engine/Dbm.Tests/Integration/Analysis/AnalysisLoopTests.cs
git commit -F - <<'EOF'
feat(analysis): analysis phase module and schema-analyst agent

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 2.8: Analysis UI, catalog endpoints, standalone export

**Files:**
- Create: `ENGINE/Dbm/Web/Endpoints/CatalogEndpoints.cs`, `ENGINE/Dbm/Web/Endpoints/WebExport.cs`, `ENGINE/Dbm/Web/Endpoints/ExportEndpoints.cs`
- Create: `ENGINE/Dbm/wwwroot/js/lib/graph.js`, `ENGINE/Dbm/wwwroot/js/views/analysis.js`
- Modify: `ENGINE/Dbm/Web/Endpoints/EndpointRegistry.cs`, `ENGINE/Dbm/wwwroot/index.html`, `ENGINE/Dbm/wwwroot/css/app.css`
- Test: `ENGINE/Dbm.Tests/Unit/Web/CatalogEndpointsTests.cs`, `ENGINE/Dbm.Tests/Unit/Web/WebExportTests.cs`, `ENGINE/Dbm.Tests/Unit/Web/CatalogHttpTests.cs`, `ENGINE/Dbm.Tests/js/graph.test.cjs`

**Interfaces:**
- Consumes: `WebState`, `WebHost.RunAsync`, `ServerControl.ReadInfo/IsAliveAsync`, `ServerInfo` (C7), `EndpointRegistry.MapAll`, `CatalogRepo`, `VectorIndex`, `Synonyms.ForProject`, `WorkflowEngine.Rediscover`, `ArtifactRepo`, `PhaseRepo`, `ProjectRepo.Get`, C9 (`DBM.h`, `DBM.api`, `DBM.components.{reviewBar, kpi, emptyState, modal, toast}`, `ctx.{artifact, api, refresh, toast, commentable}`, CSS vocabulary).
- Produces:
  ```csharp
  namespace Dbm.Web.Endpoints;
  public sealed record TableListItem(string Key, long Rows, double SizeMb, int Columns, bool HasPk, bool IsHeap, int FkOut, int FkIn, int Triggers, List<string> Refs);
  public sealed record ReferencedBy(string Table, string ForeignKey, List<string> Columns);
  public sealed record TableDetail(string Side, string Key, TableInfo Table, IReadOnlyList<string>? BestKey, bool IsHeap, List<ReferencedBy> ReferencedBy);
  public static class CatalogEndpoints { Map(app, state); List<TableListItem> ListTables(CatalogSnapshot); TableDetail? Detail(Side, CatalogSnapshot, string key); bool TryParseSide(string, out Side); }
  public interface IWebAssets { string? Read(string path); IReadOnlyList<string> List(string directory); }
  public sealed class DirectoryAssets(string root) : IWebAssets;  public sealed class FileProviderAssets(IFileProvider) : IWebAssets;
  public sealed class ExportException(string message) : Exception;  public sealed record ExportFile(string FileName, string ContentType, byte[] Content);
  public static class WebExport { LibOrder; ComponentOrder; IWebAssets LocateAssets(IFileProvider? webRoot = null); IReadOnlyList<string> ScriptOrder(IWebAssets, string view);
      string BuildHtml(IWebAssets, string view, string title, JsonNode payload); ExportFile Analysis(DbmServices, IWebAssets); JsonObject AnalysisData(DbmServices, ArtifactRow); string SaveCopy(Workspace, ExportFile); }
  public static class ExportEndpoints { delegate Task<ExportFile> Builder(WebState, IWebAssets, CancellationToken); void Register(string what, Builder builder); void Map(app, state); }
  ```
  HTTP: `GET /api/catalog/{side}` → `[TableListItem]`; `GET /api/catalog/{side}/table/{key}` → `TableDetail`; `POST /api/rediscover` → `{ok}` or 409; `GET /api/search?q=&side=&kind=&k=` → `[{side, kind, key, score, text}]`; `GET /api/export/{what}` → file download; `POST /api/export/{what}` → `{ok, file, path, download}`. Errors are `{error, message}` with 400/404/409.
  JS: `DBM.graph.layout(nodes, edges, opts?)` → `{pos: {id: {x, y, layer, order}}, width, height, layers, nodeW, nodeH}`; `DBM.graph.fkSvg(nodes, edges, opts)` → `SVGSVGElement` (+ `svg.dbmFit(full)`); `DBM.views.analysis = {title, render, onEvent}`.

- [ ] **Step 1: Write the failing C# tests**

`ENGINE/Dbm.Tests/Unit/Web/CatalogEndpointsTests.cs`
```csharp
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public sealed class CatalogEndpointsTests
{
    [Fact]
    public void ListTables_reports_keys_counts_and_fk_degrees()
    {
        var list = CatalogEndpoints.ListTables(TestCatalogs.ShopV2());
        Assert.Equal(6, list.Count);
        var customers = list.Single(t => t.Key == "app.Customers");
        Assert.Equal((10, true, false, 1, 2, 0), (customers.Columns, customers.HasPk, customers.IsHeap, customers.FkOut, customers.FkIn, customers.Triggers));
        Assert.Equal(new[] { "app.Addresses" }, customers.Refs);
        var orders = list.Single(t => t.Key == "app.Orders");
        Assert.Equal((2, 1, 1), (orders.FkOut, orders.FkIn, orders.Triggers));
        Assert.True(list.Single(t => t.Key == "app.AuditEvents").IsHeap);
        Assert.Contains("\"hasPk\":true", Json.Serialize(customers));
    }

    [Fact]
    public void Detail_includes_profiles_best_key_and_referencing_tables()
    {
        var detail = CatalogEndpoints.Detail(Side.Src, TestCatalogs.LegacyShop(), "DBO.cust")!;
        Assert.Equal(("src", "dbo.CUST", false), (detail.Side, detail.Key, detail.IsHeap));
        Assert.Equal(new[] { "CUST_ID" }, detail.BestKey);
        Assert.Equal(new[] { "dbo.ADDR", "dbo.ORD_HDR" }, detail.ReferencedBy.Select(r => r.Table).OrderBy(t => t, StringComparer.Ordinal));
        var json = Json.Serialize(detail);
        Assert.Contains("\"profile\":{\"sampledRows\":1000,\"nulls\":100", json);
        Assert.Null(CatalogEndpoints.Detail(Side.Src, TestCatalogs.LegacyShop(), "dbo.NOPE"));
    }

    [Theory]
    [InlineData("src", true, Side.Src)]
    [InlineData("TGT", true, Side.Tgt)]
    [InlineData("both", false, Side.Src)]
    public void TryParseSide(string text, bool ok, Side expected)
    {
        Assert.Equal(ok, CatalogEndpoints.TryParseSide(text, out var side));
        if (ok) Assert.Equal(expected, side);
    }
}
```

`ENGINE/Dbm.Tests/Unit/Web/WebExportTests.cs`
```csharp
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public sealed class WebExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbm-www-" + Guid.NewGuid().ToString("N"));

    public WebExportTests()
    {
        Write("index.html", """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>db-migrate</title>
              <link rel="stylesheet" href="css/app.css">
            </head>
            <body>
              <div id="app"></div>
              <script src="js/lib/dom.js"></script>
              <script src="js/api.js"></script>
              <script src="js/app.js"></script>
            </body>
            </html>
            """);
        Write("css/app.css", ":root { --accent: #4f46e5; }");
        Write("js/lib/zz.js", "/*zz*/");
        Write("js/lib/graph.js", "/*graph*/");
        Write("js/lib/dom.js", "/*dom*/");
        Write("js/components/review.js", "/*review*/");
        Write("js/components/core.js", "/*core*/");
        Write("js/api.js", "/*api*/");
        Write("js/views/analysis.js", "/*analysis*/ var s = '</script>';");
        Write("js/app.js", "/*app*/");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    [Fact]
    public void Script_order_follows_the_ui_conventions()
    {
        Assert.Equal(
            new[] { "js/lib/dom.js", "js/lib/graph.js", "js/lib/zz.js", "js/components/core.js", "js/components/review.js", "js/views/analysis.js", "js/app.js" },
            WebExport.ScriptOrder(new DirectoryAssets(_root), "analysis"));
        Assert.Throws<ExportException>(() => WebExport.ScriptOrder(new DirectoryAssets(_root), "mapping"));
    }

    [Fact]
    public void BuildHtml_inlines_assets_and_the_export_payload()
    {
        var html = WebExport.BuildHtml(new DirectoryAssets(_root), "analysis", "Demo <1> — Analysis v1",
            new JsonObject { ["note"] = "<b>bold</b>" });

        Assert.DoesNotContain("src=\"", html);
        Assert.DoesNotContain("rel=\"stylesheet\"", html);
        Assert.Contains("<style>\n:root { --accent: #4f46e5; }\n</style>", html);
        Assert.Contains("<title>Demo &lt;1&gt; — Analysis v1</title>", html);
        Assert.Contains("window.DBM_EXPORT = {\"view\":\"analysis\"", html);
        Assert.DoesNotContain("<b>bold</b>", html);
        Assert.DoesNotContain("/*api*/", html);
        Assert.Contains("var s = '<\\/script>';", html);
        Assert.Contains("<div id=\"app\"></div>", html);
        var order = new[] { "window.DBM_EXPORT", "/*dom*/", "/*graph*/", "/*zz*/", "/*core*/", "/*review*/", "/*analysis*/", "/*app*/", "</body>" }
            .Select(marker => html.IndexOf(marker, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void Analysis_export_contains_the_current_version_and_both_catalogs()
    {
        using var project = TempProject.Create("demo");
        var services = project.Services;
        services.Catalog.Save(Side.Src, TestCatalogs.LegacyShop(), "a");
        services.Catalog.Save(Side.Tgt, TestCatalogs.ShopV2(), "b");
        var payload = Analyzer.Analyze(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2());
        services.Artifacts.Add(PhaseName.Analysis, 0, Json.Serialize(payload), "script", Analyzer.Summary(payload));
        services.Phases.SetCurrentVersion(PhaseName.Analysis, 0);

        var file = WebExport.Analysis(services, new DirectoryAssets(_root));
        var html = Encoding.UTF8.GetString(file.Content);

        Assert.Equal("analysis-v0.html", file.FileName);
        Assert.StartsWith("text/html", file.ContentType);
        Assert.Contains("<title>demo — Analysis v0</title>", html);
        Assert.Contains("\"catalog\":{\"src\":[", html);
        Assert.Contains("\"app.Customers\":{\"side\":\"tgt\"", html);
        Assert.Contains("\"refs\":[\"dbo.CUST\"]", html);

        var saved = WebExport.SaveCopy(services.Ws, file);
        Assert.Equal(Path.Combine(services.Ws.ExportsDir, "analysis-v0.html"), saved);
        Assert.True(File.Exists(saved));
    }
}
```

`ENGINE/Dbm.Tests/Unit/Web/CatalogHttpTests.cs` (hosts the real server in-process through M1's `WebTestServer` with the real registries; no SQL Server needed)
```csharp
using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Analysis;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

/// <summary>Hosts the real server in-process (WebTestServer, real registries) on a workspace with a stored catalog and analysis v0.</summary>
public sealed class CatalogHttpTests : IAsyncLifetime
{
    private readonly TempProject _project = TempProject.Create("http");
    private WebTestServer _server = null!;

    public async Task InitializeAsync()
    {
        var s = _project.Services;
        s.Catalog.Save(Side.Src, TestCatalogs.LegacyShop(), "src-fp");
        s.Catalog.Save(Side.Tgt, TestCatalogs.ShopV2(), "tgt-fp");
        var (rows, _) = VectorIndex.Build(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2(), Synonyms.Default());
        s.Catalog.SaveVectors(Side.Src, rows.Where(r => r.Side == Side.Src));
        s.Catalog.SaveVectors(Side.Tgt, rows.Where(r => r.Side == Side.Tgt));
        var payload = Analyzer.Analyze(TestCatalogs.LegacyShop(), TestCatalogs.ShopV2());
        s.Artifacts.Add(PhaseName.Analysis, 0, Json.Serialize(payload), "script", Analyzer.Summary(payload));
        s.Phases.SetCurrentVersion(PhaseName.Analysis, 0);
        _server = await WebTestServer.StartAsync(_project.Ws, ws => DbmServices.Open(ws));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _project.Dispose();
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> GetJsonAsync(string path) => _server.SendAsync(HttpMethod.Get, path);

    [Fact]
    public async Task Catalog_list_and_table_detail()
    {
        var (listStatus, list) = await GetJsonAsync("/api/catalog/tgt");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var tables = list!.AsArray();
        Assert.Equal(6, tables.Count);
        var customers = tables.Single(t => (string?)t!["key"] == "app.Customers")!;
        Assert.True((bool)customers["hasPk"]!);
        Assert.Equal("app.Addresses", (string?)customers["refs"]![0]);

        var (detailStatus, detail) = await GetJsonAsync("/api/catalog/src/table/dbo.CUST");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal("dbo.CUST", (string?)detail!["key"]);
        var email = detail["table"]!["columns"]!.AsArray().Single(c => (string?)c!["name"] == "EMAIL_ADDR")!;
        Assert.Equal(100, (int)email["profile"]!["nulls"]!);
        Assert.Equal(2, detail["referencedBy"]!.AsArray().Count);
    }

    [Fact]
    public async Task Search_returns_ranked_hits()
    {
        var (status, hits) = await GetJsonAsync("/api/search?q=customer%20email&side=tgt&k=3");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("app.Customers.Email", (string?)hits!.AsArray()[0]!["key"]);
    }

    [Fact]
    public async Task Bad_side_unknown_table_and_unknown_export_are_errors()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await GetJsonAsync("/api/catalog/xyz")).Status);
        var (status, body) = await GetJsonAsync("/api/catalog/src/table/dbo.NOPE");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", (string?)body!["error"]);
        Assert.Equal(HttpStatusCode.NotFound, (await GetJsonAsync("/api/export/nope")).Status);
    }

    [Fact]
    public async Task Rediscover_needs_both_connections()
    {
        var (status, body) = await _server.SendAsync(HttpMethod.Post, "/api/rediscover");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_connections", (string?)body!["error"]);
    }

    [Fact]
    public async Task Export_analysis_downloads_and_saves_a_copy()
    {
        using var get = await _server.Client.GetAsync("/api/export/analysis");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("text/html", get.Content.Headers.ContentType!.MediaType);
        Assert.Contains("analysis-v0.html", get.Content.Headers.ContentDisposition!.ToString());
        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("window.DBM_EXPORT = {\"view\":\"analysis\"", html);
        Assert.Contains("DBM.views.analysis", html);
        Assert.Contains("DBM.graph = { layout: layout, fkSvg: fkSvg }", html);
        Assert.True(File.Exists(Path.Combine(_project.Ws.ExportsDir, "analysis-v0.html")));

        var (status, body) = await _server.SendAsync(HttpMethod.Post, "/api/export/analysis");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(".dbmigrate/exports/analysis-v0.html", (string?)body!["path"]);
        Assert.StartsWith("/api/export/analysis?t=", (string?)body["download"]);
    }

    [Fact]
    public async Task Requests_without_the_token_are_rejected()
    {
        using var anonymous = _server.Anonymous();
        using var response = await anonymous.GetAsync("/api/catalog/src");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Write the failing graph test**

`ENGINE/Dbm.Tests/js/graph.test.cjs`
```js
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'graph.js'), 'utf8'));
const G = globalThis.DBM.graph;

const shopV2 = {
  nodes: ['app.Customers', 'app.Addresses', 'app.Products', 'app.Orders', 'app.OrderLines', 'app.AuditEvents'].map((id) => ({ id })),
  edges: [
    { from: 'app.Customers', to: 'app.Addresses' },
    { from: 'app.Addresses', to: 'app.Customers' },
    { from: 'app.Orders', to: 'app.Customers' },
    { from: 'app.Orders', to: 'app.Addresses' },
    { from: 'app.OrderLines', to: 'app.Orders' },
    { from: 'app.OrderLines', to: 'app.Products' },
  ],
};

function finite(result) {
  for (const [id, p] of Object.entries(result.pos)) {
    for (const k of ['x', 'y', 'layer', 'order']) assert.ok(Number.isFinite(p[k]), `${id}.${k} = ${p[k]}`);
  }
  assert.ok(Number.isFinite(result.width) && Number.isFinite(result.height));
}

test('parents are layered above their children', () => {
  const r = G.layout(
    [{ id: 'a' }, { id: 'b' }, { id: 'c' }, { id: 'd' }],
    [{ from: 'b', to: 'a' }, { from: 'c', to: 'b' }, { from: 'd', to: 'a' }, { from: 'c', to: 'd' }]);
  assert.equal(r.pos.a.layer, 0);
  assert.equal(r.pos.b.layer, 1);
  assert.equal(r.pos.d.layer, 1);
  assert.equal(r.pos.c.layer, 2);
  assert.ok(r.pos.a.y < r.pos.b.y && r.pos.b.y < r.pos.c.y);
  finite(r);
});

test('the ShopV2 graph with its Customers <-> Addresses cycle lays out in 4 layers', () => {
  const r = G.layout(shopV2.nodes, shopV2.edges);
  assert.equal(r.layers.length, 4);
  assert.ok(r.pos['app.Orders'].layer > r.pos['app.Customers'].layer);
  assert.ok(r.pos['app.Orders'].layer > r.pos['app.Addresses'].layer);
  assert.ok(r.pos['app.OrderLines'].layer > r.pos['app.Orders'].layer);
  assert.ok(r.pos['app.OrderLines'].layer > r.pos['app.Products'].layer);
  assert.equal(Object.keys(r.pos).length, 6);
  finite(r);
});

test('self loops, duplicate and unknown edges are ignored', () => {
  const r = G.layout([{ id: 'x' }, { id: 'y' }, { id: 'x' }],
    [{ from: 'x', to: 'x' }, { from: 'y', to: 'x' }, { from: 'y', to: 'x' }, { from: 'y', to: 'nope' }]);
  assert.deepEqual(Object.keys(r.pos).sort(), ['x', 'y']);
  assert.equal(r.pos.y.layer, 1);
  finite(r);
});

test('layout is deterministic regardless of input order', () => {
  const a = G.layout(shopV2.nodes, shopV2.edges);
  const b = G.layout([...shopV2.nodes].reverse(), [...shopV2.edges].reverse());
  assert.equal(JSON.stringify(a), JSON.stringify(G.layout(shopV2.nodes, shopV2.edges)));
  assert.equal(JSON.stringify(a.pos), JSON.stringify(b.pos));
});

test('nodes of one layer do not overlap and fit inside the width', () => {
  const r = G.layout(shopV2.nodes, shopV2.edges, { nodeW: 100, gapX: 10 });
  for (const row of r.layers) {
    const xs = row.map((id) => r.pos[id].x).sort((p, q) => p - q);
    for (let i = 1; i < xs.length; i++) assert.ok(xs[i] - xs[i - 1] >= 110 - 0.01);
    for (const id of row) assert.ok(r.pos[id].x >= 0 && r.pos[id].x + 100 <= r.width + 0.01);
  }
});

test('an empty graph has no positions', () => {
  const r = G.layout([], []);
  assert.deepEqual(r.pos, {});
  assert.equal(r.layers.length, 0);
  finite(r);
});

test('a large random graph stays finite', () => {
  const nodes = Array.from({ length: 300 }, (_, i) => ({ id: 'T' + i }));
  const edges = [];
  let seed = 7;
  const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
  for (let i = 0; i < 600; i++) edges.push({ from: 'T' + Math.floor(rnd() * 300), to: 'T' + Math.floor(rnd() * 300) });
  finite(G.layout(nodes, edges));
});
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Web"`
Expected: build FAILS with `error CS0103: The name 'CatalogEndpoints' does not exist in the current context` (and `DirectoryAssets`/`WebExport` not found).

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/graph.test.cjs`
Expected: FAIL with `Error: ENOENT: no such file or directory, open '…/Dbm/wwwroot/js/lib/graph.js'`.

- [ ] **Step 4: Implement the catalog endpoints**

`ENGINE/Dbm/Web/Endpoints/CatalogEndpoints.cs`
```csharp
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>Row of GET /api/catalog/{side}. Refs = distinct referenced table keys (FK targets), used for the FK graph.</summary>
public sealed record TableListItem(string Key, long Rows, double SizeMb, int Columns, bool HasPk, bool IsHeap, int FkOut, int FkIn,
    int Triggers, List<string> Refs);

public sealed record ReferencedBy(string Table, string ForeignKey, List<string> Columns);

/// <summary>Body of GET /api/catalog/{side}/table/{key}: the TableInfo (with profiles) plus derived facts the UI needs.</summary>
public sealed record TableDetail(string Side, string Key, TableInfo Table, IReadOnlyList<string>? BestKey, bool IsHeap,
    List<ReferencedBy> ReferencedBy);

public static class CatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        app.MapGet("/api/catalog/{side}", (string side) =>
        {
            if (!TryParseSide(side, out var s)) return BadSide();
            var snapshot = state.Services.Catalog.Get(s);
            return snapshot is null ? NoCatalog() : Results.Json(ListTables(snapshot), Json.Options);
        });

        app.MapGet("/api/catalog/{side}/table/{key}", (string side, string key) =>
        {
            if (!TryParseSide(side, out var s)) return BadSide();
            var snapshot = state.Services.Catalog.Get(s);
            if (snapshot is null) return NoCatalog();
            var detail = Detail(s, snapshot, key);
            return detail is null
                ? Error(404, "not_found", $"No table '{key}' in the {side} catalog.")
                : Results.Json(detail, Json.Options);
        });

        app.MapPost("/api/rediscover", () =>
        {
            if (!state.Services.Connections.Has(Side.Src) || !state.Services.Connections.Has(Side.Tgt))
                return Error(409, "no_connections", "Save both connection strings first.");
            try
            {
                state.Services.Workflow.Rediscover();
                return Results.Json(new { ok = true }, Json.Options);
            }
            catch (WorkflowException ex)
            {
                return Error(409, "workflow", ex.Message);
            }
        });

        app.MapGet("/api/search", (string? q, string? side, string? kind, int? k) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Error(400, "bad_request", "q is required");
            Side? only = null;
            if (!string.IsNullOrEmpty(side))
            {
                if (!TryParseSide(side, out var s)) return BadSide();
                only = s;
            }
            var rows = state.Services.Catalog.Vectors();
            if (rows.Count == 0) return NoCatalog();
            var hits = VectorIndex.FromRows(rows, Synonyms.ForProject(state.Services.Ws))
                .Search(q, only, kind, Math.Clamp(k ?? 8, 1, 50))
                .Select(h => new { side = EnumText.ToText(h.Side), kind = h.Kind, key = h.Key, score = Math.Round(h.Score, 3), text = h.Text })
                .ToList();
            return Results.Json(hits, Json.Options);
        });
    }

    public static List<TableListItem> ListTables(CatalogSnapshot snapshot)
    {
        var fkIn = snapshot.Tables
            .SelectMany(t => t.ForeignKeys.Select(f => f.RefKey))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return snapshot.Tables.Select(t => new TableListItem(
            t.Key, t.Rows, t.SizeMb, t.Columns.Count, t.PrimaryKey is not null, t.IsHeap, t.ForeignKeys.Count,
            fkIn.GetValueOrDefault(t.Key), t.TriggerCount,
            t.ForeignKeys.Select(f => f.RefKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList();
    }

    public static TableDetail? Detail(Side side, CatalogSnapshot snapshot, string key)
    {
        var table = snapshot.FindTable(key);
        if (table is null) return null;
        var referencedBy = snapshot.Tables
            .SelectMany(t => t.ForeignKeys
                .Where(f => string.Equals(f.RefKey, table.Key, StringComparison.OrdinalIgnoreCase))
                .Select(f => new ReferencedBy(t.Key, f.Name, f.Columns)))
            .ToList();
        return new TableDetail(EnumText.ToText(side), table.Key, table, table.BestKey(), table.IsHeap, referencedBy);
    }

    public static bool TryParseSide(string text, out Side side)
    {
        side = Side.Src;
        if (string.Equals(text, "src", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(text, "tgt", StringComparison.OrdinalIgnoreCase)) return false;
        side = Side.Tgt;
        return true;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = code, message }, Json.Options, statusCode: status);

    private static IResult BadSide() => Error(400, "bad_request", "side must be src or tgt");

    private static IResult NoCatalog() => Error(404, "no_catalog", "No catalog yet; discovery has not completed.");
}
```

- [ ] **Step 5: Implement the standalone export**

`ENGINE/Dbm/Web/Endpoints/WebExport.cs`
```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dbm.Core;
using Dbm.Core.State;
using Microsoft.Extensions.FileProviders;

namespace Dbm.Web.Endpoints;

/// <summary>Read access to the UI assets (wwwroot) by '/'-separated relative path.</summary>
public interface IWebAssets
{
    string? Read(string path);
    IReadOnlyList<string> List(string directory);   // file names (not paths) of *.js files directly in the directory
}

public sealed class DirectoryAssets(string root) : IWebAssets
{
    public string Root { get; } = root;

    public string? Read(string path)
    {
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : null;
    }

    public IReadOnlyList<string> List(string directory)
    {
        var full = Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(full)
            ? Directory.GetFiles(full, "*.js").Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string>();
    }
}

public sealed class FileProviderAssets(IFileProvider provider) : IWebAssets
{
    public string? Read(string path)
    {
        var file = provider.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory) return null;
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public IReadOnlyList<string> List(string directory) =>
        provider.GetDirectoryContents(directory)
            .Where(f => !f.IsDirectory && f.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
}

public sealed class ExportException(string message) : Exception(message);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>Standalone, offline HTML exports (C9): index.html skeleton + inlined app.css, lib/*, components/*, the view and app.js.</summary>
public static class WebExport
{
    public static readonly string[] LibOrder = { "dom.js", "diff.js", "graph.js", "highlight.js" };
    public static readonly string[] ComponentOrder = { "core.js", "review.js" };

    private static readonly Regex ScriptTag = new(@"<script\b[^>]*\bsrc\s*=\s*[""'][^""']*[""'][^>]*>\s*</script>[ \t]*\r?\n?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StylesheetLink = new(@"<link\b[^>]*\brel\s*=\s*[""']stylesheet[""'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TitleTag = new(@"<title>[\s\S]*?</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // U+2028 / U+2029 are legal inside JSON strings but end a line in older JavaScript parsers.
    private static readonly string LineSeparator = ((char)0x2028).ToString();
    private static readonly string ParagraphSeparator = ((char)0x2029).ToString();

    /// <summary>The host's web root when it has index.html, else &lt;app dir&gt;/wwwroot, else the nearest ancestor's Dbm/wwwroot (dev/test runs).</summary>
    public static IWebAssets LocateAssets(IFileProvider? webRoot = null)
    {
        if (webRoot is not null && webRoot.GetFileInfo("index.html").Exists) return new FileProviderAssets(webRoot);
        var local = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(local, "index.html"))) return new DirectoryAssets(local);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Dbm", "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return new DirectoryAssets(candidate);
        }
        throw new ExportException("UI assets not found (wwwroot/index.html).");
    }

    public static IReadOnlyList<string> ScriptOrder(IWebAssets assets, string view)
    {
        var viewPath = $"js/views/{view}.js";
        if (assets.Read(viewPath) is null) throw new ExportException($"View script {viewPath} not found.");
        var scripts = new List<string>();
        scripts.AddRange(Ordered(assets.List("js/lib"), LibOrder).Select(n => "js/lib/" + n));
        scripts.AddRange(Ordered(assets.List("js/components"), ComponentOrder).Select(n => "js/components/" + n));
        scripts.Add(viewPath);
        scripts.Add("js/app.js");
        return scripts;
    }

    public static string BuildHtml(IWebAssets assets, string view, string title, JsonNode payload)
    {
        var index = assets.Read("index.html") ?? throw new ExportException("index.html not found.");
        var style = "<style>\n" + (assets.Read("css/app.css") ?? "") + "\n</style>";

        var html = ScriptTag.Replace(index, "");
        var styled = false;
        html = StylesheetLink.Replace(html, _ =>
        {
            if (styled) return "";
            styled = true;
            return style;
        });
        if (!styled) html = InsertBefore(html, "</head>", style + "\n");
        var titleTag = $"<title>{WebUtility.HtmlEncode(title)}</title>";
        html = TitleTag.IsMatch(html) ? TitleTag.Replace(html, _ => titleTag, 1) : InsertBefore(html, "</head>", titleTag + "\n");

        var scripts = new StringBuilder();
        var export = new JsonObject { ["view"] = view, ["title"] = title, ["payload"] = payload.DeepClone() };
        scripts.Append("<script>window.DBM_EXPORT = ").Append(SafeJson(export.ToJsonString(Json.Options))).Append(";</script>\n");
        foreach (var path in ScriptOrder(assets, view))
        {
            var js = assets.Read(path) ?? throw new ExportException($"{path} not found.");
            scripts.Append("<script>\n").Append(SafeScript(js)).Append("\n</script>\n");
        }
        return InsertBefore(html, "</body>", scripts.ToString());
    }

    /// <summary>Export of the Analysis phase's current version (payload + both catalogs, so the drill-down works offline).</summary>
    public static ExportFile Analysis(DbmServices services, IWebAssets assets)
    {
        var current = services.Phases.Get(PhaseName.Analysis).CurrentVersion;
        var artifact = (current is { } v ? services.Artifacts.Get(PhaseName.Analysis, v) : null)
            ?? services.Artifacts.Latest(PhaseName.Analysis)
            ?? throw new ExportException("No analysis artifact yet.");
        var title = $"{services.Project.Get().Name} — Analysis v{artifact.Version}";
        var html = BuildHtml(assets, "analysis", title, AnalysisData(services, artifact));
        return new ExportFile($"analysis-v{artifact.Version}.html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    }

    /// <summary>{project, exportedAt, artifact:{version,author,summary,createdAt,payload}, catalog:{src,tgt}, tables:{src:{key:TableDetail},tgt:{…}}}.</summary>
    public static JsonObject AnalysisData(DbmServices services, ArtifactRow artifact)
    {
        var catalog = new JsonObject();
        var tables = new JsonObject();
        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            var text = EnumText.ToText(side);
            var snapshot = services.Catalog.Get(side);
            catalog[text] = Json.ToNode(snapshot is null ? new List<TableListItem>() : CatalogEndpoints.ListTables(snapshot));
            var details = new JsonObject();
            foreach (var table in snapshot?.Tables ?? new List<Dbm.Core.Catalog.TableInfo>())
                details[table.Key] = Json.ToNode(CatalogEndpoints.Detail(side, snapshot!, table.Key));
            tables[text] = details;
        }
        return new JsonObject
        {
            ["project"] = services.Project.Get().Name,
            ["exportedAt"] = Clock.NowText(),
            ["artifact"] = new JsonObject
            {
                ["version"] = artifact.Version,
                ["author"] = artifact.Author,
                ["summary"] = artifact.Summary,
                ["createdAt"] = artifact.CreatedAt.ToString("O"),
                ["payload"] = JsonNode.Parse(artifact.PayloadJson),
            },
            ["catalog"] = catalog,
            ["tables"] = tables,
        };
    }

    public static string SaveCopy(Workspace ws, ExportFile file)
    {
        Directory.CreateDirectory(ws.ExportsDir);
        var path = Path.Combine(ws.ExportsDir, file.FileName);
        File.WriteAllBytes(path, file.Content);
        return path;
    }

    private static IEnumerable<string> Ordered(IReadOnlyList<string> names, string[] preferred) =>
        preferred.Where(p => names.Contains(p, StringComparer.Ordinal))
            .Concat(names.Where(n => !preferred.Contains(n, StringComparer.Ordinal)).OrderBy(n => n, StringComparer.Ordinal));

    private static string InsertBefore(string html, string marker, string text)
    {
        var i = html.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html + text : html.Insert(i, text);
    }

    private static string SafeScript(string js) => js.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string SafeJson(string json) =>
        json.Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(LineSeparator, "\\u2028", StringComparison.Ordinal)
            .Replace(ParagraphSeparator, "\\u2029", StringComparison.Ordinal);
}
```

`ENGINE/Dbm/Web/Endpoints/ExportEndpoints.cs`
```csharp
using Dbm.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Dbm.Web.Endpoints;

/// <summary>
/// GET /api/export/{what} → the file as a download; POST /api/export/{what} → {ok, file, path, download}.
/// Both save a copy under .dbmigrate/exports/. Later milestones add kinds with <see cref="Register"/> (e.g. "mapping", "sql", "report", "sqlpack").
/// </summary>
public static class ExportEndpoints
{
    public delegate Task<ExportFile> Builder(WebState state, IWebAssets assets, CancellationToken ct);

    private static readonly Dictionary<string, Builder> Builders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["analysis"] = (state, assets, _) => Task.FromResult(WebExport.Analysis(state.Services, assets)),
    };

    public static void Register(string what, Builder builder)
    {
        lock (Builders) Builders[what] = builder;
    }

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        var webRoot = app.ServiceProvider.GetService<IWebHostEnvironment>()?.WebRootFileProvider;

        app.MapGet("/api/export/{what}", async (string what, CancellationToken ct) =>
        {
            var (file, _, error) = await BuildAsync(state, webRoot, what, ct);
            return file is null ? error! : Results.File(file.Content, file.ContentType, file.FileName);
        });

        app.MapPost("/api/export/{what}", async (string what, CancellationToken ct) =>
        {
            var (file, path, error) = await BuildAsync(state, webRoot, what, ct);
            if (file is null) return error!;
            return Results.Json(new
            {
                ok = true,
                file = file.FileName,
                path = state.Services.Ws.Relative(path!),
                download = $"/api/export/{Uri.EscapeDataString(what)}?t={Uri.EscapeDataString(state.Info.Token)}",
            }, Json.Options);
        });
    }

    private static async Task<(ExportFile? File, string? Path, IResult? Error)> BuildAsync(WebState state, IFileProvider? webRoot,
        string what, CancellationToken ct)
    {
        Builder? builder;
        lock (Builders) Builders.TryGetValue(what, out builder);
        if (builder is null)
            return (null, null, Results.Json(new { error = "not_found", message = $"Unknown export '{what}'." }, Json.Options, statusCode: 404));
        try
        {
            var file = await builder(state, WebExport.LocateAssets(webRoot), ct);
            return (file, WebExport.SaveCopy(state.Services.Ws, file), null);
        }
        catch (ExportException ex)
        {
            return (null, null, Results.Json(new { error = "export_unavailable", message = ex.Message }, Json.Options, statusCode: 404));
        }
    }
}
```

In `ENGINE/Dbm/Web/Endpoints/EndpointRegistry.cs`, replace the comment line
```csharp
        // T2.8: CatalogEndpoints.Map(app, state); ExportEndpoints.Map(app, state);
```
with
```csharp
        CatalogEndpoints.Map(app, state);   // T2.8
        ExportEndpoints.Map(app, state);    // T2.8
```

- [ ] **Step 6: Implement the graph library**

`ENGINE/Dbm/wwwroot/js/lib/graph.js`
```js
/* DBM.graph — layered layout + FK graph SVG (no dependencies; layout also runs under Node for tests). */
(function (DBM) {
  'use strict';

  var DEFAULTS = { nodeW: 176, nodeH: 44, gapX: 28, gapY: 56, margin: 16, viewWidth: 1100, viewHeight: 520 };
  var uid = 0;

  function opt(opts, key) { return opts && opts[key] != null ? opts[key] : DEFAULTS[key]; }
  function cmp(a, b) { return a < b ? -1 : a > b ? 1 : 0; }
  function r1(v) { return Math.round(v * 10) / 10; }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function ellipsis(s, n) { s = String(s); return s.length > n ? s.slice(0, n - 1) + '…' : s; }

  /**
   * Pure layered layout. nodes: [{id}], edges: [{from, to}] where from = FK owner (child) and to = referenced table (parent).
   * Cycle edges are ignored for layering (DFS back edges), parents are placed above children (longest-path layering), then two
   * barycenter passes (down by parents, up by children) order each layer. Deterministic: ids are processed in sorted order.
   * Returns {pos: {id: {x, y, layer, order}}, width, height, layers, nodeW, nodeH}; x/y are the node box's top-left corner.
   */
  function layout(nodes, edges, opts) {
    var nodeW = opt(opts, 'nodeW'), nodeH = opt(opts, 'nodeH'), gapX = opt(opts, 'gapX'), gapY = opt(opts, 'gapY');
    var margin = opt(opts, 'margin');
    var ids = [], known = {};
    (nodes || []).forEach(function (n) {
      var id = String(n && n.id != null ? n.id : n);
      if (!known[id]) { known[id] = true; ids.push(id); }
    });
    ids.sort(cmp);
    if (!ids.length) return { pos: {}, width: 2 * margin, height: 2 * margin, layers: [], nodeW: nodeW, nodeH: nodeH };

    var parents = {}, dag = {}, children = {};
    ids.forEach(function (id) { parents[id] = []; dag[id] = []; children[id] = []; });
    var seen = {};
    (edges || []).forEach(function (e) {
      var a = String(e.from), b = String(e.to);
      var key = JSON.stringify([a, b]);
      if (a === b || !known[a] || !known[b] || seen[key]) return;
      seen[key] = true;
      parents[a].push(b);
    });
    ids.forEach(function (id) { parents[id].sort(cmp); });

    // 1. cycle removal: iterative DFS along child -> parent edges; an edge back to a node on the current path is dropped.
    var state = {};
    ids.forEach(function (root) {
      if (state[root]) return;
      var stack = [{ id: root, i: 0 }];
      state[root] = 1;
      while (stack.length) {
        var top = stack[stack.length - 1];
        var ps = parents[top.id];
        if (top.i < ps.length) {
          var p = ps[top.i++];
          if (state[p] === 1) continue;
          dag[top.id].push(p);
          if (!state[p]) { state[p] = 1; stack.push({ id: p, i: 0 }); }
        } else {
          state[top.id] = 2;
          stack.pop();
        }
      }
    });
    ids.forEach(function (id) { dag[id].forEach(function (p) { children[p].push(id); }); });

    // 2. longest-path layering (Kahn order over the acyclic edges): layer(child) = 1 + max(layer(parent)).
    var layer = {}, pending = {}, queue = [];
    ids.forEach(function (id) { layer[id] = 0; pending[id] = dag[id].length; if (!pending[id]) queue.push(id); });
    for (var qi = 0; qi < queue.length; qi++) {
      var n = queue[qi];
      children[n].forEach(function (c) {
        layer[c] = Math.max(layer[c], layer[n] + 1);
        if (--pending[c] === 0) queue.push(c);
      });
    }
    var maxLayer = 0;
    ids.forEach(function (id) { maxLayer = Math.max(maxLayer, layer[id]); });
    var layers = [];
    for (var l = 0; l <= maxLayer; l++) layers.push([]);
    ids.forEach(function (id) { layers[layer[id]].push(id); });

    // 3. ordering: centred index per layer, one barycenter pass down (by parents) and one up (by children).
    var pos = {};
    function place(row) { row.forEach(function (id, i) { pos[id] = i - (row.length - 1) / 2; }); }
    layers.forEach(place);
    function sweep(row, neighbours) {
      var bary = {};
      row.forEach(function (id) {
        var ns = neighbours[id];
        if (!ns.length) { bary[id] = pos[id]; return; }
        var s = 0;
        ns.forEach(function (x) { s += pos[x]; });
        bary[id] = s / ns.length;
      });
      row.sort(function (a, b) { return (bary[a] - bary[b]) || (pos[a] - pos[b]) || cmp(a, b); });
      place(row);
    }
    for (l = 1; l <= maxLayer; l++) sweep(layers[l], dag);
    for (l = maxLayer - 1; l >= 0; l--) sweep(layers[l], children);

    // 4. coordinates: every layer centred on the widest one.
    var widest = 0;
    layers.forEach(function (row) { widest = Math.max(widest, row.length); });
    var width = 2 * margin + widest * nodeW + (widest - 1) * gapX;
    var height = 2 * margin + layers.length * nodeH + (layers.length - 1) * gapY;
    var out = {};
    layers.forEach(function (row, li) {
      var rowW = row.length * nodeW + (row.length - 1) * gapX;
      var x0 = (width - rowW) / 2;
      row.forEach(function (id, i) {
        out[id] = { x: r1(x0 + i * (nodeW + gapX)), y: r1(margin + li * (nodeH + gapY)), layer: li, order: i };
      });
    });
    return { pos: out, width: r1(width), height: r1(height), layers: layers, nodeW: nodeW, nodeH: nodeH };
  }

  /**
   * FK graph as an SVG element. nodes: [{id, label?, sub?, title?, cls?}], edges: [{from, to}] (child -> parent).
   * opts: layout options + {label, onClick(id), viewWidth, viewHeight, document}. Drag pans (viewBox), hover/focus highlights a node's
   * edges and neighbours, Enter/Space or click calls onClick. The element gets svg.dbmFit(full) to toggle whole-graph / 1:1 view.
   */
  function fkSvg(nodes, edges, opts) {
    opts = opts || {};
    var doc = opts.document || document;
    var NS = 'http://www.w3.org/2000/svg';
    var L = layout(nodes, edges, opts);
    var id = 'fkg' + (++uid);
    var w = L.nodeW, h = L.nodeH;
    var fullW = Math.max(L.width, 1), fullH = Math.max(L.height, 1);
    var viewW = Math.min(fullW, opt(opts, 'viewWidth')), viewH = Math.min(fullH, opt(opts, 'viewHeight'));
    var vb = { x: 0, y: 0, w: viewW, h: viewH };
    var byId = {};
    (nodes || []).forEach(function (n) { byId[String(n.id)] = n; });

    function el(tag, attrs, parent) {
      var e = doc.createElementNS(NS, tag);
      Object.keys(attrs || {}).forEach(function (k) { if (attrs[k] != null) e.setAttribute(k, String(attrs[k])); });
      if (parent) parent.appendChild(e);
      return e;
    }
    function setViewBox() { svg.setAttribute('viewBox', [r1(vb.x), r1(vb.y), r1(vb.w), r1(vb.h)].join(' ')); }

    var svg = el('svg', { class: 'fkg', role: 'group', 'aria-label': opts.label || 'Foreign-key graph', preserveAspectRatio: 'xMidYMin meet' });
    svg.style.height = viewH + 'px';
    setViewBox();
    var defs = el('defs', {}, svg);
    var marker = el('marker', { id: id + '-arrow', viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' }, defs);
    el('path', { d: 'M0,0 L10,5 L0,10 z', class: 'fkg-arrowhead' }, marker);
    var gEdges = el('g', { class: 'fkg-edges' }, svg);
    var gNodes = el('g', { class: 'fkg-nodes' }, svg);

    var edgeEls = [], nodeEls = {};
    (edges || []).forEach(function (e) {
      var from = String(e.from), to = String(e.to), a = L.pos[from], b = L.pos[to];
      if (!a || !b) return;
      var d, cycle = false;
      if (from === to) {
        var sx = a.x + w, sy = a.y + h * 0.3, ey = a.y + h * 0.7;
        d = 'M' + sx + ',' + sy + ' C' + (sx + 36) + ',' + (sy - 18) + ' ' + (sx + 36) + ',' + (ey + 18) + ' ' + sx + ',' + ey;
        cycle = true;
      } else if (a.layer > b.layer) {
        var x1 = a.x + w / 2, y1 = a.y, x2 = b.x + w / 2, y2 = b.y + h, my = (y1 + y2) / 2;
        d = 'M' + x1 + ',' + y1 + ' C' + x1 + ',' + my + ' ' + x2 + ',' + my + ' ' + x2 + ',' + y2;
      } else {
        var cx = a.x + w, cy = a.y + h / 2, tx = b.x + w, ty = b.y + h / 2, bulge = 40 + Math.abs(cy - ty) * 0.25;
        d = 'M' + cx + ',' + cy + ' C' + (cx + bulge) + ',' + cy + ' ' + (tx + bulge) + ',' + ty + ' ' + tx + ',' + ty;
        cycle = true;
      }
      var path = el('path', { d: d, class: 'fkg-edge' + (cycle ? ' fkg-edge-cycle' : ''), 'marker-end': 'url(#' + id + '-arrow)', 'data-from': from, 'data-to': to }, gEdges);
      edgeEls.push(path);
    });

    function highlight(nid, on) {
      svg.classList.toggle('fkg-dim', on);
      edgeEls.forEach(function (p) {
        var f = p.getAttribute('data-from'), t = p.getAttribute('data-to');
        var hit = on && (f === nid || t === nid);
        p.classList.toggle('fkg-hi', hit);
        if (hit) { nodeEls[f].classList.add('fkg-hi'); nodeEls[t].classList.add('fkg-hi'); }
      });
      if (!on) Object.keys(nodeEls).forEach(function (k) { nodeEls[k].classList.remove('fkg-hi'); });
      else nodeEls[nid].classList.add('fkg-hi');
    }

    var moved = false;
    Object.keys(L.pos).sort(cmp).forEach(function (nid) {
      var p = L.pos[nid], n = byId[nid] || {};
      var g = el('g', {
        class: 'fkg-node' + (n.cls ? ' ' + n.cls : ''), transform: 'translate(' + p.x + ',' + p.y + ')', tabindex: 0, role: 'button',
        'data-id': nid, 'aria-label': (n.label || nid) + (n.sub ? ', ' + n.sub : '')
      }, gNodes);
      el('rect', { width: w, height: h, rx: 8, ry: 8 }, g);
      el('text', { x: 12, y: n.sub ? 18 : h / 2 + 4, class: 'fkg-label' }, g).textContent = ellipsis(n.label || nid, 24);
      if (n.sub) el('text', { x: 12, y: 34, class: 'fkg-sub' }, g).textContent = ellipsis(n.sub, 28);
      el('title', {}, g).textContent = n.title || n.label || nid;
      nodeEls[nid] = g;
      g.addEventListener('mouseenter', function () { highlight(nid, true); });
      g.addEventListener('mouseleave', function () { highlight(nid, false); });
      g.addEventListener('focus', function () { highlight(nid, true); });
      g.addEventListener('blur', function () { highlight(nid, false); });
      g.addEventListener('click', function () { if (!moved && opts.onClick) opts.onClick(nid); });
      g.addEventListener('keydown', function (ev) {
        if ((ev.key === 'Enter' || ev.key === ' ') && opts.onClick) { ev.preventDefault(); opts.onClick(nid); }
      });
    });

    var drag = null;
    svg.addEventListener('pointerdown', function (ev) {
      if (ev.button !== 0) return;
      drag = { x: ev.clientX, y: ev.clientY, vx: vb.x, vy: vb.y, pointer: ev.pointerId };
      moved = false;
    });
    svg.addEventListener('pointermove', function (ev) {
      if (!drag) return;
      var rect = svg.getBoundingClientRect();
      var scale = Math.max(vb.w / Math.max(rect.width, 1), vb.h / Math.max(rect.height, 1));
      var dx = (ev.clientX - drag.x) * scale, dy = (ev.clientY - drag.y) * scale;
      if (!moved && Math.abs(dx) + Math.abs(dy) < 4 * scale) return;
      if (!moved) { moved = true; svg.classList.add('is-panning'); if (svg.setPointerCapture) svg.setPointerCapture(drag.pointer); }
      vb.x = clamp(drag.vx - dx, Math.min(0, fullW - vb.w), Math.max(0, fullW - vb.w));
      vb.y = clamp(drag.vy - dy, Math.min(0, fullH - vb.h), Math.max(0, fullH - vb.h));
      setViewBox();
    });
    function endDrag() {
      if (drag && moved && svg.releasePointerCapture && svg.hasPointerCapture && svg.hasPointerCapture(drag.pointer)) svg.releasePointerCapture(drag.pointer);
      drag = null;
      svg.classList.remove('is-panning');
      setTimeout(function () { moved = false; }, 0);
    }
    svg.addEventListener('pointerup', endDrag);
    svg.addEventListener('pointercancel', endDrag);

    svg.dbmFit = function (full) {
      vb = full ? { x: 0, y: 0, w: fullW, h: fullH } : { x: 0, y: 0, w: viewW, h: viewH };
      svg.style.height = (full ? Math.min(fullH, opt(opts, 'viewHeight')) : viewH) + 'px';
      setViewBox();
    };
    svg.dbmLayout = L;
    return svg;
  }

  DBM.graph = { layout: layout, fkSvg: fkSvg };
})(globalThis.DBM = globalThis.DBM || {});
```

- [ ] **Step 7: Implement the Analysis view**

`ENGINE/Dbm/wwwroot/js/views/analysis.js`
```js
/* Analysis view (T2.8): KPIs, source/target summaries, narrative, findings, tables with drill-down drawer, FK graph, export. */
(function (DBM) {
  'use strict';

  var SEVERITIES = ['critical', 'high', 'medium', 'low', 'info'];
  var GRAPH_MAX = 150;
  var ui = { sev: 'all', q: '', tab: 'src', tableQ: '', graph: 'tgt', graphFit: false };   // survives re-renders
  var tableCache = {};

  // ---------- small helpers ----------

  function el(tag, attrs) {
    var node = DBM.h(tag, attrs || {});
    for (var i = 2; i < arguments.length; i++) add(node, arguments[i]);
    return node;
  }
  function add(node, kid) {
    if (kid == null || kid === false) return;
    if (Array.isArray(kid)) { kid.forEach(function (k) { add(node, k); }); return; }
    node.appendChild(typeof kid === 'string' || typeof kid === 'number' ? document.createTextNode(String(kid)) : kid);
  }
  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }
  function num(n) { return n == null ? '—' : Number(n).toLocaleString('en-US'); }
  function mb(v) {
    if (v == null) return '—';
    if (v === 0) return '0 MB';
    if (v < 1) return v.toFixed(2) + ' MB';
    if (v < 1024) return v.toFixed(1) + ' MB';
    return (v / 1024).toFixed(2) + ' GB';
  }
  function pct(r) { return r === 0 ? '0%' : r < 0.01 ? '<1%' : Math.round(r * 100) + '%'; }
  function minutes(m) { return m == null ? '—' : m < 1 ? '< 1 min' : m < 60 ? m.toFixed(1) + ' min' : (m / 60).toFixed(1) + ' h'; }
  function cap(s) { return s.charAt(0).toUpperCase() + s.slice(1); }
  function isExport() { return typeof window !== 'undefined' && !!window.DBM_EXPORT; }
  function exportData() { return isExport() ? window.DBM_EXPORT.payload : null; }
  function api(ctx) { return (ctx && ctx.api) || DBM.api; }
  function toast(ctx, msg, kind) {
    if (ctx && typeof ctx.toast === 'function') ctx.toast(msg, kind);
    else if (DBM.components && DBM.components.toast) DBM.components.toast(msg, kind);
  }
  function commentable(ctx, node, anchor, label) {
    if (!isExport() && ctx && typeof ctx.commentable === 'function') ctx.commentable(node, anchor, label);
    return node;
  }
  function tag(text, extra) { return el('span', { class: 'tag' + (extra ? ' ' + extra : '') }, text); }
  function sevBadge(sev) { return el('span', { class: 'badge sev-' + sev }, sev); }
  function bar(value, label) {
    var b = el('span', { class: 'bar ana-bar', role: 'img', 'aria-label': label });
    b.style.setProperty('--v', String(Math.max(0, Math.min(1, value))));
    return b;
  }
  function th(text, cls) { return el('th', { scope: 'col', class: cls || '' }, text); }

  function payloadOf(ctx) {
    var d = exportData();
    if (d && d.artifact) return d.artifact.payload;
    var a = ctx && ctx.artifact;
    if (!a) return null;
    if (a.payload) return a.payload;
    if (a.current && a.current.payload) return a.current.payload;
    return a.findings ? a : null;
  }
  function artifactMeta(ctx) {
    var d = exportData();
    if (d && d.artifact) return d.artifact;
    var a = ctx && ctx.artifact;
    if (!a) return null;
    return a.current || a;
  }
  function typeDisplay(c) {
    var t = c.dataType;
    if (['char', 'varchar', 'nchar', 'nvarchar', 'binary', 'varbinary'].indexOf(t) >= 0) return t + '(' + (c.maxLength === -1 ? 'max' : c.maxLength) + ')';
    if (t === 'decimal' || t === 'numeric') return t + '(' + c.precision + ',' + c.scale + ')';
    if (t === 'datetime2' || t === 'datetimeoffset' || t === 'time') return t + '(' + c.scale + ')';
    if (t === 'timestamp') return 'rowversion';
    return t;
  }
  function countBySeverity(findings) {
    var counts = { critical: 0, high: 0, medium: 0, low: 0, info: 0 };
    Object.keys(findings || {}).forEach(function (id) { counts[findings[id].severity] = (counts[findings[id].severity] || 0) + 1; });
    return counts;
  }
  function anchorTarget(anchor) {
    var m = /^(table|column):(src|tgt):(.+)$/.exec(anchor || '');
    if (!m) return null;
    if (m[1] === 'table') return { side: m[2], key: m[3] };
    var i = m[3].lastIndexOf('.');
    return { side: m[2], key: m[3].slice(0, i), column: m[3].slice(i + 1) };
  }
  function findingsByTable(findings) {
    var rank = { critical: 0, high: 1, medium: 2, low: 3, info: 4 };
    var map = {};
    Object.keys(findings || {}).forEach(function (id) {
      var f = findings[id], t = anchorTarget(f.anchor);
      if (!t) return;
      var k = t.side + ':' + t.key, e = map[k] || (map[k] = { count: 0, worst: 'info' });
      e.count++;
      if (rank[f.severity] < rank[e.worst]) e.worst = f.severity;
    });
    return map;
  }

  // mdLite: paragraphs, "- " / "* " bullets, **bold**, `code` — builds DOM, never HTML strings.
  function inline(text) {
    var out = [], re = /(\*\*[^*]+\*\*|`[^`]+`)/g, last = 0, m;
    text = String(text || '');
    while ((m = re.exec(text))) {
      if (m.index > last) out.push(text.slice(last, m.index));
      out.push(m[0].charAt(0) === '`' ? el('code', { class: 'mono' }, m[0].slice(1, -1)) : el('strong', {}, m[0].slice(2, -2)));
      last = re.lastIndex;
    }
    if (last < text.length) out.push(text.slice(last));
    return out;
  }
  function md(text) {
    var root = el('div', { class: 'ana-md' }), para = [], list = null;
    function flush() { if (para.length) { root.appendChild(el('p', {}, inline(para.join(' ')))); para = []; } }
    String(text || '').split(/\r?\n/).forEach(function (line) {
      var bullet = /^\s*[-*]\s+(.*)$/.exec(line);
      if (bullet) { flush(); if (!list) { list = el('ul', { class: 'ana-list' }); root.appendChild(list); } list.appendChild(el('li', {}, inline(bullet[1]))); return; }
      list = null;
      if (!line.trim()) { flush(); return; }
      para.push(line.trim());
    });
    flush();
    return root;
  }
  function list(tagName, items) {
    var l = el(tagName, { class: 'ana-list' });
    (items || []).forEach(function (t) { l.appendChild(el('li', {}, inline(t))); });
    return l;
  }

  // ---------- data ----------

  function loadCatalog(ctx) {
    var d = exportData();
    if (d) return Promise.resolve(d.catalog || { src: [], tgt: [] });
    return Promise.all([api(ctx).get('/api/catalog/src'), api(ctx).get('/api/catalog/tgt')])
      .then(function (r) { return { src: r[0] || [], tgt: r[1] || [] }; });
  }
  function loadTable(ctx, side, key) {
    var d = exportData();
    if (d) return Promise.resolve(d.tables && d.tables[side] ? d.tables[side][key] || null : null);
    var k = side + ':' + key;
    if (tableCache[k]) return Promise.resolve(tableCache[k]);
    return api(ctx).get('/api/catalog/' + side + '/table/' + encodeURIComponent(key)).then(function (t) { tableCache[k] = t; return t; });
  }

  // ---------- actions ----------

  function rediscover(ctx) {
    DBM.components.modal({
      title: 'Re-run discovery?',
      body: 'Both databases are extracted and profiled again. Analysis and every later phase become stale and need a new review.',
      confirmText: 'Re-run discovery'
    }).then(function (ok) {
      if (!ok) return null;
      return api(ctx).post('/api/rediscover').then(function () {
        toast(ctx, 'Discovery started — the analysis refreshes when it finishes.', 'ok');
        if (ctx && ctx.refresh) ctx.refresh();
      });
    }).catch(function (err) { toast(ctx, (err && err.message) || 'Could not start discovery', 'err'); });
  }
  function exportHtml(ctx, button) {
    button.classList.add('is-loading');
    button.disabled = true;
    api(ctx).post('/api/export/analysis').then(function (r) {
      toast(ctx, 'Saved ' + r.path, 'ok');
      var a = document.createElement('a');
      a.href = r.download;
      a.download = r.file;
      document.body.appendChild(a);
      a.click();
      a.remove();
    }).catch(function (err) {
      toast(ctx, 'Export failed: ' + ((err && err.message) || err), 'err');
    }).then(function () {
      button.classList.remove('is-loading');
      button.disabled = false;
    });
  }

  // ---------- sections ----------

  function header(ctx, p) {
    var meta = artifactMeta(ctx);
    var actions = el('div', { class: 'toolbar ana-actions' });
    if (!isExport()) {
      actions.appendChild(el('button', { class: 'btn btn-ghost btn-sm', type: 'button', on: { click: function () { rediscover(ctx); } } }, 'Re-run discovery'));
      actions.appendChild(el('button', { class: 'btn btn-sm', type: 'button', on: { click: function (ev) { exportHtml(ctx, ev.currentTarget); } } }, 'Export HTML'));
    }
    var sub = p.source.database + ' → ' + p.target.database;
    if (meta && meta.version != null) sub += ' · v' + meta.version + (meta.author ? ' by ' + meta.author : '');
    if (isExport() && exportData().exportedAt) sub += ' · exported ' + exportData().exportedAt.slice(0, 16).replace('T', ' ') + ' UTC';
    return el('div', { class: 'page-h ana-head' },
      el('div', { class: 'ana-title' }, el('h1', { class: 'h1' }, 'Analysis'), el('p', { class: 'muted small' }, sub)),
      el('div', { class: 'spacer' }),
      actions);
  }

  function kpiRow(p) {
    var c = countBySeverity(p.findings), k = DBM.components.kpi;
    var total = Object.keys(p.findings || {}).length;
    return el('div', { class: 'grid-kpi ana-kpis' },
      k('Tables', num(p.source.tables) + ' → ' + num(p.target.tables), num(p.source.columns) + ' → ' + num(p.target.columns) + ' columns'),
      k('Source rows', num(p.source.rows), mb(p.source.sizeMb)),
      k('Findings', num(total), c.critical + c.high ? c.critical + ' critical · ' + c.high + ' high' : 'no critical or high'),
      k('Est. transfer', minutes(p.estimates.estimatedMinutes), num(p.estimates.totalRows) + ' rows'),
      k('Target triggers', num(p.target.triggers), num(p.target.views) + ' views · ' + num(p.target.procedures) + ' procs'));
  }

  function dbCard(title, side, db, other) {
    var rows = [
      ['Server', db.server, 'mono'],
      ['Database', db.database, 'mono'],
      ['Version', db.version + (db.productVersion ? ' (' + db.productVersion + ')' : '')],
      ['Edition', db.edition],
      ['Collation', db.collation, 'mono', db.collation !== other.collation ? 'differs' : null],
      ['Compat level', String(db.compatLevel), 'num', side === 'tgt' && db.compatLevel < other.compatLevel ? 'lower than source' : null],
      ['Objects', num(db.tables) + ' tables · ' + num(db.columns) + ' columns · ' + num(db.views) + ' views · ' + num(db.procedures) + ' procs · ' + num(db.functions) + ' functions · ' + num(db.triggers) + ' triggers'],
      ['Data', num(db.rows) + ' rows · ' + mb(db.sizeMb)]
    ];
    var dl = el('dl', { class: 'ana-kv' });
    rows.forEach(function (r) {
      dl.appendChild(el('dt', {}, r[0]));
      dl.appendChild(el('dd', {}, el('span', { class: r[2] || '' }, r[1] == null || r[1] === '' ? '—' : r[1]), r[3] ? tag(r[3], 'ana-warn') : null));
    });
    return el('section', { class: 'card ana-db', 'aria-label': title + ' database' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, title), el('span', { class: 'pill' }, side)),
      el('div', { class: 'card-b' }, dl));
  }

  function narrativeCard(ctx, p) {
    var n = p.narrative, body = el('div', { class: 'card-b ana-narr' });
    if (!n) {
      body.appendChild(el('div', { class: 'empty' },
        el('p', {}, 'The schema-analyst has not written the summary yet.'),
        el('p', { class: 'muted small' }, 'It is drafted automatically while Claude is running the /db-migrate loop; otherwise run /db-migrate resume.')));
    } else {
      body.appendChild(md(n.summary));
      if (n.risks && n.risks.length) body.appendChild(el('div', { class: 'ana-sub' }, el('h3', { class: 'h3' }, 'Risks'), list('ul', n.risks)));
      if (n.recommendations && n.recommendations.length) body.appendChild(el('div', { class: 'ana-sub' }, el('h3', { class: 'h3' }, 'Recommendations'), list('ol', n.recommendations)));
    }
    var card = el('section', { class: 'card ana-narrative' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Summary & recommendations'), el('span', { class: 'muted small' }, n ? 'schema-analyst' : 'pending')),
      body);
    return commentable(ctx, card, 'narrative', 'Narrative');
  }

  function findingItem(ctx, id, f, drawer) {
    var t = anchorTarget(f.anchor);
    var obj = t
      ? el('button', { type: 'button', class: 'btn btn-ghost btn-sm mono ana-obj', title: 'Open ' + t.key, on: { click: function () { drawer.open(t.side, t.key, t.column); } } }, f.object)
      : el('span', { class: 'mono small ana-obj' }, f.object);
    var item = el('article', { class: 'ana-finding', role: 'listitem', data: { sev: f.severity } },
      el('div', { class: 'ana-finding-h' },
        sevBadge(f.severity),
        el('span', { class: 'mono small muted' }, id),
        el('span', { class: 'small ana-rule' }, f.rule + ' · ' + f.title),
        el('span', { class: 'pill' }, f.side),
        el('span', { class: 'spacer' }),
        obj),
      el('p', { class: 'ana-msg' }, f.message),
      f.commentary ? el('div', { class: 'ana-commentary' }, el('span', { class: 'small muted' }, 'Analyst'), el('p', {}, inline(f.commentary))) : null);
    return commentable(ctx, item, 'finding:' + id, 'Finding ' + id);
  }

  function findingsCard(ctx, p, drawer) {
    var entries = Object.keys(p.findings || {}).map(function (id) { return { id: id, f: p.findings[id] }; });
    var counts = countBySeverity(p.findings);
    var chips = el('div', { class: 'row-wrap ana-chips', role: 'group', 'aria-label': 'Filter by severity' });
    var search = el('input', { class: 'input ana-search', type: 'search', placeholder: 'Filter findings', 'aria-label': 'Filter findings' });
    var listNode = el('div', { class: 'ana-findings', role: 'list' });
    search.value = ui.q;
    if (ui.sev !== 'all' && !counts[ui.sev]) ui.sev = 'all';

    function renderChips() {
      clear(chips);
      ['all'].concat(SEVERITIES).forEach(function (s) {
        var n = s === 'all' ? entries.length : counts[s];
        if (s !== 'all' && !n) return;
        chips.appendChild(el('button', {
          type: 'button', class: 'chip' + (s === 'all' ? '' : ' ana-chip-' + s) + (ui.sev === s ? ' is-active' : ''),
          'aria-pressed': ui.sev === s ? 'true' : 'false',
          on: { click: function () { ui.sev = s; renderChips(); renderList(); } }
        }, (s === 'all' ? 'All' : cap(s)) + ' ', el('span', { class: 'num' }, String(n))));
      });
    }
    function renderList() {
      clear(listNode);
      var q = ui.q.trim().toLowerCase();
      var shown = entries.filter(function (e) {
        if (ui.sev !== 'all' && e.f.severity !== ui.sev) return false;
        return !q || (e.id + ' ' + e.f.rule + ' ' + e.f.title + ' ' + e.f.object + ' ' + e.f.message).toLowerCase().indexOf(q) >= 0;
      });
      if (!shown.length) { listNode.appendChild(el('p', { class: 'muted ana-none' }, entries.length ? 'No findings match the filter.' : 'No findings — nothing stood out.')); return; }
      shown.forEach(function (e) { listNode.appendChild(findingItem(ctx, e.id, e.f, drawer)); });
    }
    search.addEventListener('input', function () { ui.q = search.value; renderList(); });
    renderChips();
    renderList();
    return el('section', { class: 'card ana-findings-card' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Findings'), el('span', { class: 'muted small' }, num(entries.length) + ' from rule checks')),
      el('div', { class: 'card-b stack' }, el('div', { class: 'toolbar ana-filter' }, chips, el('span', { class: 'spacer' }), search), listNode));
  }

  function sideTabs(current, counts, onPick, label) {
    var tabs = el('div', { class: 'row ana-tabs', role: 'tablist', 'aria-label': label });
    [['src', 'Source'], ['tgt', 'Target']].forEach(function (s) {
      tabs.appendChild(el('button', {
        type: 'button', role: 'tab', class: 'chip' + (current === s[0] ? ' is-active' : ''),
        'aria-selected': current === s[0] ? 'true' : 'false',
        on: { click: function () { onPick(s[0]); } }
      }, s[1] + (counts ? ' ' + counts[s[0]] : '')));
    });
    return tabs;
  }

  function tablesCard(ctx, p, cat, drawer) {
    var byTable = findingsByTable(p.findings);
    var head = el('div', { class: 'toolbar ana-filter' });
    var filter = el('input', { class: 'input ana-search', type: 'search', placeholder: 'Filter tables', 'aria-label': 'Filter tables' });
    var wrap = el('div', { class: 'ana-tbl-wrap' });
    filter.value = ui.tableQ;
    filter.addEventListener('input', function () { ui.tableQ = filter.value; renderTable(); });

    function renderHead() {
      clear(head);
      head.appendChild(sideTabs(ui.tab, { src: cat.src.length, tgt: cat.tgt.length }, function (s) { ui.tab = s; renderHead(); renderTable(); }, 'Catalog side'));
      head.appendChild(el('span', { class: 'spacer' }));
      head.appendChild(filter);
    }
    function renderTable() {
      clear(wrap);
      var q = ui.tableQ.trim().toLowerCase();
      var rows = (cat[ui.tab] || []).filter(function (t) { return !q || t.key.toLowerCase().indexOf(q) >= 0; });
      if (!rows.length) { wrap.appendChild(el('p', { class: 'muted' }, 'No tables match.')); return; }
      var tbody = el('tbody');
      rows.forEach(function (t) {
        var side = ui.tab, fx = byTable[side + ':' + t.key];
        function open(ev) {
          if (ev && ev.target && ev.target.closest && ev.target.closest('button')) return;   // e.g. the comment button
          drawer.open(side, t.key);
        }
        var tr = el('tr', {
          class: 'tr-click', tabindex: 0, 'aria-label': 'Open ' + t.key,
          on: { click: open, keydown: function (ev) { if (ev.key === 'Enter') open(); } }
        },
          el('td', { class: 'mono ana-key' }, t.key),
          el('td', { class: 'num' }, num(t.rows)),
          el('td', { class: 'num' }, mb(t.sizeMb)),
          el('td', { class: 'num' }, String(t.columns)),
          el('td', {}, t.hasPk ? el('span', { class: 'small' }, 'PK') : tag('no PK', 'ana-warn')),
          el('td', { class: 'num' }, t.fkOut + ' / ' + t.fkIn),
          el('td', { class: 'num' }, t.triggers ? String(t.triggers) : '—'),
          el('td', { class: 'ana-flags' }, t.isHeap ? tag('heap') : null,
            fx ? el('span', { class: 'badge sev-' + fx.worst, title: fx.count + ' finding(s), worst ' + fx.worst }, String(fx.count)) : null));
        commentable(ctx, tr, 'table:' + side + ':' + t.key, 'Table ' + t.key);
        tbody.appendChild(tr);
      });
      wrap.appendChild(el('table', { class: 'tbl tbl-compact tbl-sticky ana-tbl' },
        el('thead', {}, el('tr', {}, th('Table'), th('Rows', 'num'), th('Size', 'num'), th('Cols', 'num'), th('Key'), th('FK out / in', 'num'), th('Triggers', 'num'), th('Flags'))),
        tbody));
    }
    renderHead();
    renderTable();
    return el('section', { class: 'card' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Tables'), el('span', { class: 'muted small' }, 'click a row for columns and profiles')),
      el('div', { class: 'card-b stack' }, head, wrap));
  }

  function graphCard(ctx, cat, drawer) {
    var head = el('div', { class: 'toolbar ana-filter' });
    var host = el('div', { class: 'ana-graph' });
    var fitBtn = el('button', { type: 'button', class: 'btn btn-ghost btn-sm', 'aria-pressed': ui.graphFit ? 'true' : 'false', on: { click: function () { ui.graphFit = !ui.graphFit; fitBtn.setAttribute('aria-pressed', ui.graphFit ? 'true' : 'false'); fitBtn.textContent = ui.graphFit ? 'Actual size' : 'Fit all'; draw(); } } }, ui.graphFit ? 'Actual size' : 'Fit all');

    function renderHead() {
      clear(head);
      head.appendChild(sideTabs(ui.graph, null, function (s) { ui.graph = s; renderHead(); draw(); }, 'Graph side'));
      head.appendChild(el('span', { class: 'spacer' }));
      head.appendChild(el('span', { class: 'muted small ana-hint' }, 'drag to pan · click a table · dashed = cycle edge'));
      head.appendChild(fitBtn);
    }
    function draw() {
      clear(host);
      var all = cat[ui.graph] || [];
      if (!all.length) { host.appendChild(el('p', { class: 'muted' }, 'No tables.')); return; }
      var shown = all.slice().sort(function (a, b) { return (b.fkIn + b.fkOut) - (a.fkIn + a.fkOut) || (a.key < b.key ? -1 : 1); }).slice(0, GRAPH_MAX);
      var keys = {};
      shown.forEach(function (t) { keys[t.key] = true; });
      var edges = [];
      shown.forEach(function (t) { (t.refs || []).forEach(function (r) { if (keys[r]) edges.push({ from: t.key, to: r }); }); });
      var svg = DBM.graph.fkSvg(shown.map(function (t) {
        return { id: t.key, label: t.key, sub: num(t.rows) + ' rows' + (t.isHeap ? ' · heap' : ''), cls: t.hasPk ? '' : 'fkg-warn' };
      }), edges, { label: (ui.graph === 'src' ? 'Source' : 'Target') + ' foreign-key graph', onClick: function (id) { drawer.open(ui.graph, id); } });
      if (ui.graphFit) svg.dbmFit(true);
      host.appendChild(svg);
      if (all.length > shown.length) host.appendChild(el('p', { class: 'muted small' }, 'Showing the ' + GRAPH_MAX + ' most connected of ' + num(all.length) + ' tables.'));
    }
    renderHead();
    draw();
    return el('section', { class: 'card' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Foreign-key graph'), el('span', { class: 'muted small' }, 'arrows point from child to parent')),
      el('div', { class: 'card-b stack' }, head, host));
  }

  // ---------- drawer ----------

  function profileBlock(p) {
    if (!p.sampledRows) return el('p', { class: 'small muted' }, 'No rows sampled (empty table).');
    var nullR = p.nulls / p.sampledRows, distR = p.distinct == null ? null : p.distinct / p.sampledRows;
    return el('div', { class: 'ana-prof' },
      el('div', { class: 'ana-metric' }, el('span', { class: 'small muted' }, 'null'), bar(nullR, 'null ' + pct(nullR)), el('span', { class: 'num small' }, pct(nullR))),
      distR == null ? null : el('div', { class: 'ana-metric' }, el('span', { class: 'small muted' }, 'distinct'), bar(distR, 'distinct ratio ' + distR.toFixed(2)), el('span', { class: 'num small' }, distR.toFixed(2))),
      el('div', { class: 'row-wrap ana-facts small' },
        p.semanticClass ? tag(p.semanticClass, 'ana-class') : null,
        p.maxLen != null ? el('span', { class: 'muted' }, 'len ≤ ' + p.maxLen + (p.avgLen != null ? ' (avg ' + p.avgLen.toFixed(1) + ')' : '')) : null,
        p.min != null ? el('span', { class: 'muted mono ellipsis' }, 'min ' + p.min) : null,
        p.max != null ? el('span', { class: 'muted mono ellipsis' }, 'max ' + p.max) : null),
      p.samples && p.samples.length ? el('div', { class: 'row-wrap ana-samples' }, p.samples.map(function (s) { return el('code', { class: 'mono small' }, s); })) : null,
      p.topPatterns && p.topPatterns.length ? el('div', { class: 'small muted ana-patterns' }, 'patterns ', p.topPatterns.map(function (x) { return el('code', { class: 'mono' }, x); })) : null);
  }

  function columnRow(ctx, side, key, d, c, focus) {
    var t = d.table, pk = (d.bestKey || []);
    var flags = [];
    if (!c.isNullable) flags.push(tag('NOT NULL'));
    if (pk.indexOf(c.name) >= 0) flags.push(tag('key', 'ana-pk'));
    if (c.isIdentity) flags.push(tag('identity'));
    if (c.isComputed) flags.push(tag('computed'));
    if (c.isRowVersion) flags.push(tag('rowversion'));
    (t.foreignKeys || []).forEach(function (fk) {
      var i = fk.columns.indexOf(c.name);
      if (i >= 0) flags.push(tag('FK → ' + fk.refSchema + '.' + fk.refTable + '.' + fk.refColumns[i], fk.isNotTrusted || fk.isDisabled ? 'ana-warn' : ''));
    });
    if (c.defaultDefinition) flags.push(tag('default ' + c.defaultDefinition));
    var row = el('div', { class: 'ana-col' + (focus ? ' is-active' : ''), data: { col: c.name } },
      el('div', { class: 'ana-col-h' }, el('span', { class: 'mono ana-col-name' }, c.name), el('span', { class: 'mono small muted' }, typeDisplay(c))),
      flags.length ? el('div', { class: 'row-wrap ana-flags' }, flags) : null,
      c.profile ? profileBlock(c.profile) : null);
    return commentable(ctx, row, 'column:' + side + ':' + key + '.' + c.name, 'Column ' + key + '.' + c.name);
  }

  function tableDetail(ctx, side, d, focusColumn) {
    var t = d.table, box = el('div', { class: 'stack ana-detail' });
    box.appendChild(el('div', { class: 'row-wrap ana-facts' },
      tag(num(t.rows) + ' rows'), tag(mb(t.sizeMb)),
      d.bestKey ? tag('key ' + d.bestKey.join(', '), 'ana-pk') : tag('no key', 'ana-warn'),
      d.isHeap ? tag('heap') : null,
      t.triggerCount ? tag(t.triggerCount + ' trigger' + (t.triggerCount > 1 ? 's' : '') + ((t.triggerNames || []).length ? ': ' + t.triggerNames.join(', ') : ''), 'ana-warn') : null,
      t.temporalType ? tag('temporal ' + t.temporalType) : null));
    if (t.description) box.appendChild(el('p', { class: 'muted' }, t.description));
    var fkOut = (t.foreignKeys || []).map(function (fk) {
      return el('li', {}, el('span', { class: 'mono' }, fk.name), ' ', fk.columns.join(', ') + ' → ' + fk.refSchema + '.' + fk.refTable + '(' + fk.refColumns.join(', ') + ')',
        fk.isNotTrusted ? tag('untrusted', 'ana-warn') : null, fk.isDisabled ? tag('disabled', 'ana-warn') : null);
    });
    var fkIn = (d.referencedBy || []).map(function (r) { return el('li', {}, el('span', { class: 'mono' }, r.table), ' via ' + r.foreignKey + ' (' + r.columns.join(', ') + ')'); });
    if (fkOut.length || fkIn.length) {
      box.appendChild(el('div', { class: 'ana-rel' },
        fkOut.length ? el('div', {}, el('h3', { class: 'h3' }, 'References'), el('ul', { class: 'ana-list small' }, fkOut)) : null,
        fkIn.length ? el('div', {}, el('h3', { class: 'h3' }, 'Referenced by'), el('ul', { class: 'ana-list small' }, fkIn)) : null));
    }
    box.appendChild(el('h3', { class: 'h3' }, 'Columns (' + t.columns.length + ')'));
    var cols = el('div', { class: 'ana-cols' });
    t.columns.forEach(function (c) { cols.appendChild(columnRow(ctx, side, d.key, d, c, c.name === focusColumn)); });
    box.appendChild(cols);
    return box;
  }

  function createDrawer(ctx) {
    var node = el('aside', { class: 'drawer ana-drawer', role: 'dialog', 'aria-label': 'Table details', 'aria-hidden': 'true' });
    var lastFocus = null;
    function close() {
      node.classList.remove('drawer-open');
      node.setAttribute('aria-hidden', 'true');
      if (lastFocus && lastFocus.focus) lastFocus.focus();
    }
    function open(side, key, column) {
      lastFocus = document.activeElement;
      clear(node);
      var closeBtn = el('button', { type: 'button', class: 'btn btn-ghost btn-sm', 'aria-label': 'Close details', on: { click: close } }, '✕');
      var body = el('div', { class: 'ana-drawer-b' }, el('p', { class: 'muted' }, 'Loading…'));
      node.appendChild(el('div', { class: 'ana-drawer-h' },
        el('div', { class: 'ana-title' }, el('span', { class: 'pill' }, side), el('h2', { class: 'h2 mono ellipsis', title: key }, key)),
        el('span', { class: 'spacer' }), closeBtn));
      node.appendChild(body);
      node.classList.add('drawer-open');
      node.setAttribute('aria-hidden', 'false');
      closeBtn.focus();
      loadTable(ctx, side, key).then(function (d) {
        clear(body);
        if (!d) { body.appendChild(el('p', { class: 'muted' }, 'Table not found in the catalog.')); return; }
        body.appendChild(commentable(ctx, el('div', { class: 'ana-table-anchor' }, tableDetail(ctx, side, d, column)), 'table:' + side + ':' + d.key, 'Table ' + d.key));
        if (column) {
          var hit = body.querySelector('.ana-col.is-active');
          if (hit && hit.scrollIntoView) hit.scrollIntoView({ block: 'center' });
        }
      }, function (err) {
        clear(body);
        body.appendChild(el('p', { class: 'muted' }, 'Could not load the table: ' + ((err && err.message) || err)));
      });
    }
    node.addEventListener('keydown', function (ev) { if (ev.key === 'Escape') { ev.stopPropagation(); close(); } });
    return { node: node, open: open, close: close };
  }

  // ---------- view ----------

  function render(root, ctx) {
    clear(root);
    tableCache = {};
    var page = el('div', { class: 'page ana-page' });
    root.appendChild(page);
    if (!isExport() && DBM.components && DBM.components.reviewBar) page.appendChild(DBM.components.reviewBar(ctx));
    var p = payloadOf(ctx);
    if (!p) {
      page.appendChild(DBM.components.emptyState('No analysis yet', 'Discovery and the rule checks run automatically once both connections are saved.'));
      return;
    }
    var drawer = createDrawer(ctx);
    page.appendChild(header(ctx, p));
    page.appendChild(kpiRow(p));
    page.appendChild(el('div', { class: 'grid-2 ana-dbs' }, dbCard('Source', 'src', p.source, p.target), dbCard('Target', 'tgt', p.target, p.source)));
    page.appendChild(narrativeCard(ctx, p));
    page.appendChild(findingsCard(ctx, p, drawer));
    var lower = el('div', { class: 'stack' }, el('section', { class: 'card is-loading' }, el('div', { class: 'card-b muted' }, 'Loading catalog…')));
    page.appendChild(lower);
    root.appendChild(drawer.node);
    loadCatalog(ctx).then(function (cat) {
      clear(lower);
      lower.appendChild(tablesCard(ctx, p, cat, drawer));
      lower.appendChild(graphCard(ctx, cat, drawer));
    }, function (err) {
      clear(lower);
      lower.appendChild(DBM.components.emptyState('Catalog unavailable', (err && err.message) || String(err)));
    });
  }

  function onEvent(evt, ctx) {
    if (evt && evt.type === 'drift_detected') toast(ctx, 'Schema changed since discovery — re-run discovery before approving.', 'warn');
  }

  DBM.views = DBM.views || {};
  DBM.views.analysis = { title: 'Analysis', render: render, onEvent: onEvent };
})(window.DBM = window.DBM || {});
```

- [ ] **Step 8: Add the styles and script tags**

Append this block to the end of `ENGINE/Dbm/wwwroot/css/app.css` (it uses only the C9 custom properties, so light and dark themes follow automatically):
```css
/* ===== T2.8 — Analysis view (ana-*) and FK graph (fkg-*) ===== */
.ana-head { align-items: flex-end; gap: 16px; flex-wrap: wrap; }
.ana-title { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.ana-title .h1, .ana-title .h2 { margin: 0; }
.ana-actions { gap: 8px; }
.ana-kpis { margin-block: 16px; }
.ana-dbs { gap: 16px; margin-bottom: 16px; }
.ana-kv { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 6px 16px; margin: 0; }
.ana-kv dt { color: var(--text-muted); }
.ana-kv dd { margin: 0; min-width: 0; overflow-wrap: anywhere; display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; }
.ana-warn { color: var(--warn); border-color: var(--warn); }
.ana-pk { color: var(--accent); border-color: var(--accent); }
.ana-narrative, .ana-findings-card { margin-bottom: 16px; }
.ana-narr p, .ana-md p { margin: 0 0 8px; line-height: 1.55; max-width: 76ch; }
.ana-sub { margin-top: 12px; }
.ana-sub .h3 { margin: 0 0 4px; }
.ana-list { margin: 0; padding-left: 20px; line-height: 1.5; }
.ana-list li + li { margin-top: 4px; }
.ana-filter { gap: 8px; flex-wrap: wrap; align-items: center; }
.ana-search { width: 16rem; max-width: 100%; }
.ana-chips { gap: 6px; }
.ana-chips .num { font-variant-numeric: tabular-nums; opacity: 0.8; }
.ana-findings { display: flex; flex-direction: column; gap: 8px; }
.ana-finding { border: 1px solid var(--border); border-left: 3px solid var(--border); border-radius: var(--radius); background: var(--surface); padding: 8px 12px; }
.ana-finding[data-sev="critical"] { border-left-color: var(--err); }
.ana-finding[data-sev="high"] { border-left-color: var(--warn); }
.ana-finding[data-sev="medium"] { border-left-color: var(--info); }
.ana-finding[data-sev="low"] { border-left-color: var(--text-muted); }
.ana-finding-h { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.ana-rule { color: var(--text); font-weight: 600; }
.ana-obj { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ana-msg { margin: 6px 0 0; line-height: 1.5; }
.ana-commentary { margin-top: 8px; padding: 8px 12px; border-radius: var(--radius); background: var(--surface-2); }
.ana-commentary p { margin: 2px 0 0; }
.ana-none { margin: 8px 0; }
.ana-tabs { gap: 6px; }
.ana-tbl-wrap { overflow-x: auto; max-height: 28rem; overflow-y: auto; border: 1px solid var(--border); border-radius: var(--radius); }
.ana-tbl { width: 100%; }
.ana-tbl .ana-key { white-space: nowrap; }
.ana-flags { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; }
.ana-hint { white-space: nowrap; }
.ana-graph { overflow: hidden; }
.ana-drawer { width: min(560px, 100vw); display: flex; flex-direction: column; }
.ana-drawer-h { display: flex; align-items: flex-start; gap: 8px; padding: 16px; border-bottom: 1px solid var(--border); }
.ana-drawer-h .h2 { max-width: 100%; }
.ana-drawer-b { padding: 16px; overflow-y: auto; flex: 1; }
.ana-detail .h3 { margin: 8px 0 4px; }
.ana-facts { gap: 6px; align-items: center; }
.ana-rel { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 8px 16px; }
.ana-cols { display: flex; flex-direction: column; gap: 8px; }
.ana-col { border: 1px solid var(--border); border-radius: var(--radius); padding: 8px 12px; background: var(--surface); }
.ana-col.is-active { border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent); }
.ana-col-h { display: flex; flex-wrap: wrap; align-items: baseline; gap: 8px; }
.ana-col-name { font-weight: 600; }
.ana-col .ana-flags { margin-top: 4px; }
.ana-prof { display: flex; flex-direction: column; gap: 4px; margin-top: 8px; }
.ana-metric { display: grid; grid-template-columns: 4.5rem minmax(0, 1fr) 3rem; align-items: center; gap: 8px; }
.ana-metric .num { text-align: right; font-variant-numeric: tabular-nums; }
.ana-bar { width: 100%; }
.ana-class { color: var(--accent); border-color: var(--accent); }
.ana-samples { gap: 4px; }
.ana-samples code, .ana-patterns code { background: var(--surface-2); border-radius: 4px; padding: 1px 6px; margin-right: 4px; overflow-wrap: anywhere; }

.fkg { display: block; width: 100%; background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius); cursor: grab; touch-action: none; user-select: none; }
.fkg.is-panning { cursor: grabbing; }
.fkg-node { cursor: pointer; outline: none; }
.fkg-node rect { fill: var(--surface); stroke: var(--border); stroke-width: 1; }
.fkg-node.fkg-warn rect { stroke: var(--warn); stroke-dasharray: 3 2; }
.fkg-label { fill: var(--text); font: 600 12px var(--font-mono); }
.fkg-sub { fill: var(--text-muted); font: 11px var(--font-sans); font-variant-numeric: tabular-nums; }
.fkg-node:hover rect, .fkg-node:focus-visible rect, .fkg-node.fkg-hi rect { stroke: var(--accent); stroke-width: 1.5; }
.fkg-edge { fill: none; stroke: var(--text-muted); stroke-width: 1.25; opacity: 0.7; }
.fkg-edge-cycle { stroke: var(--warn); stroke-dasharray: 5 4; }
.fkg-arrowhead { fill: var(--text-muted); }
.fkg.fkg-dim .fkg-node:not(.fkg-hi) { opacity: 0.35; }
.fkg.fkg-dim .fkg-edge:not(.fkg-hi) { opacity: 0.12; }
.fkg-edge.fkg-hi { stroke: var(--accent); stroke-width: 2; opacity: 1; }
@media (max-width: 640px) {
  .ana-search { width: 100%; }
  .ana-hint { display: none; }
  .ana-kv { grid-template-columns: minmax(0, 1fr); }
  .ana-kv dt { margin-top: 4px; }
}
@media (prefers-reduced-motion: reduce) {
  .fkg-node, .fkg-edge { transition: none; }
}
```

In `ENGINE/Dbm/wwwroot/index.html` (C9 order), directly below the comment line `<!-- lib scripts: later tasks add tags here, … -->` add
```html
  <script src="js/lib/graph.js"></script>
```
and directly below the comment line `<!-- view scripts: later tasks add tags here … -->` add
```html
  <script src="js/views/analysis.js"></script>
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `node --check plugins/db-migrate/engine/Dbm/wwwroot/js/views/analysis.js`
Expected: no output (exit 0).

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/graph.test.cjs`
Expected: `ℹ pass 7`, `ℹ fail 0`.

Run (all node tests; Node 24 needs the quoted glob): `node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"`
Expected: `ℹ fail 0` (M1's dom/diff tests plus the 7 graph tests).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Web"`
Expected: PASS (14 tests: 5 endpoint-function, 3 export, 6 HTTP).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`
Expected: PASS (whole unit suite).

- [ ] **Step 10: Manual verification (`dbm ui` on the sample pair)**

In PowerShell from the repository root:
```powershell
dotnet build plugins/db-migrate/engine/Dbm
$dll = (Resolve-Path plugins/db-migrate/engine/Dbm/bin/Debug/net8.0/Dbm.dll).Path
function dbm { dotnet $dll @args }
$S = '(localdb)\MSSQLLocalDB'
$samples = (Resolve-Path plugins/db-migrate/samples/legacyshop-to-shopv2).Path
sqlcmd -S $S -E -b -Q "DROP DATABASE IF EXISTS LegacyShop_M2; DROP DATABASE IF EXISTS ShopV2_M2; CREATE DATABASE LegacyShop_M2; CREATE DATABASE ShopV2_M2;"
sqlcmd -S $S -E -b -d LegacyShop_M2 -i "$samples\legacyshop.sql"
sqlcmd -S $S -E -b -d LegacyShop_M2 -v scale=1 -i "$samples\seed.sql"
sqlcmd -S $S -E -b -d ShopV2_M2 -i "$samples\shopv2.sql"
$demo = Join-Path $env:TEMP 'dbm-m2-demo'
Remove-Item $demo -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $demo | Out-Null
Set-Location $demo
dbm init m2-demo
```
In the Setup screen save `Server=(localdb)\MSSQLLocalDB;Database=LegacyShop_M2;Integrated Security=true;TrustServerCertificate=true` as source and the same with `Database=ShopV2_M2` as target. Within a few seconds Discovery is approved and Analysis shows "drafting". Stand in for the agent:
```powershell
$n = dbm next | ConvertFrom-Json     # action "agent", agent "schema-analyst"
@'
{"phase":"analysis","baseVersion":0,
 "ops":[{"op":"add","path":"/narrative","value":{
   "summary":"LegacyShop (8 tables, about 19.7k rows) moves into ShopV2 in under a minute. Two orphan orders, the AUDIT_LOG heap and the Customers/Addresses FK cycle need decisions before mapping.",
   "risks":["2 orphan orders in **dbo.ORD_HDR** will be rejected by the target foreign key.","dbo.AUDIT_LOG has no key, so its load cannot resume mid-table.","app.trg_Orders_Audit does not fire during bulk load."],
   "recommendations":["Decide whether to drop or re-parent the orphan orders.","Accept a single-transaction load for `dbo.AUDIT_LOG`.","Confirm the audit trigger's side effects are not needed."]}}],
 "responses":[],"summary":"Manual narrative for UI verification."}
'@ | Set-Content -Encoding utf8 $n.patchPath
dbm apply $n.patchPath
```
Check, in the light theme at ~1280 px:
1. Stepper shows Analysis "awaiting review"; the review bar shows v1 by agent with Approve / Request changes.
2. Header "Analysis — LegacyShop_M2 → ShopV2_M2 · v1 by agent" with "Re-run discovery" and "Export HTML".
3. KPIs: Tables 8 → 6 (38 → 37 columns), Source rows 19,711, Findings with "0 critical · 2 high", Est. transfer "< 1 min", Target triggers 1.
4. Source and Target cards list server, database, version, edition, collation (no "differs" tag) and compat level.
5. Narrative renders **bold** and `code`; risks as bullets, recommendations numbered.
6. Findings: severity chips with counts; "High 2" shows exactly R01 dbo.AUDIT_LOG and R09 dbo.ORD_HDR; typing "trigger" leaves R06 app.Orders; clicking the object button "dbo.ORD_HDR" opens the drawer.
7. Drawer: side pill and key; tags for rows, size, key; "References" lists FK_ORD_CUST with an "untrusted" tag; for `dbo.CUST` the EMAIL_ADDR row shows a ~10 % null bar, distinct 0.90, class "email" and three samples; Escape closes it and focus returns to the button that opened it.
8. Tables card: "Source 8" / "Target 6"; dbo.AUDIT_LOG shows "no PK", "heap" and a severity badge; Tab to a row + Enter opens the drawer; filter "ord" leaves the ORD_* tables.
9. FK graph (Target tab): four layers (Customers, Products, AuditEvents on top; Addresses; Orders; OrderLines), a dashed amber edge for the Customers ↔ Addresses cycle, hovering Orders highlights its two edges and dims the rest, dragging pans, "Fit all" / "Actual size" toggles, clicking a node opens the drawer; the Source tab draws the dbo graph.
10. Comments: hovering a finding, a table row, a drawer column and the narrative shows the comment affordance; a comment on EMAIL_ADDR appears in the feedback drawer with anchor `column:src:dbo.CUST.EMAIL_ADDR`; delete the draft.
11. "Export HTML" shows "Saved .dbmigrate/exports/analysis-v1.html" and downloads the file. Run `dbm stop`, open the downloaded file directly: it renders read-only (no review bar, no action buttons), the drawer and the graph work, and the console shows no errors. Run `dbm ui` again.
12. Dark theme (M1 theme toggle or OS dark mode): repeat 3, 6, 7 and 9 — text, bars, badges, graph boxes, edges and the amber cycle edge stay legible.
13. ~400 px wide (browser dev tools): KPI tiles wrap, the database cards stack, the finding filters wrap, the tables list scrolls horizontally inside its card (the page itself does not scroll sideways), the drawer uses the full width, the graph scales down and its hint text is hidden.
14. Drift guard: `sqlcmd -S $S -E -d ShopV2_M2 -Q "ALTER TABLE app.Orders ADD Channel varchar(10) NULL"`, click Approve → refused with "Schema changed since discovery on tgt; re-run discovery" and a drift toast. Then `sqlcmd -S $S -E -d ShopV2_M2 -Q "ALTER TABLE app.Orders DROP COLUMN Channel"` and Approve again → Analysis approved (the Mapping job that follows needs M3).
15. "Re-run discovery" asks for confirmation and, once confirmed, Discovery runs again.

Clean up: `dbm stop`, `Set-Location` back to the repository, `sqlcmd -S $S -E -Q "DROP DATABASE LegacyShop_M2; DROP DATABASE ShopV2_M2;"`.

- [ ] **Step 11: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Web/Endpoints plugins/db-migrate/engine/Dbm/wwwroot \
  plugins/db-migrate/engine/Dbm.Tests/Unit/Web plugins/db-migrate/engine/Dbm.Tests/js/graph.test.cjs
git commit -F - <<'EOF'
feat(ui): analysis screen, FK graph, catalog/search endpoints and offline HTML export

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

## Contract notes

Additions (allowed by the overview: members added, nothing renamed):

1. **C10 catalog:** `TableInfo.TriggerNames` (init, JSON `triggerNames`), `ColumnInfo.ComputedDefinition` (init), `TypeTraits` (static helpers), `CatalogExtractor.NormalizeMaxLength`, `Profiler.MaxColumnsPerQuery/SampleMaxChars/Truncate`, `ProfilerSql` (SQL builders + `Quote`), `CatalogText` (new file `Dbm/Core/Catalog/CatalogText.cs`, not in the file map), `DriftResult` lives in `DriftChecker.cs`.
2. **`VectorRow` is created in T2.4**, in `Dbm/Core/State/CatalogRepo.cs`, because `VectorIndex` needs it before T2.5 adds the `CatalogRepo` class to that file.
3. **C11 matching:** `Synonyms.ForProject(Workspace)` (default + `~/.dbmigrate/synonyms.json` + `<ws>/.dbmigrate/synonyms.json`; M3's `AutomapJob` should use it too), `Synonyms.Knows`, `VectorIndex.Separator/ContextWeight/Blend/TextOf/TokensOf`. Behaviour worth knowing in M3: `Tokens` looks an unknown plural up in its singular form (`Clients` → `customer`); `at`/`on` expand to `date` (`CreatedAt` ≡ `CRT_DT`); a synonym group that overlaps an existing group absorbs it. Stored column vectors are blended with their table's vector (weight 0.5) — good for search, but M3 name scoring should vectorise column tokens itself rather than reuse stored vectors. Column documents also carry value-class and description tokens (spec §4.3 "comments and value-pattern signatures").
4. **C6:** `DbmServices.Catalog` is a get-only property created on first use (`_catalog ??= new CatalogRepo(Db)`), inserted below `public ConnectionRepo Connections { get; }`.
5. **C7:** the drift guard is `ApprovalGuards.DriftGuardAsync`, the first element of the `All` initializer (later milestones append their guards as further elements). If the drift check itself fails (database unreachable) approval is **not** blocked; a `log` warning event is published instead.
6. **Registrations:** `DiscoverJob`, `AnalyzeJob` and `AnalysisModule` are added below `// milestone registrations below`. `AnalysisModule` has a parameterless constructor (it reads services from `ModuleContext.Services`), so the line is `new Dbm.Core.Analysis.AnalysisModule()` although M1's doc comment shows `(s)`.
7. **HTTP API additions (T2.8):** `POST /api/export/{what}` → `{ok, file, path, download}` (the UI uses it; `download` is the GET URL with `?t=`), GET also saves a copy under `.dbmigrate/exports/`. `GET /api/catalog/{side}` rows carry `refs` (FK target keys) for the graph. `GET /api/catalog/{side}/table/{key}` returns `TableDetail {side, key, table, bestKey, isHeap, referencedBy[]}` (the `TableInfo` is under `table`). `GET /api/search` also accepts `kind`. `POST /api/rediscover` answers 409 `no_connections` before both connections exist, 409 `workflow` when the engine refuses.
8. **Export extension point:** `ExportEndpoints.Register(string what, ExportEndpoints.Builder builder)` with `delegate Task<ExportFile> Builder(WebState, IWebAssets, CancellationToken)`. T3.5 (`mapping`), T4.4 (`sql`, `sqlpack`) and T5.6 (`report`) should register builders instead of adding routes; `WebExport.BuildHtml(IWebAssets, view, title, payload)` + `WebExport.LocateAssets(IFileProvider?)` + `WebExport.SaveCopy` are reusable by M6's `dbm export`.
9. **Export payload (C9):** `DBM_EXPORT = {view, title, payload}`; for analysis `payload = {project, exportedAt, artifact:{version, author, summary, createdAt, payload}, catalog:{src, tgt}, tables:{src:{key: TableDetail}, tgt:{…}}}` — exactly what M1's `renderExport` expects (`ctx.artifact` = `payload.artifact`, `ctx.export` = `payload`). Later export builders should use the same `artifact` envelope and put extra data next to it. Only `app.css`, `js/lib/*`, `js/components/*`, the view and `app.js` are inlined.
10. **UI:** the Analysis view uses M1's `DBM.h`, `DBM.components.{reviewBar, kpi, emptyState, modal, toast}`, `ctx.{artifact, api, refresh, toast, commentable, readOnly}` as published; it reads `ctx.artifact.payload` live and `window.DBM_EXPORT.payload` (= `ctx.export`) in export mode. Its table drawer is a second `.drawer` element inside the view; M1's feedback drawer (`#drawer`, later in the DOM) opens above it, so commenting on a column keeps the table visible underneath.
11. **CLI:** commands open services through `CliContext.RequireProject()` / `OpenServices(ws)`; `search` adds `--kind table|column`; `show` also accepts finding ids (`F001`, from the current Analysis artifact) and bare table names; `discover` without `--inline` calls `ServerControl.EnsureRunningAsync` so the server runs the queued job, and never enqueues a second discover while one is queued/running.
12. **Test support for M3–M6:** `SampleDatabases` / `SamplePair` / `SamplePairFixture` (seeded pair; databases `legacyshop_<8 hex>` / `shopv2_<8 hex>` via `TempDatabase.CreateAsync(prefix)`), `TempProject` (a `TestWorkspace` + real-registry `DbmServices` with a named project), `SampleProject.CreateAsync(pair)` (TempProject with both connections saved) and `TestCatalogs` (in-code structural copies of both catalogs; `Dbm.Tests/Support/TestCatalogs.cs`, not in the file map).
13. **Quoting:** M2 uses `ProfilerSql.Quote` because `SqlQuote` (C3) arrives in T4.1; M4 may switch callers over.

Upstream (M1) change:

14. `m1-core-platform.md` → `DbmServicesTests`: the "registries are empty" assertions (`Assert.Empty(s.Modules)`, `Assert.Empty(s.JobHandlers)`, `Assert.Empty(plain.Modules)`) now compare against `ModuleRegistry.Create(...)` / `JobRegistry.Create(...)`, so every milestone's registrations keep that M1 test green. The integration repo carries the same edit in the M2 commit.

Clarifications and deliberate choices:

15. **C15 counts (normative for M3–M6):** ORD_HDR = 3000·scale **+ 5**, ORD_LINE = 9000·scale **+ 2**. The 2 orphan orders (CUST_ID −1/−2, one line each) and the 3 long-comment orders (valid customers, **no lines**) are extra rows; the zero-quantity line is ORD_ID 1 / LINE_NO 1. Without "no lines" for the long-comment orders their 9 lines would also be rejected and the C15 outcome of 8 rejected rows could not hold.
16. **dbm control tables:** the extractor skips every table whose name starts with `__dbm_` (M5's `dbo.__dbm_checkpoint`) and `sysdiagrams`, so `Fingerprint`, `DriftChecker`, analysis and mapping never see them (asserted by `CatalogExtractorTests.Dbm_control_tables_are_not_part_of_the_catalog`). The fingerprint also excludes FK `IsNotTrusted`/`IsDisabled`, descriptions and server metadata, so M5's NOCHECK/WITH CHECK toggling and a server patch never read as drift.
17. **Profiling:** non-string columns get a type-derived `SemanticClass` (`integer`, `decimal`, `date`, `datetime`, `guid`, `flag`) when they have values; string `Min`/`Max` are withheld when `SampleValues` is off (they are real values).
18. **Spec §5.1 "truncation risk / out-of-range values vs target types"** needs a mapping, so it is not an M2 rule; M3's `TypeCompat`/`ColumnMap.TypeRisk` covers it. The sample pair's 300-character comments surface there.
19. **AnalyzeJob ignores carry-over:** after a rediscovery the analysis is recomputed from scratch and the agent writes a new narrative (old commentary may reference findings that no longer exist).

## Self-review

**Spec coverage (M2 scope):**

| Spec item | Where |
|---|---|
| §3.1 DISCOVERY: normalised catalogs, profile, vector index, fingerprints | T2.2, T2.3, T2.4, T2.5 (`DiscoverJob`) |
| §3.2 drift check before approval, UI warning | T2.5 guard + `drift_detected` event; T2.8 toast (pre-execution check is M5) |
| §4.2 schema-analyst: digest packet, rework with feedback slice, patch + responses, `dbm show/search` | T2.7 (module packets, agent playbook) |
| §4.3 compact packets, local TF-IDF vector index, `dbm search … --side -k` | T2.4, T2.5, T2.7 |
| §5.1 inventory: tables, columns, rows, sizes, indexes, FKs, triggers, views/procs/functions, computed/identity/rowversion, temporal | T2.2 model; T2.6 `DbSummary`; T2.8 cards and drawer |
| §5.1 findings: heaps/no key, deprecated + LOB types, collation, orphaned FK data, target triggers, identity, null-heavy columns, estimated size/time | R01, R02/R03, R04, R09, R06, R05, R10, `Estimates` |
| §5.1 agent summary, narrative, recommendations | T2.7 |
| §6 `dbm discover`, `dbm search`, `dbm show` | T2.5 |
| §8 Analysis screen: KPI tiles, side-by-side, risks by severity, per-table drill-down, FK graph, anchored comments, approve/request changes, single-file export | T2.8 (review bar and feedback drawer are M1) |
| §10 secrets: none in packets/exports; sample values switchable | packets carry names/profiles only (asserted in `AnalysisLoopTests`); `ProjectSettings.SampleValues` honoured by the profiler |
| §11 integration fixture with renames, splits, merges, type changes, orphans, a heap, an FK cycle | T2.1 |

**Placeholder scan:** every step has complete code or exact commands; edits to M0/M1 files name their real anchor lines (`// milestone registrations below`, `// Later milestones append their commands below this line.`, the `ApprovalGuards.All` declaration, the `// T2.8:` comment in `EndpointRegistry`, the two index.html marker comments, `</Project>`) and give the exact lines.

**Type consistency:** names match C1–C15 (`CatalogSnapshot`, `TableInfo.Key/BestKey`, `ColumnProfile.NullRatio`, `CatalogRepo.Save/Get/Fingerprint/SaveVectors/Vectors`, `VectorRow`, `VectorIndex.Build/FromRows/Search`, `DriftChecker.CheckAsync`, `IPhaseModule` members, `JobResult(DraftPayload, Summary)`, `ApprovalGuards.All`, `EndpointRegistry.MapAll`). Anchors follow C5 (`finding:<id>`, `table:<side>:<key>`, `column:<side>:<key>.<col>`, `narrative`).

**Executed:** this plan text was replayed onto the M0+M1 integration repository (all full-file blocks written, all M0/M1 edits applied at the anchors named above) and committed there as "M2": 335 unit tests (132 from M2), 33 LocalDB integration tests (25 from M2, including `AnalysisLoopTests`) and 18 node tests (7 from M2) pass; `CatalogHttpTests` run the real server through `WebTestServer`. Not automated: the manual UI check (T2.8 Step 10).
