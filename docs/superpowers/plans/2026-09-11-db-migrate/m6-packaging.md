# Milestone 6 — Packaging, Demo & Docs

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** Make db-migrate something a teammate can install from git and try in five minutes. `dbm demo` builds the LegacyShop → ShopV2 sample pair on any SQL Server and can attach it to the current project, so nobody has to paste connection strings. `dbm export` writes the standalone HTML reports and the SQL script pack from the command line. A release script produces the committed `engine/dist`, and the launchers rebuild a stale dist safely. `dbm doctor` additionally reports the encryption round-trip, this project's server and the engine build version. The docs cover users (README, user guide), the orchestrator (troubleshooting playbook, polished SKILL.md) and maintainers (development + release). A manual end-to-end run of the installed plugin in the CLI and the Desktop app closes the milestone and records real token costs.

**Tasks:** 6.1 `dbm demo` + `dbm export` · 6.2 Release build, dist, launcher/doctor hardening · 6.3 Docs + skill polish + troubleshooting · 6.4 Installed-plugin end-to-end verification (CLI + Desktop).

**Upstream facts this milestone builds on (verified against the M0–M5 code):**
- `CliContext.RequireProject()` throws `CliFailure("no_project", …)`; commands open services with `ctx.OpenServices(ws)` / `ctx.OpenProject()` (the test seam), and report errors by throwing `CliFailure(code, message)` — `CliApp` turns that into `{"error":…,"message":…}` with exit 1.
- `Args.BooleanFlags` lists every option that takes no value; each milestone appends its own.
- `SampleSql` (T2.1) exposes `LegacyShopSchema`, `ShopV2Schema`, `Seed(int scale)`, `SplitBatches(string)`, `UntrustedForeignKey` and `ExecuteAsync(SqlConnection, string, CancellationToken)`.
- Exports are registered with `ExportEndpoints.Register(what, builder)`: `analysis` (T2.8), `sql` + `sqlpack` (T4.4), `report` (T5.6). `POST /api/export/{what}` builds the file, saves a copy under `.dbmigrate/exports/` and answers `{ok, file, path, download}`; unknown kinds give 404 `{error:"not_found"}` and a phase with nothing to export 404 `{error:"export_unavailable"}`.
- `DoctorCommand` owns `public sealed record Check(string Name, bool Ok, string Detail)`, prints `{ok, checks}` and, with `--quiet`, prints one line only when a check fails.
- `ServerControl.ReadInfo/IsAliveAsync/EnsureRunningAsync/StopAsync` and `ServerInfo {Port, Pid, Token, StartedAt}` with `BaseUrl`/`UiUrl`; the detached server is started with `serve --workspace <root> --detached` (Windows: `UseShellExecute=true`, so the caller's stdout pipe is not inherited; elsewhere `nohup … &`).
- `dbm version` prints `{"version":"<plugin version>","runtime":"<framework>"}`.
- Test helpers: `TestWorkspace` (`Ws`, `Root`, `OpenServices()`), `CliRunner.RunAsync(ws, factory, args…)` → `CliResult {Exit, Out, Err, Json}` (it appends `--workspace <root>`), `SqlTestServer.ConnectionString/ForDatabase`, `WebTestServer.StartAsync(ws, factory)`, `ServerControl.SpawnOverrides[root]` (replaces spawning a real server process), `SampleDatabases`/`SamplePair`, `TempProject`.

**Rule for every test in this milestone:** a test may only drop the databases it created, by their exact names. Never drop by name pattern.

---

### Task 6.1: `dbm demo` + `dbm export`

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Samples/DemoDatabases.cs`
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/DemoCommand.cs`
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/ExportCommand.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` (two entries), `plugins/db-migrate/engine/Dbm/Cli/Args.cs` (one flag)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DemoCommandTests.cs`, `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ExportCommandTests.cs`, `plugins/db-migrate/engine/Dbm.Tests/Integration/Samples/DemoCommandTests.cs`

**Interfaces:**

```csharp
namespace Dbm.Core.Samples;
public sealed record DemoResult(string SourceDatabase, string SourceConnectionString, string TargetDatabase, string TargetConnectionString);
public sealed class DemoExistsException(string message) : Exception(message);
public static partial class DemoDatabases
{
    public const string DefaultPrefix = "DbmDemo_";
    public static bool IsValidPrefix(string prefix);                                  // ^[A-Za-z0-9_]{0,50}$
    public static (string Source, string Target) Names(string prefix);                // (prefix+"LegacyShop", prefix+"ShopV2")
    public static string ForDatabase(string serverConnectionString, string database); // same string, Initial Catalog replaced
    public static Task<DemoResult> CreateAsync(string serverConnectionString, int scale, string prefix, bool force, CancellationToken ct);
    public static Task DropAsync(string serverConnectionString, string prefix, CancellationToken ct);   // exactly the two demo names
}

namespace Dbm.Cli.Commands;
public sealed class DemoCommand : ICommand      // Name "demo"
{
    public static object ConnectionView(string database, string connectionString);    // {database, connectionString (secrets -> ***), describe}
}
public sealed class ExportCommand : ICommand    // Name "export"
{
    public static readonly string[] Kinds;      // analysis, sql, sqlpack, report
    public static string CopyToOut(string? outOption, string savedPath, string fileName);
}
```

CLI surface:
- `dbm demo --server "<conn>" [--scale n] [--prefix name] [--force] [--attach]` → `{"ok":true,"scale":1,"source":{"database":…,"connectionString":…,"describe":…},"target":{…},"attached":false,"next":"…"}`. Errors: `usage`, `no_project` (with `--attach` outside a project), `locked` (transfer already started), `demo_exists`, `sql_error`.
- `dbm export <analysis|sql|sqlpack|report> [--out path]` → `{"ok":true,"what":"analysis","file":"analysis-v1.html","path":"<absolute>","bytes":123456}`. Errors: `usage`, `no_project`, plus whatever the server returns (`not_found`, `export_unavailable`).

Design decisions (normative):
- The demo connects to `master` on the server given by `--server` and creates `<prefix>LegacyShop` + `<prefix>ShopV2`. Without `--force` an existing database of either exact name is an error; with `--force` exactly those two are dropped and recreated. No other database is ever touched.
- The printed connection strings are the `--server` string with `Initial Catalog` replaced and every secret replaced by `***` (`Redactor.Scrub`), so the non-`--attach` path stays usable with integrated auth while no secret is ever printed.
- `--attach` requires an initialised project whose Transfer phase is still `pending`; both checks run **before** any database is touched. It then saves both connections exactly like `POST /api/connections/{side}` does (probe → `ConnectionRepo.Save` → `state_changed` event → `WorkflowEngine.OnConnectionsSaved()`), so discovery is queued and a running server picks the change up through its job loop and event pump.
- `dbm export` asks the project's own server (`POST /api/export/{what}`, started on demand with `ServerControl.EnsureRunningAsync`). CLI and UI exports are then identical files with identical names, and M6 needs no knowledge of `WebExport`/`ScriptPack` internals. The server already saves a copy under `.dbmigrate/exports/`; `--out` copies that file to a directory (keeping its name) or to an exact path.

- [ ] **Step 1: Write the failing unit tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DemoCommandTests.cs`:

```csharp
using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Unit.Cli;

public class DemoCommandTests
{
    [Fact]
    public async Task Demo_without_server_is_a_usage_error()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo");

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
        Assert.Contains("--server", r.Json["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("bad;name")]
    [InlineData("has space")]
    [InlineData("x]y")]
    public async Task Demo_rejects_an_unsafe_prefix(string prefix)
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere", "--prefix", prefix);

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    public async Task Demo_rejects_a_scale_outside_the_range(string scale)
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere", "--scale", scale);

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Demo_attach_without_a_project_fails_before_touching_sql()
    {
        using var tw = new TestWorkspace();

        // "Server=nowhere" would time out if the command connected first: the project check must come first.
        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", "Server=nowhere;Connect Timeout=1", "--attach");

        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", r.Json["error"]!.GetValue<string>());
        Assert.False(File.Exists(tw.Ws.StateDbPath));
    }

    [Fact]
    public void Names_add_the_prefix()
    {
        Assert.Equal(("DbmDemo_LegacyShop", "DbmDemo_ShopV2"), DemoDatabases.Names(DemoDatabases.DefaultPrefix));
        Assert.Equal(("LegacyShop", "ShopV2"), DemoDatabases.Names(""));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Team_01_", true)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    public void IsValidPrefix_allows_only_letters_digits_and_underscore(string prefix, bool expected) =>
        Assert.Equal(expected, DemoDatabases.IsValidPrefix(prefix));

    [Fact]
    public void IsValidPrefix_rejects_more_than_50_characters() => Assert.False(DemoDatabases.IsValidPrefix(new string('a', 51)));

    [Fact]
    public void ForDatabase_replaces_only_the_database()
    {
        var cs = DemoDatabases.ForDatabase(
            "Server=srv1;Database=master;Integrated Security=true;TrustServerCertificate=true", "DbmDemo_ShopV2");

        var b = new SqlConnectionStringBuilder(cs);
        Assert.Equal("DbmDemo_ShopV2", b.InitialCatalog);
        Assert.Equal("srv1", b.DataSource);
        Assert.True(b.IntegratedSecurity);
        Assert.True(b.TrustServerCertificate);
    }

    [Fact]
    public void ConnectionView_never_shows_the_password()
    {
        var view = Json.ToNode(DemoCommand.ConnectionView("DbmDemo_LegacyShop",
            "Server=srv1;Database=DbmDemo_LegacyShop;User ID=demo;Password=Secr3tPass;TrustServerCertificate=true"));

        Assert.DoesNotContain("Secr3tPass", view.ToJsonString());
        Assert.Contains("***", view["connectionString"]!.GetValue<string>());
        Assert.Equal("DbmDemo_LegacyShop", view["database"]!.GetValue<string>());
        Assert.Contains("user=demo", view["describe"]!.GetValue<string>());
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ExportCommandTests.cs` (no SQL Server: the CLI's "start the server" step is answered by an in-process `WebTestServer`):

```csharp
using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class ExportCommandTests
{
    private const string AnalysisPayload = """{"summary":"demo export marker","findings":[]}""";

    /// <summary>Gives the workspace an analysis v1 to export.</summary>
    private static void SeedAnalysis(TestWorkspace tw)
    {
        using var services = tw.OpenServices();
        services.Artifacts.Add(PhaseName.Analysis, 1, AnalysisPayload, "script", "analysis v1");
        services.Phases.SetCurrentVersion(PhaseName.Analysis, 1);
        services.Phases.SetStatus(PhaseName.Analysis, PhaseStatus.AwaitingReview);
    }

    /// <summary>Runs <paramref name="body"/> with the CLI's "start the server" step answered by an in-process WebHost.</summary>
    private static async Task WithServerAsync(TestWorkspace tw, Func<Task> body)
    {
        WebTestServer? server = null;
        ServerControl.SpawnOverrides[tw.Ws.Root] = async (ws, _) => server = await WebTestServer.StartAsync(ws, w => DbmServices.Open(w));
        try
        {
            await body();
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
            if (server is not null) await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Export_analysis_writes_a_standalone_html_into_the_exports_folder()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis");

            Assert.Equal(0, r.Exit);
            Assert.Equal("analysis", r.Json["what"]!.GetValue<string>());
            Assert.Equal("analysis-v1.html", r.Json["file"]!.GetValue<string>());
            var path = r.Json["path"]!.GetValue<string>();
            Assert.Equal(Path.Combine(tw.Ws.ExportsDir, "analysis-v1.html"), path);
            var html = await File.ReadAllTextAsync(path);
            Assert.Contains("DBM_EXPORT", html);
            Assert.Contains("demo export marker", html);
            Assert.Equal(new FileInfo(path).Length, r.Json["bytes"]!.GetValue<long>());
        });
    }

    [Fact]
    public async Task Export_copies_the_file_to_an_out_directory_or_an_out_file()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);
        var directory = Directory.CreateDirectory(Path.Combine(tw.Root, "out")).FullName;
        var file = Path.Combine(tw.Root, "reports", "analysis.html");

        await WithServerAsync(tw, async () =>
        {
            var toDirectory = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", directory);
            var toFile = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis", "--out", file);

            Assert.Equal(Path.Combine(directory, "analysis-v1.html"), toDirectory.Json["path"]!.GetValue<string>());
            Assert.True(File.Exists(Path.Combine(directory, "analysis-v1.html")));
            Assert.Equal(file, toFile.Json["path"]!.GetValue<string>());
            Assert.True(File.Exists(file));
            // the copy under .dbmigrate/exports is kept as well
            Assert.True(File.Exists(Path.Combine(tw.Ws.ExportsDir, "analysis-v1.html")));
        });
    }

    [Fact]
    public async Task Export_of_an_unknown_kind_reports_the_servers_error()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "nope");

            Assert.Equal(1, r.Exit);
            Assert.Equal("not_found", r.Json["error"]!.GetValue<string>());
            Assert.Contains("nope", r.Json["message"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Export_report_before_any_completed_run_reports_export_unavailable()
    {
        using var tw = new TestWorkspace();
        SeedAnalysis(tw);

        await WithServerAsync(tw, async () =>
        {
            var r = await CliRunner.RunAsync(tw.Ws, null, "export", "report");

            Assert.Equal(1, r.Exit);
            Assert.Equal("export_unavailable", r.Json["error"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Export_without_a_kind_is_a_usage_error()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "export");

        Assert.Equal(1, r.Exit);
        Assert.Equal("usage", r.Json["error"]!.GetValue<string>());
        Assert.Contains("sqlpack", r.Json["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_without_a_project_fails()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "export", "analysis");

        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", r.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public void CopyToOut_without_an_out_option_keeps_the_saved_copy()
    {
        Assert.Equal(@"C:\ws\.dbmigrate\exports\analysis-v1.html",
            ExportCommand.CopyToOut(null, @"C:\ws\.dbmigrate\exports\analysis-v1.html", "analysis-v1.html"));
    }
}
```

- [ ] **Step 2: Write the failing integration test**

`plugins/db-migrate/engine/Dbm.Tests/Integration/Samples/DemoCommandTests.cs`:

```csharp
using Dbm.Core.Samples;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Samples;

/// <summary>`dbm demo` against the test server. Every test uses its own prefix and drops exactly those two databases.</summary>
[Trait("Category", "Integration")]
public sealed class DemoCommandTests : IAsyncLifetime
{
    private readonly string _prefix = "DbmT" + Guid.NewGuid().ToString("N")[..8] + "_";

    private string SourceCs => DemoDatabases.ForDatabase(SqlTestServer.ConnectionString, _prefix + "LegacyShop");
    private string TargetCs => DemoDatabases.ForDatabase(SqlTestServer.ConnectionString, _prefix + "ShopV2");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => DemoDatabases.DropAsync(SqlTestServer.ConnectionString, _prefix, CancellationToken.None);

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Demo_creates_and_seeds_both_databases()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);

        Assert.Equal(0, r.Exit);
        Assert.True(r.Json["ok"]!.GetValue<bool>());
        Assert.Equal(_prefix + "LegacyShop", r.Json["source"]!["database"]!.GetValue<string>());
        Assert.Equal(_prefix + "ShopV2", r.Json["target"]!["database"]!.GetValue<string>());
        Assert.False(r.Json["attached"]!.GetValue<bool>());
        Assert.Equal(1000, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST"));
        Assert.Equal(200, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.PROD"));
        Assert.Equal(4, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_STATUS"));
        Assert.Equal(3005, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.ORD_HDR"));
        Assert.Equal(6, await ScalarAsync(TargetCs,
            "SELECT COUNT_BIG(*) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = 'app'"));
        Assert.Equal(0, await ScalarAsync(TargetCs, "SELECT COUNT_BIG(*) FROM app.Customers"));
    }

    [Fact]
    public async Task Demo_refuses_to_overwrite_without_force_and_recreates_with_it()
    {
        using var tw = new TestWorkspace();
        var first = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
        Assert.Equal(0, first.Exit);

        var again = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix);
        var forced = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--force");

        Assert.Equal(1, again.Exit);
        Assert.Equal("demo_exists", again.Json["error"]!.GetValue<string>());
        Assert.Contains("--force", again.Json["message"]!.GetValue<string>());
        Assert.Equal(0, forced.Exit);
        Assert.Equal(1000, await ScalarAsync(SourceCs, "SELECT COUNT_BIG(*) FROM dbo.CUST"));   // recreated, not seeded twice
    }

    [Fact]
    public async Task Demo_attach_saves_both_connections_and_queues_discovery()
    {
        using var tw = new TestWorkspace();
        using var services = tw.OpenServices();

        var r = await CliRunner.RunAsync(tw.Ws, null, "demo", "--server", SqlTestServer.ConnectionString, "--prefix", _prefix, "--attach");

        Assert.Equal(0, r.Exit);
        Assert.True(r.Json["attached"]!.GetValue<bool>());
        Assert.True(services.Connections.Has(Side.Src));
        Assert.True(services.Connections.Has(Side.Tgt));
        Assert.Equal(_prefix + "LegacyShop", services.Connections.GetMeta(Side.Src)!.Database);
        Assert.Equal(_prefix + "ShopV2", services.Connections.GetMeta(Side.Tgt)!.Database);
        Assert.Equal(PhaseStatus.Approved, services.Phases.Get(PhaseName.Setup).Status);
        Assert.Equal(PhaseStatus.Running, services.Phases.Get(PhaseName.Discovery).Status);
        Assert.Contains(services.Jobs.Active(), j => j.Kind == "discover");
    }
}
```

- [ ] **Step 3: Run the tests to confirm they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --nologo --filter "FullyQualifiedName~Dbm.Tests.Unit.Cli.DemoCommandTests|FullyQualifiedName~Dbm.Tests.Unit.Cli.ExportCommandTests"`
Expected: the build FAILS with `error CS0246: The type or namespace name 'DemoDatabases' could not be found` (and the same for `DemoCommand`, `ExportCommand`).

- [ ] **Step 4: Implement `DemoDatabases`**

`plugins/db-migrate/engine/Dbm/Core/Samples/DemoDatabases.cs`:

```csharp
using System.Text.RegularExpressions;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Samples;

/// <summary>The pair of databases `dbm demo` created, with the connection string for each.</summary>
public sealed record DemoResult(string SourceDatabase, string SourceConnectionString, string TargetDatabase, string TargetConnectionString);

/// <summary>At least one demo database exists already and --force was not given.</summary>
public sealed class DemoExistsException(string message) : Exception(message);

/// <summary>Creates (and drops) the LegacyShop → ShopV2 demo pair from the embedded sample scripts.</summary>
public static partial class DemoDatabases
{
    public const string DefaultPrefix = "DbmDemo_";

    [GeneratedRegex("^[A-Za-z0-9_]{0,50}$")]
    private static partial Regex PrefixPattern();

    public static bool IsValidPrefix(string prefix) => PrefixPattern().IsMatch(prefix);

    /// <summary>("&lt;prefix&gt;LegacyShop", "&lt;prefix&gt;ShopV2").</summary>
    public static (string Source, string Target) Names(string prefix) => (prefix + "LegacyShop", prefix + "ShopV2");

    /// <summary>The same server connection string with Initial Catalog replaced.</summary>
    public static string ForDatabase(string serverConnectionString, string database) =>
        new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>Creates both databases and seeds the source. With <paramref name="force"/> existing ones are dropped first.</summary>
    public static async Task<DemoResult> CreateAsync(string serverConnectionString, int scale, string prefix, bool force,
        CancellationToken ct)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be >= 1");
        if (!IsValidPrefix(prefix)) throw new ArgumentException($"Invalid demo prefix '{prefix}'.", nameof(prefix));
        var (source, target) = Names(prefix);

        await using (var master = await SqlConnect.OpenAsync(ForDatabase(serverConnectionString, "master"), ct))
        {
            var existing = new List<string>();
            foreach (var database in new[] { source, target })
            {
                if (await ExistsAsync(master, database, ct)) existing.Add(database);
            }
            if (existing.Count > 0 && !force)
            {
                throw new DemoExistsException($"Database {string.Join(" and ", existing)} already exists. " +
                    "Re-run with --force to drop and recreate the demo databases, or choose another --prefix.");
            }
            SqlConnection.ClearAllPools();   // pooled sessions of a dropped database are dead
            foreach (var database in existing) await DropOneAsync(master, database, ct);
            foreach (var database in new[] { source, target })
            {
                await ExecuteAsync(master, $"CREATE DATABASE {SqlQuote.Ident(database)};", ct);
            }
        }

        var sourceCs = ForDatabase(serverConnectionString, source);
        var targetCs = ForDatabase(serverConnectionString, target);
        await RunScriptAsync(sourceCs, SampleSql.LegacyShopSchema, ct);
        await RunScriptAsync(sourceCs, SampleSql.Seed(scale), ct);
        await RunScriptAsync(targetCs, SampleSql.ShopV2Schema, ct);
        return new DemoResult(source, sourceCs, target, targetCs);
    }

    /// <summary>Drops this prefix's two demo databases when they exist. Never touches any other database.</summary>
    public static async Task DropAsync(string serverConnectionString, string prefix, CancellationToken ct)
    {
        var (source, target) = Names(prefix);
        SqlConnection.ClearAllPools();
        await using var master = await SqlConnect.OpenAsync(ForDatabase(serverConnectionString, "master"), ct);
        foreach (var database in new[] { source, target })
        {
            if (await ExistsAsync(master, database, ct)) await DropOneAsync(master, database, ct);
        }
        SqlConnection.ClearAllPools();
    }

    private static async Task<bool> ExistsAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT DB_ID(@name);", master);
        cmd.Parameters.AddWithValue("@name", database);
        return await cmd.ExecuteScalarAsync(ct) is not (null or DBNull);
    }

    private static Task DropOneAsync(SqlConnection master, string database, CancellationToken ct)
    {
        var ident = SqlQuote.Ident(database);
        return ExecuteAsync(master, $"ALTER DATABASE {ident} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {ident};", ct);
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RunScriptAsync(string connectionString, string script, CancellationToken ct)
    {
        await using var conn = await SqlConnect.OpenAsync(connectionString, ct);
        await SampleSql.ExecuteAsync(conn, script, ct);
    }
}
```

- [ ] **Step 5: Implement `DemoCommand`**

`plugins/db-migrate/engine/Dbm/Cli/Commands/DemoCommand.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Cli.Commands;

/// <summary>
/// `dbm demo --server "&lt;conn&gt;" [--scale n] [--prefix name] [--force] [--attach]` — build the LegacyShop → ShopV2
/// sample pair for a trial run. With --attach both connections are saved into this project, so nothing is pasted anywhere.
/// </summary>
public sealed class DemoCommand : ICommand
{
    public string Name => "demo";
    public string Help => "Create the LegacyShop/ShopV2 sample databases (--attach saves them as this project's connections)";

    /// <summary>What the CLI prints per side: the connection string with every secret replaced by ***.</summary>
    public static object ConnectionView(string database, string connectionString) => new
    {
        database,
        connectionString = Redactor.Scrub(connectionString, Redactor.SecretsOf(connectionString)),
        describe = Redactor.Describe(connectionString),
    };

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var server = args.Opt("server");
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new CliFailure("usage",
                "dbm demo --server \"<connection string to any database on the server>\" [--scale n] [--prefix name] [--force] [--attach]");
        }

        var scale = args.Int("scale", 1);
        if (scale is < 1 or > 1000) throw new CliFailure("usage", "--scale must be between 1 and 1000.");

        var prefix = args.Opt("prefix") ?? DemoDatabases.DefaultPrefix;
        if (!DemoDatabases.IsValidPrefix(prefix))
            throw new CliFailure("usage", "--prefix may contain only letters, digits and '_' (at most 50 characters).");

        var attach = args.Flag("attach");
        Workspace? ws = null;
        if (attach)
        {
            ws = ctx.RequireProject();
            using var project = ctx.OpenServices(ws);
            if (project.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Pending)
            {
                throw new CliFailure("locked",
                    "Connections cannot change after the transfer has started; run the demo in a new project folder.");
            }
        }

        try
        {
            var result = await DemoDatabases.CreateAsync(server, scale, prefix, args.Flag("force"), CancellationToken.None);
            if (attach) await AttachAsync(ctx, ws!, result, CancellationToken.None);
            return Output.Ok(ctx, new
            {
                ok = true,
                scale,
                source = ConnectionView(result.SourceDatabase, result.SourceConnectionString),
                target = ConnectionView(result.TargetDatabase, result.TargetConnectionString),
                attached = attach,
                next = attach
                    ? "Both connections are saved and discovery is queued - continue in the browser (dbm ui)."
                    : "Paste each connectionString into the Setup screen, or re-run inside a project folder with --attach.",
            });
        }
        catch (DemoExistsException ex)
        {
            throw new CliFailure("demo_exists", ex.Message);
        }
        catch (SqlException ex)
        {
            throw new CliFailure("sql_error", Redactor.Scrub(ex.Message, Redactor.SecretsOf(server)));
        }
    }

    /// <summary>Saves both connections exactly like POST /api/connections/{side} does, then lets the workflow continue.</summary>
    private static async Task AttachAsync(CliContext ctx, Workspace ws, DemoResult result, CancellationToken ct)
    {
        var sourceMeta = await SqlConnect.ProbeAsync(result.SourceConnectionString, ct);
        var targetMeta = await SqlConnect.ProbeAsync(result.TargetConnectionString, ct);
        using var services = ctx.OpenServices(ws);
        services.Connections.Save(Side.Src, result.SourceConnectionString, sourceMeta);
        services.Connections.Save(Side.Tgt, result.TargetConnectionString, targetMeta);
        services.Sink.Publish("state_changed", new { phase = PhaseName.Setup, status = services.Phases.Get(PhaseName.Setup).Status });
        services.Workflow.OnConnectionsSaved();
    }
}
```

- [ ] **Step 6: Implement `ExportCommand`**

`plugins/db-migrate/engine/Dbm/Cli/Commands/ExportCommand.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>
/// `dbm export &lt;analysis|sql|sqlpack|report&gt; [--out path]` — the same files the UI's Export buttons produce.
/// The project server builds them (POST /api/export/{what}), so CLI and UI exports are identical.
/// </summary>
public sealed class ExportCommand : ICommand
{
    /// <summary>The kinds the server registers (T2.8 analysis, T4.4 sql/sqlpack, T5.6 report).</summary>
    public static readonly string[] Kinds = ["analysis", "sql", "sqlpack", "report"];

    public string Name => "export";
    public string Help => "Write a standalone export: analysis, sql or report (HTML), sqlpack (zip), to .dbmigrate/exports or --out";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var what = args.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(what)) throw new CliFailure("usage", $"dbm export <{string.Join('|', Kinds)}> [--out path]");

        var ws = ctx.RequireProject();
        var info = await ServerControl.EnsureRunningAsync(ws);

        using var http = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/export/{Uri.EscapeDataString(what)}");
        request.Headers.Add(TokenGuard.Header, info.Token);
        using var response = await http.SendAsync(request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (!response.IsSuccessStatusCode)
        {
            throw new CliFailure(body?["error"]?.GetValue<string>() ?? "export_failed",
                body?["message"]?.GetValue<string>() ?? $"The export request failed with HTTP {(int)response.StatusCode}.");
        }

        var file = body!["file"]!.GetValue<string>();
        var saved = Path.Combine(ws.Root, body["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
        var path = CopyToOut(args.Opt("out"), saved, file);
        return Output.Ok(ctx, new { ok = true, what, file, path, bytes = new FileInfo(path).Length });
    }

    /// <summary>
    /// Without --out the saved copy under .dbmigrate/exports is the result. --out naming an existing directory (or ending
    /// with a separator) keeps the file name inside it; any other --out is the exact target file.
    /// </summary>
    public static string CopyToOut(string? outOption, string savedPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(outOption)) return savedPath;
        var full = Path.GetFullPath(outOption);
        var target = Directory.Exists(full) || outOption.EndsWith('/') || outOption.EndsWith('\\')
            ? Path.Combine(full, fileName)
            : full;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(savedPath, target, overwrite: true);
        return target;
    }
}
```

- [ ] **Step 7: Register the commands and the `--attach` flag**

In `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs`, `All()` ends with the M5 transfer commands. Add the two M6 entries as the last elements, before the closing `];`:

```csharp
        new TransferStatusCommand(),  // T5.5
        new DemoCommand(),            // T6.1
        new ExportCommand(),          // T6.1
    ];
```

In `plugins/db-migrate/engine/Dbm/Cli/Args.cs`, `BooleanFlags` ends with the T5.5 line. Append the M6 flag so that `--attach` never swallows the next token:

```csharp
        "skip-errors", "truncate",   // T5.5
        "attach",                    // T6.1
    };
```

- [ ] **Step 8: Run the unit tests**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --nologo --filter "FullyQualifiedName~Dbm.Tests.Unit.Cli.DemoCommandTests|FullyQualifiedName~Dbm.Tests.Unit.Cli.ExportCommandTests"`
Expected: `Passed! - Failed: 0, Passed: 23` (each `InlineData` row counts as one test).

- [ ] **Step 9: Run the integration tests**

LocalDB must be startable (`sqllocaldb start MSSQLLocalDB`), or set `DBM_TEST_SQL`.

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --nologo --filter "FullyQualifiedName~Dbm.Tests.Integration.Samples.DemoCommandTests"`
Expected: `Passed! - Failed: 0, Passed: 3` in about 6 s. Afterwards
`sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "SELECT name FROM sys.databases WHERE name LIKE 'DbmT%'"` lists no rows (each test dropped its own two databases by name).

- [ ] **Step 10: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Samples/DemoDatabases.cs \
        plugins/db-migrate/engine/Dbm/Cli/Commands/DemoCommand.cs \
        plugins/db-migrate/engine/Dbm/Cli/Commands/ExportCommand.cs \
        plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs \
        plugins/db-migrate/engine/Dbm/Cli/Args.cs \
        plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DemoCommandTests.cs \
        plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ExportCommandTests.cs \
        plugins/db-migrate/engine/Dbm.Tests/Integration/Samples/DemoCommandTests.cs
git commit -m "$(cat <<'EOF'
feat(cli): add dbm demo and dbm export

dbm demo creates and seeds the LegacyShop/ShopV2 sample pair on any SQL
Server and can attach both connections to the current project; dbm export
writes the standalone exports through the project server.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
)"
```

---

### Task 6.2: Release build, dist, launcher/doctor hardening

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorChecks.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorCommand.cs` (`RunAsync` only), `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorCommandTests.cs` (one assertion)
- Replace: `plugins/db-migrate/bin/dbm`, `plugins/db-migrate/bin/dbm.cmd` (full text below; keeps every M0 behaviour)
- Create: `plugins/db-migrate/engine/release.ps1`, `plugins/db-migrate/engine/release.sh`
- Modify: `.gitignore`, `.gitattributes`
- Create (committed build output): `plugins/db-migrate/engine/dist/**` incl. `dist/VERSION`
- Create: `docs/superpowers/verification/cross-platform-check.md`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorChecksTests.cs`

**Interfaces:**

```csharp
namespace Dbm.Cli.Commands;
public static class DoctorChecks          // all checks reuse DoctorCommand.Check(Name, Ok, Detail)
{
    public const string ProbeText = "dbm-doctor-probe";
    public static IReadOnlyList<DoctorCommand.Check> Quick(string baseDirectory);                 // protector, dist (no network)
    public static Task<IReadOnlyList<DoctorCommand.Check>> AllAsync(Workspace ws, string baseDirectory, CancellationToken ct);
    public static DoctorCommand.Check Protector(Func<ISecretProtector>? factory = null);
    public static Task<DoctorCommand.Check> ServerAsync(Workspace ws, CancellationToken ct);
    public static DoctorCommand.Check Dist(string baseDirectory);
}
```

`dbm doctor` now prints 8 checks: the five machine checks from M0 plus `protector`, `server`, `dist`. `dbm doctor --quiet` runs only the checks that need no network (`protector`, `dist` on top of the machine checks) and stays silent when they all pass.

**Launcher contract (both launchers, on top of M0's):** a build is needed when `engine/dist/Dbm.dll` is missing, `DBM_REBUILD=1`, the command is `doctor --rebuild`, **or `engine/dist/VERSION` differs from the version in `plugin.json`**. With an SDK present the launcher takes the `engine/.build.lock` directory, re-checks (another launcher may have just built), best-effort `dotnet engine/dist/Dbm.dll stop`, publishes into `dist.tmp`, prunes the natives db-migrate never loads, writes `VERSION`, renames `dist` → `dist.old.<n>`, renames `dist.tmp` → `dist` and deletes the old one. A locked or failed build always leaves the previous `engine/dist` working. Without an SDK a *missing* dist is M0's exit 4, while a *stale* dist simply runs (`dbm doctor` reports it).

- [ ] **Step 1: Write the failing doctor tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorChecksTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Cli;
using Dbm.Cli.Commands;
using Dbm.Core;
using Dbm.Core.Crypto;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class DoctorChecksTests
{
    private sealed class FakeProtector(Func<string, string> protect, Func<string, string> unprotect) : ISecretProtector
    {
        public string Protect(string plaintext) => protect(plaintext);
        public string Unprotect(string protectedText) => unprotect(protectedText);
    }

    /// <summary>Builds &lt;root&gt;/plugin/{.claude-plugin/plugin.json, engine/dist/VERSION} and returns the dist path.</summary>
    private static string MakeDist(string root, string? pluginVersion, string? distVersion)
    {
        var plugin = Path.Combine(root, "plugin");
        var dist = Directory.CreateDirectory(Path.Combine(plugin, "engine", "dist")).FullName;
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        if (pluginVersion is not null)
        {
            File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"),
                "{\n  \"name\": \"db-migrate\",\n  \"version\": \"" + pluginVersion + "\"\n}\n");
        }
        if (distVersion is not null) File.WriteAllText(Path.Combine(dist, "VERSION"), distVersion);
        return dist + Path.DirectorySeparatorChar;   // the shape of AppContext.BaseDirectory
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Protector_round_trips_for_the_current_user()
    {
        var check = DoctorChecks.Protector();

        Assert.Equal("protector", check.Name);
        Assert.True(check.Ok, check.Detail);
        Assert.Contains("round-trip", check.Detail);
    }

    [Fact]
    public void Protector_reports_the_reason_when_it_throws()
    {
        var check = DoctorChecks.Protector(() => throw new InvalidOperationException("no key store"));

        Assert.False(check.Ok);
        Assert.Contains("no key store", check.Detail);
    }

    [Fact]
    public void Protector_fails_when_nothing_is_encrypted()
    {
        var check = DoctorChecks.Protector(() => new FakeProtector(p => p, p => p));

        Assert.False(check.Ok);
        Assert.Contains("plaintext", check.Detail);
    }

    [Fact]
    public void Protector_fails_when_decryption_returns_other_text()
    {
        var check = DoctorChecks.Protector(() => new FakeProtector(
            p => "fake:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p)), _ => "something else"));

        Assert.False(check.Ok);
        Assert.Contains("different text", check.Detail);
    }

    [Fact]
    public async Task Server_check_says_so_without_a_project()
    {
        using var tw = new TestWorkspace();

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.Equal("server", check.Name);
        Assert.True(check.Ok);
        Assert.Contains("no project", check.Detail);
    }

    [Fact]
    public async Task Server_check_says_not_running_for_a_project_without_server_json()
    {
        using var tw = new TestWorkspace();
        using (tw.OpenServices()) { }

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains("not running", check.Detail);
    }

    [Fact]
    public async Task Server_check_finds_a_running_server()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains($"running on {server.Info.BaseUrl}", check.Detail);
        Assert.DoesNotContain(server.Info.Token, check.Detail);
    }

    [Fact]
    public async Task Server_check_reports_a_stale_server_json()
    {
        using var tw = new TestWorkspace();
        using (tw.OpenServices()) { }
        File.WriteAllText(tw.Ws.ServerJsonPath, Json.Serialize(new ServerInfo(FreePort(), 999_999, "tok", Clock.Now())));

        var check = await DoctorChecks.ServerAsync(tw.Ws, CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains("stale server.json", check.Detail);
        Assert.DoesNotContain("tok", check.Detail);
    }

    [Fact]
    public void Dist_check_passes_when_the_versions_match()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.3.0", "0.3.0"));

        Assert.Equal("dist", check.Name);
        Assert.True(check.Ok);
        Assert.Contains("0.3.0 matches", check.Detail);
    }

    [Fact]
    public void Dist_check_fails_on_a_version_mismatch()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.4.0", "0.3.0"));

        Assert.False(check.Ok);
        Assert.Contains("0.3.0", check.Detail);
        Assert.Contains("0.4.0", check.Detail);
    }

    [Fact]
    public void Dist_check_fails_without_a_version_file()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(MakeDist(tw.Root, "0.4.0", null));

        Assert.False(check.Ok);
        Assert.Contains("VERSION", check.Detail);
    }

    [Fact]
    public void Dist_check_is_silent_for_a_development_build()
    {
        using var tw = new TestWorkspace();

        var check = DoctorChecks.Dist(Path.Combine(tw.Root, "bin", "Debug", "net8.0") + Path.DirectorySeparatorChar);

        Assert.True(check.Ok);
        Assert.Contains("development build", check.Detail);
    }

    [Fact]
    public async Task Doctor_reports_the_machine_and_project_checks()
    {
        var stdout = new StringWriter();

        var exit = await CliApp.RunAsync(["doctor"], stdout, new StringWriter());

        Assert.Equal(0, exit);
        var json = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(new[] { "runtime", "aspnetcore", "sqlclient", "sqlite", "home", "protector", "server", "dist" },
            json["checks"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
    }
}
```

In `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorCommandTests.cs` (T0.2), `Doctor_prints_json_report` asserts the number of checks. Change that one line:

```csharp
        Assert.Equal(8, json["checks"]!.AsArray().Count);   // 5 machine checks + protector, server, dist (T6.2)
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --nologo --filter "FullyQualifiedName~Dbm.Tests.Unit.Cli.DoctorChecksTests|FullyQualifiedName~Dbm.Tests.Unit.Cli.DoctorCommandTests"`
Expected: the build FAILS with `error CS0103: The name 'DoctorChecks' does not exist in the current context`.

- [ ] **Step 3: Implement `DoctorChecks`**

`plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorChecks.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Crypto;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>
/// The project- and installation-level checks appended to <see cref="DoctorCommand.RunChecks"/> (T6.2):
/// can this user encrypt and decrypt, is this project's server healthy, and does engine/dist match plugin.json.
/// </summary>
public static class DoctorChecks
{
    public const string ProbeText = "dbm-doctor-probe";

    /// <summary>protector + dist only: no network calls, so `doctor --quiet` stays fast for the SessionStart hook.</summary>
    public static IReadOnlyList<DoctorCommand.Check> Quick(string baseDirectory) => [Protector(), Dist(baseDirectory)];

    /// <summary>The quick checks plus this workspace's server status.</summary>
    public static async Task<IReadOnlyList<DoctorCommand.Check>> AllAsync(Workspace ws, string baseDirectory, CancellationToken ct) =>
        [Protector(), await ServerAsync(ws, ct), Dist(baseDirectory)];

    /// <summary>Encrypts and decrypts a probe value with this user's protector (DPAPI on Windows, AES-GCM key file elsewhere).</summary>
    public static DoctorCommand.Check Protector(Func<ISecretProtector>? factory = null)
    {
        try
        {
            var protector = (factory ?? SecretProtector.ForCurrentUser)();
            var sealedText = protector.Protect(ProbeText);
            if (sealedText.Contains(ProbeText, StringComparison.Ordinal))
                return new("protector", false, "the protector returned the plaintext: connection strings would not be encrypted");
            if (protector.Unprotect(sealedText) != ProbeText)
                return new("protector", false, $"decrypting a freshly encrypted value returned different text. {Hint()}");
            var colon = sealedText.IndexOf(':');
            return new("protector", true, $"{(colon > 0 ? sealedText[..colon] : "protector")} round-trip");
        }
        catch (Exception ex)
        {
            return new("protector", false, $"{ex.GetType().Name}: {ex.Message}. {Hint()}");
        }
    }

    /// <summary>Whether this workspace's server is running; never a failure, because it is started on demand.</summary>
    public static async Task<DoctorCommand.Check> ServerAsync(Workspace ws, CancellationToken ct)
    {
        if (!ws.Exists) return new("server", true, $"no project in {ws.Root}");
        var info = ServerControl.ReadInfo(ws);
        if (info is null) return new("server", true, "not running (starts on demand)");
        return await ServerControl.IsAliveAsync(info, ct)
            ? new("server", true, $"running on {info.BaseUrl} (pid {info.Pid})")
            : new("server", true, $"stale server.json: pid {info.Pid} on port {info.Port} does not answer; it restarts on demand");
    }

    /// <summary>engine/dist/VERSION against .claude-plugin/plugin.json; skipped (ok) when not running from engine/dist.</summary>
    public static DoctorCommand.Check Dist(string baseDirectory)
    {
        var dist = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)));
        if (!string.Equals(dist.Name, "dist", StringComparison.OrdinalIgnoreCase) || dist.Parent?.Parent is null)
            return new("dist", true, "development build (not engine/dist)");

        var manifest = Path.Combine(dist.Parent.Parent.FullName, ".claude-plugin", "plugin.json");
        if (!File.Exists(manifest)) return new("dist", true, "no plugin.json next to engine/dist");

        string? pluginVersion;
        try
        {
            pluginVersion = JsonNode.Parse(File.ReadAllText(manifest))?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return new("dist", false, $"plugin.json is unreadable: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(pluginVersion)) return new("dist", false, "plugin.json has no version");

        var versionFile = Path.Combine(dist.FullName, "VERSION");
        var distVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
        if (distVersion.Length == 0)
            return new("dist", false, $"engine/dist has no VERSION file (plugin.json is {pluginVersion}); {Rebuild}");
        return distVersion == pluginVersion
            ? new("dist", true, $"engine/dist {distVersion} matches plugin.json")
            : new("dist", false, $"engine/dist is {distVersion} but plugin.json is {pluginVersion}; {Rebuild}");
    }

    private const string Rebuild = "rebuild it with engine/release.ps1 (or release.sh), or DBM_REBUILD=1 dbm version";

    private static string Hint() => OperatingSystem.IsWindows()
        ? "DPAPI needs an interactive Windows user profile."
        : $"Check that {Path.Combine(UserHome.Dir, "key")} is 32 bytes and readable only by you (chmod 600).";
}
```

- [ ] **Step 4: Append the checks in `DoctorCommand`**

In `plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorCommand.cs` replace the whole `RunAsync` method (T0.2's version is synchronous and returns `Task.FromResult(...)`) with:

```csharp
    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        // --rebuild is handled by bin/dbm(.cmd) before this process starts; accepted here so it is not an error.
        var quiet = args.Flag("quiet");
        // T6.2: --quiet runs on every session start, so it skips the check that touches the network.
        var checks = RunChecks()
            .Concat(quiet
                ? DoctorChecks.Quick(AppContext.BaseDirectory)
                : await DoctorChecks.AllAsync(ctx.Workspace(), AppContext.BaseDirectory, CancellationToken.None))
            .ToList();
        var ok = checks.All(c => c.Ok);
        if (quiet)
        {
            if (ok) return 0;
            var problems = string.Join("; ", checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}"));
            ctx.Out.WriteLine($"dbm doctor: {problems}");
            return 1;
        }
        if (!ok) return Output.Write(ctx, new { ok, checks }, 1);
        return Output.Ok(ctx, new { ok, checks });
    }
```

`RunChecks()` and everything below it stay untouched, so M0's `All_checks_pass_on_a_healthy_machine` still sees exactly its five machine checks.

- [ ] **Step 5: Run the doctor tests**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --nologo --filter "FullyQualifiedName~Dbm.Tests.Unit.Cli.DoctorChecksTests|FullyQualifiedName~Dbm.Tests.Unit.Cli.DoctorCommandTests"`
Expected: `Passed! - Failed: 0, Passed: 16`.

- [ ] **Step 6: Replace the POSIX launcher**

Replace the whole content of `plugins/db-migrate/bin/dbm` with this. It keeps M0's structure, messages and exit codes (3 = runtime missing, 4 = not built / build failed) and adds the stale-version rebuild, the build lock, the stop-before-rebuild, the `dist.tmp` swap and the native pruning. On Windows this is the launcher Claude Code actually uses, because its Bash tool is Git Bash.

```sh
#!/bin/sh
# db-migrate launcher (macOS, Linux, Git Bash on Windows).
# Runs engine/dist/Dbm.dll with the installed .NET runtime; builds it when missing, when DBM_REBUILD=1, or when
# engine/dist/VERSION no longer matches plugin.json (needs the .NET SDK).
# Exit codes: 3 = runtime missing, 4 = engine not built / build failed; otherwise the engine's own exit code.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(dirname -- "$here")
engine="$root/engine"
dll="$engine/dist/Dbm.dll"
manifest="$root/.claude-plugin/plugin.json"
lock="$engine/.build.lock"

install_help() {
  echo "Install the ASP.NET Core Runtime 8 (or later), then open a new terminal:" >&2
  case "$(uname -s 2>/dev/null || echo unknown)" in
    Darwin) echo "  macOS:   brew install --cask dotnet" >&2 ;;
    Linux) echo "  Linux:   install package aspnetcore-runtime-8.0 (apt/dnf) - see https://learn.microsoft.com/dotnet/core/install/linux" >&2 ;;
    MINGW* | MSYS* | CYGWIN*) echo "  Windows: winget install Microsoft.DotNet.AspNetCore.8" >&2 ;;
    *) echo "  See https://dotnet.microsoft.com/download/dotnet/8.0" >&2 ;;
  esac
}

plugin_version() {
  sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" 2>/dev/null | head -n 1
}

dist_version() {
  if [ -f "$engine/dist/VERSION" ]; then tr -d ' \r\n' < "$engine/dist/VERSION"; fi
}

# The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker (SqlClient's Entra modes
# use MSAL's managed/browser path, not the broker) and runtimes for platforms this plugin does not target. Removing
# them keeps the committed engine/dist near 30 MB. Keep engine/release.* in sync with this list.
prune_natives() {
  target="$1/runtimes"
  [ -d "$target" ] || return 0
  for rid in browser browser-wasm maccatalyst-x64 maccatalyst-arm64 linux-armel linux-mips64 linux-ppc64le \
    linux-s390x linux-musl-s390x linux-riscv64 linux-musl-riscv64 linux-x86; do
    rm -rf "$target/$rid"
  done
  find "$target" -type f -name '*msalruntime*' -delete
}

# Why this is needed: the plugin ships a built engine/dist, so an update that changes plugin.json leaves a stale dist
# behind in a checkout that has the SDK. Publishing into dist.tmp and swapping keeps the old build usable if anything
# fails (on Windows a running server keeps its DLLs locked, so the rename is the only safe way to replace them).
build_engine() {
  echo "dbm: building the engine ($reason, about a minute)..." >&2
  if [ -f "$dll" ]; then dotnet "$dll" stop >/dev/null 2>&1 || true; fi
  rm -rf "$engine/dist.tmp"
  if ! dotnet publish "$engine/Dbm/Dbm.csproj" -c Release -o "$engine/dist.tmp" --nologo -v q \
      "-p:Version=${version:-0.0.0}" -p:DebugType=none -p:UseAppHost=false >&2; then
    rm -rf "$engine/dist.tmp"
    echo "dbm: engine build failed (see the messages above)." >&2
    exit 4
  fi
  prune_natives "$engine/dist.tmp"
  printf '%s' "${version:-0.0.0}" > "$engine/dist.tmp/VERSION"
  old="$engine/dist.old.$$"
  if [ -d "$engine/dist" ] && ! mv "$engine/dist" "$old" 2>/dev/null; then
    rm -rf "$engine/dist.tmp"
    echo "dbm: engine/dist is in use by a running dbm server - run 'dbm stop' in that project folder and retry." >&2
    exit 4
  fi
  mv "$engine/dist.tmp" "$engine/dist"
  rm -rf "$old" 2>/dev/null || true
}

acquire_lock() {
  waited=0
  while ! mkdir "$lock" 2>/dev/null; do
    waited=$((waited + 1))
    if [ "$waited" -gt 180 ]; then
      rm -rf "$lock"
      waited=0
    fi
    sleep 1
  done
}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dbm: 'dotnet' was not found on PATH." >&2
  install_help
  exit 3
fi

aspnet_major=$(dotnet --list-runtimes 2>/dev/null |
  awk '$1 == "Microsoft.AspNetCore.App" { split($2, v, "."); if (v[1] + 0 > m) m = v[1] + 0 } END { print m + 0 }')
if [ "$aspnet_major" -lt 8 ]; then
  echo "dbm: the ASP.NET Core Runtime 8 or later is required (found: ${aspnet_major:-none})." >&2
  install_help
  exit 3
fi

if [ "${1:-}" = "doctor" ]; then
  for arg in "$@"; do
    if [ "$arg" = "--rebuild" ]; then DBM_REBUILD=1; fi
  done
fi

version=$(plugin_version)
installed=$(dist_version)
reason=""
if [ ! -f "$dll" ]; then
  reason="engine/dist is missing"
elif [ "${DBM_REBUILD:-}" = "1" ]; then
  reason="rebuild requested"
elif [ -n "$version" ] && [ "$installed" != "$version" ]; then
  reason="engine/dist ${installed:-without VERSION} differs from plugin.json $version"
fi

if [ -n "$reason" ]; then
  if dotnet --list-sdks 2>/dev/null | grep -q .; then
    acquire_lock
    trap 'rmdir "$lock" 2>/dev/null || true' EXIT INT TERM
    installed=$(dist_version)   # another launcher may have built it while we waited for the lock
    if [ ! -f "$dll" ] || [ "${DBM_REBUILD:-}" = "1" ] || { [ -n "$version" ] && [ "$installed" != "$version" ]; }; then
      build_engine
    fi
    rmdir "$lock" 2>/dev/null || true
    trap - EXIT INT TERM
  elif [ ! -f "$dll" ]; then
    echo "dbm: engine not built ($dll is missing) and no .NET SDK is installed to build it." >&2
    echo "     Install the .NET 8 SDK or use a release of the plugin that ships engine/dist." >&2
    exit 4
  fi
  # A stale dist without an SDK still runs; `dbm doctor` reports the mismatch.
fi

exec dotnet "$dll" "$@"
```

- [ ] **Step 7: Replace the Windows launcher**

Replace the whole content of `plugins/db-migrate/bin/dbm.cmd` with this (same contract; keeps M0's `consider` subroutine, messages and exit codes). Messages must stay free of `( ) & | < >`, which cmd would parse.

```bat
@echo off
rem db-migrate launcher (Windows cmd / PowerShell).
rem Runs engine\dist\Dbm.dll with the installed .NET runtime; builds it when missing, when DBM_REBUILD=1, or when
rem engine\dist\VERSION no longer matches plugin.json (needs the .NET SDK).
rem Exit codes: 3 = runtime missing, 4 = engine not built / build failed; otherwise the engine's own exit code.
setlocal EnableExtensions DisableDelayedExpansion

set "DBM_ENGINE=%~dp0..\engine"
set "DBM_DLL=%DBM_ENGINE%\dist\Dbm.dll"
set "DBM_MANIFEST=%~dp0..\.claude-plugin\plugin.json"
set "DBM_LOCK=%DBM_ENGINE%\.build.lock"

where dotnet >nul 2>nul
if errorlevel 1 (
  >&2 echo dbm: 'dotnet' was not found on PATH.
  >&2 echo Install the ASP.NET Core Runtime 8 or later, then open a new terminal:
  >&2 echo   winget install Microsoft.DotNet.AspNetCore.8
  exit /b 3
)

set "DBM_ASPNET_MAJOR=0"
for /f "tokens=2" %%v in ('dotnet --list-runtimes 2^>nul ^| findstr /b /c:"Microsoft.AspNetCore.App "') do call :consider %%v
if %DBM_ASPNET_MAJOR% LSS 8 (
  >&2 echo dbm: the ASP.NET Core Runtime 8 or later is required - found major version %DBM_ASPNET_MAJOR%.
  >&2 echo Install it, then open a new terminal:
  >&2 echo   Windows: winget install Microsoft.DotNet.AspNetCore.8
  >&2 echo   macOS:   brew install --cask dotnet
  >&2 echo   Linux:   https://learn.microsoft.com/dotnet/core/install/linux
  exit /b 3
)

if /i "%~1"=="doctor" for %%a in (%*) do if /i "%%~a"=="--rebuild" set "DBM_REBUILD=1"

set "DBM_PLUGIN_VERSION="
if exist "%DBM_MANIFEST%" for /f "usebackq tokens=2 delims=:, " %%a in (`findstr /r /c:"^ *.version. *:" "%DBM_MANIFEST%"`) do if not defined DBM_PLUGIN_VERSION set "DBM_PLUGIN_VERSION=%%~a"
call :read_dist_version

set "DBM_REASON="
if not exist "%DBM_DLL%" set "DBM_REASON=engine\dist is missing"
if not defined DBM_REASON if "%DBM_REBUILD%"=="1" set "DBM_REASON=rebuild requested"
if not defined DBM_REASON if defined DBM_PLUGIN_VERSION if not "%DBM_DIST_VERSION%"=="%DBM_PLUGIN_VERSION%" set "DBM_REASON=engine\dist %DBM_DIST_VERSION% differs from plugin.json %DBM_PLUGIN_VERSION%"
if not defined DBM_REASON goto run

dotnet --list-sdks 2>nul | findstr /r "." >nul
if errorlevel 1 (
  rem A stale dist without an SDK still runs; `dbm doctor` reports the mismatch.
  if exist "%DBM_DLL%" goto run
  >&2 echo dbm: engine not built - %DBM_DLL% is missing and no .NET SDK is installed to build it.
  >&2 echo      Install the .NET 8 SDK or use a release of the plugin that ships engine\dist.
  exit /b 4
)

call :acquire_lock
call :build_engine
set "DBM_BUILD_EXIT=%ERRORLEVEL%"
rd "%DBM_LOCK%" 2>nul
if not "%DBM_BUILD_EXIT%"=="0" exit /b 4

:run
dotnet "%DBM_DLL%" %*
exit /b %ERRORLEVEL%

:consider
for /f "tokens=1 delims=." %%m in ("%~1") do if %%m GTR %DBM_ASPNET_MAJOR% set "DBM_ASPNET_MAJOR=%%m"
exit /b 0

:read_dist_version
set "DBM_DIST_VERSION="
if exist "%DBM_ENGINE%\dist\VERSION" set /p DBM_DIST_VERSION=<"%DBM_ENGINE%\dist\VERSION"
exit /b 0

:acquire_lock
set /a DBM_WAITED=0
:acquire_lock_loop
mkdir "%DBM_LOCK%" 2>nul && exit /b 0
set /a DBM_WAITED+=1
if %DBM_WAITED% GEQ 180 rd /s /q "%DBM_LOCK%" 2>nul & set /a DBM_WAITED=0
ping -n 2 127.0.0.1 >nul
goto acquire_lock_loop

rem Publishes into dist.tmp and swaps it in, so a failed or locked build always leaves a working engine\dist behind.
:build_engine
call :read_dist_version
if exist "%DBM_DLL%" if not "%DBM_REBUILD%"=="1" if defined DBM_PLUGIN_VERSION if "%DBM_DIST_VERSION%"=="%DBM_PLUGIN_VERSION%" exit /b 0
>&2 echo dbm: building the engine - %DBM_REASON% - about a minute...
if exist "%DBM_DLL%" dotnet "%DBM_DLL%" stop >nul 2>&1
if exist "%DBM_ENGINE%\dist.tmp" rd /s /q "%DBM_ENGINE%\dist.tmp"
set "DBM_BUILD_VERSION=%DBM_PLUGIN_VERSION%"
if not defined DBM_BUILD_VERSION set "DBM_BUILD_VERSION=0.0.0"
dotnet publish "%DBM_ENGINE%\Dbm\Dbm.csproj" -c Release -o "%DBM_ENGINE%\dist.tmp" --nologo -v q -p:Version=%DBM_BUILD_VERSION% -p:DebugType=none -p:UseAppHost=false 1>&2
if errorlevel 1 (
  rd /s /q "%DBM_ENGINE%\dist.tmp" 2>nul
  >&2 echo dbm: engine build failed - see the messages above.
  exit /b 1
)
call :prune_natives
<nul set /p "=%DBM_BUILD_VERSION%" >"%DBM_ENGINE%\dist.tmp\VERSION"
set "DBM_OLD=%DBM_ENGINE%\dist.old.%RANDOM%%RANDOM%"
if exist "%DBM_ENGINE%\dist" move "%DBM_ENGINE%\dist" "%DBM_OLD%" >nul 2>&1 || goto build_in_use
move "%DBM_ENGINE%\dist.tmp" "%DBM_ENGINE%\dist" >nul 2>&1 || goto build_no_swap
rd /s /q "%DBM_OLD%" 2>nul
exit /b 0

rem The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker - SqlClient's Entra
rem modes use MSAL's managed/browser path, not the broker - and runtimes for platforms this plugin does not target.
rem Removing them keeps the committed engine\dist near 30 MB. Keep engine\release.* in sync with this list.
:prune_natives
if not exist "%DBM_ENGINE%\dist.tmp\runtimes" exit /b 0
for %%r in (browser browser-wasm maccatalyst-x64 maccatalyst-arm64 linux-armel linux-mips64 linux-ppc64le linux-s390x linux-musl-s390x linux-riscv64 linux-musl-riscv64 linux-x86) do if exist "%DBM_ENGINE%\dist.tmp\runtimes\%%r" rd /s /q "%DBM_ENGINE%\dist.tmp\runtimes\%%r"
for /r "%DBM_ENGINE%\dist.tmp\runtimes" %%f in (*msalruntime*) do del /f /q "%%f" >nul 2>&1
exit /b 0

:build_in_use
rd /s /q "%DBM_ENGINE%\dist.tmp" 2>nul
>&2 echo dbm: engine\dist is in use by a running dbm server - run "dbm stop" in that project folder and retry.
exit /b 1

:build_no_swap
if exist "%DBM_OLD%" move "%DBM_OLD%" "%DBM_ENGINE%\dist" >nul 2>&1
rd /s /q "%DBM_ENGINE%\dist.tmp" 2>nul
>&2 echo dbm: engine\dist could not be replaced.
exit /b 1
```

- [ ] **Step 8: Ignore the transient build folders, keep dist tracked**

Append to `.gitignore`, directly after the `plugins/db-migrate/engine/**/obj/` line:

```gitignore
# Transient folders of the launcher/release build swap (engine/dist itself is committed)
plugins/db-migrate/engine/dist.tmp/
plugins/db-migrate/engine/dist.old.*/
plugins/db-migrate/engine/.build.lock/
```

Append to `.gitattributes` (M0 already pins the launcher line endings):

```gitattributes

# Committed build output: never convert line endings, never diff.
plugins/db-migrate/engine/dist/** binary
```

- [ ] **Step 9: Create the release scripts**

`plugins/db-migrate/engine/release.ps1`:

```powershell
#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
  Builds the committed engine/dist for the version in plugin.json.
.DESCRIPTION
  Checks that plugin.json, marketplace.json and Directory.Build.props agree on the version, runs the unit tests (and the
  node UI tests when node is installed), publishes Release into dist.tmp, swaps it into engine/dist with a VERSION file,
  smoke-tests it, validates the plugin with the claude CLI when available, and prints a size summary.
.EXAMPLE
  pwsh plugins/db-migrate/engine/release.ps1
#>
[CmdletBinding()]
param([switch]$SkipTests, [switch]$KeepAllRuntimes)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker (SqlClient's Entra modes
# use MSAL's managed/browser path, not the broker) and runtimes for platforms this plugin does not target. Removing them
# keeps the committed engine/dist near 30 MB. -KeepAllRuntimes publishes everything. Keep bin/dbm(.cmd) in sync.
$PrunedRuntimes = @(
    'browser', 'browser-wasm', 'maccatalyst-x64', 'maccatalyst-arm64', 'linux-armel', 'linux-mips64',
    'linux-ppc64le', 'linux-s390x', 'linux-musl-s390x', 'linux-riscv64', 'linux-musl-riscv64', 'linux-x86')

function Remove-UnusedNatives([string]$Root) {
    $runtimes = Join-Path $Root 'runtimes'
    if (-not (Test-Path $runtimes)) { return }
    foreach ($rid in $PrunedRuntimes) {
        $dir = Join-Path $runtimes $rid
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }
    Get-ChildItem $runtimes -Recurse -File | Where-Object Name -like '*msalruntime*' | Remove-Item -Force
}

$engine = $PSScriptRoot
$pluginRoot = Split-Path -Parent $engine
$repoRoot = Split-Path -Parent (Split-Path -Parent $pluginRoot)
$manifest = Join-Path $pluginRoot '.claude-plugin/plugin.json'
$dist = Join-Path $engine 'dist'
$tmp = Join-Path $engine 'dist.tmp'
$old = $null

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

$version = (Get-Content -Raw $manifest | ConvertFrom-Json).version
if ([string]::IsNullOrWhiteSpace($version)) { throw "No version in $manifest" }
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "plugin.json version '$version' is not SemVer (x.y.z[-pre])" }
Write-Host "db-migrate release $version" -ForegroundColor Cyan

$marketplace = Join-Path $repoRoot '.claude-plugin/marketplace.json'
if (Test-Path $marketplace) {
    $entry = (Get-Content -Raw $marketplace | ConvertFrom-Json).plugins | Where-Object { $_.name -eq 'db-migrate' }
    if ($entry -and $entry.PSObject.Properties['version'] -and $entry.version -ne $version) {
        throw "marketplace.json lists db-migrate $($entry.version) but plugin.json says $version"
    }
}
$propsVersion = ([xml](Get-Content -Raw (Join-Path $engine 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($propsVersion -ne $version) { throw "Directory.Build.props has <Version>$propsVersion</Version> but plugin.json says $version" }

if (-not $SkipTests) {
    Write-Host '== unit tests'
    Invoke-Checked 'unit tests' { dotnet test (Join-Path $engine 'Dbm.Tests') -c Release --nologo --filter 'Category!=Integration' }
    $jsDir = Join-Path $engine 'Dbm.Tests/js'
    if ((Get-Command node -ErrorAction SilentlyContinue) -and (Test-Path $jsDir)) {
        $jsFiles = @(Get-ChildItem $jsDir -Filter '*.test.cjs' | ForEach-Object FullName)
        if ($jsFiles.Count -gt 0) {
            Write-Host '== ui tests'
            Invoke-Checked 'ui tests' { node --test @jsFiles }
        }
    }
}

if (Test-Path (Join-Path $dist 'Dbm.dll')) {
    & dotnet (Join-Path $dist 'Dbm.dll') stop *> $null   # best effort: frees the DLLs if a server runs from this dist
}

Write-Host '== publish'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Invoke-Checked 'dotnet publish' {
    dotnet publish (Join-Path $engine 'Dbm/Dbm.csproj') -c Release -o $tmp --nologo `
        "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false
}
if (-not $KeepAllRuntimes) { Remove-UnusedNatives $tmp }
Set-Content -Path (Join-Path $tmp 'VERSION') -Value $version -NoNewline

if (Test-Path $dist) {
    $old = Join-Path $engine ('dist.old.' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Move-Item -LiteralPath $dist -Destination $old
    }
    catch {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
        throw "engine/dist is in use by a running dbm server. Run 'dbm stop' in each project folder that uses this checkout, then retry."
    }
}
Move-Item -LiteralPath $tmp -Destination $dist
if ($old) { Remove-Item -Recurse -Force $old -ErrorAction SilentlyContinue }

Write-Host '== smoke test'
$versionOut = (& dotnet (Join-Path $dist 'Dbm.dll') version) -join ' '
if ($LASTEXITCODE -ne 0 -or $versionOut -notmatch [regex]::Escape($version)) {
    throw "Smoke test failed: 'dbm version' printed: $versionOut"
}
Write-Host "  $versionOut"

if (Get-Command claude -ErrorAction SilentlyContinue) {
    Write-Host '== plugin validation'
    Invoke-Checked 'plugin validate' { claude plugin validate $pluginRoot }
    Invoke-Checked 'marketplace validate' { claude plugin validate $repoRoot }
}
else {
    Write-Warning "'claude' is not on PATH; skipped 'claude plugin validate'."
}

Write-Host '== size summary'
$files = @(Get-ChildItem $dist -Recurse -File)
$total = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ('  {0} files, {1:N1} MB in {2}' -f $files.Count, ($total / 1MB), $dist)
$files | Sort-Object Length -Descending | Select-Object -First 8 | ForEach-Object {
    Write-Host ('  {0,9:N0} KB  {1}' -f ($_.Length / 1KB), [IO.Path]::GetRelativePath($dist, $_.FullName))
}
if ($total -gt 60MB) { Write-Warning 'engine/dist is larger than 60 MB; check for unexpected dependencies before committing.' }

Write-Host ''
Write-Host "Next: git add -A plugins/db-migrate/engine/dist && git commit -m 'build: release engine $version'" -ForegroundColor Cyan
Write-Host "      claude plugin tag plugins/db-migrate --push   (creates db-migrate--v$version)" -ForegroundColor Cyan
```

`plugins/db-migrate/engine/release.sh`:

```sh
#!/bin/sh
# Builds the committed engine/dist for the version in plugin.json (the POSIX twin of release.ps1).
# Usage: sh plugins/db-migrate/engine/release.sh [--skip-tests] [--keep-all-runtimes]
set -eu

engine=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
plugin_root=$(dirname -- "$engine")
repo_root=$(dirname -- "$(dirname -- "$plugin_root")")
manifest="$plugin_root/.claude-plugin/plugin.json"
dist="$engine/dist"
tmp="$engine/dist.tmp"
old="$engine/dist.old.$$"
skip_tests=0
keep_all_runtimes=0
for arg in "$@"; do
  case "$arg" in
    --skip-tests) skip_tests=1 ;;
    --keep-all-runtimes) keep_all_runtimes=1 ;;
    *) echo "usage: release.sh [--skip-tests] [--keep-all-runtimes]" >&2; exit 1 ;;
  esac
done

die() {
  echo "release: $*" >&2
  exit 1
}

# The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker (SqlClient's Entra modes
# use MSAL's managed/browser path, not the broker) and runtimes for platforms this plugin does not target. Removing them
# keeps the committed engine/dist near 30 MB. --keep-all-runtimes publishes everything. Keep bin/dbm(.cmd) in sync.
prune_natives() {
  target="$1/runtimes"
  [ -d "$target" ] || return 0
  for rid in browser browser-wasm maccatalyst-x64 maccatalyst-arm64 linux-armel linux-mips64 linux-ppc64le \
    linux-s390x linux-musl-s390x linux-riscv64 linux-musl-riscv64 linux-x86; do
    rm -rf "$target/$rid"
  done
  find "$target" -type f -name '*msalruntime*' -delete
}

version=$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
[ -n "$version" ] || die "no version in $manifest"
echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' || die "plugin.json version '$version' is not SemVer (x.y.z[-pre])"
echo "db-migrate release $version"

props_version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$engine/Directory.Build.props" | head -n 1)
[ "$props_version" = "$version" ] || die "Directory.Build.props has <Version>$props_version</Version> but plugin.json says $version"

if [ "$skip_tests" = 0 ]; then
  echo "== unit tests"
  dotnet test "$engine/Dbm.Tests" -c Release --nologo --filter "Category!=Integration" || die "unit tests failed"
  if command -v node >/dev/null 2>&1 && ls "$engine"/Dbm.Tests/js/*.test.cjs >/dev/null 2>&1; then
    echo "== ui tests"
    node --test "$engine"/Dbm.Tests/js/*.test.cjs || die "ui tests failed"
  fi
fi

if [ -f "$dist/Dbm.dll" ]; then dotnet "$dist/Dbm.dll" stop >/dev/null 2>&1 || true; fi

echo "== publish"
rm -rf "$tmp"
dotnet publish "$engine/Dbm/Dbm.csproj" -c Release -o "$tmp" --nologo \
  "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false || die "dotnet publish failed"
[ "$keep_all_runtimes" = 1 ] || prune_natives "$tmp"
printf '%s' "$version" > "$tmp/VERSION"

if [ -d "$dist" ]; then
  mv "$dist" "$old" 2>/dev/null || { rm -rf "$tmp"; die "engine/dist is in use by a running dbm server; run 'dbm stop' in each project folder, then retry"; }
fi
mv "$tmp" "$dist"
rm -rf "$old" 2>/dev/null || true

echo "== smoke test"
version_out=$(dotnet "$dist/Dbm.dll" version) || die "dbm version failed"
case "$version_out" in
  *"$version"*) echo "  $version_out" ;;
  *) die "smoke test failed: dbm version printed: $version_out" ;;
esac

if command -v claude >/dev/null 2>&1; then
  echo "== plugin validation"
  claude plugin validate "$plugin_root" || die "plugin validate failed"
  claude plugin validate "$repo_root" || die "marketplace validate failed"
else
  echo "warning: 'claude' is not on PATH; skipped 'claude plugin validate'" >&2
fi

echo "== size summary"
files=$(find "$dist" -type f | wc -l | tr -d ' ')
kb=$(du -sk "$dist" | cut -f1)
echo "  $files files, $((kb / 1024)) MB in $dist"
find "$dist" -type f -exec du -k {} + | sort -n -r | head -n 8 | while IFS="$(printf '\t')" read -r size path; do
  printf '  %9s KB  %s\n' "$size" "${path#"$dist"/}"
done
[ "$kb" -le 61440 ] || echo "warning: engine/dist is larger than 60 MB; check for unexpected dependencies before committing" >&2

echo
echo "Next: git add -A plugins/db-migrate/engine/dist && git commit -m 'build: release engine $version'"
echo "      claude plugin tag plugins/db-migrate --push   (creates db-migrate--v$version)"
```

Mark both executable: `git update-index --chmod=+x plugins/db-migrate/engine/release.sh` (after `git add`).

- [ ] **Step 10: Build the release**

Run: `pwsh -NoProfile -File plugins/db-migrate/engine/release.ps1`
Expected, in order: `db-migrate release 0.1.0`; the unit tests pass; the UI tests pass; `== publish`; `== smoke test` printing `{"version":"0.1.0","runtime":".NET 8.0.28"}`; `== plugin validation` with `✔ Validation passed` twice; `== size summary` reporting **86 files, 29.5 MB** with `runtimes\win-x64\native\e_sqlite3.dll` (1,932 KB) at the top and no size warning.

Then check what will be committed:

```bash
git check-ignore -v plugins/db-migrate/engine/dist/Dbm.dll; echo "check-ignore exit: $?"
ls plugins/db-migrate/engine/dist/*.exe plugins/db-migrate/engine/dist/*.pdb 2>&1 | head -3
cat plugins/db-migrate/engine/dist/VERSION; echo
```

Expected: `check-ignore exit: 1` (dist is **not** ignored), "No such file or directory" for both `*.exe` and `*.pdb` (no apphost, no symbols), and `VERSION` printing `0.1.0`.

- [ ] **Step 11: Verify the launchers by hand**

From the repository root, in Git Bash:

```bash
E=plugins/db-migrate/engine
printf '0.0.0-stale' > $E/dist/VERSION
bash plugins/db-migrate/bin/dbm version
cat $E/dist/VERSION; du -sh $E/dist
ls -d $E/dist.tmp $E/dist.old.* $E/.build.lock 2>/dev/null
bash plugins/db-migrate/bin/dbm version
```

Expected:
1. stderr `dbm: building the engine (engine/dist 0.0.0-stale differs from plugin.json 0.1.0, about a minute)...`, then stdout `{"version":"0.1.0","runtime":".NET 8.0.28"}`;
2. `VERSION` is `0.1.0` again and the folder is back to `30M`;
3. the `ls` prints nothing — no `dist.tmp`, `dist.old.*` or `.build.lock` is left behind;
4. the second run prints only the JSON, with no build line.

Then in PowerShell 7:

```powershell
.\plugins\db-migrate\bin\dbm.cmd version; "exit=$LASTEXITCODE"
.\plugins\db-migrate\bin\dbm.cmd doctor --quiet; "quiet exit=$LASTEXITCODE"
```

Expected: the same version JSON with `exit=0`, and no output at all from `doctor --quiet` with `quiet exit=0`.

- [ ] **Step 12: Create the cross-platform check record**

Create `docs/superpowers/verification/cross-platform-check.md` with the template below and fill in the Windows rows from Steps 10–11 (the macOS/Linux rows are for a teammate's machine).

````markdown
# db-migrate — cross-platform launcher check

Fill in the Result column (PASS / FAIL + note) and the header fields. Commands run from the repository root.

- Date:
- Tester:
- Commit:
- Plugin version (plugin.json):
- engine/dist size (from the release summary):

## Windows (verified in M6)

| # | Check | Command | Expected | Result |
|---|---|---|---|---|
| W1 | sh launcher syntax | `sh -n plugins/db-migrate/bin/dbm` | no output, exit 0 | |
| W2 | sh launcher under Git Bash | `bash plugins/db-migrate/bin/dbm version` | `{"version":"<v>","runtime":"..."}`, no build line | |
| W3 | cmd launcher | `plugins\db-migrate\bin\dbm.cmd version` (PowerShell) | the same JSON line | |
| W4 | exec bit + line endings | `git ls-files -s plugins/db-migrate/bin/dbm` ; `git ls-files --eol plugins/db-migrate/bin/*` | `100755`; `dbm` `i/lf`, `dbm.cmd` `attr/text eol=crlf` | |
| W5 | doctor | `plugins\db-migrate\bin\dbm.cmd doctor` | `ok:true`; 8 checks; `protector` detail starts `dpapi`; `dist` says it matches plugin.json | |
| W6 | stale dist rebuilds | write `0.0.0-stale` into `engine/dist/VERSION`, run the launcher again | stderr `dbm: building the engine (engine/dist 0.0.0-stale differs from plugin.json <v>, about a minute)...`, then the version JSON; `VERSION` back to `<v>`; no `dist.tmp`, `dist.old.*` or `.build.lock` left behind | |
| W7 | forced rebuild | `DBM_REBUILD=1 bash plugins/db-migrate/bin/dbm version` | build line `rebuild requested`, then the JSON | |
| W8 | locked dist | start a server (`dbm init` in a temp folder), then force a rebuild from the repo | either `engine\dist is in use by a running dbm server ...` and the old build keeps working, or the rebuild succeeds and a `dist.old.*` folder remains until that server stops — record which | |
| W9 | runtime missing | `env -u DOTNET_ROOT PATH=/usr/bin:/bin HOME=/tmp bash plugins/db-migrate/bin/dbm version` | `dbm: 'dotnet' was not found on PATH.` plus the winget hint, exit 3 | |

## macOS / Linux (expected behaviour — run on a teammate's machine and record)

The same `bin/dbm` runs everywhere. Only two things differ from Windows:

1. **Detached server spawn.** `ServerControl` starts `dotnet Dbm.dll serve --workspace <root> --detached` through
   `/bin/sh -c 'nohup … </dev/null >/dev/null 2>&1 &'`, so the server survives the tool call, the terminal and Claude
   itself. (On Windows the same effect comes from `UseShellExecute=true` with a hidden window.)
2. **Key storage.** Connection strings are encrypted with AES-GCM using a 32-byte key at `~/.dbmigrate/key` (or
   `$DBM_HOME/key`), created with mode `0600`; encrypted values start with `aesgcm:`. Windows uses DPAPI (`dpapi:`) and
   has no key file.

| # | Check | Command | Expected | Result |
|---|---|---|---|---|
| U1 | launcher | `plugins/db-migrate/bin/dbm version` | the version JSON; no apphost needed (dist has no `Dbm` executable) | |
| U2 | runtime hint | on a machine without ASP.NET Core 8+: `plugins/db-migrate/bin/dbm version` | exit 3 with the apt/dnf (Linux) or brew (macOS) hint | |
| U3 | detached server | `mkdir /tmp/dbmx && cd /tmp/dbmx && <repo>/plugins/db-migrate/bin/dbm init x --no-browser`, close the terminal, then in a new one: `curl -s "http://127.0.0.1:<port>/api/health" -H "X-Dbm-Token: <token from .dbmigrate/server.json>"` | `{"ok":true,"pid":<pid>}` after the first terminal is gone | |
| U4 | own session | `ps -o pid,ppid,pgid,command -p <pid>` | PPID is 1 (nohup'd), command is `dotnet …/Dbm.dll serve --workspace /tmp/dbmx --detached` | |
| U5 | key file mode | Linux `stat -c '%a %s' ~/.dbmigrate/key` · macOS `stat -f '%Lp %z' ~/.dbmigrate/key` | `600 32` | |
| U6 | doctor | `plugins/db-migrate/bin/dbm doctor` | `ok:true`; `protector` detail starts `aesgcm` | |
| U7 | cleanup | `cd /tmp/dbmx && <repo>/plugins/db-migrate/bin/dbm stop` | `{"ok":true,...}`; the pid is gone | |
````

- [ ] **Step 13: Commit**

```bash
git add .gitattributes .gitignore \
        plugins/db-migrate/bin/dbm plugins/db-migrate/bin/dbm.cmd \
        plugins/db-migrate/engine/release.ps1 plugins/db-migrate/engine/release.sh \
        plugins/db-migrate/engine/dist \
        plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorChecks.cs \
        plugins/db-migrate/engine/Dbm/Cli/Commands/DoctorCommand.cs \
        plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorChecksTests.cs \
        plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/DoctorCommandTests.cs \
        docs/superpowers/verification/cross-platform-check.md
git update-index --chmod=+x plugins/db-migrate/engine/release.sh
git commit -m "$(cat <<'EOF'
build: release scripts, committed engine/dist and launcher/doctor hardening

Launchers rebuild a dist whose VERSION no longer matches plugin.json, under
a lock, into dist.tmp, swapping it in only when the build succeeded. dbm
doctor gains the protector round-trip, the project server and the dist
version. release.ps1/release.sh produce the committed 30 MB engine/dist.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
)"
```

---

### Task 6.3: Docs + skill polish + troubleshooting

**Files:**
- Create: `README.md` (repository root), `docs/README.md` (user guide), `plugins/db-migrate/skills/db-migrate/reference/troubleshooting.md`
- Modify: `plugins/db-migrate/skills/db-migrate/SKILL.md` (T1.10) — four additions, loop unchanged

**Interfaces:** documentation only. Everything it states must match the shipped behaviour: launcher exit codes 3/4 with plain-text stderr (not JSON), CLI error codes `no_project`, `usage`, `unknown_command`, `internal`, `demo_exists`, `locked`, `sql_error`, `not_found`, `export_unavailable`, the eight `dbm doctor` checks, the export kinds `analysis`, `sql`, `sqlpack`, `report`, and the sub-commands `/db-migrate [resume|status|ui|stop]`.

- [ ] **Step 1: Polish SKILL.md**

T1.10's file stays as it is except for four additions: the demo exception in the first hard rule, a new hard rule pointing at the troubleshooting reference, and step 4 for surfaces that do not wake on a finished background command. Replace `plugins/db-migrate/skills/db-migrate/SKILL.md` with:

````markdown
---
name: db-migrate
description: Migrate or copy data from one SQL Server database into another existing SQL Server database with a different schema — schema analysis, source-to-target mapping and migration SQL reviewed by a human in a local web UI, then a resumable transfer. Use when the user asks to migrate, move, copy or transfer data between SQL Server databases, map a legacy schema to a new one, or types /db-migrate.
argument-hint: "[resume|status|ui|stop]"
---

# db-migrate orchestrator

The `dbm` engine (on PATH via this plugin) owns all state and decides every step. You only relay between `dbm`, the
three db-migrate subagents and the user. Keep every message to the user to ONE line, except the final completion
report or an error.

## Hard rules

- NEVER ask for, accept, print or repeat a connection string. Connections are entered only in the browser UI — or, for
  the sample databases, by running `dbm demo --server "<connection string the user gave you>" --attach`.
- Do not read `.dbmigrate/state.db`, `server.json` or work packets yourself — subagents read their packet.
- Do not edit artifacts yourself; changes come only from subagent patches applied with `dbm apply`.
- When a `dbm` command fails (non-zero exit, or `{"error":...}`), read `reference/troubleshooting.md` next to this file
  and follow the remedy for that error before doing anything else.

## Arguments

- none or `resume` → run the loop below.
- `status` → run `dbm status`, report phase and status in one line, stop.
- `ui` → run `dbm ui`, tell the user the page was opened (give the `url` if they ask), stop.
- `stop` → run `dbm stop`, say the UI server is stopped, stop.

## Loop

1. Run `dbm init` (safe to repeat; it reuses the project in this folder). If it printed `"created":true`, tell the user:
   "Opened the db-migrate UI in your browser — paste the source and target connection strings there."
2. Run `dbm next` and act on the `action` field of its JSON:
   - `agent` → use the Agent tool with `subagent_type: "db-migrate:<agent>"` and this exact prompt:
     `Work packet: <packet>\nWrite your patch to: <patchPath>\nFollow your playbook. Reply with one line.`
     Then run `dbm apply <patchPath>`.
     - If `apply` exits non-zero, dispatch the same subagent once more with the prompt above plus
       `\nThe previous patch was rejected: <errors joined with "; ">`, then `dbm apply <patchPath>` again.
     - If it fails again, tell the user in one line that the <phase> step needs attention in the UI, and continue.
     Go back to step 2.
   - `await` → run `dbm await` with the Bash tool's `run_in_background: true`, then end your turn with at most one short line:
     `reason` = `review` → "Waiting for your review of <phase> in the browser."; `setup` → "Waiting for the connection
     strings in the browser."; `execute` → "Ready to run the transfer from the browser."; `paused` → "Paused — press
     Resume in the UI to continue."; `job` / `transfer` / `transfer_paused` → "Working in the background — I'll continue
     when it's done." When the background command completes, read its output (a NextAction JSON) and handle it exactly
     like the output of `dbm next`.
   - `stop` → report and end:
     - `complete` → up to 3 lines: the `summary`, and that the final report is in the UI.
     - `job_failed` → one line with the `summary`; suggest fixing the cause and pressing Retry in the UI.
     - `transfer_failed` / `transfer_cancelled` → one line with the `summary`.
     - `server_stopped` → one line: the UI server was stopped; `/db-migrate resume` continues.
3. If a background `dbm await` fails or times out, run `dbm next` and continue from step 2.
4. If the user says nothing happened after they acted in the browser, this surface does not wake on a finished
   background command: from then on run `dbm await --timeout 540` in the foreground, repeating while it returns `await`.
````

- [ ] **Step 2: Write the orchestrator troubleshooting reference**

Create `plugins/db-migrate/skills/db-migrate/reference/troubleshooting.md`:

````markdown
# db-migrate troubleshooting (orchestrator reference)

Read this when a `dbm` command exits non-zero, prints `{"error":"<code>","message":"<text>"}`, or the user reports a
problem. Apply the remedy and tell the user **one line** (plus the exact command they must run, if any). Never ask for
or repeat a connection string; connection problems are fixed by the user in the browser Setup screen.

## Launcher and engine (before the CLI even runs)

The launcher (`bin/dbm`, `bin/dbm.cmd`) writes plain text to stderr, not JSON:

- **exit 3, "'dotnet' was not found" or "the ASP.NET Core Runtime 8 or later is required"** — give the user the install
  command for their OS from the message (Windows `winget install Microsoft.DotNet.AspNetCore.8`; macOS
  `brew install --cask dotnet`; Linux package `aspnetcore-runtime-8.0`) and tell them to restart Claude Code afterwards.
  Nothing else works until then.
- **exit 4, "engine not built ... and no .NET SDK is installed"** — the plugin copy has no `engine/dist`. Tell the user
  to run `claude plugin update db-migrate@db-migrate` (releases ship the build) or install the .NET 8 SDK, then restart.
- **exit 4, "engine build failed"** — a development checkout failed to compile. Report the first compiler error from the
  output; suggest `dotnet build plugins/db-migrate/engine/Dbm.sln` to see it in full.
- **"engine/dist is in use by a running dbm server"** — a rebuild could not replace the engine because another project's
  server is running from it. The command still ran on the existing build. Suggest `dbm stop` in that folder; no rush.

`dbm doctor` prints one line per check (`runtime`, `aspnetcore`, `sqlclient`, `sqlite`, `home`, `protector`, `server`,
`dist`). Report any check whose `ok` is false, with its `detail`.

- **`protector` false** — connection strings cannot be encrypted or read back. On Windows this needs an interactive user
  profile; elsewhere check `~/.dbmigrate/key` (32 bytes, mode 600). Saved connections may have to be entered again.
- **`dist` false** — the built engine does not match the plugin version. In a checkout with the .NET SDK the launcher
  rebuilds by itself; otherwise tell the user to update the plugin or run `plugins/db-migrate/engine/release.ps1`.

## CLI errors

- **`no_project`** — the command ran outside a project folder. Run `dbm init` in the folder the user means (ask which
  one if unclear), or `cd` to the folder that contains `.dbmigrate/`.
- **`usage`** — a wrong or missing argument; the message shows the correct form.
- **`unknown_command`** — the engine is older or newer than this skill. Run `dbm version` and `dbm doctor`, and tell the
  user to update the plugin (`claude plugin update db-migrate@db-migrate`, then restart Claude Code).
- **`internal`** — an unexpected engine error. Report the message; `dbm status` shows whether the project is still usable.

## Server and browser

**"The dbm server did not start within 20 s"** (from `dbm init`, `dbm ui`, `dbm await`, `dbm export`):

1. Run `dbm stop`, then `dbm ui` again.
2. If it still fails, read the last 40 lines of `.dbmigrate/server.log` with the Read tool and act on it: "address
   already in use" → `dbm stop` and retry; "database is locked" → another `dbm` process holds the project, wait 10 s;
   access denied → the folder is read-only or on a synced/network drive, so suggest a local folder.
3. Run `dbm doctor` and report any failing check.

**`unauthorized` (401) in the browser** — the server restarted and issued a new token, so the old tab is dead. Run
`dbm ui` and give the user the new URL.

**`forbidden_host` (403)** — the page was opened through another host name or a proxy. Tell the user to use the exact
`http://127.0.0.1:<port>/?t=<token>` URL that `dbm ui` prints.

**"Agent offline" banner** — only means no Claude session is running the loop. If you are running, start a background
`dbm await` again (you probably ended the loop).

## Workflow

**`stop` with reason `job_failed`** — a server job (discover, analyze, automap, sqlgen) failed; `summary` holds the
error. Common causes: connection or login failure (see *Connections*), missing `VIEW DEFINITION` / read permission on
the source, or a timeout on a very large database. Tell the user the cause and that **Retry** on the failed phase in the
UI re-runs the job, then start a background `dbm await` (Retry changes state and wakes you).

**`dbm apply` rejected**

- The message says the base version changed, or the phase is no longer `drafting`/`reworking`: the human edited or
  approved meanwhile. Not a failure — run `dbm next` and continue.
- Validation errors (unknown table or column, bad JSON pointer, a feedback item without a response, SQL validation
  failed): dispatch the same subagent once more with the packet plus `The previous patch was rejected: <message>`, then
  `dbm apply` again.
- Rejected twice: stop retrying. One line to the user: "The <phase> patch was rejected twice: <short message>. Add
  clarifying feedback or edit directly in the UI, then press Request changes." Keep a background `dbm await`.

**Approval blocked (409 `blocked` in the UI)** — mapping approval needs every source column mapped or dropped and every
non-nullable target column without a default filled in. The UI lists the blockers; the user resolves them there.

**Drift detected** (`drift_detected` event, or approval refused because a schema changed) — tell the user to press
**Re-discover** in the UI. Re-discovery marks the later phases stale and the loop regenerates them for review.

**Phase shows `stale`** — an upstream phase was reopened; nothing to fix. After the upstream approval the engine re-runs
the stale phase and `dbm next` returns work again.

**`await` with reason `paused`** — the user paused the project. Say "Paused — press Resume in the UI to continue." and
keep a background `dbm await`. A patch you already hold can still be applied.

## Transfer

Details and options are in `reference/transfer.md`. The short version:

- **`await` / `transfer_paused`** — the user paused the run; **Resume** on the Execute screen continues from the last
  committed chunk. Keep a background `dbm await`.
- **`stop` / `transfer_failed`** — `summary` holds the error. Typical: row errors with *stop on error* (the failing rows
  are listed in the report; fix the mapping or resume with *skip and log*), a full transaction log on the target (grow
  it or switch to SIMPLE recovery, then Resume), a dropped connection (Resume continues from the checkpoint), or missing
  `ALTER`/`INSERT` permission on a target table. Report the remedy in one line, then keep a background `dbm await`.
- **`stop` / `transfer_cancelled`** — the user cancelled; rows already committed stay in the target. Report and end.

## Connections (the user fixes these in the Setup screen)

Never ask for the connection string. Match the error text the user or the job reports:

| Error text contains | Remedy to tell the user |
|---|---|
| `certificate chain was issued by an authority that is not trusted`, `SSL Provider` | SqlClient encrypts by default: add `TrustServerCertificate=True` for test servers, or install the server certificate's CA, or set `HostNameInCertificate=<name on the certificate>`. |
| `A network-related or instance-specific error`, `error: 26`, `error: 40` | Wrong host, instance or port, or a firewall. Use `Server=tcp:<host>,<port>`; named instances need the SQL Browser (UDP 1434) reachable. |
| `Login failed for user`, `18456` | Wrong credentials, a disabled login, or no access to the database in `Initial Catalog`. With Windows auth the login is the user running Claude Code. |
| `Cannot open database`, `4060` | `Database`/`Initial Catalog` names a database that does not exist or is not accessible to this login. |
| `Cannot find an authentication provider for 'ActiveDirectory<Mode>'` | The engine is missing the Entra ID package: update the plugin (`claude plugin update db-migrate@db-migrate`). |
| `AADSTS...`, `DefaultAzureCredential failed` | Entra sign-in failed. `Active Directory Default` uses an existing `az login` / IDE sign-in; service principals need `Authentication=Active Directory Service Principal;User Id=<app id>;Password=<secret>`; `Managed Identity` works only on an Azure host. |
| `(localdb)`, `error: 50` | LocalDB is Windows-only and per user: `sqllocaldb start MSSQLLocalDB` in a terminal. |

After the user saves a corrected connection the engine re-runs discovery by itself; keep a background `dbm await`.

## Demo and exports

- **`demo_exists`** — the demo databases exist already. Re-run with `--force` (drops and recreates exactly those two
  databases) or choose another `--prefix`.
- **`locked`** (`dbm demo --attach`) — this project already started a transfer; run the demo in a new, empty folder.
- **`sql_error`** (`dbm demo`) — usually the login on `--server` cannot connect or lacks `CREATE DATABASE` (`dbcreator`).
- **`not_found`** (`dbm export`) — unknown export kind. Valid kinds: `analysis`, `sql`, `sqlpack`, `report`.
- **`export_unavailable`** (`dbm export`) — that phase has nothing to export yet (e.g. `report` before a completed run).
````

- [ ] **Step 3: Write the root README**

Create `README.md` at the repository root with exactly the content below.

````markdown
# db-migrate

A Claude Code plugin that moves data from one SQL Server database into another **existing SQL Server database with a
different schema**. You approve every step in a local web page:

1. **Analysis** — both schemas are extracted and profiled; you get a risk and data-quality report.
2. **Mapping** — an auto-mapper pairs tables and columns, Claude resolves the uncertain ones, you review or edit.
3. **Migration SQL** — generated, validated against the real databases, reviewed line by line, downloadable as a script pack.
4. **Transfer** — chunked, checkpointed and resumable, with live progress, rejected-row capture and a final report.

Pause at any point, close Claude Code, come back tomorrow: everything is stored in your project folder and the engine
knows where to continue.

## Requirements

| What | Details |
|---|---|
| Claude Code | CLI or the Desktop app (Code tab, **local** session). Cloud sessions are not supported. |
| ASP.NET Core Runtime 8 or later | One installer that also contains the .NET runtime. Windows: `winget install Microsoft.DotNet.AspNetCore.8` · macOS: `brew install --cask dotnet` · Debian/Ubuntu: `sudo apt-get install -y aspnetcore-runtime-8.0` · Fedora/RHEL: `sudo dnf install aspnetcore-runtime-8.0`. Check with `dotnet --list-runtimes` (look for `Microsoft.AspNetCore.App 8.x` or later). |
| SQL Server source and target | Both reachable from **your machine**. They may live on different servers; no linked server is needed, because the data streams through the local engine. Developed against SQL Server 2025 and LocalDB. |
| Permissions | Source: read access plus `VIEW DEFINITION` (and `VIEW DATABASE STATE` for size estimates). Target: `db_datareader`, `db_datawriter` and `db_ddladmin`, or equivalent: `INSERT` and `ALTER` on the target tables (identity insert, constraint `NOCHECK`) plus `CREATE TABLE` for the `dbo.__dbm_checkpoint` control table. |
| A browser | Any modern browser. The UI has no CDN and works offline. |

Every authentication mode Microsoft.Data.SqlClient supports works verbatim: SQL logins, Windows integrated/Kerberos and
the Entra ID modes (`Active Directory Default`, `Interactive`, `Integrated`, `Service Principal`, `Device Code Flow`,
`Managed Identity`, `Workload Identity`).

## Install

In Claude Code:

```
/plugin marketplace add <git-url-of-this-repo>
/plugin install db-migrate@db-migrate
```

`<git-url-of-this-repo>` is the HTTPS or SSH URL you would clone this repository from (on GitHub, `owner/repo` works
too). From a local clone use its path with forward slashes, e.g. `/plugin marketplace add D:/src/DatamigrationAgent`.
Start a new Claude Code session afterwards.

The same from a terminal: `claude plugin marketplace add <git-url-of-this-repo>` then
`claude plugin install db-migrate@db-migrate`. Updates: `claude plugin marketplace update db-migrate` and
`claude plugin update db-migrate@db-migrate`, then restart Claude Code.

Each session start runs `dbm doctor --quiet`, which is silent when everything is healthy and otherwise prints exactly
what is wrong (missing runtime, unusable encryption, stale engine build).

## Quick start with the demo (5 minutes)

The demo creates a sample source (`DbmDemo_LegacyShop`: cryptic names, a heap, orphan rows, over-long comments, an
untrusted foreign key) and a redesigned target (`DbmDemo_ShopV2`: renamed tables, split name columns, stricter types, a
CHECK constraint, an FK cycle, a trigger).

1. Create an empty folder, open a terminal there and run `claude`.
2. Type `/db-migrate`. Claude creates the project (`.dbmigrate/`), starts the local server and opens the browser on the
   **Setup** screen.
3. Ask Claude to create and attach the demo databases — for LocalDB on Windows:

   ```
   run: dbm demo --server "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true" --attach
   ```

   For a local SQL Server use `--server "Server=localhost;Integrated Security=true;TrustServerCertificate=true"`.
   `--attach` saves both connections into the project, so there is nothing to paste. Useful flags: `--force` (drop and
   recreate exactly those two demo databases), `--prefix Team_` (other names), `--scale 10` (ten times the rows).
4. In the browser, discovery runs by itself. Then review each phase: **Analysis** (comment on a finding, press *Request
   changes*, then *Approve* the reworked version), **Mapping** (check the auto-mapped columns, fix a transform, approve),
   **SQL** (read the generated tasks, approve).
5. On **Execute**, choose *skip and log* for row errors, type the target database name and press **Execute**. The final
   report shows **8 rejected rows**: 2 orphan orders, their 2 order lines, 3 comments too long for the target column and
   1 zero-quantity line. They are wrong on purpose, to show how bad rows are captured.

Afterwards, drop the two demo databases, e.g.
`sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "DROP DATABASE DbmDemo_LegacyShop; DROP DATABASE DbmDemo_ShopV2"`.

## Using it on real databases

1. Run `/db-migrate` in your project folder.
2. In the browser **Setup** screen paste the source and target ADO.NET connection strings, press **Test**, then **Save**.
   Never paste connection strings into the Claude chat — Claude never needs them.
3. Review and approve Analysis → Mapping → SQL, then run the transfer from **Execute**. The
   [user guide](docs/README.md) walks through every screen.

Sub-commands: `/db-migrate` (start or continue here) · `/db-migrate resume` (continue in a new session) ·
`/db-migrate status` · `/db-migrate ui` (re-open the browser) · `/db-migrate stop` (stop the local server).

## How it works

```
Claude Code session ── /db-migrate skill + 3 subagents ──> dbm (CLI, on PATH via the plugin)
                                                             │
                                  detached local server ─────┤  state: <project>/.dbmigrate/state.db (SQLite)
                                  (http://127.0.0.1:<port>)  │
                                         ▲                   └─ SQL Server source ──stream──> SQL Server target
                                         │
                                      your browser
```

- **The engine owns the workflow**: `SETUP → DISCOVERY → ANALYSIS ⟲ → MAPPING ⟲ → SQL ⟲ → READY → TRANSFER → COMPLETE`.
  All deterministic work runs as server jobs that cost no tokens — catalog extraction, profiling, rule checks,
  auto-mapping, SQL templating and validation, the transfer itself and every rendered page. They keep running when
  Claude Code is closed.
- **Claude only does judgement.** Three subagents: `schema-analyst` writes the analysis narrative, `mapping-architect`
  resolves uncertain mappings and writes transforms, `sql-engineer` writes custom SQL. Each reads one compact work
  packet and returns a small JSON patch.
- **Human review loop.** Each reviewable phase shows version 1. You approve, or add feedback — general, or pinned to a
  table, column, finding, mapping row, SQL task or line — and press **Request changes**. Claude answers every item
  (addressed, or declined with a reason) in the next version and you see a diff. Every version and comment is kept;
  approving locks the version.
- **Reopen and staleness.** Reopening an approved phase marks the later phases *stale*; each is regenerated and must be
  approved again.
- **Drift check.** Schema fingerprints are re-checked before approval and before execution; if a database changed, the
  UI offers re-discovery.
- **Pause / resume.** **Pause** in the top bar stops agent work; a running transfer finishes and commits its current
  chunk first. **Resume** continues. If Claude Code was closed, `/db-migrate resume` in any new session picks up exactly
  where the engine stopped.
- **Token-saving design.** Scripts do the deterministic work and the engine decides the next step (`dbm next`), so the
  skill stays tiny. Packets summarise the confident items and detail only the uncertain ones; agents look things up with
  a local vector search instead of loading whole schemas. Rework sends only your feedback and the affected slice.
  Waiting for you is a background command that costs nothing.
- **Transfer.** `SqlDataReader` → `SqlBulkCopy` in key-ordered chunks (100 000 rows by default). Each chunk commits
  together with its checkpoint inside the target transaction, so a crash or a pause resumes exactly where it stopped.
  Failing batches are bisected to isolate bad rows, which are logged (skip and log) or stop the task (stop on error).
  Foreign-key order is respected; cycles load with `NOCHECK` and are re-checked `WITH CHECK` afterwards.

## Security

- **Local only.** The server binds to `127.0.0.1`, requires a random per-project token on every API call and rejects
  foreign `Host` headers. Only `dbm init`/`dbm ui` print the tokenised URL.
- **Connection strings** are entered in the browser (or saved by `dbm demo --attach`) and encrypted at rest: DPAPI for
  the current user on Windows, AES-GCM with a key in `~/.dbmigrate/key` (mode 600) elsewhere. They are masked after
  saving and never appear in logs, events, work packets, exports, CLI output or anything sent to Claude.
- **What Claude sees:** schema metadata (names, types, keys, row counts, sizes), column profiles (null share, distinct
  ratio, lengths, value patterns and a few sample values while the project's `SampleValues` setting is on, which is the
  default), rule findings, the mapping, the SQL plan and your feedback.
- **What Claude never sees:** credentials, connection strings or bulk row data. The transfer streams source → target
  inside the local engine; Claude is not in the data path.
- **Rejected rows** are stored in `<project>/.dbmigrate/state.db` for the final report, so treat the project folder like
  the data itself. `.dbmigrate/` carries its own `.gitignore` that excludes everything.
- Destructive actions (execute, truncate target) need the target database name typed to confirm.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `dbm: 'dotnet' was not found` or `ASP.NET Core Runtime 8 or later is required` (exit 3) | Install the runtime with the command above, then restart Claude Code. |
| `engine not built ... no .NET SDK` (exit 4) | `claude plugin update db-migrate@db-migrate`, then restart Claude Code. |
| The browser did not open | Run `/db-migrate ui` (or `dbm ui`) and open the printed `http://127.0.0.1:<port>/?t=<token>` URL. |
| "Unauthorized" in the browser | The server restarted with a new token: run `dbm ui` for a fresh link. |
| "Agent offline" banner | No Claude session is running the loop: type `/db-migrate resume`. |
| A connection test fails with a certificate error | Add `TrustServerCertificate=True` (test servers) or install the server's CA certificate. |
| `demo_exists` | Add `--force` to recreate the two demo databases, or use another `--prefix`. |
| Anything else | Claude reads `plugins/db-migrate/skills/db-migrate/reference/troubleshooting.md` when a command fails; you can read it too. |

`dbm doctor` prints a health report: runtime, ASP.NET Core, SqlClient, SQLite, user profile, encryption round-trip, this
project's server and the engine build version.

## Development

```
.claude-plugin/marketplace.json   marketplace "db-migrate"
plugins/db-migrate/               the plugin: skills/, agents/, hooks/, bin/, samples/, engine/
  engine/Dbm/                     .NET 8 app: Cli/, Core/, Web/, wwwroot/
  engine/Dbm.Tests/               xUnit tests (+ js/*.test.cjs for the UI libraries)
  engine/dist/                    committed release build (framework-dependent)
docs/                             user guide, design spec, implementation plan
```

- **Build:** `dotnet build plugins/db-migrate/engine/Dbm.sln` (.NET SDK 8 or later).
- **Unit tests:** `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`.
- **Integration tests:** need a SQL Server. `DBM_TEST_SQL` selects it (default
  `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`). Without LocalDB, run
  `docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Your_str0ng_pw' -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest`
  and set `DBM_TEST_SQL="Server=localhost,1433;User ID=sa;Password=Your_str0ng_pw;TrustServerCertificate=true"`. Then
  `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"`.
- **UI tests:** `node --test plugins/db-migrate/engine/Dbm.Tests/js/` (Node is a development-only dependency).
- **Run the plugin from the working tree:** `claude --plugin-dir plugins/db-migrate`. The launcher rebuilds
  `engine/dist` whenever `dist/VERSION` differs from `plugin.json`, when `DBM_REBUILD=1` is set, or on
  `dbm doctor --rebuild`.
- **Validate:** `claude plugin validate plugins/db-migrate` and `claude plugin validate .`.
- **Release:**
  1. Bump `version` in `plugins/db-migrate/.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` and
     `<Version>` in `plugins/db-migrate/engine/Directory.Build.props` (the release script checks they agree).
  2. Run `pwsh plugins/db-migrate/engine/release.ps1` (or `sh plugins/db-migrate/engine/release.sh`): unit tests, UI
     tests, Release publish into `engine/dist`, `VERSION`, smoke test, plugin validation, size summary.
  3. Commit `engine/dist` and tag with `claude plugin tag plugins/db-migrate --push` (creates `db-migrate--v<version>`).

  Teammates update with `claude plugin update db-migrate@db-migrate`.

Design spec: [docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md](docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md).
````

- [ ] **Step 4: Write the user guide**

Create `docs/README.md` with exactly the content below.

````markdown
# db-migrate — user guide

This guide walks through every screen of the db-migrate web UI and says what you are expected to do there. Installation
and the 5-minute demo are in the [main README](../README.md).

## Starting, continuing, re-opening

| You want to | Type this in Claude Code |
|---|---|
| Start a migration project in the current folder | `/db-migrate` |
| Continue after closing Claude Code | `/db-migrate resume` (new session, same folder) |
| See where the project stands | `/db-migrate status` |
| Get the browser page back | `/db-migrate ui` |
| Stop the local server | `/db-migrate stop` |

The project lives in `<folder>/.dbmigrate/`: `state.db` holds everything, `exports/` the files you export. One folder is
one migration (one source, one target).

The web server keeps running after Claude Code exits, so the UI stays usable: browse, comment, approve, pause and even
run the transfer without Claude. Claude is only needed when an agent has to write something; until it is back, the top
bar shows **Agent offline — run `/db-migrate resume` in Claude Code**.

## The screen

- **Left: phase stepper** — Setup, Discovery, Analysis, Mapping, SQL, Execute, Transfer, Report, each with a status:
  `pending` (not started), `running` (a server job is working), `drafting` (Claude is writing the first version),
  `awaiting review` (your turn), `reworking` (Claude is addressing your feedback), `approved`, `stale` (an earlier phase
  was reopened, so this one must be redone).
- **Top bar** — project name, the agent online/offline dot, **Pause / Resume**.
- **Right drawer** — the feedback thread for the current phase (your items and Claude's responses) and the version
  history with a diff between any two versions.

## Setup

Paste the **source** and **target** ADO.NET connection strings into the two masked fields and press **Test**. The
detected server version, edition, collation and compatibility level appear. Press **Save** for each side; when both are
saved, discovery starts automatically.

- Connection strings are encrypted on your machine and masked after saving. Don't paste them into the Claude chat.
- To try the tool without your own databases, ask Claude to run
  `dbm demo --server "<your server>" --attach` (see the README).
- A certificate error on Test: add `TrustServerCertificate=True` for test servers, or install the server's CA certificate.
- Entra ID: for example `Authentication=Active Directory Default` (uses your `az login` or IDE sign-in) or
  `Active Directory Interactive`, which opens a sign-in window on this machine.

## Discovery

Runs by itself: extracts both catalogs, profiles the columns (null share, distinct values, lengths, value patterns,
sample values), builds the search index and records a schema fingerprint per database. Nothing to do; large databases
take a few minutes. On failure the error is shown with a **Retry** button — fix the cause first (usually permissions).

## Analysis

- **KPI tiles**: tables, columns, rows, size and risk count for both sides.
- **Side-by-side inventory** with per-table drill-down, an FK dependency graph and data-quality findings.
- **Risks by severity**, for example heaps (no key, so no mid-table resume), deprecated or LOB types, collation
  differences, orphaned FK data, triggers on the target, truncation risk (source longer than target), and the estimated
  transfer volume.
- **Claude's summary**, risk narrative and recommendations at the top.

**What to do:** read the summary and the high-severity findings. Hover over any finding, table, column or the narrative
and press its comment button to attach feedback to exactly that element ("this table is obsolete, skip it", "explain the
collation risk"). Then press **Approve**, or **Request changes (n)** to send the n open items to Claude, which answers
each one as *addressed* (with a note) or *declined* (with a reason) in version n+1, with a diff.

## Giving good feedback

Feedback is either *general* or *anchored*. Anchors keep Claude's rework small and precise:

| Anchor | Where you click |
|---|---|
| finding | a finding row in Analysis |
| table / column (source or target) | a table or column in Analysis or Mapping |
| narrative | the summary or recommendations text |
| table mapping | a row in the Mapping grid |
| column mapping | a column row inside an expanded table mapping |
| SQL task | a task header on the SQL screen |
| SQL line | a line number inside a task's SQL |

Draft items stay private until you press **Request changes**, and you can delete drafts. Be concrete ("split CUST_NM on
the first space into FirstName and LastName"). Claude may decline with a reason; answer by adding another item.

## Mapping

- **Table grid**: source → target with a confidence bar and a method badge — `exact`, `fuzzy`, `vector`, `agent`
  (Claude), `human` (you), `carried` (kept from an earlier approved version).
- **Expand a row** for its column mappings: source expression, transform, default, confidence, rationale and type risk
  (e.g. `varchar(500) → nvarchar(200)` truncation).
- **Direct editing**: change an expression, pick another candidate, set a default, or mark a source column or table as
  **dropped**. Every direct edit creates a new version authored by you.
- **Blocking panels**: *unmapped source columns* (map or drop each) and *unresolved target columns* (non-nullable, no
  default, no expression). **Approve stays disabled until both are empty.**

**What to do:** check the low-confidence rows first, fix simple things yourself, and use anchored feedback for anything
that needs reasoning (splits, merges, lookups, T-SQL transforms).

## SQL

- **Tasks in execution order**, one per target table (parents before children), each with its source query, column
  bindings, mode (`direct` bulk copy or `staging_merge`), identity insert and pre/post/validation scripts. Global
  pre/post scripts handle FK cycles and similar cases.
- The SQL is validated against the real databases (parse-only compile, result-set shape, target column compatibility);
  errors and warnings appear per task.
- Click a line number to comment on that line, and use the version diff to see exactly what Claude changed.
- **Download script pack** gives the plan as `.sql` files plus a README for a DBA review.

**What to do:** read every task with warnings or custom SQL, comment where needed, approve.

## Reopen, stale phases and drift

- **Reopen** an approved phase to change it: every later phase up to Execute becomes `stale`, is regenerated after your
  re-approval (carrying over what still applies) and comes back for review. Reopening is not possible once a transfer
  has started.
- **Drift**: before approval and before execution the engine re-reads both schemas and compares fingerprints. If a
  database changed, a warning offers **Re-discover**, which refreshes the catalogs and marks the later phases stale.

## Execute

1. **Pre-flight checklist**: connectivity to both databases, unchanged fingerprints, current target row counts
   (non-empty targets are highlighted), permissions, and the estimated volume. Fix anything red first.
2. **Options**:

   | Option | Default | Meaning |
   |---|---|---|
   | Chunk size | 100 000 | rows per committed chunk (LOB-heavy tasks use a smaller size automatically) |
   | Parallelism | 4 | tasks loaded at the same time within one dependency level |
   | Row errors | stop | **stop** = a bad row stops its task; **skip and log** = bad rows are isolated, recorded and skipped |
   | Truncate target first | off | empties each target table before loading |
   | Table lock | off | bulk-load with `TABLOCK`: faster, but blocks other users of the target |
   | Validate checksums | on | per-column aggregate checks for tables that were empty before the run |
   | Fire triggers | off | let target triggers run during the bulk insert |
   | Keep control table | off | keep `dbo.__dbm_checkpoint` after completion, for auditing |

3. Type the **target database name** and press **Execute**.

**Live view**: overall and per-task progress bars, rows per second, ETA, a throughput sparkline, the error count and a
log tail. **Pause** finishes the current chunk of every task, commits and stops; **Resume** continues from the
checkpoints even after a crash or reboot; **Cancel** stops for good, and rows already committed stay in the target.

## Final report

Per task: source rows, target rows added, rejected rows, duration, throughput and validation results (row counts and
column checksums). Rejected rows are listed with their error and a sample of the row. In the demo with *skip and log*
you see 8 rejected rows: 2 orphan orders and their 2 lines (foreign key), 3 comments longer than the target column
(truncation) and 1 zero quantity (CHECK constraint).

## Exports

Every reviewable screen has an **Export** button that writes a single self-contained HTML file — it opens anywhere,
offline and read-only. The SQL screen also offers the **script pack** zip. The same files from Claude Code or a terminal:

```
dbm export analysis          # -> .dbmigrate/exports/analysis-v<n>.html
dbm export sql               # -> the SQL screen as one HTML file
dbm export sqlpack           # -> <project>-sql-v<n>.zip (the DBA script pack)
dbm export report            # -> final-report.html (after a completed run)
dbm export analysis --out ./review/     # also copy it somewhere else
```

Every export is saved under `.dbmigrate/exports/` as well, and none of them contains a connection string.

## Pause, resume and Claude

- **Pause** (top bar) works in every phase. Claude finishes and saves any patch it is writing, then waits at no token cost.
- **Resume** continues immediately when the Claude session is still open. Otherwise type `/db-migrate resume` in any new
  session in the same folder; the engine knows where to continue.
- While Claude waits for you, its session holds a background `dbm await` task. Approving, requesting changes, pausing,
  resuming or reopening completes that command and Claude continues on its own. If your Claude surface does not wake up,
  tell Claude to continue once and it switches to a foreground wait for the rest of the session.
````

- [ ] **Step 5: Check the docs**

Run (Git Bash, repository root):

```bash
wc -w plugins/db-migrate/skills/db-migrate/SKILL.md
grep -c 'db-migrate:<agent>' plugins/db-migrate/skills/db-migrate/SKILL.md
grep -c 'reference/troubleshooting.md' plugins/db-migrate/skills/db-migrate/SKILL.md
grep -o '](\([^)#]*\))' README.md docs/README.md | sed 's/.*](\(.*\))/\1/' | grep -v '^https\?://' | sort -u
claude plugin validate plugins/db-migrate
```

Expected: the skill is under 700 words; `1` for the agent pattern; `2` for the troubleshooting pointer; the only
relative links are `../README.md`, `docs/README.md` and
`docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md` (all three exist); `✔ Validation passed`.

- [ ] **Step 6: Commit**

```bash
git add README.md docs/README.md \
        plugins/db-migrate/skills/db-migrate/SKILL.md \
        plugins/db-migrate/skills/db-migrate/reference/troubleshooting.md
git commit -m "$(cat <<'EOF'
docs: README, user guide, orchestrator troubleshooting and SKILL.md polish

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
)"
```

---

### Task 6.4: Installed-plugin end-to-end verification (CLI + Desktop)

**Files:**
- Create: `docs/superpowers/verification/e2e-results.md` (template in Step 1, filled in during Steps 2–9)
- Modify only when a check fails, with the exact edits in Step 10: `plugins/db-migrate/skills/db-migrate/SKILL.md`, `README.md`, `docs/README.md`

**Interfaces:** manual verification of the installed plugin, using the real CLI (`claude plugin validate [--strict] <path>`, `claude plugin marketplace add|list|remove`, `claude plugin install|list|details|uninstall`) and the in-session commands `/plugin`, `/agents`, `/hooks`, `/tasks`, `/usage` (alias `/cost`), `/context`, `/exit`.

Prerequisites: Tasks 6.1–6.3 are committed, `engine/dist` was built by `release.ps1`, LocalDB is startable (`sqllocaldb start MSSQLLocalDB`), and Claude Desktop is installed and signed in.

**Database safety:** check for the exact demo names only —
`sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "SELECT name FROM sys.databases WHERE name IN ('DbmDemo_LegacyShop','DbmDemo_ShopV2')"`.
If either exists, the demo step below uses `--force`, which drops and recreates exactly those two. Never list or drop databases by name pattern.

- [ ] **Step 1: Create the results template**

Create `docs/superpowers/verification/e2e-results.md`:

````markdown
# db-migrate — installed-plugin end-to-end results

- Date:
- Tester:
- Commit (`git rev-parse --short HEAD`):
- Plugin version (plugin.json):
- Claude Code CLI version (`claude --version`):
- Claude Desktop version:
- OS:
- SQL Server used: `(localdb)\MSSQLLocalDB`

Before starting: `sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "SELECT name FROM sys.databases WHERE name IN ('DbmDemo_LegacyShop','DbmDemo_ShopV2')"`.
If either exact name is listed, the demo step below uses `--force`, which drops and recreates exactly those two
databases. Never drop databases by name pattern.

## A. Packaging and install

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| P1 | `claude plugin validate plugins/db-migrate` | exit 0, no errors | | |
| P2 | `claude plugin validate .` (marketplace) | exit 0, no errors | | |
| P3 | `claude plugin validate plugins/db-migrate --strict` | exit 0; record any warnings | | |
| P4 | `claude plugin marketplace add D:/DatamigrationAgent`, then `claude plugin marketplace list` | marketplace `db-migrate` listed with the local path | | |
| P5 | `claude plugin install db-migrate@db-migrate`, then `claude plugin list` | installed and enabled, version = plugin.json | | |
| P6 | `claude plugin details db-migrate` | 1 skill, 3 agents, a SessionStart hook; record the projected token cost | | |
| P7 | installed copy has `engine/dist/VERSION` = the version | no build happens on first use | | |

## B. First CLI session

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| S1 | new session in an empty folder | no hook error; `/hooks` lists the plugin's SessionStart `dbm doctor --quiet` | | |
| S2 | type `/db-` | the skill is offered; record the exact name(s) (`/db-migrate`, `/db-migrate:db-migrate`) | | |
| S3 | `/agents` | `schema-analyst`, `mapping-architect`, `sql-engineer` listed; record the exact names | | |
| S4 | ask "run `dbm version`" | one JSON line with the version, no build message | | |
| S5 | `/db-migrate` | `dbm init` runs, the browser opens on Setup, one line with the URL, a background `dbm await` appears in `/tasks` | | |
| S6 | ask Claude: `run: dbm demo --server "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true" --attach` | `ok:true, attached:true`; Setup shows both connections saved; discovery starts; nothing pasted by hand | | |
| S7 | first agent dispatch | the Agent tool call shows `subagent_type: "db-migrate:schema-analyst"` and succeeds | | |

## C. Review loops

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| A1 | Analysis v1 | summary, narrative and findings; the heap finding for `dbo.AUDIT_LOG` is there | | |
| A2 | 2 feedback items → **Request changes (2)** | Claude wakes by itself, reworks, v2 answers both items and shows a v1→v2 diff | | |
| A3 | **Approve** Analysis | auto-mapper runs, `db-migrate:mapping-architect` drafts, Mapping v1 awaits review | | |
| M1 | edit `app.Customers.Email` to `LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))` | new version authored `human` | | |
| M2 | anchored feedback on `app.Customers.Phone` ("trim surrounding spaces"), left as a draft | listed as a draft in the drawer | | |
| R1 | **Pause** | Claude prints "Paused — press Resume in the UI to continue." and keeps a background await | | |
| R2 | `/exit`, keep using the browser | UI still works; the agent dot goes offline within ~2 minutes | | |
| R3 | new session, `/db-migrate resume` | one line reporting Paused, nothing else | | |
| R4 | **Resume** | Claude continues by itself: "Waiting for your review of mapping in the browser." | | |
| M3 | **Request changes (1)** | mapping-architect reworks, Phone trimmed, item `addressed`, the Email edit preserved | | |
| M4 | **Approve** Mapping | no blockers; SQL generated; v1 awaits review | | |
| Q1 | line comment on the Orders task → **Request changes (1)** | `db-migrate:sql-engineer` reworks; the diff shows the added SQL comment; item `addressed` | | |
| Q2 | **Download script pack** | zip with `.sql` files and a README | | |
| Q3 | **Approve** SQL | Execute screen active; Claude says "Ready to run the transfer from the browser." | | |
| Z1 | idle 10 minutes at a review | `/usage` totals unchanged before and after (waiting costs nothing) | | |

## D. Execute and report

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| X1 | pre-flight | every check passes; target row counts 0 | | |
| X2 | Execute with **skip and log**, typed confirmation `DbmDemo_ShopV2` | live progress, rows/s, ETA; Claude: "Working in the background — I'll continue when it's done." | | |
| X3 | final report | **8 rejected rows**: app.Orders 5 (2 FK orphans + 3 over-long comments), app.OrderLines 3 (2 lines of the orphan orders + 1 CHECK violation); app.Customers 1000, app.Addresses 1500, app.Products 200, app.AuditEvents 5000 loaded | | |
| X4 | Claude after completion | wakes by itself, reports the `complete` summary, ends the loop | | |
| X5 | ask "run `dbm export report`" | `{ok:true,file:"final-report.html",path:...}`; the file opens offline with the same numbers | | |

## E. Claude Desktop (Code tab, local session)

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| D1 | new local session in `%TEMP%\dbm-e2e-desktop` | the plugin is available (skill in the `/` menu) | | |
| D2 | `/db-migrate`, then the demo command with `--prefix DbmDesk_ --attach` | browser opens; Analysis reaches review; Claude ends its turn with a background `dbm await` | | |
| D3 | **Approve** Analysis in the browser, type nothing in Desktop | the session resumes by itself within ~10 s and dispatches mapping-architect | | |
| D4 | only if D3 failed: type "continue" | Claude switches to a foreground `dbm await --timeout 540` (SKILL.md step 4) and carries on | | |

Cleanup: `dbm stop` in both folders, then drop exactly the demo databases used here:
`sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "DROP DATABASE DbmDemo_LegacyShop; DROP DATABASE DbmDemo_ShopV2; DROP DATABASE DbmDesk_LegacyShop; DROP DATABASE DbmDesk_ShopV2"`.

## F. Token usage (from `/usage`, one row per checkpoint)

| Checkpoint | Session | Total tokens | Cost | Delta vs previous | Context (`/context`) |
|---|---|---|---|---|---|
| C0 after `/db-migrate` (S5) | 1 | | | – | |
| C1 Analysis approved (A3) | 1 | | | | |
| C2 paused before exit (R1) | 1 | | | | |
| C3 resumed (R4) | 2 | | | – (new session) | |
| C4 Mapping approved (M4) | 2 | | | | |
| C5 SQL approved (Q3) | 2 | | | | |
| C6 complete (X4) | 2 | | | | |

Per phase: Analysis = C1−C0 · Mapping = (C2−C1)+(C4−C3) · SQL = C5−C4 · Transfer = C6−C5. Flag any phase above
200k tokens as a finding for the token-strategy review.

## G. Findings and fixes

| # | Check | Finding | Fix applied (commit) |
|---|---|---|---|
| | | | |
````

- [ ] **Step 2: Packaging and install (P1–P7)**

From the repository root in PowerShell:

```powershell
claude plugin validate plugins/db-migrate
claude plugin validate .
claude plugin validate plugins/db-migrate --strict
claude plugin marketplace list
```

If `db-migrate` is already listed from an earlier trial, run `claude plugin uninstall db-migrate@db-migrate` (ignore "not installed") and `claude plugin marketplace remove db-migrate` first. Then:

```powershell
claude plugin marketplace add D:/DatamigrationAgent
claude plugin marketplace list
claude plugin install db-migrate@db-migrate
claude plugin list
claude plugin details db-migrate
claude plugin list --json
```

For P7 find the installed copy — the install-path field of the `db-migrate@db-migrate` entry in `claude plugin list --json`, or `Get-ChildItem $HOME\.claude\plugins -Recurse -Filter VERSION | Where-Object FullName -like '*db-migrate*engine*dist*'` — and `Get-Content` it. Expected: the plugin.json version.

- [ ] **Step 3: First CLI session (S1–S5)**

```powershell
New-Item -ItemType Directory -Force $env:TEMP\dbm-e2e | Out-Null
Set-Location $env:TEMP\dbm-e2e
claude
```

In the session: `/hooks` (S1); type `/db-` and read the menu, then Esc (S2); `/agents` (S3); prompt `run dbm version` (S4); `/db-migrate` and check `/tasks` for the background `dbm await` (S5). Then run `/usage` and record **C0**.

- [ ] **Step 4: Demo attach and first agent dispatch (S6–S7)**

Prompt Claude with exactly:

```
run: dbm demo --server "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true" --attach
```

Expected: Claude runs it and reports `attached`; the Setup screen shows both sides saved (masked); the stepper moves Discovery `running` → Analysis `running` → `drafting`; the background `dbm await` completes and Claude dispatches the schema-analyst. Expand that Agent tool call (Ctrl+O) and record the `subagent_type` value (S7).

- [ ] **Step 5: Analysis loop (A1–A3)**

In the browser: comment on the heap finding for `dbo.AUDIT_LOG` with `Explain how this heap affects resuming the transfer and what you recommend.`; add a general item `Keep the executive summary under 120 words.`; press **Request changes (2)**. Expected: without typing anything in Claude Code, the session continues, the schema-analyst reworks, and v2 shows both responses and a diff (A2). Press **Approve** (A3), then record **C1** with `/usage`.

- [ ] **Step 6: Mapping edit, pause, exit, resume (M1–M3, R1–R4, Z1)**

1. In Mapping, expand `app.Customers`, set `Email` to `LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))` and save (M1).
2. On the `Phone` row add the feedback `Trim surrounding spaces from the phone number.` and leave it as a draft (M2).
3. Z1: run `/usage`, wait 10 minutes without touching anything, run `/usage` again — the totals must be identical.
4. Press **Pause** (R1), record **C2**, then `/exit`.
5. In the browser open Analysis (read-only) and Mapping: both work, and the agent dot goes offline within ~2 minutes (R2).
6. In the same folder run `claude` and type `/db-migrate resume` (R3); record **C3**.
7. Press **Resume** in the browser: Claude continues by itself (R4).
8. Press **Request changes (1)** (M3), then **Approve** (M4). If Approve is blocked, the blocking panels name the columns — record that as a finding, resolve them with direct edits and approve. Record **C4** once SQL v1 is shown.

- [ ] **Step 7: SQL loop (Q1–Q3)**

Open the task for `app.Orders`, click the line number of the line selecting `st.[STATUS_CD]` and comment `Add a short SQL comment above this line explaining the ORD_STATUS lookup.` Press **Request changes (1)** (Q1). Then **Download script pack** and open the zip (Q2), press **Approve** (Q3) and record **C5**.

- [ ] **Step 8: Execute and report (X1–X5)**

Run the pre-flight (X1); set **Row errors** to skip and log, leave the other options at their defaults, type `DbmDemo_ShopV2`, press **Execute** (X2); wait for completion and record the per-task numbers from the final report (X3); check that Claude printed the completion summary by itself (X4) and record **C6**; prompt `run dbm export report` and open the file with networking disabled (X5).

- [ ] **Step 9: Claude Desktop (D1–D4)**

Open Claude Desktop → **Code** tab → new **local** session in `%TEMP%\dbm-e2e-desktop` (create the folder first). Check the skill is offered (D1); run `/db-migrate` and the demo command with `--prefix DbmDesk_ --attach` (D2); approve Analysis in the browser and watch for 30 s without typing (D3); only if that fails, type `continue` (D4). Then stop the server and drop exactly the four demo databases as listed in the template.

- [ ] **Step 10: Apply fixes for failed checks**

Apply only what failed and record each in section G.
- **S7 failed** (subagent not found): use the name `/agents` shows. If it is the bare `schema-analyst`, edit SKILL.md Loop step 2 and replace `` `subagent_type: "db-migrate:<agent>"` `` with `` `subagent_type: "<agent>"` ``; re-run T6.3 Step 5 (the grep for `db-migrate:<agent>` then returns 0); `claude plugin marketplace update db-migrate` and `claude plugin update db-migrate@db-migrate`; repeat Steps 3–5.
- **S2 shows only `/db-migrate:db-migrate`**: list the invocations with `grep -n '/db-migrate' README.md docs/README.md plugins/db-migrate/skills/db-migrate/SKILL.md plugins/db-migrate/skills/db-migrate/reference/troubleshooting.md` and replace `/db-migrate` with `/db-migrate:db-migrate` in every line where a user types it (never in a file path such as `plugins/db-migrate/…`); in README.md Quick start step 2 drop the sentence about the menu name.
- **D3 failed** (Desktop does not wake on a finished background command): add this row to the README Troubleshooting table under the "Agent offline" row — `| Claude Desktop doesn't continue after you act in the browser | Type "continue" once; Claude then waits in the foreground (a few tokens every 9 minutes) for the rest of that session. |` — and add to the end of the user guide's *Pause, resume and Claude* section: `In the Claude Desktop app the automatic continuation may not trigger; if nothing happens within a few seconds of your action, type "continue" once.`
- **X3 differs from 8 rejected rows:** do not change the docs. Record the per-task numbers and open a bug against T5.7, which asserts the same 8. Note any rework in M3/Q1 that could have changed row outcomes.
- **Z1 shows token growth while idle:** record the delta and the `/tasks` state; it usually means the background await was not started. Check that SKILL.md step 3 says `run_in_background: true` and that Claude followed it.

- [ ] **Step 11: Commit the results (and any fixes)**

```bash
git add docs/superpowers/verification/e2e-results.md
git add -u README.md docs/README.md plugins/db-migrate/skills/db-migrate/SKILL.md
git commit -m "$(cat <<'EOF'
test(e2e): record installed-plugin verification for CLI and Desktop

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
)"
```

Expected: every check in A–E is PASS, or FAIL with a fix in G and re-verified; section F is filled in.

---

## Contract notes

**Verified against the real M0–M5 code** (this milestone was executed end to end before the plan was finalised; test counts below are the observed ones).

**Upstream files this milestone edits** (all minimal, all verified against their real text):
| File | Change |
|---|---|
| `Dbm/Cli/CommandRegistry.cs` | two entries appended after the M5 transfer commands |
| `Dbm/Cli/Args.cs` | `"attach"` appended to `BooleanFlags` (T6.1) |
| `Dbm/Cli/Commands/DoctorCommand.cs` | `RunAsync` becomes async and appends `DoctorChecks`; `RunChecks()` untouched |
| `Dbm.Tests/Unit/Cli/DoctorCommandTests.cs` | `Doctor_prints_json_report` expects 8 checks instead of 5 |
| `bin/dbm`, `bin/dbm.cmd` | replaced; every M0 behaviour (dotnet + ASP.NET ≥ 8 check, exit codes 3/4, messages, `doctor --rebuild`, `consider` subroutine) preserved |
| `skills/db-migrate/SKILL.md` | four additions; T1.10's loop, arguments and stop reasons unchanged |
| `.gitignore`, `.gitattributes` | transient build folders ignored; `engine/dist/**` marked binary |

**Additions:** `Dbm.Core.Samples.DemoDatabases`/`DemoResult`/`DemoExistsException`; `DemoCommand`, `ExportCommand`, `DoctorChecks`; error codes `demo_exists`, `locked`, `sql_error` (demo) — `export` surfaces the server's own `not_found`/`export_unavailable`; `engine/release.ps1`, `engine/release.sh`, committed `engine/dist/**`.

**Deviations from the overview, for review:**
1. **Export kinds are `analysis`, `sql`, `sqlpack`, `report`** — C7 also lists `mapping`, but no milestone registers a mapping export (`ExportEndpoints` gets `analysis` in T2.8, `sql`+`sqlpack` in T4.4, `report` in T5.6). `dbm export mapping` therefore answers `not_found`. Adding it later needs only one `ExportEndpoints.Register` call plus the kind in `ExportCommand.Kinds`.
2. **`dbm demo` prints the connection strings with secrets scrubbed** (`***`), as asked, so the non-`--attach` path is usable. This is a narrow relaxation of "no connection strings in CLI output"; dropping the `connectionString` property from `ConnectionView` would enforce the strict reading and only `ConnectionView_never_shows_the_password` would change.
3. **`engine/dist` is pruned before it is committed.** A full publish is 105.9 MB, of which 61.6 MB is MSAL's WAM broker natives (`*msalruntime*`) and ~14 MB is runtimes for platforms db-migrate does not target. Both launchers and both release scripts remove the same list, giving **86 files, 29.5 MB**. SqlClient's Entra modes use MSAL's managed/browser path, so the broker binaries are not needed; `release.ps1 -KeepAllRuntimes` / `release.sh --keep-all-runtimes` publishes everything if that ever proves wrong.
4. **`dbm export` starts the project server** when it is not already running (it builds through `POST /api/export/{what}`), so CLI and UI exports are the same bytes with the same names.

**Observed results** (Windows 11, .NET 8.0.28, LocalDB): unit `765 passed` (36 new), integration `76 passed` (3 new), node `62 passed`; `release.ps1` → 86 files / 29.5 MB, smoke test `{"version":"0.1.0","runtime":".NET 8.0.28"}`, `claude plugin validate` passes for plugin and marketplace; the stale-VERSION rebuild, the forced rebuild and both launchers behave exactly as Step 11 of T6.2 states.

## Self-review

**Spec coverage (M6 row of spec §12 plus the M6 items in §2.4, §3.4, §11, §13):**

| Requirement | Where |
|---|---|
| `dbm demo` builds the C15 fixture pair for a trial run, repeatable, nothing pasted | T6.1 (`--force`, `--prefix`, `--scale`, `--attach`) |
| Standalone exports from the CLI | T6.1 `ExportCommand` |
| Committed framework-dependent `engine/dist` built from the plugin.json version | T6.2 Steps 9–10 |
| Launcher builds when dist is missing **or stale**, stops the server first (Windows DLL locks) | T6.2 Steps 6–7, 11 |
| `dbm doctor` reports runtime/build health (protector, server, dist) | T6.2 Steps 1–5 |
| Cross-platform check (Git Bash run; macOS/Linux spawn path and key 0600) | T6.2 Steps 11–12 |
| Marketplace install from git; README; user guide; troubleshooting; short skill | T6.3 |
| Desktop re-check of the background wake-up (spec §3.4 and §13 risk) plus a fallback | T6.4 D1–D4, Step 10; SKILL.md step 4 |
| Pause during review → exit → `/db-migrate resume` continues | T6.4 R1–R4 |
| Final report shows 8 rejected rows (C15 expected outcome) | T6.4 X3 |
| Token usage per phase measured | T6.4 section F |
| Subagent naming verified | T6.4 S3/S7 + fix |

**Placeholder scan:** every code block is complete and byte-identical to what compiled and passed. `<git-url-of-this-repo>` in the README is a value the reader supplies (explained in the same paragraph); `<v>`, `<port>`, `<token>`, `<phase>` in expected-output lines stand for values the reader reads off their own run. No "TBD"/"TODO", no "same as Task X".

**Consistency:** `DoctorChecks.Quick/AllAsync/Protector/ServerAsync/Dist` are identical in the Interfaces block, the tests, the implementation and the `DoctorCommand` edit; `DemoDatabases.Names/IsValidPrefix/ForDatabase/CreateAsync/DropAsync` and `ExportCommand.Kinds/CopyToOut` match their tests; the pruned-runtime list is the same in all four scripts; test counts in the steps (23, 3, 16) are the observed ones.

**Risks:**
1. Each release adds ~30 MB of binaries to git history. The release script prints the size and warns above 60 MB; a Git LFS migration later would not change the launchers.
2. Pruning the MSAL broker natives is a judgement call — if a team needs WAM broker auth, release with `-KeepAllRuntimes`.
3. Desktop may not wake on a finished background command; D3 detects it and SKILL.md step 4 is the documented fallback.
4. The launchers read the version out of `plugin.json` with `sed`/`findstr`, so the file must stay pretty-printed with `"version"` on its own line (the release script fails the build if plugin.json, marketplace.json and Directory.Build.props disagree).
5. T6.4 is entirely manual: until it is run, the installed-plugin path, the Desktop wake-up and the per-phase token numbers stay unverified.
