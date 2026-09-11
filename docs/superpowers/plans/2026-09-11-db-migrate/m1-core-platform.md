# Milestone 1 — Core platform

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** everything the three review phases and the transfer will plug into: workspace + SQLite state store, per-user encryption of connection strings, SQL connection probing and redaction, JSON patches, the phase state machine (`WorkflowEngine`) with work packets, the background job runner, the detached localhost web server (token + host guard, SSE, agent long-poll, core REST API), the M1 CLI (`init/status/pause/resume/serve/ui/stop/next/await/apply/artifact/feedback/run-jobs`), the web UI shell with the generic review loop (feedback drawer, version history + diff) and the Setup screen, and the `/db-migrate` orchestrator skill. M1 ships with empty module/job registries: M2–M4 register the real modules and handlers; M1 tests drive the whole loop with fakes.

Conventions for every task: run commands from the repository root; unit tests = `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`; integration tests = `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"` (need LocalDB or `DBM_TEST_SQL`). "Expected: Passed N" counts are cumulative for the unit filter.

---

### Task 1.1: Core utilities (EnumText, Clock, Workspace, IEventSink)

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/EnumText.cs`, `Clock.cs`, `Workspace.cs`, `IEventSink.cs` (`Json.cs` was created in T0.1, `UserHome.cs` in T0.2)
- Modify: `plugins/db-migrate/engine/Dbm/Cli/CliContext.cs` (adds `Workspace()`)
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/TestWorkspace.cs`, `Support/ProcessStateCollection.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Core/EnumTextTests.cs`, `Unit/Core/WorkspaceTests.cs`

**Interfaces:**
- Consumes: `Dbm.Core.Json` (T0.1), `UserHome` (T0.2).
- Produces (C1): `EnumText.ToText<T>(T)`, `EnumText.Parse<T>(string)` plus `EnumText.ToText(Enum)` and `EnumText.TryParse<T>(string?, out T)`; `Clock.Now` / `Clock.NowText()`; `Workspace` (all C1 members plus `const string DirName = ".dbmigrate"`); `IEventSink.Publish(type, payload, persist)`; `CliContext.Workspace()`. Test support: `TestWorkspace` (`Root`, `Ws`, `Dispose` deletes the folder; T1.5 adds `OpenServices(modules, jobs)`), `[Collection(ProcessStateCollection.Name)]` for tests that change env vars or the current directory.

- [ ] **Step 1: Write the failing tests and test support**

`plugins/db-migrate/engine/Dbm.Tests/Support/TestWorkspace.cs` (T1.5 adds `OpenServices`):

```csharp
using Dbm.Core;
using Microsoft.Data.Sqlite;

namespace Dbm.Tests.Support;

/// <summary>A throw-away workspace folder under %TEMP%, deleted on Dispose.</summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbm-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Ws = new Workspace(Root);
    }

    public string Root { get; }
    public Workspace Ws { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Support/ProcessStateCollection.cs`:

```csharp
namespace Dbm.Tests.Support;

/// <summary>Tests that change process-wide state (environment variables, current directory) run alone in this collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "process-state";
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Core/EnumTextTests.cs`:

```csharp
using Dbm.Core;

namespace Dbm.Tests.Unit.Core;

public class EnumTextTests
{
    public enum Status { Pending, AwaitingReview, Approved }
    public enum Where { Src, Tgt }

    [Theory]
    [InlineData(Status.AwaitingReview, "awaiting_review")]
    [InlineData(Status.Pending, "pending")]
    [InlineData(Where.Tgt, "tgt")]
    public void ToText_is_snake_case_lower(Enum value, string expected)
    {
        Assert.Equal(expected, EnumText.ToText(value));
    }

    [Fact]
    public void Generic_round_trip_matches_the_json_converter()
    {
        foreach (var status in Enum.GetValues<Status>())
        {
            var text = EnumText.ToText(status);
            Assert.Equal(status, EnumText.Parse<Status>(text));
            Assert.Equal($"\"{text}\"", Json.Serialize(status));
        }
    }

    [Fact]
    public void Parse_accepts_pascal_case_and_any_case()
    {
        Assert.Equal(Status.AwaitingReview, EnumText.Parse<Status>("AwaitingReview"));
        Assert.Equal(Status.AwaitingReview, EnumText.Parse<Status>("AWAITING_REVIEW"));
    }

    [Fact]
    public void Parse_rejects_unknown_text_listing_valid_values()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnumText.Parse<Where>("source"));
        Assert.Contains("src, tgt", ex.Message);
        Assert.False(EnumText.TryParse<Where>(null, out _));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Core/WorkspaceTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Core;

public class WorkspaceTests
{
    [Fact]
    public void Paths_live_under_dot_dbmigrate()
    {
        using var tw = new TestWorkspace();
        var ws = tw.Ws;

        Assert.Equal(Path.Combine(tw.Root, ".dbmigrate"), ws.Dir);
        Assert.Equal(Path.Combine(ws.Dir, "state.db"), ws.StateDbPath);
        Assert.Equal(Path.Combine(ws.Dir, "server.json"), ws.ServerJsonPath);
        Assert.Equal(Path.Combine(ws.Dir, "server.log"), ws.ServerLogPath);
        Assert.Equal(Path.Combine(ws.Dir, "exports"), ws.ExportsDir);
        Assert.Equal(Path.Combine(ws.Dir, "work"), ws.WorkDir);
    }

    [Fact]
    public void EnsureCreated_makes_folders_and_a_gitignore_but_Exists_needs_state_db()
    {
        using var tw = new TestWorkspace();

        tw.Ws.EnsureCreated();

        Assert.True(Directory.Exists(tw.Ws.WorkDir));
        Assert.True(Directory.Exists(tw.Ws.ExportsDir));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(tw.Ws.Dir, ".gitignore")));
        Assert.False(tw.Ws.Exists);
        File.WriteAllText(tw.Ws.StateDbPath, "");
        Assert.True(tw.Ws.Exists);
    }

    [Fact]
    public void Relative_uses_forward_slashes()
    {
        using var tw = new TestWorkspace();

        Assert.Equal(".dbmigrate/work/a.json", tw.Ws.Relative(Path.Combine(tw.Ws.WorkDir, "a.json")));
    }

    [Fact]
    public void Resolve_prefers_the_explicit_root()
    {
        using var tw = new TestWorkspace();

        Assert.Equal(tw.Ws.Root, Workspace.Resolve(tw.Root).Root);
    }
}

[Collection(ProcessStateCollection.Name)]
public class WorkspaceResolveTests
{
    [Fact]
    public void Resolve_uses_env_then_nearest_ancestor_then_cwd()
    {
        using var project = new TestWorkspace();
        project.Ws.EnsureCreated();
        File.WriteAllText(project.Ws.StateDbPath, "");
        var nested = Directory.CreateDirectory(Path.Combine(project.Root, "a", "b")).FullName;
        using var other = new TestWorkspace();
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(nested);
            Assert.Equal(project.Ws.Root, Workspace.Resolve(null).Root);

            Environment.SetEnvironmentVariable("DBM_WORKSPACE", other.Root);
            Assert.Equal(other.Ws.Root, Workspace.Resolve(null).Root);
            Environment.SetEnvironmentVariable("DBM_WORKSPACE", null);

            Directory.SetCurrentDirectory(other.Root);
            Assert.Equal(other.Ws.Root, Workspace.Resolve(null).Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBM_WORKSPACE", null);
            Directory.SetCurrentDirectory(originalCwd);
        }
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0103: The name 'EnumText' does not exist in the current context` and `CS0246: The type or namespace name 'Workspace' could not be found`.

- [ ] **Step 3: Implement the utilities**

`plugins/db-migrate/engine/Dbm/Core/EnumText.cs` (uses the same `JsonNamingPolicy.SnakeCaseLower` as the JSON enum converter, so SQLite text and JSON always agree):

```csharp
using System.Text.Json;

namespace Dbm.Core;

/// <summary>Enum ⇄ snake_case_lower text, identical to the JSON enum converter ("AwaitingReview" ⇄ "awaiting_review").</summary>
public static class EnumText
{
    public static string ToText<T>(T value) where T : struct, Enum =>
        Cache<T>.ToText.TryGetValue(value, out var text) ? text : Convert(value.ToString());

    /// <summary>Non-generic form used by parameter binding.</summary>
    public static string ToText(Enum value) => Convert(value.ToString());

    public static T Parse<T>(string text) where T : struct, Enum =>
        TryParse<T>(text, out var value)
            ? value
            : throw new ArgumentException(
                $"'{text}' is not a valid {typeof(T).Name}; expected one of: {string.Join(", ", Cache<T>.ToText.Values)}");

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return text is not null && Cache<T>.FromText.TryGetValue(text, out value);
    }

    private static string Convert(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static class Cache<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> ToText = Enum.GetValues<T>().ToDictionary(v => v, v => Convert(v.ToString()));

        public static readonly Dictionary<string, T> FromText = BuildFromText();

        private static Dictionary<string, T> BuildFromText()
        {
            var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Enum.GetValues<T>())
            {
                map[Convert(v.ToString())] = v;
                map.TryAdd(v.ToString(), v);
            }
            return map;
        }
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Clock.cs`:

```csharp
namespace Dbm.Core;

public static class Clock
{
    /// <summary>UTC now. Tests may replace it (restore it in a finally block).</summary>
    public static Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;

    public static string NowText() => Now().ToString("O");
}
```

`plugins/db-migrate/engine/Dbm/Core/Workspace.cs`:

```csharp
namespace Dbm.Core;

/// <summary>A project folder. All project state lives in Root/.dbmigrate.</summary>
public sealed class Workspace
{
    public const string DirName = ".dbmigrate";

    public Workspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Dir = Path.Combine(Root, DirName);
        StateDbPath = Path.Combine(Dir, "state.db");
        ServerJsonPath = Path.Combine(Dir, "server.json");
        ServerLogPath = Path.Combine(Dir, "server.log");
        ExportsDir = Path.Combine(Dir, "exports");
        WorkDir = Path.Combine(Dir, "work");
    }

    public string Root { get; }
    public string Dir { get; }
    public string StateDbPath { get; }
    public string ServerJsonPath { get; }
    public string ServerLogPath { get; }
    public string ExportsDir { get; }
    public string WorkDir { get; }

    public bool Exists => Directory.Exists(Dir) && File.Exists(StateDbPath);

    /// <summary>explicit &gt; env DBM_WORKSPACE &gt; nearest ancestor of cwd containing .dbmigrate/state.db &gt; cwd.</summary>
    public static Workspace Resolve(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return new Workspace(explicitRoot);

        var env = Environment.GetEnvironmentVariable("DBM_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(env)) return new Workspace(env);

        var cwd = Directory.GetCurrentDirectory();
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, DirName, "state.db"))) return new Workspace(dir.FullName);
        }
        return new Workspace(cwd);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(ExportsDir);
        Directory.CreateDirectory(WorkDir);
        var gitignore = Path.Combine(Dir, ".gitignore");
        if (!File.Exists(gitignore)) File.WriteAllText(gitignore, "*\n");
    }

    public string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, Path.GetFullPath(absolutePath)).Replace('\\', '/');
}
```

`plugins/db-migrate/engine/Dbm/Core/IEventSink.cs`:

```csharp
namespace Dbm.Core;

/// <summary>Where engine events go. persist:false events are live-only (SSE) and never stored.</summary>
public interface IEventSink
{
    void Publish(string type, object? payload = null, bool persist = true);
}
```

- [ ] **Step 4: Add `Workspace()` to `CliContext`**

Replace the whole of `plugins/db-migrate/engine/Dbm/Cli/CliContext.cs` with (inside this class the method name hides the type, hence the fully qualified `Dbm.Core.Workspace`):

```csharp
namespace Dbm.Cli;

public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }

    /// <summary>From the global --workspace option.</summary>
    public string? WorkspaceOverride { get; init; }

    public Dbm.Core.Workspace Workspace() => Dbm.Core.Workspace.Resolve(WorkspaceOverride);
}
```

- [ ] **Step 5: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:    38` (EnumTextTests 6, WorkspaceTests 4, WorkspaceResolveTests 1 added).

- [ ] **Step 6: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(core): enum text, clock, workspace resolution, event sink" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.2: State store + repositories

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/State/schema.sql`, `StateDb.cs`, `Rows.cs`, `ReaderExtensions.cs`, `ProjectRepo.cs`, `PhaseRepo.cs`, `ArtifactRepo.cs`, `FeedbackRepo.cs`, `EventRepo.cs`, `JobRepo.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Dbm.csproj` (embed `schema.sql`)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/State/StateDbTests.cs`, `ProjectPhaseRepoTests.cs`, `ArtifactFeedbackRepoTests.cs`, `EventJobRepoTests.cs`

**Interfaces:**
- Consumes: `EnumText`, `Clock`, `Json` (T0.1/T1.1).
- Produces (C2): `StateDb` (`Open`, `Connection`, `InTransaction`, `Execute`, `Scalar<T>`, `Query<T>`, plus `Path`, `static object ToDb(object?)`, `const string SchemaResource`); enums `Side`, `PhaseName`, `PhaseStatus`, `FeedbackStatus`, `JobStatus`; records `ProjectRow`, `ProjectSettings`, `PhaseRow`, `ArtifactRow`, `ArtifactMeta`, `FeedbackRow`, `EventRow`, `JobRow`; repositories `ProjectRepo`, `PhaseRepo`, `ArtifactRepo`, `FeedbackRepo`, `EventRepo`, `JobRepo` with every C2 member, plus `JobRepo.TryClaim(long id)` (atomic queued → running; false if another runner won) and `JobRepo.Recent(int limit)` (newest first); `ReaderExtensions` (`Str`, `IntN`, `LongN`, `Bool`, `Ts`, `TsN`, `Enum<T>`, `EnumN<T>`, `ParseTs`) for all later repositories.

Behaviour notes (normative for later tasks): one `SqliteConnection` per `StateDb` (pooling off, so test folders can be deleted); every public member takes one re-entrant lock and `InTransaction` holds it for the whole transaction (`BEGIN IMMEDIATE`; nested calls join); commands automatically join the active transaction; parameters bind from anonymous-object properties (or an `IDictionary<string, object?>`) to `$Name`, converting enums via `EnumText`, `DateTimeOffset` via `"O"` (UTC), `bool` → 0/1, `null` → `DBNull`. Inserts that need the id use `INSERT … RETURNING id`.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/State/StateDbTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class StateDbTests
{
    [Fact]
    public void Open_applies_pragmas_and_schema_version_1()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.True(File.Exists(tw.Ws.StateDbPath));
        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1L, db.Scalar<long>("PRAGMA foreign_keys"));
        Assert.Equal(1L, db.Scalar<long>("PRAGMA user_version"));
        var tables = db.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0));
        Assert.Equal(new[] { "artifact", "catalog", "connection", "error_row", "event", "feedback", "job", "phase", "project",
            "transfer_run", "transfer_task", "vector" }, tables);
    }

    [Fact]
    public void Reopening_keeps_data_and_does_not_reapply_the_schema()
    {
        using var tw = new TestWorkspace();
        using (var db = StateDb.Open(tw.Ws.StateDbPath)) db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'x')");

        using var again = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Equal(1L, again.Scalar<long>("SELECT COUNT(*) FROM event"));
    }

    public enum Colour { DeepBlue }

    [Fact]
    public void Binds_anonymous_properties_converting_enums_dates_bools_and_nulls()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        db.Execute("CREATE TABLE t (e TEXT, d TEXT, b INTEGER, n TEXT)");
        var when = new DateTimeOffset(2026, 9, 11, 12, 30, 0, TimeSpan.FromHours(2));

        db.Execute("INSERT INTO t VALUES ($E, $D, $B, $N)", new { E = Colour.DeepBlue, D = when, B = true, N = (string?)null });

        var row = db.Query("SELECT e, d, b, n FROM t", r => (E: r.GetString(0), D: r.GetString(1), B: r.GetInt64(2), N: r.IsDBNull(3)));
        Assert.Equal(("deep_blue", "2026-09-11T10:30:00.0000000+00:00", 1L, true), row.Single());
    }

    [Fact]
    public void Binds_dictionary_arguments()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        var v = db.Scalar<long>("SELECT $A + $B", new Dictionary<string, object?> { ["A"] = 2L, ["B"] = 3L });

        Assert.Equal(5L, v);
    }

    [Fact]
    public void Scalar_converts_nulls_and_types()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Null(db.Scalar<long?>("SELECT NULL"));
        Assert.Null(db.Scalar<string>("SELECT NULL"));
        Assert.Equal(42, db.Scalar<int>("SELECT 42"));
        Assert.True(db.Scalar<bool>("SELECT 1"));
    }

    [Fact]
    public void Failed_transaction_rolls_back_including_nested_work()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Throws<InvalidOperationException>(() => db.InTransaction(() =>
        {
            db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'outer')");
            db.InTransaction(() => db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'inner')"));
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM event"));
        var committed = db.InTransaction(() => db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'ok')"));
        Assert.Equal(1, committed);
    }

    [Fact]
    public async Task Concurrent_writers_on_one_instance_are_serialised()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                db.InTransaction(() =>
                {
                    var n = db.Scalar<long>("SELECT COUNT(*) FROM event");
                    db.Execute("INSERT INTO event (ts, type) VALUES ($Ts, $Type)", new { Ts = n.ToString(), Type = $"t{t}" });
                });
            }
        })));

        Assert.Equal(200L, db.Scalar<long>("SELECT COUNT(*) FROM event"));
    }

    [Fact]
    public void Two_instances_on_the_same_file_see_each_others_commits()
    {
        using var tw = new TestWorkspace();
        using var a = StateDb.Open(tw.Ws.StateDbPath);
        using var b = StateDb.Open(tw.Ws.StateDbPath);

        a.InTransaction(() => a.Execute("INSERT INTO event (ts, type) VALUES ('t', 'from-a')"));

        Assert.Equal("from-a", b.Scalar<string>("SELECT type FROM event"));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/State/ProjectPhaseRepoTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class ProjectPhaseRepoTests
{
    [Fact]
    public void Init_creates_the_project_and_one_phase_per_name_in_order()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var project = new ProjectRepo(db);
        var phases = new PhaseRepo(db);

        Assert.False(project.Exists());
        project.Init("shop");

        Assert.True(project.Exists());
        var p = project.Get();
        Assert.Equal("shop", p.Name);
        Assert.False(p.Paused);
        Assert.Null(p.AgentSeenAt);
        var all = phases.All();
        Assert.Equal(Enum.GetValues<PhaseName>(), all.Select(r => r.Name));
        Assert.Equal(Enumerable.Range(0, 8), all.Select(r => r.Ordinal));
        Assert.Equal(PhaseStatus.AwaitingReview, all[0].Status);
        Assert.All(all.Skip(1), r => Assert.Equal(PhaseStatus.Pending, r.Status));
        Assert.Throws<InvalidOperationException>(() => project.Init("again"));
    }

    [Fact]
    public void Pause_agent_touch_and_settings_round_trip()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var project = new ProjectRepo(db);
        project.Init("shop");

        project.SetPaused(true);
        project.TouchAgent();
        Assert.Equal(0.85, project.GetSettings().AutoAcceptScore);
        project.SaveSettings(new ProjectSettings { SampleValues = false, AutoAcceptScore = 0.9 });

        var p = project.Get();
        Assert.True(p.Paused);
        Assert.NotNull(p.AgentSeenAt);
        Assert.True(DateTimeOffset.UtcNow - p.AgentSeenAt!.Value < TimeSpan.FromMinutes(1));
        var settings = project.GetSettings();
        Assert.False(settings.SampleValues);
        Assert.Equal(0.9, settings.AutoAcceptScore);
        Assert.Equal(100_000, settings.ProfileSampleRows);
    }

    [Fact]
    public void Phase_status_versions_and_approval()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        new ProjectRepo(db).Init("shop");
        var phases = new PhaseRepo(db);

        phases.SetStatus(PhaseName.Analysis, PhaseStatus.Drafting);
        phases.SetCurrentVersion(PhaseName.Analysis, 2);
        Assert.Equal((PhaseStatus.Drafting, (int?)2), (phases.Get(PhaseName.Analysis).Status, phases.Get(PhaseName.Analysis).CurrentVersion));

        phases.SetApproved(PhaseName.Analysis, 2, "abc:def");
        var approved = phases.Get(PhaseName.Analysis);
        Assert.Equal(PhaseStatus.Approved, approved.Status);
        Assert.Equal(2, approved.ApprovedVersion);
        Assert.Equal("abc:def", approved.ApprovedFingerprint);

        phases.ClearApproval(PhaseName.Analysis);
        var cleared = phases.Get(PhaseName.Analysis);
        Assert.Null(cleared.ApprovedVersion);
        Assert.Null(cleared.ApprovedFingerprint);
        Assert.Equal(2, cleared.CurrentVersion);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/State/ArtifactFeedbackRepoTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class ArtifactFeedbackRepoTests
{
    [Fact]
    public void Artifacts_are_versioned_per_phase()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var artifacts = new ArtifactRepo(db);

        Assert.Equal(0, artifacts.NextVersion(PhaseName.Mapping));
        var v0 = artifacts.Add(PhaseName.Mapping, 0, "{\"a\":1}", "script", "draft");
        artifacts.Add(PhaseName.Mapping, 1, "{\"a\":2}", "agent", null);
        artifacts.Add(PhaseName.Sql, 0, "{}", "script", null);

        Assert.True(v0.Id > 0);
        Assert.Equal(2, artifacts.NextVersion(PhaseName.Mapping));
        Assert.Equal("{\"a\":2}", artifacts.Latest(PhaseName.Mapping)!.PayloadJson);
        Assert.Equal("draft", artifacts.Get(PhaseName.Mapping, 0)!.Summary);
        Assert.Null(artifacts.Get(PhaseName.Mapping, 5));
        Assert.Equal(new[] { (0, "script"), (1, "agent") }, artifacts.List(PhaseName.Mapping).Select(m => (m.Version, m.Author)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => artifacts.Add(PhaseName.Mapping, 1, "{}", "agent", null));
    }

    [Fact]
    public void Feedback_lifecycle_draft_open_responded()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var feedback = new FeedbackRepo(db);

        var a = feedback.Add(PhaseName.Analysis, 1, "finding:heap:dbo.AUDIT_LOG", "Explain the heap");
        var b = feedback.Add(PhaseName.Analysis, 1, null, "General remark");
        feedback.Add(PhaseName.Mapping, 1, null, "Other phase");

        Assert.Equal(FeedbackStatus.Draft, a.Status);
        Assert.True(feedback.DeleteDraft(b.Id));
        Assert.Null(feedback.Get(b.Id));
        Assert.Equal(1, feedback.SubmitDrafts(PhaseName.Analysis));
        Assert.False(feedback.DeleteDraft(a.Id));
        Assert.Single(feedback.List(PhaseName.Analysis, FeedbackStatus.Open));

        feedback.Respond(a.Id, FeedbackStatus.Addressed, "Explained.", 2);

        var responded = feedback.Get(a.Id)!;
        Assert.Equal(FeedbackStatus.Addressed, responded.Status);
        Assert.Equal("Explained.", responded.Response);
        Assert.Equal(2, responded.RespondedVersion);
        Assert.Equal("finding:heap:dbo.AUDIT_LOG", responded.Anchor);
        Assert.Empty(feedback.List(PhaseName.Analysis, FeedbackStatus.Open));
        Assert.Single(feedback.List(PhaseName.Analysis));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/State/EventJobRepoTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class EventJobRepoTests
{
    [Fact]
    public void Events_append_and_page_by_id()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var events = new EventRepo(db);

        Assert.Equal(0, events.LastId());
        var first = events.Append("paused");
        var second = events.Append("state_changed", new { phase = PhaseName.Analysis, status = PhaseStatus.AwaitingReview });

        Assert.Equal(second, events.LastId());
        var since = events.Since(first);
        Assert.Single(since);
        Assert.Equal("{\"phase\":\"analysis\",\"status\":\"awaiting_review\"}", since[0].PayloadJson);
        Assert.Equal("{}", events.Since(0)[0].PayloadJson);
        Assert.Single(events.Since(0, limit: 1));
    }

    [Fact]
    public void Jobs_queue_claim_finish_and_requeue()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var jobs = new JobRepo(db);

        var a = jobs.Enqueue("discover", PhaseName.Discovery);
        var b = jobs.Enqueue("analyze", PhaseName.Analysis);
        Assert.Equal(a, jobs.NextQueued()!.Id);
        Assert.True(jobs.TryClaim(a));
        Assert.False(jobs.TryClaim(a));
        Assert.Equal(b, jobs.NextQueued()!.Id);
        Assert.Equal(2, jobs.Active().Count);

        jobs.MarkDone(a);
        jobs.MarkRunning(b);
        jobs.MarkFailed(b, "boom");
        var failed = jobs.Get(b)!;
        Assert.Equal((JobStatus.Failed, "boom"), (failed.Status, failed.Error));
        Assert.NotNull(failed.StartedAt);
        Assert.NotNull(failed.EndedAt);
        Assert.Equal(JobStatus.Done, jobs.LatestFor(PhaseName.Discovery)!.Status);
        Assert.Null(jobs.LatestFor(PhaseName.Sql));
        Assert.Equal(new[] { b, a }, jobs.Recent(10).Select(j => j.Id));

        var c = jobs.Enqueue("sqlgen", null);
        jobs.MarkRunning(c);
        Assert.Equal(1, jobs.RequeueStaleRunning());
        Assert.Equal(JobStatus.Queued, jobs.Get(c)!.Status);
        Assert.Null(jobs.Get(c)!.Phase);
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'StateDb' could not be found` (and `ProjectRepo`, `PhaseName`, …).

- [ ] **Step 3: Add the schema and embed it**

`plugins/db-migrate/engine/Dbm/Core/State/schema.sql` (exactly C2):

```sql
CREATE TABLE project (id INTEGER PRIMARY KEY CHECK (id = 1), name TEXT NOT NULL, created_at TEXT NOT NULL,
  paused INTEGER NOT NULL DEFAULT 0, agent_seen_at TEXT, settings_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE connection (side TEXT PRIMARY KEY CHECK (side IN ('src','tgt')), encrypted TEXT NOT NULL,
  server_meta_json TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE phase (name TEXT PRIMARY KEY, ordinal INTEGER NOT NULL, status TEXT NOT NULL, current_version INTEGER,
  approved_version INTEGER, approved_fingerprint TEXT, updated_at TEXT NOT NULL);
CREATE TABLE artifact (id INTEGER PRIMARY KEY AUTOINCREMENT, phase TEXT NOT NULL, version INTEGER NOT NULL,
  payload_json TEXT NOT NULL, author TEXT NOT NULL, summary TEXT, created_at TEXT NOT NULL, UNIQUE (phase, version));
CREATE TABLE feedback (id INTEGER PRIMARY KEY AUTOINCREMENT, phase TEXT NOT NULL, version INTEGER NOT NULL, anchor TEXT,
  text TEXT NOT NULL, status TEXT NOT NULL, response TEXT, responded_version INTEGER, created_at TEXT NOT NULL);
CREATE TABLE event (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, type TEXT NOT NULL, payload_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE job (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, phase TEXT, status TEXT NOT NULL, error TEXT,
  created_at TEXT NOT NULL, started_at TEXT, ended_at TEXT);
CREATE TABLE catalog (side TEXT PRIMARY KEY, snapshot_json TEXT NOT NULL, fingerprint TEXT NOT NULL, extracted_at TEXT NOT NULL);
CREATE TABLE vector (side TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL, text TEXT NOT NULL, vec_json TEXT NOT NULL,
  PRIMARY KEY (side, kind, key));
CREATE TABLE transfer_run (id INTEGER PRIMARY KEY AUTOINCREMENT, sql_version INTEGER NOT NULL, status TEXT NOT NULL,
  options_json TEXT NOT NULL, started_at TEXT, ended_at TEXT, summary_json TEXT);
CREATE TABLE transfer_task (run_id INTEGER NOT NULL, task_id TEXT NOT NULL, target TEXT NOT NULL, ordinal INTEGER NOT NULL,
  status TEXT NOT NULL, rows_source INTEGER, rows_before INTEGER, rows_done INTEGER NOT NULL DEFAULT 0,
  rows_error INTEGER NOT NULL DEFAULT 0, last_key_json TEXT, heartbeat_at TEXT, started_at TEXT, ended_at TEXT,
  error TEXT, validation_json TEXT, PRIMARY KEY (run_id, task_id));
CREATE TABLE error_row (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, task_id TEXT NOT NULL,
  key_json TEXT, row_json TEXT, error TEXT NOT NULL, ts TEXT NOT NULL);
```

In `plugins/db-migrate/engine/Dbm/Dbm.csproj`, insert after the `<ItemGroup>` that contains `<Content Update="wwwroot\**" … />`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Core\State\schema.sql" LogicalName="Dbm.Core.State.schema.sql" />
  </ItemGroup>
```

- [ ] **Step 4: Implement the row types and `StateDb`**

`plugins/db-migrate/engine/Dbm/Core/State/Rows.cs`:

```csharp
namespace Dbm.Core.State;

public enum Side { Src, Tgt }
public enum PhaseName { Setup, Discovery, Analysis, Mapping, Sql, Ready, Transfer, Complete }
public enum PhaseStatus { Pending, Running, Drafting, AwaitingReview, Reworking, Approved, Stale }
public enum FeedbackStatus { Draft, Open, Addressed, Declined }
public enum JobStatus { Queued, Running, Done, Failed }

public sealed record ProjectRow(string Name, DateTimeOffset CreatedAt, bool Paused, DateTimeOffset? AgentSeenAt);

public sealed record ProjectSettings
{
    public bool SampleValues { get; init; } = true;
    public int ProfileSampleRows { get; init; } = 100_000;
    public double AutoAcceptScore { get; init; } = 0.85;
    public double CandidateScore { get; init; } = 0.50;
}

public sealed record PhaseRow(PhaseName Name, int Ordinal, PhaseStatus Status, int? CurrentVersion, int? ApprovedVersion,
    string? ApprovedFingerprint, DateTimeOffset UpdatedAt);

public sealed record ArtifactRow(long Id, PhaseName Phase, int Version, string PayloadJson, string Author, string? Summary,
    DateTimeOffset CreatedAt);

public sealed record ArtifactMeta(int Version, string Author, string? Summary, DateTimeOffset CreatedAt);

public sealed record FeedbackRow(long Id, PhaseName Phase, int Version, string? Anchor, string Text, FeedbackStatus Status,
    string? Response, int? RespondedVersion, DateTimeOffset CreatedAt);

public sealed record EventRow(long Id, DateTimeOffset Ts, string Type, string PayloadJson);

public sealed record JobRow(long Id, string Kind, PhaseName? Phase, JobStatus Status, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
```

`plugins/db-migrate/engine/Dbm/Core/State/ReaderExtensions.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

/// <summary>Null-aware column readers shared by every repository.</summary>
public static class ReaderExtensions
{
    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static int? IntN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static long? LongN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static bool Bool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;

    public static DateTimeOffset Ts(this SqliteDataReader r, int i) => ParseTs(r.GetString(i));

    public static DateTimeOffset? TsN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseTs(r.GetString(i));

    public static T Enum<T>(this SqliteDataReader r, int i) where T : struct, System.Enum => EnumText.Parse<T>(r.GetString(i));

    public static T? EnumN<T>(this SqliteDataReader r, int i) where T : struct, System.Enum =>
        r.IsDBNull(i) ? null : EnumText.Parse<T>(r.GetString(i));

    public static DateTimeOffset ParseTs(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
```

`plugins/db-migrate/engine/Dbm/Core/State/StateDb.cs`:

```csharp
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

/// <summary>
/// One SQLite connection per process. Thread-safe: every public member takes a re-entrant lock and
/// InTransaction holds it for the whole transaction, so commands on other threads wait instead of interleaving.
/// </summary>
public sealed class StateDb : IDisposable
{
    public const string SchemaResource = "Dbm.Core.State.schema.sql";

    private readonly object _gate = new();
    private SqliteTransaction? _tx;

    private StateDb(SqliteConnection connection, string path)
    {
        Connection = connection;
        Path = path;
    }

    public SqliteConnection Connection { get; }
    public string Path { get; }

    public static StateDb Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        var db = new StateDb(connection, full);
        try
        {
            db.Execute("PRAGMA journal_mode=WAL;");
            db.Execute("PRAGMA busy_timeout=5000;");
            db.Execute("PRAGMA foreign_keys=ON;");
            db.Migrate();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void InTransaction(Action work) => InTransaction<object?>(() =>
    {
        work();
        return null;
    });

    /// <summary>BEGIN IMMEDIATE; a nested call joins the outer transaction.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        lock (_gate)
        {
            if (_tx is not null) return work();
            _tx = Connection.BeginTransaction(deferred: false);
            try
            {
                var result = work();
                _tx.Commit();
                return result;
            }
            catch
            {
                try { _tx.Rollback(); } catch (SqliteException) { /* already rolled back */ }
                throw;
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
            }
        }
    }

    /// <summary>args: anonymous object or IDictionary; property X binds to $X.</summary>
    public int Execute(string sql, object? args = null)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public T? Scalar<T>(string sql, object? args = null)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return ConvertScalar<T>(cmd.ExecuteScalar());
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, object? args = null)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read()) list.Add(map(reader));
            return list;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tx?.Dispose();
            _tx = null;
            Connection.Dispose();
        }
    }

    /// <summary>Converts a CLR value to what SQLite stores: enums as snake text, timestamps as "O", bool as 0/1.</summary>
    public static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        Enum e => EnumText.ToText(e),
        DateTimeOffset d => d.ToUniversalTime().ToString("O"),
        DateTime d => new DateTimeOffset(d.ToUniversalTime()).ToString("O"),
        bool b => b ? 1L : 0L,
        _ => value,
    };

    internal static string LoadSchema()
    {
        using var stream = typeof(StateDb).Assembly.GetManifestResourceStream(SchemaResource)
                           ?? throw new InvalidOperationException($"embedded resource {SchemaResource} is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Migrate()
    {
        if (Scalar<long>("PRAGMA user_version") >= 1) return;
        InTransaction(() =>
        {
            if (Scalar<long>("PRAGMA user_version") >= 1) return;   // another process won the race
            Execute(LoadSchema());
            Execute("PRAGMA user_version = 1");
        });
    }

    private SqliteCommand Command(string sql, object? args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        Bind(cmd, args);
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, object? args)
    {
        switch (args)
        {
            case null:
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var (key, value) in pairs) cmd.Parameters.AddWithValue("$" + key, ToDb(value));
                return;
            default:
                foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length == 0) cmd.Parameters.AddWithValue("$" + p.Name, ToDb(p.GetValue(args)));
                }
                return;
        }
    }

    private static T? ConvertScalar<T>(object? value)
    {
        if (value is null || value is DBNull) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsInstanceOfType(value)) return (T)value;
        if (target == typeof(bool)) return (T)(object)(Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 5: Implement the repositories**

`plugins/db-migrate/engine/Dbm/Core/State/ProjectRepo.cs` (phase ordinals = `(int)PhaseName`):

```csharp
namespace Dbm.Core.State;

public sealed class ProjectRepo(StateDb db)
{
    public bool Exists() => db.Scalar<long>("SELECT COUNT(*) FROM project") > 0;

    /// <summary>Creates the project row and one phase row per PhaseName (Setup = awaiting_review, others = pending).</summary>
    public void Init(string name) => db.InTransaction(() =>
    {
        if (Exists()) throw new InvalidOperationException("project already initialised");
        var now = Clock.NowText();
        db.Execute("INSERT INTO project (id, name, created_at, paused, settings_json) VALUES (1, $Name, $Now, 0, '{}')",
            new { Name = name, Now = now });
        foreach (var phase in Enum.GetValues<PhaseName>())
        {
            db.Execute("INSERT INTO phase (name, ordinal, status, updated_at) VALUES ($Name, $Ordinal, $Status, $Now)", new
            {
                Name = phase,
                Ordinal = (int)phase,
                Status = phase == PhaseName.Setup ? PhaseStatus.AwaitingReview : PhaseStatus.Pending,
                Now = now,
            });
        }
    });

    public ProjectRow Get() =>
        db.Query("SELECT name, created_at, paused, agent_seen_at FROM project WHERE id = 1",
                r => new ProjectRow(r.GetString(0), r.Ts(1), r.Bool(2), r.TsN(3)))
            .FirstOrDefault()
        ?? throw new InvalidOperationException("project not initialised");

    public void SetPaused(bool paused) =>
        db.Execute("UPDATE project SET paused = $Paused WHERE id = 1", new { Paused = paused });

    public void TouchAgent() =>
        db.Execute("UPDATE project SET agent_seen_at = $Now WHERE id = 1", new { Now = Clock.NowText() });

    public ProjectSettings GetSettings() =>
        Json.Deserialize<ProjectSettings>(db.Scalar<string>("SELECT settings_json FROM project WHERE id = 1") ?? "{}");

    public void SaveSettings(ProjectSettings settings) =>
        db.Execute("UPDATE project SET settings_json = $SettingsJson WHERE id = 1", new { SettingsJson = Json.Serialize(settings) });
}
```

`plugins/db-migrate/engine/Dbm/Core/State/PhaseRepo.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class PhaseRepo(StateDb db)
{
    private const string Columns = "name, ordinal, status, current_version, approved_version, approved_fingerprint, updated_at";

    public PhaseRow Get(PhaseName phase) =>
        db.Query($"SELECT {Columns} FROM phase WHERE name = $Name", Map, new { Name = phase }).FirstOrDefault()
        ?? throw new KeyNotFoundException($"phase {EnumText.ToText(phase)} not found (project not initialised?)");

    public IReadOnlyList<PhaseRow> All() => db.Query($"SELECT {Columns} FROM phase ORDER BY ordinal", Map);

    public void SetStatus(PhaseName phase, PhaseStatus status) =>
        db.Execute("UPDATE phase SET status = $Status, updated_at = $Now WHERE name = $Name",
            new { Name = phase, Status = status, Now = Clock.NowText() });

    public void SetCurrentVersion(PhaseName phase, int? version) =>
        db.Execute("UPDATE phase SET current_version = $Version, updated_at = $Now WHERE name = $Name",
            new { Name = phase, Version = version, Now = Clock.NowText() });

    /// <summary>Also sets status = approved.</summary>
    public void SetApproved(PhaseName phase, int version, string? fingerprint) =>
        db.Execute("""
            UPDATE phase SET status = 'approved', approved_version = $Version, approved_fingerprint = $Fingerprint,
                             updated_at = $Now
            WHERE name = $Name
            """, new { Name = phase, Version = version, Fingerprint = fingerprint, Now = Clock.NowText() });

    public void ClearApproval(PhaseName phase) =>
        db.Execute("UPDATE phase SET approved_version = NULL, approved_fingerprint = NULL, updated_at = $Now WHERE name = $Name",
            new { Name = phase, Now = Clock.NowText() });

    private static PhaseRow Map(SqliteDataReader r) =>
        new(r.Enum<PhaseName>(0), r.GetInt32(1), r.Enum<PhaseStatus>(2), r.IntN(3), r.IntN(4), r.Str(5), r.Ts(6));
}
```

`plugins/db-migrate/engine/Dbm/Core/State/ArtifactRepo.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class ArtifactRepo(StateDb db)
{
    private const string Columns = "id, phase, version, payload_json, author, summary, created_at";

    /// <summary>0 when the phase has no artifact yet, else max(version) + 1.</summary>
    public int NextVersion(PhaseName phase)
    {
        var max = db.Scalar<long?>("SELECT MAX(version) FROM artifact WHERE phase = $Phase", new { Phase = phase });
        return max is null ? 0 : (int)max.Value + 1;
    }

    /// <summary>author: "script" | "agent" | "human".</summary>
    public ArtifactRow Add(PhaseName phase, int version, string payloadJson, string author, string? summary)
    {
        var now = Clock.Now();
        var id = db.Scalar<long>("""
            INSERT INTO artifact (phase, version, payload_json, author, summary, created_at)
            VALUES ($Phase, $Version, $Payload, $Author, $Summary, $Now) RETURNING id
            """, new { Phase = phase, Version = version, Payload = payloadJson, Author = author, Summary = summary, Now = now });
        return new ArtifactRow(id, phase, version, payloadJson, author, summary, now);
    }

    public ArtifactRow? Get(PhaseName phase, int version) =>
        db.Query($"SELECT {Columns} FROM artifact WHERE phase = $Phase AND version = $Version", Map,
            new { Phase = phase, Version = version }).FirstOrDefault();

    public ArtifactRow? Latest(PhaseName phase) =>
        db.Query($"SELECT {Columns} FROM artifact WHERE phase = $Phase ORDER BY version DESC LIMIT 1", Map,
            new { Phase = phase }).FirstOrDefault();

    /// <summary>Ascending version, without payloads.</summary>
    public IReadOnlyList<ArtifactMeta> List(PhaseName phase) =>
        db.Query("SELECT version, author, summary, created_at FROM artifact WHERE phase = $Phase ORDER BY version",
            r => new ArtifactMeta(r.GetInt32(0), r.GetString(1), r.Str(2), r.Ts(3)), new { Phase = phase });

    private static ArtifactRow Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.Enum<PhaseName>(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.Str(5), r.Ts(6));
}
```

`plugins/db-migrate/engine/Dbm/Core/State/FeedbackRepo.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class FeedbackRepo(StateDb db)
{
    private const string Columns = "id, phase, version, anchor, text, status, response, responded_version, created_at";

    /// <summary>New items start as drafts; RequestChanges submits them.</summary>
    public FeedbackRow Add(PhaseName phase, int version, string? anchor, string text)
    {
        var now = Clock.Now();
        var id = db.Scalar<long>("""
            INSERT INTO feedback (phase, version, anchor, text, status, created_at)
            VALUES ($Phase, $Version, $Anchor, $Text, 'draft', $Now) RETURNING id
            """, new { Phase = phase, Version = version, Anchor = anchor, Text = text, Now = now });
        return new FeedbackRow(id, phase, version, anchor, text, FeedbackStatus.Draft, null, null, now);
    }

    public FeedbackRow? Get(long id) =>
        db.Query($"SELECT {Columns} FROM feedback WHERE id = $Id", Map, new { Id = id }).FirstOrDefault();

    /// <summary>draft → open for the phase; returns how many were submitted.</summary>
    public int SubmitDrafts(PhaseName phase) =>
        db.Execute("UPDATE feedback SET status = 'open' WHERE phase = $Phase AND status = 'draft'", new { Phase = phase });

    public IReadOnlyList<FeedbackRow> List(PhaseName phase, FeedbackStatus? status = null) =>
        status is null
            ? db.Query($"SELECT {Columns} FROM feedback WHERE phase = $Phase ORDER BY id", Map, new { Phase = phase })
            : db.Query($"SELECT {Columns} FROM feedback WHERE phase = $Phase AND status = $Status ORDER BY id", Map,
                new { Phase = phase, Status = status.Value });

    public void Respond(long id, FeedbackStatus status, string response, int respondedVersion) =>
        db.Execute("UPDATE feedback SET status = $Status, response = $Response, responded_version = $Version WHERE id = $Id",
            new { Id = id, Status = status, Response = response, Version = respondedVersion });

    public bool DeleteDraft(long id) =>
        db.Execute("DELETE FROM feedback WHERE id = $Id AND status = 'draft'", new { Id = id }) > 0;

    private static FeedbackRow Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.Enum<PhaseName>(1), r.GetInt32(2), r.Str(3), r.GetString(4), r.Enum<FeedbackStatus>(5),
            r.Str(6), r.IntN(7), r.Ts(8));
}
```

`plugins/db-migrate/engine/Dbm/Core/State/EventRepo.cs`:

```csharp
namespace Dbm.Core.State;

/// <summary>Append-only audit log; also the feed the server's EventPump turns into SSE.</summary>
public sealed class EventRepo(StateDb db)
{
    public long Append(string type, object? payload = null) =>
        db.Scalar<long>("INSERT INTO event (ts, type, payload_json) VALUES ($Now, $Type, $Payload) RETURNING id",
            new { Now = Clock.NowText(), Type = type, Payload = payload is null ? "{}" : Json.Serialize(payload) });

    public IReadOnlyList<EventRow> Since(long afterId, int limit = 500) =>
        db.Query("SELECT id, ts, type, payload_json FROM event WHERE id > $After ORDER BY id LIMIT $Limit",
            r => new EventRow(r.GetInt64(0), r.Ts(1), r.GetString(2), r.GetString(3)),
            new { After = afterId, Limit = limit });

    public long LastId() => db.Scalar<long?>("SELECT MAX(id) FROM event") ?? 0;
}
```

`plugins/db-migrate/engine/Dbm/Core/State/JobRepo.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class JobRepo(StateDb db)
{
    private const string Columns = "id, kind, phase, status, error, created_at, started_at, ended_at";

    public long Enqueue(string kind, PhaseName? phase) =>
        db.Scalar<long>("INSERT INTO job (kind, phase, status, created_at) VALUES ($Kind, $Phase, 'queued', $Now) RETURNING id",
            new { Kind = kind, Phase = phase, Now = Clock.NowText() });

    public JobRow? Get(long id) =>
        db.Query($"SELECT {Columns} FROM job WHERE id = $Id", Map, new { Id = id }).FirstOrDefault();

    public JobRow? NextQueued() =>
        db.Query($"SELECT {Columns} FROM job WHERE status = 'queued' ORDER BY id LIMIT 1", Map).FirstOrDefault();

    public JobRow? LatestFor(PhaseName phase) =>
        db.Query($"SELECT {Columns} FROM job WHERE phase = $Phase ORDER BY id DESC LIMIT 1", Map, new { Phase = phase })
            .FirstOrDefault();

    public void MarkRunning(long id) =>
        db.Execute("UPDATE job SET status = 'running', started_at = $Now, error = NULL WHERE id = $Id",
            new { Id = id, Now = Clock.NowText() });

    /// <summary>Atomically moves a queued job to running; false when another runner (process) took it first.</summary>
    public bool TryClaim(long id) =>
        db.Execute("UPDATE job SET status = 'running', started_at = $Now, error = NULL WHERE id = $Id AND status = 'queued'",
            new { Id = id, Now = Clock.NowText() }) == 1;

    public void MarkDone(long id) =>
        db.Execute("UPDATE job SET status = 'done', ended_at = $Now WHERE id = $Id", new { Id = id, Now = Clock.NowText() });

    public void MarkFailed(long id, string error) =>
        db.Execute("UPDATE job SET status = 'failed', error = $Error, ended_at = $Now WHERE id = $Id",
            new { Id = id, Error = error, Now = Clock.NowText() });

    /// <summary>Queued or running, oldest first.</summary>
    public IReadOnlyList<JobRow> Active() =>
        db.Query($"SELECT {Columns} FROM job WHERE status IN ('queued', 'running') ORDER BY id", Map);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<JobRow> Recent(int limit) =>
        db.Query($"SELECT {Columns} FROM job ORDER BY id DESC LIMIT $Limit", Map, new { Limit = limit });

    /// <summary>running → queued; called when a server starts (the previous process died mid-job).</summary>
    public int RequeueStaleRunning() =>
        db.Execute("UPDATE job SET status = 'queued', started_at = NULL WHERE status = 'running'");

    private static JobRow Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.EnumN<PhaseName>(2), r.Enum<JobStatus>(3), r.Str(4), r.Ts(5), r.TsN(6), r.TsN(7));
}
```

- [ ] **Step 6: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:    53` (StateDbTests 8, ProjectPhaseRepoTests 3, ArtifactFeedbackRepoTests 2, EventJobRepoTests 2 added).

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(state): SQLite state store, schema v1 and repositories" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.3: Crypto, redaction, connections, SqlConnect

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Crypto/ISecretProtector.cs`, `DpapiProtector.cs`, `AesGcmFileKeyProtector.cs`, `SecretProtector.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Sql/ServerMeta.cs`, `Redactor.cs`, `SqlConnect.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/State/ConnectionRepo.cs`
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/SqlTestServer.cs`, `Support/TempDatabase.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Crypto/ProtectorTests.cs`, `Unit/Sql/RedactorTests.cs`, `Unit/State/ConnectionRepoTests.cs`, `Integration/Sql/SqlConnectTests.cs`

**Interfaces:**
- Consumes: `StateDb`, `Side` (T1.2), `UserHome` (T0.2), `EnumText`, `Clock`, `Json`.
- Produces (C2/C3): `ISecretProtector`, `DpapiProtector` (`const Prefix = "dpapi:"`), `AesGcmFileKeyProtector(keyPath)` (`const Prefix = "aesgcm:"`, `KeyPath`), `SecretProtector.ForCurrentUser()`; `ServerMeta`; `Redactor.Describe/Scrub/SecretsOf` plus `Redactor.AuthMode(string)` (`"integrated"`, `"sql"`, `"default"`, `"unknown"`, or the snake_case `Authentication` keyword, e.g. `"active_directory_default"`); `SqlConnect.Normalize/OpenAsync/ProbeAsync`; `ConnectionRepo`. Test support (public API other milestones rely on): `SqlTestServer.ConnectionString`, `SqlTestServer.MasterConnectionString`, `SqlTestServer.ForDatabase(string db)`; `TempDatabase.CreateAsync(string prefix = "dbm_test")` (database `<prefix>_<8 hex>`), `string Name`, `string ConnectionString`, `Task ExecAsync(string sql)` (splits batches on lines that are exactly `GO`), `Task<T> ScalarAsync<T>(string sql)`, `static IReadOnlyList<string> SplitBatches(string script)`, `ValueTask DisposeAsync()` (SINGLE_USER WITH ROLLBACK IMMEDIATE, then DROP).

- [ ] **Step 1: Write the failing tests and SQL test support**

`plugins/db-migrate/engine/Dbm.Tests/Support/SqlTestServer.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>The SQL Server used by integration tests: env DBM_TEST_SQL, else LocalDB.</summary>
public static class SqlTestServer
{
    public const string DefaultConnectionString = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBM_TEST_SQL") is { Length: > 0 } cs ? cs : DefaultConnectionString;

    public static string MasterConnectionString => ForDatabase("master");

    public static string ForDatabase(string database) =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;
}
```

`plugins/db-migrate/engine/Dbm.Tests/Support/TempDatabase.cs`:

```csharp
using System.Text;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>A throw-away database "dbm_test_&lt;8 hex&gt;" on the test server; dropped on dispose.</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private TempDatabase(string name)
    {
        Name = name;
        ConnectionString = SqlTestServer.ForDatabase(name);
    }

    public string Name { get; }
    public string ConnectionString { get; }

    /// <summary>Creates "&lt;prefix&gt;_&lt;8 hex&gt;" (default "dbm_test_xxxxxxxx").</summary>
    public static async Task<TempDatabase> CreateAsync(string prefix = "dbm_test")
    {
        var db = new TempDatabase(prefix + "_" + Guid.NewGuid().ToString("N")[..8]);
        await ExecOnMasterAsync($"CREATE DATABASE [{db.Name}]");
        return db;
    }

    /// <summary>Runs a script, splitting batches on lines that are exactly "GO" (case-insensitive, surrounding spaces ignored).</summary>
    public async Task ExecAsync(string script)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public static IReadOnlyList<string> SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }
        if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
        return batches;
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await ExecOnMasterAsync($"""
            IF DB_ID('{Name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Name}];
            END
            """);
    }

    private static async Task ExecOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(SqlTestServer.MasterConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Crypto/ProtectorTests.cs` (the DPAPI test calls the protector outside lambdas so the CA1416 platform analyzer sees the `OperatingSystem.IsWindows()` guard):

```csharp
using System.Security.Cryptography;
using Dbm.Core.Crypto;

namespace Dbm.Tests.Unit.Crypto;

public class ProtectorTests
{
    private const string Secret = "Server=db;Database=Shop;User ID=migrator;Password=S3cr3t!pw;";

    private static string TempKeyPath() => Path.Combine(Path.GetTempPath(), "dbm-key-" + Guid.NewGuid().ToString("N"), "key");

    [Fact]
    public void AesGcm_round_trips_with_a_fresh_nonce_each_time()
    {
        var keyPath = TempKeyPath();
        var p = new AesGcmFileKeyProtector(keyPath);

        var a = p.Protect(Secret);
        var b = p.Protect(Secret);

        Assert.StartsWith("aesgcm:", a);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("S3cr3t", a);
        Assert.Equal(Secret, p.Unprotect(a));
        Assert.Equal(Secret, new AesGcmFileKeyProtector(keyPath).Unprotect(b));
        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
    }

    [Fact]
    public void AesGcm_rejects_tampering_foreign_prefixes_and_other_keys()
    {
        var p = new AesGcmFileKeyProtector(TempKeyPath());
        var protectedText = p.Protect(Secret);
        var bytes = Convert.FromBase64String(protectedText["aesgcm:".Length..]);
        bytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect("aesgcm:" + Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect("dpapi:AAAA"));
        Assert.ThrowsAny<CryptographicException>(() => new AesGcmFileKeyProtector(TempKeyPath()).Unprotect(protectedText));
    }

    [Fact]
    public void Dpapi_round_trips_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiProtector();

        var protectedText = p.Protect(Secret);

        Assert.StartsWith("dpapi:", protectedText);
        Assert.DoesNotContain("S3cr3t", protectedText);
        Assert.Equal(Secret, p.Unprotect(protectedText));
        CryptographicException? rejected = null;
        try
        {
            p.Unprotect("aesgcm:AAAA");
        }
        catch (CryptographicException ex)
        {
            rejected = ex;
        }
        Assert.NotNull(rejected);
    }

    [Fact]
    public void ForCurrentUser_picks_the_platform_protector()
    {
        var p = SecretProtector.ForCurrentUser();

        if (OperatingSystem.IsWindows()) Assert.IsType<DpapiProtector>(p);
        else Assert.IsType<AesGcmFileKeyProtector>(p);
        Assert.Equal(Secret, p.Unprotect(p.Protect(Secret)));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Sql/RedactorTests.cs` — the last test is the verified fact from the overview: with `Microsoft.Data.SqlClient.Extensions.Azure` referenced, the Entra ID provider is found without any registration code; the test fails if someone removes the package:

```csharp
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Unit.Sql;

public class RedactorTests
{
    [Fact]
    public void Describe_sql_login_shows_user_but_never_the_password()
    {
        var text = Redactor.Describe("Server=tcp:db.example.com,1433;Database=Shop;User ID=migrator;Password=S3cr3t!pw;Encrypt=true");

        Assert.Equal("server=tcp:db.example.com,1433; database=Shop; auth=sql; user=migrator", text);
    }

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true", @"server=(localdb)\MSSQLLocalDB; database=(default); auth=integrated")]
    [InlineData("Server=x.database.windows.net;Database=D;Authentication=Active Directory Default", "server=x.database.windows.net; database=D; auth=active_directory_default")]
    [InlineData("this is ; not = valid ; = ;", "invalid connection string")]
    public void Describe_summarises_the_auth_mode(string cs, string expected)
    {
        Assert.Equal(expected, Redactor.Describe(cs));
    }

    [Fact]
    public void SecretsOf_returns_passwords_even_for_unparsable_strings()
    {
        Assert.Equal(new[] { "S3cr3t!pw" }, Redactor.SecretsOf("Server=a;User ID=u;Password=S3cr3t!pw"));
        Assert.Empty(Redactor.SecretsOf("Server=a;Integrated Security=true"));
        Assert.Equal(new[] { "p;w=x" }, Redactor.SecretsOf("Server=a;Bogus Key=1;Pwd='p;w=x'"));
    }

    [Fact]
    public void Scrub_replaces_each_secret_of_four_or_more_characters()
    {
        var text = Redactor.Scrub("login failed for S3cr3t!pw and abc", ["S3cr3t!pw", "abc", null, ""]);

        Assert.Equal("login failed for *** and abc", text);
    }

    [Fact]
    public void Normalize_sets_application_name_only_when_left_default()
    {
        var normalized = new SqlConnectionStringBuilder(SqlConnect.Normalize("Server=a;Integrated Security=true"));
        var custom = new SqlConnectionStringBuilder(SqlConnect.Normalize("Server=a;Integrated Security=true;Application Name=etl"));

        Assert.Equal("dbm", normalized.ApplicationName);
        Assert.Equal("etl", custom.ApplicationName);
    }

    [Fact]
    public void Entra_id_provider_comes_from_the_azure_extension_package()
    {
        // Guards the Microsoft.Data.SqlClient.Extensions.Azure reference: without it this returns null.
        var provider = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault);

        Assert.NotNull(provider);
        Assert.Equal("Microsoft.Data.SqlClient.ActiveDirectoryAuthenticationProvider", provider!.GetType().FullName);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/State/ConnectionRepoTests.cs`:

```csharp
using Dbm.Core.Crypto;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class ConnectionRepoTests
{
    private static ServerMeta Meta(string db) =>
        new("SRV1", db, "Microsoft SQL Server 2022", "16.0.1000.6", 16, "Developer Edition (64-bit)",
            "SQL_Latin1_General_CP1_CI_AS", "Latin1_General_CI_AS", 160, "sql");

    [Fact]
    public void Stores_only_ciphertext_and_round_trips()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var repo = new ConnectionRepo(db, new AesGcmFileKeyProtector(Path.Combine(tw.Root, "key")));
        const string cs = "Server=SRV1;Database=Legacy;User ID=u;Password=S3cr3t!pw";

        Assert.False(repo.Has(Side.Src));
        repo.Save(Side.Src, cs, Meta("Legacy"));

        Assert.True(repo.Has(Side.Src));
        Assert.False(repo.Has(Side.Tgt));
        var stored = db.Scalar<string>("SELECT encrypted FROM connection WHERE side = 'src'")!;
        Assert.StartsWith("aesgcm:", stored);
        Assert.DoesNotContain("S3cr3t", stored);
        Assert.Equal(cs, repo.GetConnectionString(Side.Src));
        Assert.Equal(Meta("Legacy"), repo.GetMeta(Side.Src));
        Assert.Null(repo.GetConnectionString(Side.Tgt));
        Assert.Null(repo.GetMeta(Side.Tgt));
    }

    [Fact]
    public void Saving_again_replaces_the_side()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        var repo = new ConnectionRepo(db, new AesGcmFileKeyProtector(Path.Combine(tw.Root, "key")));

        repo.Save(Side.Tgt, "Server=a;Database=One;Integrated Security=true", Meta("One"));
        repo.Save(Side.Tgt, "Server=a;Database=Two;Integrated Security=true", Meta("Two"));

        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM connection"));
        Assert.Equal("Two", repo.GetMeta(Side.Tgt)!.Database);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/Sql/SqlConnectTests.cs`:

```csharp
using Dbm.Core.Sql;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Sql;

[Trait("Category", "Integration")]
public class SqlConnectTests
{
    [Fact]
    public async Task Probe_reads_server_and_database_metadata()
    {
        await using var db = await TempDatabase.CreateAsync();

        var meta = await SqlConnect.ProbeAsync(db.ConnectionString, CancellationToken.None);

        Assert.Equal(db.Name, meta.Database);
        Assert.False(string.IsNullOrEmpty(meta.Server));
        Assert.StartsWith("Microsoft SQL Server", meta.Version);
        Assert.DoesNotContain('\n', meta.Version);
        Assert.True(meta.MajorVersion >= 13, $"major {meta.MajorVersion}");
        Assert.StartsWith(meta.MajorVersion + ".", meta.ProductVersion);
        Assert.False(string.IsNullOrEmpty(meta.Edition));
        Assert.False(string.IsNullOrEmpty(meta.ServerCollation));
        Assert.False(string.IsNullOrEmpty(meta.DatabaseCollation));
        Assert.True(meta.CompatLevel >= 100);
        Assert.Equal("integrated", meta.AuthSummary);
    }

    [Fact]
    public async Task Open_tags_the_session_with_application_name_dbm()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var connection = await SqlConnect.OpenAsync(db.ConnectionString, CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT APP_NAME()";

        Assert.Equal("dbm", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Probe_of_a_missing_database_throws_SqlException()
    {
        var cs = SqlTestServer.ForDatabase("dbm_does_not_exist_" + Guid.NewGuid().ToString("N")[..6]);

        await Assert.ThrowsAsync<SqlException>(() => SqlConnect.ProbeAsync(cs, CancellationToken.None));
    }

    [Fact]
    public async Task TempDatabase_runs_go_separated_batches()
    {
        await using var db = await TempDatabase.CreateAsync();

        await db.ExecAsync("CREATE TABLE dbo.T (X int)\nGO\nCREATE VIEW dbo.V AS SELECT X FROM dbo.T\n  go  \nINSERT dbo.T VALUES (1)");

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.V"));
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'AesGcmFileKeyProtector' could not be found` (and `Redactor`, `SqlConnect`, `ConnectionRepo`, `ServerMeta`).

- [ ] **Step 3: Implement the protectors**

`plugins/db-migrate/engine/Dbm/Core/Crypto/ISecretProtector.cs`:

```csharp
namespace Dbm.Core.Crypto;

/// <summary>Encrypts secrets (connection strings) for the current OS user. Output is tagged "dpapi:" or "aesgcm:".</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}
```

`plugins/db-migrate/engine/Dbm/Core/Crypto/DpapiProtector.cs` (P/Invoke to `crypt32`, CurrentUser scope, fixed entropy `db-migrate/v1`, `LocalFree` on the output blob; no extra NuGet package):

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Dbm.Core.Crypto;

/// <summary>Windows DPAPI (CurrentUser scope) via crypt32; no extra NuGet package needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    public const string Prefix = "dpapi:";

    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "db-migrate/v1"u8.ToArray();

    public string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plaintext), protect: true));

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("not a DPAPI-protected value (expected prefix 'dpapi:')");
        var data = Convert.FromBase64String(protectedText[Prefix.Length..]);
        return Encoding.UTF8.GetString(Transform(data, protect: false));
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok) throw new CryptographicException($"DPAPI {(protect ? "protect" : "unprotect")} failed", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
```

`plugins/db-migrate/engine/Dbm/Core/Crypto/AesGcmFileKeyProtector.cs` (32-byte random key created with `UnixCreateMode` 0600 and re-asserted with `File.SetUnixFileMode` on macOS/Linux; output = `aesgcm:` + base64(nonce12 | tag16 | cipher)):

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Dbm.Core.Crypto;

/// <summary>AES-256-GCM with a random per-user key file (created 0600). Used on macOS/Linux.</summary>
public sealed class AesGcmFileKeyProtector(string keyPath) : ISecretProtector
{
    public const string Prefix = "aesgcm:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public string KeyPath { get; } = Path.GetFullPath(keyPath);

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(LoadOrCreateKey(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("not an AES-GCM-protected value (expected prefix 'aesgcm:')");
        var blob = Convert.FromBase64String(protectedText[Prefix.Length..]);
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("protected value is truncated");
        var plain = new byte[blob.Length - NonceSize - TagSize];
        using var aes = new AesGcm(LoadOrCreateKey(), TagSize);
        aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(KeyPath)) return ReadKey();

        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(KeyPath, options)) stream.Write(key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
        catch (IOException) when (File.Exists(KeyPath))
        {
            return ReadKey();   // another process created it first
        }
    }

    private byte[] ReadKey()
    {
        var key = File.ReadAllBytes(KeyPath);
        return key.Length == KeySize ? key : throw new CryptographicException($"key file {KeyPath} must be {KeySize} bytes");
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Crypto/SecretProtector.cs`:

```csharp
namespace Dbm.Core.Crypto;

public static class SecretProtector
{
    /// <summary>Windows → DPAPI (CurrentUser); macOS/Linux → AES-GCM with UserHome/key.</summary>
    public static ISecretProtector ForCurrentUser() =>
        OperatingSystem.IsWindows()
            ? new DpapiProtector()
            : new AesGcmFileKeyProtector(Path.Combine(UserHome.Dir, "key"));
}
```

- [ ] **Step 4: Implement the SQL helpers and the connection repository**

`plugins/db-migrate/engine/Dbm/Core/Sql/ServerMeta.cs`:

```csharp
namespace Dbm.Core.Sql;

/// <summary>What SETUP captures about each side. Never contains credentials.</summary>
public sealed record ServerMeta(string Server, string Database, string Version, string ProductVersion, int MajorVersion,
    string Edition, string ServerCollation, string DatabaseCollation, int CompatLevel, string AuthSummary);
```

`plugins/db-migrate/engine/Dbm/Core/Sql/Redactor.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Sql;

/// <summary>Everything that may show a connection string to a human, a log or Claude goes through here.</summary>
public static partial class Redactor
{
    /// <summary>"server=X; database=Y; auth=&lt;mode&gt;[; user=U]" — never secrets.</summary>
    public static string Describe(string connectionString)
    {
        SqlConnectionStringBuilder b;
        try
        {
            b = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            return "invalid connection string";
        }
        var database = string.IsNullOrEmpty(b.InitialCatalog) ? "(default)" : b.InitialCatalog;
        var text = $"server={b.DataSource}; database={database}; auth={AuthMode(b)}";
        return string.IsNullOrEmpty(b.UserID) ? text : $"{text}; user={b.UserID}";
    }

    /// <summary>"integrated", "sql", or the snake_case Authentication keyword (e.g. "active_directory_default").</summary>
    public static string AuthMode(string connectionString)
    {
        try
        {
            return AuthMode(new SqlConnectionStringBuilder(connectionString));
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Replaces each secret value (length ≥ 4) with "***".</summary>
    public static string Scrub(string text, IEnumerable<string?> secrets)
    {
        foreach (var s in secrets)
        {
            if (!string.IsNullOrEmpty(s) && s.Length >= 4) text = text.Replace(s, "***", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Password / client-secret values; falls back to a regex when the string does not parse.</summary>
    public static IEnumerable<string> SecretsOf(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return string.IsNullOrEmpty(b.Password) ? [] : [b.Password];
        }
        catch (Exception)
        {
            return PasswordPattern().Matches(connectionString)
                .Select(m => m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["raw"].Value)
                .Where(v => v.Length > 0)
                .ToList();
        }
    }

    private static string AuthMode(SqlConnectionStringBuilder b)
    {
        if (b.Authentication != SqlAuthenticationMethod.NotSpecified) return EnumText.ToText(b.Authentication);
        if (b.IntegratedSecurity) return "integrated";
        return string.IsNullOrEmpty(b.UserID) ? "default" : "sql";
    }

    [GeneratedRegex("""(?i)(?:password|pwd)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<raw>[^;]*))""")]
    private static partial Regex PasswordPattern();
}
```

`plugins/db-migrate/engine/Dbm/Core/Sql/SqlConnect.cs` (`Normalize` compares with the driver's own default application name, so it works across SqlClient versions):

```csharp
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Sql;

public static class SqlConnect
{
    private static readonly string DriverDefaultApplicationName = new SqlConnectionStringBuilder().ApplicationName;

    /// <summary>Sets Application Name=dbm when the user left the driver default; everything else is kept verbatim.</summary>
    public static string Normalize(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(b.ApplicationName) || b.ApplicationName == DriverDefaultApplicationName) b.ApplicationName = "dbm";
        return b.ConnectionString;
    }

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(Normalize(connectionString));
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task<ServerMeta> ProbeAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionString, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(@@SERVERNAME, CAST(SERVERPROPERTY('ServerName') AS nvarchar(256))),
                   DB_NAME(),
                   @@VERSION,
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Collation') AS nvarchar(128)),
                   CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
                   (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME())
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("server metadata query returned no row");

        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        var productVersion = Text(3);
        var versionLine = Text(2).Split('\n')[0].Trim();
        var major = int.TryParse(productVersion.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var compat = r.IsDBNull(7) ? 0 : Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture);

        return new ServerMeta(Text(0), Text(1), versionLine, productVersion, major, Text(4), Text(5), Text(6), compat,
            Redactor.AuthMode(connectionString));
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/State/ConnectionRepo.cs`:

```csharp
using Dbm.Core.Crypto;
using Dbm.Core.Sql;

namespace Dbm.Core.State;

/// <summary>Connection strings are stored only in encrypted form.</summary>
public sealed class ConnectionRepo(StateDb db, ISecretProtector protector)
{
    public void Save(Side side, string connectionString, ServerMeta meta) =>
        db.Execute("""
            INSERT INTO connection (side, encrypted, server_meta_json, updated_at) VALUES ($Side, $Encrypted, $Meta, $Now)
            ON CONFLICT (side) DO UPDATE SET encrypted = excluded.encrypted, server_meta_json = excluded.server_meta_json,
                                             updated_at = excluded.updated_at
            """, new { Side = side, Encrypted = protector.Protect(connectionString), Meta = Json.Serialize(meta), Now = Clock.NowText() });

    public string? GetConnectionString(Side side)
    {
        var encrypted = db.Scalar<string>("SELECT encrypted FROM connection WHERE side = $Side", new { Side = side });
        return encrypted is null ? null : protector.Unprotect(encrypted);
    }

    public ServerMeta? GetMeta(Side side)
    {
        var json = db.Scalar<string>("SELECT server_meta_json FROM connection WHERE side = $Side", new { Side = side });
        return json is null ? null : Json.Deserialize<ServerMeta>(json);
    }

    public bool Has(Side side) =>
        db.Scalar<long>("SELECT COUNT(*) FROM connection WHERE side = $Side", new { Side = side }) > 0;
}
```

- [ ] **Step 5: Run the unit tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:    67` (ProtectorTests 4, RedactorTests 8, ConnectionRepoTests 2 added), no CA1416 warnings.

- [ ] **Step 6: Run the integration tests against LocalDB**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:     4` (SqlConnectTests). If LocalDB is not running, start it with `sqllocaldb start MSSQLLocalDB` (or set `DBM_TEST_SQL`, e.g. `Server=localhost;Integrated Security=true;TrustServerCertificate=true`). No `dbm_test_*` database may be left behind: `sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "SELECT name FROM sys.databases WHERE name LIKE 'dbm_test_%'"` prints no rows.

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(core): DPAPI/AES-GCM secret protection, redaction, SQL probe, connection repo" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.4: JSON patch

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Patching/Patch.cs`, `JsonPatch.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Patching/JsonPatchTests.cs`, `Unit/Patching/PatchParseTests.cs`

**Interfaces:**
- Consumes: `Json` (T0.1).
- Produces (C4): `PatchOp`, `FeedbackResponse`, `Patch`, `PatchException`, `JsonPatch.Apply`; plus `Patch.Parse(string json)` (missing `ops`/`responses` become empty lists, null notes become `""`, structural problems throw `PatchException`) and `JsonPatch.Resolve(JsonNode document, string pointer)` → the node or `null` (used by `dbm artifact --path`).

Rules (normative): the input document is never mutated (deep clone first); pointers follow RFC 6901 (`~1` = `/`, `~0` = `~`, `""` = root); `add` creates missing intermediate objects, overwrites an existing member, inserts into arrays at an index `0..count` or appends with `-`; `replace` and `remove` require the target to exist; array indexes are digits without leading zeros; `remove ""` is an error; every error message starts with `op <index> (<op> <path>): `.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Patching/JsonPatchTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Patching;

namespace Dbm.Tests.Unit.Patching;

public class JsonPatchTests
{
    private static JsonNode Doc() => JsonNode.Parse("""
        {"tables":{"dbo.Customer":{"columns":{"Email":{"expr":"s.EMAIL"}}},"a/b":{"x~y":1}},"list":[1,2,3],"n":null}
        """)!;

    private static JsonNode Apply(JsonNode doc, params PatchOp[] ops) => JsonPatch.Apply(doc, ops);

    [Fact]
    public void Replace_changes_a_member_without_touching_the_input()
    {
        var doc = Doc();

        var result = Apply(doc, new PatchOp("replace", "/tables/dbo.Customer/columns/Email/expr", JsonValue.Create("LOWER(s.EMAIL)")));

        Assert.Equal("LOWER(s.EMAIL)", result["tables"]!["dbo.Customer"]!["columns"]!["Email"]!["expr"]!.GetValue<string>());
        Assert.Equal("s.EMAIL", doc["tables"]!["dbo.Customer"]!["columns"]!["Email"]!["expr"]!.GetValue<string>());
    }

    [Fact]
    public void Add_creates_missing_intermediate_objects_and_overwrites_existing_members()
    {
        var result = Apply(Doc(),
            new PatchOp("add", "/tables/dbo.Orders/columns/Id/expr", JsonValue.Create("s.ID")),
            new PatchOp("add", "/n", JsonValue.Create(5)));

        Assert.Equal("s.ID", result["tables"]!["dbo.Orders"]!["columns"]!["Id"]!["expr"]!.GetValue<string>());
        Assert.Equal(5, result["n"]!.GetValue<int>());
    }

    [Fact]
    public void Add_to_arrays_by_index_and_dash()
    {
        var result = Apply(Doc(),
            new PatchOp("add", "/list/0", JsonValue.Create(0)),
            new PatchOp("add", "/list/-", JsonValue.Create(9)),
            new PatchOp("add", "/list/5", JsonValue.Create(10)));

        Assert.Equal("[0,1,2,3,9,10]", result["list"]!.ToJsonString());
    }

    [Fact]
    public void Remove_members_and_array_elements()
    {
        var result = Apply(Doc(),
            new PatchOp("remove", "/list/1"),
            new PatchOp("remove", "/tables/dbo.Customer"));

        Assert.Equal("[1,3]", result["list"]!.ToJsonString());
        Assert.False(result["tables"]!.AsObject().ContainsKey("dbo.Customer"));
    }

    [Fact]
    public void Pointer_escapes_are_decoded()
    {
        var result = Apply(Doc(), new PatchOp("replace", "/tables/a~1b/x~0y", JsonValue.Create(2)));

        Assert.Equal(2, result["tables"]!["a/b"]!["x~y"]!.GetValue<int>());
    }

    [Fact]
    public void Null_value_sets_json_null_and_root_can_be_replaced()
    {
        var withNull = Apply(Doc(), new PatchOp("replace", "/list", null));
        var root = Apply(Doc(), new PatchOp("replace", "", new JsonObject { ["fresh"] = true }));

        Assert.Null(withNull["list"]);
        Assert.True(withNull.AsObject().ContainsKey("list"));
        Assert.Equal("{\"fresh\":true}", root.ToJsonString());
    }

    [Theory]
    [InlineData("replace", "/tables/missing", "member 'missing' does not exist")]
    [InlineData("remove", "/list/7", "index 7 is out of range")]
    [InlineData("add", "/list/9", "index 9 is out of range")]
    [InlineData("replace", "/list/01", "'01' is not a valid array index")]
    [InlineData("replace", "/nothing/here", "path segment 'nothing' does not exist")]
    [InlineData("move", "/list", "unknown op 'move'")]
    [InlineData("add", "list", "must start with '/'")]
    [InlineData("remove", "", "cannot remove the document root")]
    public void Errors_name_the_op_index_path_and_reason(string op, string path, string reason)
    {
        var ex = Assert.Throws<PatchException>(() => Apply(Doc(),
            new PatchOp("add", "/ok", JsonValue.Create(1)),
            new PatchOp(op, path, JsonValue.Create(1))));

        Assert.StartsWith($"op 1 ({op} {path}): ", ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Resolve_returns_the_node_or_null()
    {
        var doc = Doc();

        Assert.Equal("s.EMAIL", JsonPatch.Resolve(doc, "/tables/dbo.Customer/columns/Email/expr")!.GetValue<string>());
        Assert.Equal(3, JsonPatch.Resolve(doc, "/list/2")!.GetValue<int>());
        Assert.Same(doc, JsonPatch.Resolve(doc, ""));
        Assert.Null(JsonPatch.Resolve(doc, "/list/9"));
        Assert.Null(JsonPatch.Resolve(doc, "/tables/none/x"));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Patching/PatchParseTests.cs`:

```csharp
using Dbm.Core.Patching;

namespace Dbm.Tests.Unit.Patching;

public class PatchParseTests
{
    [Fact]
    public void Parses_the_documented_patch_file_format()
    {
        var patch = Patch.Parse("""
            {"phase":"mapping","baseVersion":3,
             "ops":[{"op":"replace","path":"/tables/dbo.Customers/columns/Email/expr","value":"LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))"}],
             "responses":[{"feedbackId":12,"status":"addressed","note":"Email is now trimmed and lower-cased."}],
             "summary":"Normalised email; mapped FAX to drop list."}
            """);

        Assert.Equal("mapping", patch.Phase);
        Assert.Equal(3, patch.BaseVersion);
        Assert.Equal("LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))", patch.Ops.Single().Value!.GetValue<string>());
        Assert.Equal(12, patch.Responses.Single().FeedbackId);
        Assert.Equal("Normalised email; mapped FAX to drop list.", patch.Summary);
    }

    [Fact]
    public void Missing_lists_become_empty()
    {
        var patch = Patch.Parse("""{"phase":"analysis","baseVersion":0}""");

        Assert.Empty(patch.Ops);
        Assert.Empty(patch.Responses);
        Assert.Null(patch.Summary);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("null", "patch is empty")]
    [InlineData("""{"baseVersion":1}""", "patch.phase is required")]
    [InlineData("""{"phase":"sql","baseVersion":1,"ops":[{"op":"add"}]}""", "op 0: 'op' and 'path' are required")]
    public void Structural_problems_throw_PatchException(string json, string message)
    {
        var ex = Assert.Throws<PatchException>(() => Patch.Parse(json));

        Assert.Contains(message, ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'PatchOp' could not be found`.

- [ ] **Step 3: Implement**

`plugins/db-migrate/engine/Dbm/Core/Patching/Patch.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dbm.Core.Patching;

/// <summary>op: "add" | "replace" | "remove"; Path is a JSON pointer into the artifact payload.</summary>
public sealed record PatchOp(string Op, string Path, JsonNode? Value = null);

/// <summary>status: "addressed" | "declined".</summary>
public sealed record FeedbackResponse(long FeedbackId, string Status, string Note);

public sealed record Patch(string Phase, int BaseVersion, List<PatchOp> Ops, List<FeedbackResponse> Responses, string? Summary = null)
{
    /// <summary>Parses a patch file; missing lists become empty; structural problems throw PatchException.</summary>
    public static Patch Parse(string json)
    {
        Patch? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Patch>(json, Json.Options);
        }
        catch (JsonException ex)
        {
            throw new PatchException($"patch is not valid JSON: {ex.Message}");
        }
        if (raw is null) throw new PatchException("patch is empty");
        if (string.IsNullOrWhiteSpace(raw.Phase)) throw new PatchException("patch.phase is required");

        var ops = raw.Ops ?? [];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is null || string.IsNullOrWhiteSpace(ops[i].Op) || ops[i].Path is null)
                throw new PatchException($"op {i}: 'op' and 'path' are required");
        }
        var responses = raw.Responses ?? [];
        for (var i = 0; i < responses.Count; i++)
        {
            if (responses[i] is null) throw new PatchException($"response {i} is null");
        }
        return raw with { Ops = ops, Responses = responses.Select(r => r with { Note = r.Note ?? "" }).ToList() };
    }
}

public sealed class PatchException(string message) : Exception(message);
```

`plugins/db-migrate/engine/Dbm/Core/Patching/JsonPatch.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Dbm.Core.Patching;

/// <summary>RFC 6902 subset: add, replace, remove. Pure: the input document is never modified.</summary>
public static class JsonPatch
{
    public static JsonNode Apply(JsonNode document, IEnumerable<PatchOp> ops)
    {
        JsonNode root = document.DeepClone();
        var index = 0;
        foreach (var op in ops)
        {
            try
            {
                root = ApplyOne(root, op);
            }
            catch (PatchException ex)
            {
                throw new PatchException($"op {index} ({op.Op} {op.Path}): {ex.Message}");
            }
            index++;
        }
        return root;
    }

    /// <summary>The node at <paramref name="pointer"/> ("" = root), or null when it does not exist.</summary>
    public static JsonNode? Resolve(JsonNode document, string pointer)
    {
        JsonNode? current = document;
        foreach (var segment in Parse(pointer))
        {
            current = current switch
            {
                JsonObject o => o.TryGetPropertyValue(segment, out var child) ? child : null,
                JsonArray a => TryIndex(segment, out var i) && i < a.Count ? a[i] : null,
                _ => null,
            };
            if (current is null) return null;
        }
        return current;
    }

    private static JsonNode ApplyOne(JsonNode root, PatchOp op)
    {
        var segments = Parse(op.Path);
        var kind = op.Op.ToLowerInvariant();
        if (kind is not ("add" or "replace" or "remove")) throw new PatchException($"unknown op '{op.Op}' (use add, replace or remove)");

        if (segments.Count == 0)
        {
            if (kind == "remove") throw new PatchException("cannot remove the document root");
            return op.Value?.DeepClone() ?? throw new PatchException("the document root cannot be null");
        }

        var parent = Walk(root, segments, createMissing: kind == "add");
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                switch (kind)
                {
                    case "add":
                        obj[last] = op.Value?.DeepClone();
                        break;
                    case "replace":
                        if (!obj.ContainsKey(last)) throw new PatchException($"member '{last}' does not exist");
                        obj[last] = op.Value?.DeepClone();
                        break;
                    default:
                        if (!obj.Remove(last)) throw new PatchException($"member '{last}' does not exist");
                        break;
                }
                break;
            case JsonArray arr:
                if (kind == "add" && last == "-")
                {
                    arr.Add(op.Value?.DeepClone());
                    break;
                }
                if (!TryIndex(last, out var i)) throw new PatchException($"'{last}' is not a valid array index");
                switch (kind)
                {
                    case "add":
                        if (i > arr.Count) throw new PatchException($"index {i} is out of range (count {arr.Count})");
                        arr.Insert(i, op.Value?.DeepClone());
                        break;
                    case "replace":
                        if (i >= arr.Count) throw new PatchException($"index {i} is out of range (count {arr.Count})");
                        arr[i] = op.Value?.DeepClone();
                        break;
                    default:
                        if (i >= arr.Count) throw new PatchException($"index {i} is out of range (count {arr.Count})");
                        arr.RemoveAt(i);
                        break;
                }
                break;
            default:
                throw new PatchException("parent is not an object or array");
        }
        return root;
    }

    /// <summary>Returns the container that holds the last segment; "add" creates missing intermediate objects.</summary>
    private static JsonNode Walk(JsonNode root, IReadOnlyList<string> segments, bool createMissing)
    {
        var current = root;
        for (var s = 0; s < segments.Count - 1; s++)
        {
            var segment = segments[s];
            JsonNode? next;
            switch (current)
            {
                case JsonObject obj:
                    if (!obj.TryGetPropertyValue(segment, out next) || next is null)
                    {
                        if (!createMissing) throw new PatchException($"path segment '{segment}' does not exist");
                        next = new JsonObject();
                        obj[segment] = next;
                    }
                    break;
                case JsonArray arr:
                    if (!TryIndex(segment, out var i) || i >= arr.Count)
                        throw new PatchException($"array index '{segment}' is out of range");
                    next = arr[i] ?? throw new PatchException($"array element {i} is null");
                    break;
                default:
                    throw new PatchException($"cannot descend into a value at '{segment}'");
            }
            current = next;
        }
        return current;
    }

    private static List<string> Parse(string pointer)
    {
        if (pointer.Length == 0) return [];
        if (pointer[0] != '/') throw new PatchException($"path '{pointer}' must start with '/'");
        return pointer[1..].Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToList();
    }

    /// <summary>Digits only, no leading zeros (RFC 6901); range checks are done by the caller.</summary>
    private static bool TryIndex(string segment, out int index)
    {
        index = -1;
        if (segment.Length == 0 || segment.Length > 9 || !segment.All(char.IsAsciiDigit)) return false;
        if (segment.Length > 1 && segment[0] == '0') return false;
        index = int.Parse(segment);
        return true;
    }
}
```

- [ ] **Step 4: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:    88` (JsonPatchTests 15, PatchParseTests 6 added).

- [ ] **Step 5: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(patching): JSON patch subset with pointer resolution and patch file parsing" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---
### Task 1.5: Workflow engine (+ services composition)

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Workflow/Phases.cs`, `NextAction.cs`, `IPhaseModule.cs`, `WorkflowEngine.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/DbmServices.cs`, `ModuleRegistry.cs`, `JobRegistry.cs`, `DbEventSink.cs` (moved here from T1.6: the engine needs them)
- Create: `plugins/db-migrate/engine/Dbm/Core/Jobs/IJobHandler.cs` (`JobResult`, `JobContext`, `IJobHandler` — moved here from T1.6 because `DbmServices` exposes `JobHandlers`; `JobRunner` stays in T1.6)
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/FakeModule.cs` (`FakeModule`, `FakeModules`, `FakeJobHandler`, `FakeServices`)
- Modify (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/TestWorkspace.cs` (adds `OpenServices`)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Core/DbmServicesTests.cs`, `Unit/Workflow/WorkflowTransitionTests.cs`, `Unit/Workflow/WorkflowNextTests.cs`, `Unit/Workflow/WorkflowPatchTests.cs`

**Interfaces:**
- Consumes: all repositories (T1.2/T1.3), `SecretProtector` (T1.3), `JsonPatch`/`Patch` (T1.4), `Workspace`, `IEventSink`.
- Produces (C5/C6): `NextAction`, `PacketMode`, `PayloadCheck`, `ModuleContext`, `IPhaseModule`, `ApplyResult` (+ `static Fail(params string[])`), `WorkflowException`, `WorkflowEngine` with every C5 member plus `Peek()` (same as `Next()` without writing packet files), `CarryOver(PhaseName)` and `const string PatchRules`; `Phases` helper (`Reviewable`, `DefaultJobKinds`, `Text(this PhaseName)`, `TryParse`, `Parse`, `After`); `DbmServices` (C6 minus `Catalog` — T2.5 adds it — and `Transfers` — T5.1 adds it); `DbEventSink`; `ModuleRegistry.Create` / `JobRegistry.Create` (iterator bodies ending with the marker line `// milestone registrations below`; later milestones append `yield return new X(s);` lines after it); `JobResult`, `JobContext`, `IJobHandler`. Test support (public API for all milestones): `TestWorkspace.OpenServices(Func<DbmServices, IEnumerable<IPhaseModule>>? modules = null, Func<DbmServices, IEnumerable<IJobHandler>>? jobs = null)` — creates `.dbmigrate`, runs `ProjectRepo.Init("test")` on the first open, and disposes every opened instance with the workspace; `FakeServices.*` (below) is the richer helper used by M1's own tests (its project is named `test-project`).

Normative behaviour implemented here (beyond the C5 table):
- Every mutation runs in `Db.InTransaction` and publishes `state_changed {phase,status}` for each status change, `artifact_created {phase,version,author}`, `feedback_changed {phase}`, `paused`/`resumed`, `job_failed {id,kind,phase,error}`.
- A job result is ignored (a `log` warning is published) when the phase is no longer `running` or a newer job for the phase exists (connections re-saved mid-discovery, phase reopened mid-job). Starting a phase's job does not enqueue a duplicate while one is still `queued`.
- `discover` usually returns no draft: Discovery is approved with `approved_version = 0` (or the stored draft's version if it returned one) and `approved_fingerprint` = catalog fingerprints.
- Job kind for a phase: the module's `JobKind`, else `Phases.DefaultJobKinds` (`discover|analyze|automap|sqlgen`), so M1 with empty registries still enqueues `analyze` (which then fails with "no handler for analyze" — expected until M2).
- Validate-then-store (C5 `ApplyPatch` row): `ApplyPatch` and `HumanEdit` pass the patched `JsonNode` to `module.Validate` and then store that same instance, so annotations a module writes during validation (e.g. SqlModule's `Custom`/`Errors`/`Warnings`) are persisted; `WorkflowPatchTests.The_payload_is_stored_after_validation_so_module_annotations_persist` pins this.
- `RetryJob(phase)` works for a `running` phase (its job failed) and for a `drafting` phase (regenerates the script draft: status → `running`, job enqueued); any other status, or a job of the phase that is still queued/running, throws `WorkflowException`.
- `ApplyPatch`: responses must answer exactly the open items (each needs status `addressed|declined` and a non-empty note); in `drafting` there are no open items, so any response is an error. `HumanEdit` ignores responses. `dryRun` validates everything and writes nothing (no events).
- `Next()` for a phase that is `pending`/`stale` while all earlier phases are approved (a transient state) returns `await/job`. `Url` is set for the `setup`, `review`, `execute`, `transfer*` awaits.
- Packets are written compactly (`Json.Options`); writing a packet deletes an existing file at its `patchPath` so an old patch can never be applied to a new packet.
- Carry-over: because approvals are cleared when a phase goes stale, re-running jobs get their carry-over from `WorkflowEngine.CarryOver(phase)` (= the phase's latest artifact at the time the job starts).

- [ ] **Step 1: Write the test support and the failing tests**

Replace the whole of `plugins/db-migrate/engine/Dbm.Tests/Support/TestWorkspace.cs` with (adds `OpenServices`; other milestones use it):

```csharp
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Workflow;
using Microsoft.Data.Sqlite;

namespace Dbm.Tests.Support;

/// <summary>
/// A throw-away workspace folder under %TEMP%, deleted on Dispose (together with every services instance it opened).
/// Public API relied on by all milestones: Ws, Root, OpenServices(modules, jobs).
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly List<DbmServices> _opened = new();

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbm-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Ws = new Workspace(Root);
    }

    public string Root { get; }
    public Workspace Ws { get; }

    /// <summary>
    /// Opens services on this workspace (real registries unless <paramref name="modules"/>/<paramref name="jobs"/> are given);
    /// creates .dbmigrate and runs ProjectRepo.Init("test") the first time. Disposed with the workspace.
    /// </summary>
    public DbmServices OpenServices(Func<DbmServices, IEnumerable<IPhaseModule>>? modules = null,
        Func<DbmServices, IEnumerable<IJobHandler>>? jobs = null)
    {
        Ws.EnsureCreated();
        var services = DbmServices.Open(Ws, null, modules, jobs);
        if (!services.Project.Exists()) services.Project.Init("test");
        lock (_opened) _opened.Add(services);
        return services;
    }

    public void Dispose()
    {
        lock (_opened)
        {
            foreach (var s in _opened) s.Dispose();
            _opened.Clear();
        }
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Support/FakeModule.cs` — `FakeServices.Open` wires fakes into real services (real SQLite, real protector) and `DriveTo…` helpers reach any review state without SQL Server:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Tests.Support;

/// <summary>Configurable stand-in for AnalysisModule / MappingModule / SqlModule.</summary>
public sealed class FakeModule(PhaseName phase, string agent, string jobKind) : IPhaseModule
{
    public PhaseName Phase => phase;
    public string Agent => agent;
    public string JobKind => jobKind;

    public bool NeedsAgentResult { get; set; } = true;
    public Func<JsonNode, PayloadCheck> ValidateFn { get; set; } = _ => PayloadCheck.Pass();
    public Func<JsonNode, IReadOnlyList<string>> BlockersFn { get; set; } = _ => [];
    public List<PacketMode> PacketsBuilt { get; } = new();

    public bool NeedsAgent(JsonNode draft) => NeedsAgentResult;

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        lock (PacketsBuilt) PacketsBuilt.Add(mode);
        return new JsonObject { ["fake"] = true, ["baseVersion"] = ctx.Current.Version, ["openFeedback"] = ctx.OpenFeedback.Count };
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload) => ValidateFn(payload);

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload) => BlockersFn(payload);

    public string Summarize(JsonNode payload) => payload["summary"]?.GetValue<string>() ?? $"{phase} artifact";
}

public sealed class FakeModules
{
    public FakeModule Analysis { get; } = new(PhaseName.Analysis, "schema-analyst", "analyze");
    public FakeModule Mapping { get; } = new(PhaseName.Mapping, "mapping-architect", "automap");
    public FakeModule Sql { get; } = new(PhaseName.Sql, "sql-engineer", "sqlgen");
    public IEnumerable<IPhaseModule> All => [Analysis, Mapping, Sql];

    public FakeModule this[PhaseName phase] => phase switch
    {
        PhaseName.Analysis => Analysis,
        PhaseName.Mapping => Mapping,
        PhaseName.Sql => Sql,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}

/// <summary>Job handler returning a canned result ("discover" → no draft, others → FakeServices.Draft()).</summary>
public sealed class FakeJobHandler(string kind, Func<JobContext, JobResult>? run = null) : IJobHandler
{
    private int _runs;

    public string Kind => kind;
    public int Runs => Volatile.Read(ref _runs);

    public Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        return Task.FromResult((run ?? Default)(ctx));
    }

    private static JobResult Default(JobContext ctx) =>
        ctx.Job.Kind == "discover" ? new JobResult(null, "catalogs extracted") : new JobResult(FakeServices.Draft($"{ctx.Job.Kind} draft"), null);

    public static List<IJobHandler> Standard() =>
        [new FakeJobHandler("discover"), new FakeJobHandler("analyze"), new FakeJobHandler("automap"), new FakeJobHandler("sqlgen")];
}

/// <summary>Services wired with fakes, plus helpers that drive the workflow without SQL Server.</summary>
public static class FakeServices
{
    public const string SrcConnection = "Server=src-host;Database=Legacy;Integrated Security=true";
    public const string TgtConnection = "Server=tgt-host;Database=ShopV2;Integrated Security=true";

    /// <param name="initProject">false = leave project creation to the code under test (e.g. `dbm init`).</param>
    public static DbmServices Open(Workspace ws, FakeModules? modules = null, IEnumerable<IJobHandler>? handlers = null,
        IEventSink? sink = null, bool initProject = true)
    {
        ws.EnsureCreated();
        var m = modules ?? new FakeModules();
        var h = handlers?.ToList() ?? FakeJobHandler.Standard();
        var s = DbmServices.Open(ws, sink, _ => m.All, _ => h);
        if (initProject && !s.Project.Exists()) s.Project.Init("test-project");
        return s;
    }

    public static Func<Workspace, DbmServices> Factory(FakeModules? modules = null, IEnumerable<IJobHandler>? handlers = null,
        bool initProject = true) =>
        ws => Open(ws, modules, handlers, initProject: initProject);

    public static JsonObject Draft(string summary = "script draft") =>
        new() { ["summary"] = summary, ["items"] = new JsonObject { ["a"] = 1, ["b"] = 2 } };

    public static ServerMeta Meta(string server, string database) =>
        new(server, database, "Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)", "17.0.1000.7", 17, "Developer Edition (64-bit)",
            "SQL_Latin1_General_CP1_CI_AS", "SQL_Latin1_General_CP1_CI_AS", 170, "integrated");

    /// <summary>Saves both (fake) connections and fires OnConnectionsSaved → Discovery running, "discover" queued.</summary>
    public static void SaveConnections(DbmServices s)
    {
        s.Connections.Save(Side.Src, SrcConnection, Meta("src-host", "Legacy"));
        s.Connections.Save(Side.Tgt, TgtConnection, Meta("tgt-host", "ShopV2"));
        s.Workflow.OnConnectionsSaved();
    }

    public static void SetCatalogFingerprints(DbmServices s, string src, string tgt) =>
        s.Db.Execute("""
            INSERT OR REPLACE INTO catalog (side, snapshot_json, fingerprint, extracted_at)
            VALUES ('src', '{}', $Src, $Now), ('tgt', '{}', $Tgt, $Now)
            """, new { Src = src, Tgt = tgt, Now = Clock.NowText() });

    /// <summary>Completes the oldest queued job the way JobRunner would (without running a handler).</summary>
    public static JobRow CompleteNextJob(DbmServices s, JsonNode? payload = null, string? summary = null)
    {
        var job = s.Jobs.NextQueued() ?? throw new InvalidOperationException("no queued job");
        s.Jobs.MarkRunning(job.Id);
        s.Db.InTransaction(() =>
        {
            s.Jobs.MarkDone(job.Id);
            s.Workflow.OnJobDone(job, payload, summary);
        });
        return job;
    }

    /// <summary>Agent patch replacing /summary on the current version (with responses for every open item).</summary>
    public static ApplyResult ApplySummary(DbmServices s, PhaseName phase, string summary)
    {
        var row = s.Phases.Get(phase);
        var responses = s.Feedback.List(phase, FeedbackStatus.Open)
            .Select(f => new FeedbackResponse(f.Id, "addressed", $"done: {f.Text}"))
            .ToList();
        var patch = new Patch(phase.Text(), row.CurrentVersion ?? -1,
            [new PatchOp("replace", "/summary", JsonValue.Create(summary))], responses, summary);
        return s.Workflow.ApplyPatch(patch);
    }

    /// <summary>Setup → Discovery → Analysis drafting (v0 script draft).</summary>
    public static void DriveToAnalysisDraft(DbmServices s)
    {
        SaveConnections(s);
        CompleteNextJob(s);            // discover
        CompleteNextJob(s, Draft());   // analyze
    }

    /// <summary>Drives <paramref name="phase"/> (Analysis, Mapping or Sql) to awaiting_review with an agent v1.</summary>
    public static void DriveToReview(DbmServices s, PhaseName phase)
    {
        DriveToAnalysisDraft(s);
        ApplySummary(s, PhaseName.Analysis, "analysis v1");
        foreach (var next in new[] { PhaseName.Mapping, PhaseName.Sql })
        {
            if (phase < next) return;
            s.Workflow.Approve(next == PhaseName.Mapping ? PhaseName.Analysis : PhaseName.Mapping);
            CompleteNextJob(s, Draft());
            ApplySummary(s, next, $"{next.Text()} v1");
        }
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Core/DbmServicesTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Core;

public class DbmServicesTests
{
    [Fact]
    public void Default_open_uses_the_real_registries_and_a_persisting_sink()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();

        using var s = DbmServices.Open(tw.Ws);

        Assert.Empty(s.Modules);        // M1: registries are empty until later milestones append
        Assert.Empty(s.JobHandlers);
        Assert.IsType<DbEventSink>(s.Sink);
        Assert.NotNull(s.Workflow);
        Assert.True(File.Exists(tw.Ws.StateDbPath));
    }

    [Fact]
    public void Fakes_are_registered_by_phase_and_kind()
    {
        using var tw = new TestWorkspace();

        using var s = FakeServices.Open(tw.Ws);

        Assert.Equal(new[] { PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql }, s.Modules.Keys.Order());
        Assert.Equal(new[] { "analyze", "automap", "discover", "sqlgen" }, s.JobHandlers.Keys.Order());
    }

    [Fact]
    public void TestWorkspace_OpenServices_initialises_the_project_once()
    {
        using var tw = new TestWorkspace();

        var plain = tw.OpenServices();
        plain.Project.SetPaused(true);
        var faked = tw.OpenServices(_ => new FakeModules().All, _ => FakeJobHandler.Standard());

        Assert.Equal("test", faked.Project.Get().Name);
        Assert.True(faked.Project.Get().Paused);
        Assert.Empty(plain.Modules);
        Assert.Equal(3, faked.Modules.Count);
        Assert.Equal(4, faked.JobHandlers.Count);
    }

    [Fact]
    public void UiUrl_comes_from_server_json_or_is_empty()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        Assert.Equal("", s.UiUrl());
        File.WriteAllText(tw.Ws.ServerJsonPath, "{\"port\":5123,\"pid\":7,\"token\":\"abc\",\"startedAt\":\"2026-09-11T00:00:00+00:00\"}");
        Assert.Equal("http://127.0.0.1:5123/?t=abc", s.UiUrl());
        File.WriteAllText(tw.Ws.ServerJsonPath, "{ half written");
        Assert.Equal("", s.UiUrl());
    }

    [Fact]
    public void DbEventSink_persists_only_persistent_events()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        s.Sink.Publish("log", new { message = "kept" });
        s.Sink.Publish("transfer_progress", new { rows = 1 }, persist: false);

        Assert.Equal(new[] { "log" }, s.Events.Since(0).Select(e => e.Type));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Workflow/WorkflowTransitionTests.cs` (one test per row of the C5 transition table, plus superseded jobs, retry and re-discovery):

```csharp
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

public class WorkflowTransitionTests
{
    private static PhaseStatus StatusOf(DbmServices s, PhaseName p) => s.Phases.Get(p).Status;

    private static List<string> EventTypes(DbmServices s) => s.Events.Since(0, 10_000).Select(e => e.Type).ToList();

    [Fact]
    public void Saving_one_side_changes_nothing()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        s.Connections.Save(Side.Src, FakeServices.SrcConnection, FakeServices.Meta("src-host", "Legacy"));
        s.Workflow.OnConnectionsSaved();

        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Setup));
        Assert.Empty(s.Jobs.Active());
    }

    [Fact]
    public void Saving_both_sides_approves_setup_and_queues_discover()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        FakeServices.SaveConnections(s);

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Setup));
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        var job = Assert.Single(s.Jobs.Active());
        Assert.Equal(("discover", (PhaseName?)PhaseName.Discovery), (job.Kind, job.Phase));
        Assert.Contains("state_changed", EventTypes(s));
    }

    [Fact]
    public void Resaving_connections_marks_started_later_phases_stale()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        FakeServices.SaveConnections(s);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Analysis));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Mapping));
        Assert.Equal("discover", Assert.Single(s.Jobs.Active()).Kind);
    }

    [Fact]
    public void Discover_done_approves_discovery_with_the_fingerprint_and_queues_analyze()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        FakeServices.SetCatalogFingerprints(s, "aa", "bb");

        FakeServices.CompleteNextJob(s);

        var discovery = s.Phases.Get(PhaseName.Discovery);
        Assert.Equal(PhaseStatus.Approved, discovery.Status);
        Assert.Equal("aa:bb", discovery.ApprovedFingerprint);
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Analysis));
        Assert.Equal("analyze", Assert.Single(s.Jobs.Active()).Kind);
    }

    [Theory]
    [InlineData(true, PhaseStatus.Drafting)]
    [InlineData(false, PhaseStatus.AwaitingReview)]
    public void Module_job_done_stores_the_script_draft_as_v0(bool needsAgent, PhaseStatus expected)
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.NeedsAgentResult = needsAgent;
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.SaveConnections(s);
        FakeServices.CompleteNextJob(s);

        FakeServices.CompleteNextJob(s, FakeServices.Draft("rules found 3 risks"));

        var row = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal(expected, row.Status);
        Assert.Equal(0, row.CurrentVersion);
        var artifact = s.Artifacts.Get(PhaseName.Analysis, 0)!;
        Assert.Equal("script", artifact.Author);
        Assert.Equal("rules found 3 risks", artifact.Summary);
        Assert.Contains("artifact_created", EventTypes(s));
    }

    [Fact]
    public void Failed_job_keeps_the_phase_running_and_publishes_job_failed()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        var job = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(job.Id);
        s.Jobs.MarkFailed(job.Id, "timeout");

        s.Workflow.OnJobFailed(job, "timeout");

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        var last = s.Events.Since(0).Last();
        Assert.Equal("job_failed", last.Type);
        Assert.Contains("\"error\":\"timeout\"", last.PayloadJson);
        Assert.Contains("\"phase\":\"discovery\"", last.PayloadJson);
    }

    [Fact]
    public void Result_of_a_superseded_job_is_ignored()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        var first = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(first.Id);
        FakeServices.SaveConnections(s);   // queues a newer discover

        s.Workflow.OnJobDone(first, null, null);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        Assert.Contains(s.Events.Since(0), e => e.Type == "log" && e.PayloadJson.Contains("Ignored"));
    }

    [Fact]
    public void Request_changes_needs_feedback_then_moves_to_reworking()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        var ex = Assert.Throws<WorkflowException>(() => s.Workflow.RequestChanges(PhaseName.Analysis));
        Assert.Contains("at least one feedback item", ex.Message);
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Analysis));

        s.Feedback.Add(PhaseName.Analysis, 1, null, "More detail on risks");
        s.Workflow.RequestChanges(PhaseName.Analysis);

        Assert.Equal(PhaseStatus.Reworking, StatusOf(s, PhaseName.Analysis));
        Assert.Single(s.Feedback.List(PhaseName.Analysis, FeedbackStatus.Open));
        Assert.Contains("feedback_changed", EventTypes(s));
    }

    [Fact]
    public void Approving_analysis_records_the_fingerprint_and_starts_mapping()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        FakeServices.SetCatalogFingerprints(s, "s1", "t1");

        s.Workflow.Approve(PhaseName.Analysis);

        var analysis = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal(PhaseStatus.Approved, analysis.Status);
        Assert.Equal(1, analysis.ApprovedVersion);
        Assert.Equal("s1:t1", analysis.ApprovedFingerprint);
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Mapping));
        Assert.Equal("automap", Assert.Single(s.Jobs.Active()).Kind);
    }

    [Fact]
    public void Approving_mapping_starts_sql_and_approving_sql_readies_execution()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        FakeServices.DriveToReview(s, PhaseName.Sql);

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Mapping));
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Sql));
        Assert.Contains(s.Jobs.Recent(10), j => j.Kind == "sqlgen");
        s.Workflow.Approve(PhaseName.Sql);
        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Sql));
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Ready));
    }

    [Fact]
    public void Approval_blockers_throw_with_details_and_change_nothing()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Mapping.BlockersFn = _ => ["dbo.CUST.FAX_NO is unmapped"];
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToReview(s, PhaseName.Mapping);

        var ex = Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Mapping));

        Assert.Equal(new[] { "dbo.CUST.FAX_NO is unmapped" }, ex.Details);
        Assert.Equal(PhaseStatus.AwaitingReview, StatusOf(s, PhaseName.Mapping));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Sql));
    }

    [Fact]
    public void Only_a_reviewable_phase_awaiting_review_can_be_approved()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Analysis));
        Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Setup));
        Assert.Throws<WorkflowException>(() => s.Workflow.Approve(PhaseName.Ready));
    }

    [Fact]
    public void Reopen_restores_the_approved_version_and_marks_later_phases_stale()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);

        s.Workflow.Reopen(PhaseName.Analysis);

        var analysis = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal(PhaseStatus.AwaitingReview, analysis.Status);
        Assert.Equal(1, analysis.CurrentVersion);
        Assert.Null(analysis.ApprovedVersion);
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Mapping));
        Assert.Null(s.Phases.Get(PhaseName.Mapping).ApprovedVersion);
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Sql));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Ready));
        Assert.Throws<WorkflowException>(() => s.Workflow.Reopen(PhaseName.Mapping));   // stale, not approved
    }

    [Fact]
    public void Approving_a_reopened_phase_reruns_its_stale_successor_with_carry_over()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.Workflow.Reopen(PhaseName.Mapping);

        s.Workflow.Approve(PhaseName.Mapping);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Sql));
        Assert.Equal("sqlgen", Assert.Single(s.Jobs.Active()).Kind);
        Assert.Equal(1, s.Workflow.CarryOver(PhaseName.Sql)!.Version);
        FakeServices.CompleteNextJob(s, FakeServices.Draft("sql redraft"));
        var sql = s.Phases.Get(PhaseName.Sql);
        Assert.Equal(PhaseStatus.Drafting, sql.Status);
        Assert.Equal(2, sql.CurrentVersion);
    }

    [Fact]
    public void Changes_upstream_are_refused_once_the_transfer_started()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);
        s.Workflow.Approve(PhaseName.Sql);

        s.Workflow.OnTransferStarted();

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Ready));
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Transfer));
        Assert.Throws<WorkflowException>(() => s.Workflow.Reopen(PhaseName.Analysis));
        Assert.Throws<WorkflowException>(() => FakeServices.SaveConnections(s));
        Assert.Throws<WorkflowException>(() => s.Workflow.Rediscover());
    }

    [Fact]
    public void Completed_transfer_approves_transfer_and_complete()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        s.Workflow.OnTransferStarted();
        s.Artifacts.Add(PhaseName.Complete, 0, "{}", "script", "8 rows rejected");

        s.Workflow.OnTransferFinished("failed", null);
        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Transfer));
        s.Workflow.OnTransferFinished("completed", 0);

        Assert.Equal(PhaseStatus.Approved, StatusOf(s, PhaseName.Transfer));
        var complete = s.Phases.Get(PhaseName.Complete);
        Assert.Equal(PhaseStatus.Approved, complete.Status);
        Assert.Equal(0, complete.ApprovedVersion);
    }

    [Fact]
    public void Pause_and_resume_publish_events()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        s.Workflow.SetPaused(true);
        Assert.True(s.Project.Get().Paused);
        s.Workflow.SetPaused(false);

        Assert.False(s.Project.Get().Paused);
        Assert.Equal(new[] { "paused", "resumed" }, EventTypes(s).Where(t => t is "paused" or "resumed"));
    }

    [Fact]
    public void Retry_requeues_the_failed_job_of_a_running_phase()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        var job = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(job.Id);
        s.Jobs.MarkFailed(job.Id, "boom");

        s.Workflow.RetryJob(PhaseName.Discovery);

        Assert.Equal("discover", s.Jobs.NextQueued()!.Kind);
        Assert.Throws<WorkflowException>(() => s.Workflow.RetryJob(PhaseName.Discovery));   // already queued
        Assert.Throws<WorkflowException>(() => s.Workflow.RetryJob(PhaseName.Mapping));     // pending
    }

    [Fact]
    public void Retry_reruns_the_job_of_a_drafting_phase()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        s.Workflow.RetryJob(PhaseName.Analysis);

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Analysis));
        Assert.Equal("analyze", Assert.Single(s.Jobs.Active()).Kind);
        FakeServices.CompleteNextJob(s, FakeServices.Draft("second script draft"));
        var row = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal((PhaseStatus.Drafting, (int?)1), (row.Status, row.CurrentVersion));
        Assert.Equal("second script draft", s.Artifacts.Get(PhaseName.Analysis, 1)!.Summary);
    }

    [Fact]
    public void Rediscover_marks_analysis_to_ready_stale()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        Assert.Throws<WorkflowException>(() => s.Workflow.Rediscover());   // setup not done
        FakeServices.DriveToReview(s, PhaseName.Mapping);

        s.Workflow.Rediscover();

        Assert.Equal(PhaseStatus.Running, StatusOf(s, PhaseName.Discovery));
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Analysis));
        Assert.Equal(PhaseStatus.Stale, StatusOf(s, PhaseName.Mapping));
        Assert.Equal(PhaseStatus.Pending, StatusOf(s, PhaseName.Sql));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Workflow/WorkflowNextTests.cs` (every `Next()` branch):

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

public class WorkflowNextTests
{
    [Fact]
    public void Paused_project_awaits_with_reason_paused()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        s.Workflow.SetPaused(true);

        Assert.Equal(new NextAction("await", Reason: "paused"), s.Workflow.Next());
    }

    [Fact]
    public void Setup_awaits_with_the_ui_url_from_server_json()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        File.WriteAllText(tw.Ws.ServerJsonPath, "{\"port\":5123,\"pid\":1,\"token\":\"tok\"}");

        Assert.Equal(new NextAction("await", Reason: "setup", Phase: "setup", Url: "http://127.0.0.1:5123/?t=tok"), s.Workflow.Next());
    }

    [Fact]
    public void Running_phase_awaits_its_job_or_stops_when_the_job_failed()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);

        Assert.Equal(new NextAction("await", Reason: "job", Phase: "discovery"), s.Workflow.Next());

        var job = s.Jobs.NextQueued()!;
        s.Jobs.MarkRunning(job.Id);
        s.Jobs.MarkFailed(job.Id, "Login failed for user 'x'.");
        Assert.Equal(new NextAction("stop", Reason: "job_failed", Phase: "discovery", Summary: "Login failed for user 'x'."), s.Workflow.Next());
    }

    [Fact]
    public void Drafting_phase_returns_an_agent_action_and_writes_the_packet()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);
        var stalePatch = Path.Combine(tw.Ws.WorkDir, "analysis-v0-draft.patch.json");
        File.WriteAllText(stalePatch, "{}");

        var a = s.Workflow.Next();

        var packetPath = Path.Combine(tw.Ws.WorkDir, "analysis-v0-draft.json").Replace('\\', '/');
        Assert.Equal(new NextAction("agent", Agent: "schema-analyst", Phase: "analysis", Mode: "draft",
            Packet: packetPath, PatchPath: packetPath.Replace(".json", ".patch.json")), a);
        Assert.True(Path.IsPathFullyQualified(a.Packet!));
        Assert.False(File.Exists(stalePatch));

        var packet = JsonNode.Parse(File.ReadAllText(a.Packet!))!;
        Assert.Equal("analysis", packet["phase"]!.GetValue<string>());
        Assert.Equal("draft", packet["mode"]!.GetValue<string>());
        Assert.Equal(0, packet["baseVersion"]!.GetValue<int>());
        Assert.Equal(a.PatchPath, packet["patchPath"]!.GetValue<string>());
        Assert.Equal("schema-analyst", packet["agent"]!.GetValue<string>());
        Assert.Empty(packet["feedback"]!.AsArray());
        Assert.Equal(WorkflowEngine.PatchRules, packet["rules"]!.GetValue<string>());
        Assert.True(packet["data"]!["fake"]!.GetValue<bool>());
    }

    [Fact]
    public void Reworking_phase_packet_carries_only_the_open_feedback()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        var item = s.Feedback.Add(PhaseName.Analysis, 1, "finding:heap:dbo.AUDIT_LOG", "Explain the heap");
        s.Workflow.RequestChanges(PhaseName.Analysis);
        s.Feedback.Add(PhaseName.Analysis, 1, null, "a new draft, not part of this round");

        var a = s.Workflow.Next();

        Assert.Equal(("agent", "rework"), (a.Action, a.Mode));
        Assert.EndsWith("/analysis-v1-rework.json", a.Packet);
        var packet = JsonNode.Parse(File.ReadAllText(a.Packet!))!;
        var only = Assert.Single(packet["feedback"]!.AsArray())!;
        Assert.Equal(item.Id, only["id"]!.GetValue<long>());
        Assert.Equal("finding:heap:dbo.AUDIT_LOG", only["anchor"]!.GetValue<string>());
        Assert.Equal("Explain the heap", only["text"]!.GetValue<string>());
        Assert.Equal(1, packet["data"]!["openFeedback"]!.GetValue<int>());
    }

    [Fact]
    public void Peek_does_not_write_packets()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var a = s.Workflow.Peek();

        Assert.Equal("agent", a.Action);
        Assert.False(File.Exists(a.Packet!));
    }

    [Fact]
    public void Awaiting_review_awaits_review_and_ready_awaits_execute()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Sql);

        Assert.Equal(new NextAction("await", Reason: "review", Phase: "sql", Url: ""), s.Workflow.Next());
        s.Workflow.Approve(PhaseName.Sql);
        Assert.Equal(new NextAction("await", Reason: "execute", Phase: "ready", Url: ""), s.Workflow.Next());
    }

    [Theory]
    [InlineData("running", "await", "transfer")]
    [InlineData("paused", "await", "transfer_paused")]
    [InlineData("failed", "stop", "transfer_failed")]
    [InlineData("cancelled", "stop", "transfer_cancelled")]
    public void Transfer_run_status_drives_the_action(string runStatus, string action, string reason)
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        foreach (var p in WorkflowEngine.Order.Where(p => p < PhaseName.Transfer)) s.Phases.SetStatus(p, PhaseStatus.Approved);
        s.Phases.SetStatus(PhaseName.Transfer, PhaseStatus.Running);
        s.Db.Execute("INSERT INTO transfer_run (sql_version, status, options_json) VALUES (1, 'completed', '{}'), (1, $Status, '{}')",
            new { Status = runStatus });

        var a = s.Workflow.Next();

        Assert.Equal((action, reason, "transfer"), (a.Action, a.Reason, a.Phase));
    }

    [Fact]
    public void All_approved_stops_with_the_report_summary()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        foreach (var p in WorkflowEngine.Order) s.Phases.SetStatus(p, PhaseStatus.Approved);
        s.Artifacts.Add(PhaseName.Complete, 0, "{}", "script", "Migrated 12,000 rows; 8 rejected.");

        Assert.Equal(new NextAction("stop", Reason: "complete", Summary: "Migrated 12,000 rows; 8 rejected."), s.Workflow.Next());
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Workflow/WorkflowPatchTests.cs` (ApplyPatch/HumanEdit rules):

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

public class WorkflowPatchTests
{
    private static Patch SummaryPatch(string phase, int baseVersion, string summary, params FeedbackResponse[] responses) =>
        new(phase, baseVersion, [new PatchOp("replace", "/summary", JsonValue.Create(summary))], responses.ToList(), summary);

    [Fact]
    public void Agent_patch_on_a_draft_creates_v1_and_awaits_review()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "Executive summary"));

        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(1, r.Version);
        var v1 = s.Artifacts.Get(PhaseName.Analysis, 1)!;
        Assert.Equal("agent", v1.Author);
        Assert.Equal("Executive summary", v1.Summary);
        var payload = JsonNode.Parse(v1.PayloadJson)!;
        Assert.Equal("Executive summary", payload["summary"]!.GetValue<string>());
        Assert.Equal(2, payload["items"]!["b"]!.GetValue<int>());
        var row = s.Phases.Get(PhaseName.Analysis);
        Assert.Equal((PhaseStatus.AwaitingReview, (int?)1), (row.Status, row.CurrentVersion));
    }

    [Fact]
    public void Base_version_must_match_the_current_version()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 3, "x"));

        Assert.False(r.Ok);
        Assert.Equal("baseVersion 3 does not match the current version 0", r.Errors.Single());
    }

    [Fact]
    public void Agent_patches_are_refused_outside_drafting_or_reworking()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 1, "x"));

        Assert.False(r.Ok);
        Assert.Contains("is awaiting_review; agent patches are accepted only while drafting or reworking", r.Errors.Single());
    }

    [Fact]
    public void Rework_needs_a_valid_response_for_every_open_item()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        var f1 = s.Feedback.Add(PhaseName.Analysis, 1, null, "one");
        var f2 = s.Feedback.Add(PhaseName.Analysis, 1, null, "two");
        s.Workflow.RequestChanges(PhaseName.Analysis);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 1, "v2",
            new FeedbackResponse(f1.Id, "done", "ok"),
            new FeedbackResponse(999, "addressed", "x")));

        Assert.False(r.Ok);
        Assert.Contains($"response for feedback {f1.Id}: status must be 'addressed' or 'declined'", r.Errors);
        Assert.Contains("feedback 999 is not an open item of this phase", r.Errors);
        Assert.Contains($"missing response for feedback {f2.Id}", r.Errors);
        Assert.Equal(PhaseStatus.Reworking, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Null(s.Artifacts.Get(PhaseName.Analysis, 2));
    }

    [Fact]
    public void Rework_responses_are_recorded_against_the_new_version()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);
        var f1 = s.Feedback.Add(PhaseName.Analysis, 1, "narrative", "Add detail");
        var f2 = s.Feedback.Add(PhaseName.Analysis, 1, null, "Rename things");
        s.Workflow.RequestChanges(PhaseName.Analysis);

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 1, "v2",
            new FeedbackResponse(f1.Id, "addressed", "Added detail."),
            new FeedbackResponse(f2.Id, "declined", "Out of scope.")));

        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(2, r.Version);
        var a = s.Feedback.Get(f1.Id)!;
        var b = s.Feedback.Get(f2.Id)!;
        Assert.Equal((FeedbackStatus.Addressed, "Added detail.", (int?)2), (a.Status, a.Response, a.RespondedVersion));
        Assert.Equal((FeedbackStatus.Declined, "Out of scope.", (int?)2), (b.Status, b.Response, b.RespondedVersion));
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public void Module_validation_errors_reject_and_warnings_pass_through()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.ValidateFn = p => p["summary"]!.GetValue<string>().Length < 5
            ? new PayloadCheck(["summary too short"], [])
            : new PayloadCheck([], ["consider listing risks"]);
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToAnalysisDraft(s);

        var bad = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "x"));
        var good = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "A proper summary"));

        Assert.Equal(new[] { "summary too short" }, bad.Errors);
        Assert.True(good.Ok);
        Assert.Equal(new[] { "consider listing risks" }, good.Warnings);
    }

    [Fact]
    public void The_payload_is_stored_after_validation_so_module_annotations_persist()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.ValidateFn = p =>
        {
            p["validatedBy"] = "module";   // e.g. SqlModule writes Custom/Errors/Warnings during Validate
            return PayloadCheck.Pass();
        };
        using var s = FakeServices.Open(tw.Ws, modules);
        FakeServices.DriveToAnalysisDraft(s);

        var agent = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "agent version"));
        var human = s.Workflow.HumanEdit(SummaryPatch("analysis", 1, "human version"));

        Assert.True(agent.Ok && human.Ok);
        Assert.Equal("module", JsonNode.Parse(s.Artifacts.Get(PhaseName.Analysis, 1)!.PayloadJson)!["validatedBy"]!.GetValue<string>());
        Assert.Equal("module", JsonNode.Parse(s.Artifacts.Get(PhaseName.Analysis, 2)!.PayloadJson)!["validatedBy"]!.GetValue<string>());
    }

    [Fact]
    public void Op_errors_are_reported_with_the_op_index()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var r = s.Workflow.ApplyPatch(new Patch("analysis", 0, [new PatchOp("replace", "/nope", JsonValue.Create(1))], []));

        Assert.StartsWith("op 0 (replace /nope): ", r.Errors.Single());
    }

    [Fact]
    public void Dry_run_validates_but_writes_nothing()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);
        var lastEvent = s.Events.LastId();

        var r = s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "Executive summary"), dryRun: true);

        Assert.True(r.Ok);
        Assert.Null(r.Version);
        Assert.Equal(1, s.Artifacts.NextVersion(PhaseName.Analysis));
        Assert.Equal(PhaseStatus.Drafting, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Equal(lastEvent, s.Events.LastId());
    }

    [Fact]
    public void Patches_are_accepted_while_paused()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);
        s.Workflow.SetPaused(true);

        Assert.True(s.Workflow.ApplyPatch(SummaryPatch("analysis", 0, "done while paused")).Ok);
        Assert.Equal("paused", s.Workflow.Next().Reason);
    }

    [Fact]
    public void Unknown_phases_and_phases_without_a_module_are_rejected()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        Assert.Equal(new[] { "unknown phase 'nope'" }, s.Workflow.ApplyPatch(SummaryPatch("nope", 0, "x")).Errors);
        Assert.Equal(new[] { "phase 'discovery' does not accept patches" }, s.Workflow.ApplyPatch(SummaryPatch("discovery", 0, "x")).Errors);
    }

    [Fact]
    public void Human_edit_creates_a_human_version_and_stays_in_review()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToReview(s, PhaseName.Analysis);

        var r = s.Workflow.HumanEdit(SummaryPatch("analysis", 1, "Edited by a human"));

        Assert.True(r.Ok);
        Assert.Equal(2, r.Version);
        Assert.Equal("human", s.Artifacts.Get(PhaseName.Analysis, 2)!.Author);
        Assert.Equal(PhaseStatus.AwaitingReview, s.Phases.Get(PhaseName.Analysis).Status);

        s.Feedback.Add(PhaseName.Analysis, 2, null, "rework please");
        s.Workflow.RequestChanges(PhaseName.Analysis);
        var refused = s.Workflow.HumanEdit(SummaryPatch("analysis", 2, "again"));
        Assert.Contains("direct edits are allowed only while it awaits review", refused.Errors.Single());
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'IPhaseModule' could not be found` (and `DbmServices`, `WorkflowEngine`, `NextAction`, `JobResult`, …).

- [ ] **Step 3: Implement the contracts**

`plugins/db-migrate/engine/Dbm/Core/Jobs/IJobHandler.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.State;

namespace Dbm.Core.Jobs;

/// <summary>DraftPayload: the phase's script draft (null for jobs that produce no artifact, e.g. "discover").</summary>
public sealed record JobResult(JsonNode? DraftPayload, string? Summary);

public sealed class JobContext
{
    public required DbmServices Services { get; init; }
    public required JobRow Job { get; init; }

    /// <summary>Publishes a "log" event (shown live in the UI).</summary>
    public required Action<string> Log { get; init; }
}

/// <summary>Deterministic server-side work; registered in JobRegistry.</summary>
public interface IJobHandler
{
    string Kind { get; }
    Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct);
}
```

`plugins/db-migrate/engine/Dbm/Core/Workflow/NextAction.cs`:

```csharp
namespace Dbm.Core.Workflow;

/// <summary>
/// What the orchestrator does next. Action: "agent" | "await" | "stop".
/// await reasons: setup | job | review | execute | transfer | transfer_paused | paused.
/// stop reasons: complete | job_failed | transfer_failed | transfer_cancelled.
/// </summary>
public sealed record NextAction(string Action, string? Reason = null, string? Agent = null, string? Phase = null,
    string? Mode = null, string? Packet = null, string? PatchPath = null, string? Summary = null, string? Url = null);

public enum PacketMode { Draft, Rework }
```

`plugins/db-migrate/engine/Dbm/Core/Workflow/IPhaseModule.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.State;

namespace Dbm.Core.Workflow;

public sealed record PayloadCheck(List<string> Errors, List<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
    public static PayloadCheck Pass() => new([], []);
}

public sealed class ModuleContext
{
    public required DbmServices Services { get; init; }
    public required ArtifactRow Current { get; init; }
    public required IReadOnlyList<FeedbackRow> OpenFeedback { get; init; }
}

/// <summary>One per reviewable phase (Analysis, Mapping, Sql); registered in ModuleRegistry.</summary>
public interface IPhaseModule
{
    PhaseName Phase { get; }

    /// <summary>"schema-analyst" | "mapping-architect" | "sql-engineer".</summary>
    string Agent { get; }

    /// <summary>"analyze" | "automap" | "sqlgen".</summary>
    string JobKind { get; }

    /// <summary>false → the script draft goes straight to awaiting_review.</summary>
    bool NeedsAgent(JsonNode draft);

    /// <summary>The module-specific "data" section of the work packet.</summary>
    JsonNode BuildPacket(ModuleContext ctx, PacketMode mode);

    /// <summary>Runs on every agent/human patch result; errors reject the patch.</summary>
    PayloadCheck Validate(ModuleContext ctx, JsonNode payload);

    IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload);

    /// <summary>One line.</summary>
    string Summarize(JsonNode payload);
}
```

`plugins/db-migrate/engine/Dbm/Core/Workflow/Phases.cs`:

```csharp
using Dbm.Core.State;

namespace Dbm.Core.Workflow;

/// <summary>Phase-name helpers shared by the engine, the API and the CLI.</summary>
public static class Phases
{
    /// <summary>Phases with a script draft + agent/human review loop.</summary>
    public static readonly PhaseName[] Reviewable = [PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql];

    /// <summary>Job kind that produces each phase's draft when no module overrides it.</summary>
    public static readonly IReadOnlyDictionary<PhaseName, string> DefaultJobKinds = new Dictionary<PhaseName, string>
    {
        [PhaseName.Discovery] = "discover",
        [PhaseName.Analysis] = "analyze",
        [PhaseName.Mapping] = "automap",
        [PhaseName.Sql] = "sqlgen",
    };

    public static string Text(this PhaseName phase) => EnumText.ToText(phase);

    public static bool TryParse(string? text, out PhaseName phase) => EnumText.TryParse(text, out phase);

    /// <summary>Throws WorkflowException("unknown phase") for bad input (the API maps it to 400/409).</summary>
    public static PhaseName Parse(string text) =>
        TryParse(text, out var p) ? p : throw new WorkflowException($"unknown phase '{text}'");

    public static PhaseName? After(PhaseName phase) =>
        phase == PhaseName.Complete ? null : (PhaseName)((int)phase + 1);
}
```

- [ ] **Step 4: Implement the services composition**

`plugins/db-migrate/engine/Dbm/Core/DbEventSink.cs`:

```csharp
using Dbm.Core.State;

namespace Dbm.Core;

/// <summary>Default sink outside the server: persists events; live-only (persist:false) events are dropped.</summary>
public sealed class DbEventSink(EventRepo events) : IEventSink
{
    public void Publish(string type, object? payload = null, bool persist = true)
    {
        if (persist) events.Append(type, payload);
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs` (later milestones append `yield return new XModule(s);` lines after `// milestone registrations below`; the leading `if (s is null) yield break;` keeps the empty body a valid iterator):

```csharp
using Dbm.Core.Workflow;

namespace Dbm.Core;

public static class ModuleRegistry
{
    /// <summary>
    /// Every phase module. Later milestones append one line each below the marker comment:
    /// T2.7 <c>yield return new Dbm.Core.Analysis.AnalysisModule(s);</c>, T3.4 <c>… MappingModule(s)</c>, T4.4 <c>… SqlModule(s)</c>.
    /// </summary>
    public static IEnumerable<IPhaseModule> Create(DbmServices s)
    {
        if (s is null) yield break;   // keeps this an iterator while no module is registered
        // milestone registrations below
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs`:

```csharp
using Dbm.Core.Jobs;

namespace Dbm.Core;

public static class JobRegistry
{
    /// <summary>
    /// Every job handler (a queued job without a handler fails with "no handler for &lt;kind&gt;"). Later milestones append
    /// their lines below the marker comment: T2.5 <c>yield return new Dbm.Core.Catalog.DiscoverJob();</c>, T2.6 AnalyzeJob,
    /// T3.3 AutomapJob, T4.3 SqlGenJob.
    /// </summary>
    public static IEnumerable<IJobHandler> Create(DbmServices s)
    {
        if (s is null) yield break;   // keeps this an iterator while no handler is registered
        // milestone registrations below
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/DbmServices.cs` (`UiUrl()` parses `server.json` itself so Core never references `Dbm.Web`):

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Crypto;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core;

/// <summary>Composition root for one workspace. The server shares one instance across all threads.</summary>
public sealed class DbmServices : IDisposable
{
    private DbmServices(Workspace ws, StateDb db, ISecretProtector protector)
    {
        Ws = ws;
        Db = db;
        Protector = protector;
        Project = new ProjectRepo(db);
        Phases = new PhaseRepo(db);
        Artifacts = new ArtifactRepo(db);
        Feedback = new FeedbackRepo(db);
        Events = new EventRepo(db);
        Jobs = new JobRepo(db);
        Connections = new ConnectionRepo(db, protector);
        Sink = new DbEventSink(Events);
        Workflow = new WorkflowEngine(this);
    }

    public Workspace Ws { get; }
    public StateDb Db { get; }
    public ISecretProtector Protector { get; }
    public ProjectRepo Project { get; }
    public PhaseRepo Phases { get; }
    public ArtifactRepo Artifacts { get; }
    public FeedbackRepo Feedback { get; }
    public EventRepo Events { get; }
    public JobRepo Jobs { get; }
    public ConnectionRepo Connections { get; }
    public IEventSink Sink { get; set; }
    public WorkflowEngine Workflow { get; }
    public IReadOnlyDictionary<PhaseName, IPhaseModule> Modules { get; private set; } = new Dictionary<PhaseName, IPhaseModule>();
    public IReadOnlyDictionary<string, IJobHandler> JobHandlers { get; private set; } = new Dictionary<string, IJobHandler>();

    /// <param name="sink">Default: DbEventSink (persist only).</param>
    /// <param name="modules">Default: ModuleRegistry.Create (tests pass fakes).</param>
    /// <param name="jobs">Default: JobRegistry.Create (tests pass fakes).</param>
    public static DbmServices Open(Workspace ws, IEventSink? sink = null,
        Func<DbmServices, IEnumerable<IPhaseModule>>? modules = null,
        Func<DbmServices, IEnumerable<IJobHandler>>? jobs = null)
    {
        var db = StateDb.Open(ws.StateDbPath);
        try
        {
            var s = new DbmServices(ws, db, SecretProtector.ForCurrentUser());
            if (sink is not null) s.Sink = sink;
            s.Modules = (modules ?? ModuleRegistry.Create)(s).ToDictionary(m => m.Phase);
            s.JobHandlers = (jobs ?? JobRegistry.Create)(s).ToDictionary(h => h.Kind, StringComparer.Ordinal);
            return s;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>The browser URL (with token) from server.json; "" when no server has written one.</summary>
    public string UiUrl()
    {
        try
        {
            if (!File.Exists(Ws.ServerJsonPath)) return "";
            var node = JsonNode.Parse(File.ReadAllText(Ws.ServerJsonPath));
            var port = node?["port"]?.GetValue<int>();
            var token = node?["token"]?.GetValue<string>();
            return port is > 0 && !string.IsNullOrEmpty(token) ? $"http://127.0.0.1:{port}/?t={token}" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void Dispose() => Db.Dispose();
}
```

- [ ] **Step 5: Implement `WorkflowEngine`**

`plugins/db-migrate/engine/Dbm/Core/Workflow/WorkflowEngine.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Patching;
using Dbm.Core.State;

namespace Dbm.Core.Workflow;

public sealed record ApplyResult(bool Ok, int? Version, List<string> Errors, List<string> Warnings)
{
    public static ApplyResult Fail(params string[] errors) => new(false, null, errors.ToList(), []);
}

public sealed class WorkflowException(string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

/// <summary>Owns every phase transition (see the transition table in 00-overview.md C5). All mutations are transactional.</summary>
public sealed class WorkflowEngine(DbmServices services)
{
    public static readonly PhaseName[] Order = Enum.GetValues<PhaseName>();

    public const string PatchRules =
        "Write a Patch JSON to patchPath: ops (add|replace|remove, JSON pointer into the artifact), a response for every feedback id, and a one-line summary.";

    // ---------------------------------------------------------------- next action

    /// <summary>The orchestrator's next step; writes the work packet for "agent" actions.</summary>
    public NextAction Next() => Compute(writePacket: true);

    /// <summary>Same as Next() without writing packet files (used by /api/state and `dbm status`).</summary>
    public NextAction Peek() => Compute(writePacket: false);

    private NextAction Compute(bool writePacket)
    {
        var url = services.UiUrl();
        if (services.Project.Get().Paused) return new NextAction("await", Reason: "paused");

        foreach (var row in services.Phases.All())
        {
            if (row.Status == PhaseStatus.Approved) continue;
            var name = row.Name.Text();
            if (row.Name == PhaseName.Setup) return new NextAction("await", Reason: "setup", Phase: name, Url: url);

            switch (row.Status)
            {
                case PhaseStatus.Running when row.Name == PhaseName.Transfer:
                    return TransferAction(url);
                case PhaseStatus.Running:
                    var job = services.Jobs.LatestFor(row.Name);
                    return job?.Status == JobStatus.Failed
                        ? new NextAction("stop", Reason: "job_failed", Phase: name, Summary: job.Error ?? $"{job.Kind} failed")
                        : new NextAction("await", Reason: "job", Phase: name);
                case PhaseStatus.Drafting:
                    return AgentAction(row, PacketMode.Draft, writePacket);
                case PhaseStatus.Reworking:
                    return AgentAction(row, PacketMode.Rework, writePacket);
                case PhaseStatus.AwaitingReview:
                    return new NextAction("await", Reason: row.Name == PhaseName.Ready ? "execute" : "review", Phase: name, Url: url);
                default:
                    // pending / stale while every earlier phase is approved: an upstream job is about to (re)start it.
                    return new NextAction("await", Reason: "job", Phase: name);
            }
        }

        var summary = services.Artifacts.Latest(PhaseName.Complete)?.Summary;
        return new NextAction("stop", Reason: "complete", Summary: summary ?? "Migration complete.");
    }

    private NextAction TransferAction(string url)
    {
        var status = services.Db.Scalar<string>("SELECT status FROM transfer_run ORDER BY id DESC LIMIT 1");
        const string phase = "transfer";
        return status switch
        {
            "paused" => new NextAction("await", Reason: "transfer_paused", Phase: phase, Url: url),
            "failed" => new NextAction("stop", Reason: "transfer_failed", Phase: phase, Summary: "The transfer failed; see the Execute screen.", Url: url),
            "cancelled" => new NextAction("stop", Reason: "transfer_cancelled", Phase: phase, Summary: "The transfer was cancelled.", Url: url),
            _ => new NextAction("await", Reason: "transfer", Phase: phase, Url: url),
        };
    }

    private NextAction AgentAction(PhaseRow row, PacketMode mode, bool writePacket)
    {
        if (!services.Modules.TryGetValue(row.Name, out var module) || row.CurrentVersion is not int baseVersion)
            return new NextAction("await", Reason: "job", Phase: row.Name.Text());

        var modeText = EnumText.ToText(mode);
        var stem = $"{row.Name.Text()}-v{baseVersion}-{modeText}";
        var packetPath = Path.Combine(services.Ws.WorkDir, stem + ".json");
        var patchPath = Path.Combine(services.Ws.WorkDir, stem + ".patch.json");
        if (writePacket) WritePacket(module, row.Name, mode, baseVersion, packetPath, patchPath);
        return new NextAction("agent", Agent: module.Agent, Phase: row.Name.Text(), Mode: modeText,
            Packet: Slashes(packetPath), PatchPath: Slashes(patchPath));
    }

    private void WritePacket(IPhaseModule module, PhaseName phase, PacketMode mode, int baseVersion, string packetPath, string patchPath)
    {
        var current = services.Artifacts.Get(phase, baseVersion)
                      ?? throw new WorkflowException($"artifact {phase.Text()} v{baseVersion} is missing");
        var open = mode == PacketMode.Rework ? services.Feedback.List(phase, FeedbackStatus.Open) : [];
        var ctx = new ModuleContext { Services = services, Current = current, OpenFeedback = open };
        var data = module.BuildPacket(ctx, mode);
        var envelope = new JsonObject
        {
            ["phase"] = phase.Text(),
            ["mode"] = EnumText.ToText(mode),
            ["baseVersion"] = baseVersion,
            ["patchPath"] = Slashes(patchPath),
            ["agent"] = module.Agent,
            ["feedback"] = new JsonArray(open
                .Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["anchor"] = f.Anchor, ["text"] = f.Text })
                .ToArray()),
            ["rules"] = PatchRules,
            ["data"] = data.Parent is null ? data : data.DeepClone(),
        };
        Directory.CreateDirectory(services.Ws.WorkDir);
        if (File.Exists(patchPath)) File.Delete(patchPath);   // never apply a patch written for an older packet
        File.WriteAllText(packetPath, envelope.ToJsonString(Json.Options));
    }

    // ---------------------------------------------------------------- setup & jobs

    /// <summary>Both sides saved → Setup approved, Discovery running + "discover"; started later phases → stale.</summary>
    public void OnConnectionsSaved() => services.Db.InTransaction(() =>
    {
        if (!services.Connections.Has(Side.Src) || !services.Connections.Has(Side.Tgt)) return;
        EnsureTransferPending("Connections cannot change after the transfer has started.");
        SetStatus(PhaseName.Setup, PhaseStatus.Approved);
        foreach (var later in Order.Where(p => p > PhaseName.Discovery)) MarkStale(later);
        StartJob(PhaseName.Discovery);
    });

    /// <summary>Re-extract both catalogs; Analysis..Ready become stale. Only while Transfer is pending.</summary>
    public void Rediscover() => services.Db.InTransaction(() =>
    {
        EnsureTransferPending("Re-discovery is not possible after the transfer has started.");
        if (services.Phases.Get(PhaseName.Setup).Status != PhaseStatus.Approved)
            throw new WorkflowException("Save both connections before running discovery.");
        foreach (var later in Order.Where(p => p > PhaseName.Discovery && p <= PhaseName.Ready)) MarkStale(later);
        StartJob(PhaseName.Discovery);
    });

    public void OnJobDone(JobRow job, JsonNode? draftPayload, string? summary) => services.Db.InTransaction(() =>
    {
        if (job.Phase is not PhaseName phase) return;
        var row = services.Phases.Get(phase);
        var latest = services.Jobs.LatestFor(phase);
        if (row.Status != PhaseStatus.Running || (latest is not null && latest.Id != job.Id))
        {
            Publish("log", new { level = "warn", message = $"Ignored the result of job {job.Id} ({job.Kind}): {phase.Text()} is {EnumText.ToText(row.Status)} or has a newer job." });
            return;
        }

        if (phase == PhaseName.Discovery)
        {
            var version = row.CurrentVersion ?? 0;
            if (draftPayload is not null) version = StoreArtifact(phase, draftPayload, "script", summary);
            services.Phases.SetApproved(phase, version, CatalogFingerprint());
            Publish("state_changed", new { phase, status = PhaseStatus.Approved });
            StartJob(PhaseName.Analysis);
            return;
        }

        if (!services.Modules.TryGetValue(phase, out var module))
            throw new WorkflowException($"No module is registered for phase {phase.Text()}.");
        if (draftPayload is null) throw new WorkflowException($"Job {job.Kind} returned no draft for {phase.Text()}.");
        StoreArtifact(phase, draftPayload, "script", summary ?? module.Summarize(draftPayload));
        SetStatus(phase, module.NeedsAgent(draftPayload) ? PhaseStatus.Drafting : PhaseStatus.AwaitingReview);
    });

    /// <summary>The phase stays running; Next() reports stop/job_failed until the job is retried.</summary>
    public void OnJobFailed(JobRow job, string error) =>
        Publish("job_failed", new { id = job.Id, kind = job.Kind, phase = job.Phase, error });

    /// <summary>Re-enqueues the phase's job: `running` (its job failed) or `drafting` (regenerate the script draft).</summary>
    public void RetryJob(PhaseName phase) => services.Db.InTransaction(() =>
    {
        var row = services.Phases.Get(phase);
        if (row.Status is not (PhaseStatus.Running or PhaseStatus.Drafting))
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; only a running or drafting phase's job can be retried.");
        if (services.Jobs.Active().Any(j => j.Phase == phase))
            throw new WorkflowException($"A job for {phase.Text()} is already queued or running.");
        services.Jobs.Enqueue(JobKind(phase), phase);
        if (row.Status == PhaseStatus.Drafting) SetStatus(phase, PhaseStatus.Running);
        else Publish("state_changed", new { phase, status = row.Status });
    });

    /// <summary>The artifact a re-running job should carry over (the latest version before the re-run).</summary>
    public ArtifactRow? CarryOver(PhaseName phase) => services.Artifacts.Latest(phase);

    // ---------------------------------------------------------------- review loop

    /// <summary>Agent patch (author "agent"); allowed while paused so in-flight work is never lost.</summary>
    public ApplyResult ApplyPatch(Patch patch, bool dryRun = false) => Apply(patch, "agent", dryRun);

    /// <summary>Direct edit from the UI (author "human"); the phase must be awaiting review.</summary>
    public ApplyResult HumanEdit(Patch patch) => Apply(patch, "human", dryRun: false);

    private ApplyResult Apply(Patch patch, string author, bool dryRun)
    {
        if (!Phases.TryParse(patch.Phase, out var phase)) return ApplyResult.Fail($"unknown phase '{patch.Phase}'");
        if (!services.Modules.TryGetValue(phase, out var module)) return ApplyResult.Fail($"phase '{phase.Text()}' does not accept patches");
        var human = author == "human";

        return services.Db.InTransaction(() =>
        {
            var row = services.Phases.Get(phase);
            var status = EnumText.ToText(row.Status);
            if (human && row.Status != PhaseStatus.AwaitingReview)
                return ApplyResult.Fail($"phase {phase.Text()} is {status}; direct edits are allowed only while it awaits review");
            if (!human && row.Status is not (PhaseStatus.Drafting or PhaseStatus.Reworking))
                return ApplyResult.Fail($"phase {phase.Text()} is {status}; agent patches are accepted only while drafting or reworking");
            if (row.CurrentVersion is not int current || patch.BaseVersion != current)
                return ApplyResult.Fail($"baseVersion {patch.BaseVersion} does not match the current version {row.CurrentVersion?.ToString() ?? "(none)"}");

            var open = !human && row.Status == PhaseStatus.Reworking ? services.Feedback.List(phase, FeedbackStatus.Open) : [];
            var responses = human ? [] : patch.Responses ?? [];
            var errors = CheckResponses(open, responses);
            if (errors.Count > 0) return new ApplyResult(false, null, errors, []);

            var baseRow = services.Artifacts.Get(phase, current)!;
            JsonNode updated;
            try
            {
                updated = JsonPatch.Apply(JsonNode.Parse(baseRow.PayloadJson)!, patch.Ops ?? []);
            }
            catch (PatchException ex)
            {
                return ApplyResult.Fail(ex.Message);
            }

            var check = module.Validate(new ModuleContext { Services = services, Current = baseRow, OpenFeedback = open }, updated);
            if (!check.Ok) return new ApplyResult(false, null, check.Errors, check.Warnings);
            if (dryRun) return new ApplyResult(true, null, [], check.Warnings);

            var summary = string.IsNullOrWhiteSpace(patch.Summary) ? module.Summarize(updated) : patch.Summary;
            var version = StoreArtifact(phase, updated, author, summary);
            foreach (var r in responses)
            {
                var fs = r.Status == "declined" ? FeedbackStatus.Declined : FeedbackStatus.Addressed;
                services.Feedback.Respond(r.FeedbackId, fs, r.Note, version);
            }
            if (responses.Count > 0) Publish("feedback_changed", new { phase });
            if (!human) SetStatus(phase, PhaseStatus.AwaitingReview);
            return new ApplyResult(true, version, [], check.Warnings);
        });
    }

    private static List<string> CheckResponses(IReadOnlyList<FeedbackRow> open, IReadOnlyList<FeedbackResponse> responses)
    {
        var errors = new List<string>();
        var openIds = open.Select(f => f.Id).ToHashSet();
        foreach (var r in responses)
        {
            if (r.Status is not ("addressed" or "declined"))
                errors.Add($"response for feedback {r.FeedbackId}: status must be 'addressed' or 'declined'");
            if (!openIds.Contains(r.FeedbackId))
                errors.Add($"feedback {r.FeedbackId} is not an open item of this phase");
            if (string.IsNullOrWhiteSpace(r.Note))
                errors.Add($"response for feedback {r.FeedbackId}: note is required");
        }
        foreach (var id in openIds.Except(responses.Select(r => r.FeedbackId)).Order())
            errors.Add($"missing response for feedback {id}");
        return errors;
    }

    /// <summary>Submits draft feedback; requires at least one open item; phase → reworking.</summary>
    public void RequestChanges(PhaseName phase) => services.Db.InTransaction(() =>
    {
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.AwaitingReview)
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; changes can be requested only while it awaits review.");
        services.Feedback.SubmitDrafts(phase);
        if (services.Feedback.List(phase, FeedbackStatus.Open).Count == 0)
            throw new WorkflowException("Add at least one feedback item before requesting changes.");
        Publish("feedback_changed", new { phase });
        SetStatus(phase, PhaseStatus.Reworking);
    });

    /// <summary>Throws WorkflowException(details = blockers). Records approved_fingerprint = "&lt;src&gt;:&lt;tgt&gt;".</summary>
    public void Approve(PhaseName phase) => services.Db.InTransaction(() =>
    {
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} is not approved from a review screen.");
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.AwaitingReview)
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; only a phase awaiting review can be approved.");
        if (!services.Modules.TryGetValue(phase, out var module))
            throw new WorkflowException($"No module is registered for phase {phase.Text()}.");

        var current = services.Artifacts.Get(phase, row.CurrentVersion ?? -1)
                      ?? throw new WorkflowException($"{phase.Text()} has no current version to approve.");
        var ctx = new ModuleContext { Services = services, Current = current, OpenFeedback = services.Feedback.List(phase, FeedbackStatus.Open) };
        var blockers = module.ApprovalBlockers(ctx, JsonNode.Parse(current.PayloadJson)!);
        if (blockers.Count > 0) throw new WorkflowException("Approval is blocked.", blockers);

        services.Phases.SetApproved(phase, current.Version, CatalogFingerprint());
        Publish("state_changed", new { phase, status = PhaseStatus.Approved });
        switch (phase)
        {
            case PhaseName.Analysis: StartJob(PhaseName.Mapping); break;
            case PhaseName.Mapping: StartJob(PhaseName.Sql); break;
            case PhaseName.Sql: SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview); break;
        }
    });

    /// <summary>approved → awaiting_review at the approved version; later phases up to Ready → stale.</summary>
    public void Reopen(PhaseName phase) => services.Db.InTransaction(() =>
    {
        EnsureTransferPending("Phases cannot be reopened after the transfer has started.");
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} cannot be reopened.");
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.Approved) throw new WorkflowException($"{phase.Text()} is not approved.");
        services.Phases.ClearApproval(phase);
        if (row.ApprovedVersion is int approved) services.Phases.SetCurrentVersion(phase, approved);
        SetStatus(phase, PhaseStatus.AwaitingReview);
        foreach (var later in Order.Where(p => p > phase && p <= PhaseName.Ready)) MarkStale(later);
    });

    public void SetPaused(bool paused) => services.Db.InTransaction(() =>
    {
        services.Project.SetPaused(paused);
        Publish(paused ? "paused" : "resumed");
    });

    // ---------------------------------------------------------------- transfer

    public void OnTransferStarted() => services.Db.InTransaction(() =>
    {
        SetStatus(PhaseName.Ready, PhaseStatus.Approved);
        SetStatus(PhaseName.Transfer, PhaseStatus.Running);
    });

    /// <summary>"completed" → Transfer and Complete approved (the report artifact is already stored).</summary>
    public void OnTransferFinished(string runStatus, int? reportVersion) => services.Db.InTransaction(() =>
    {
        if (runStatus != "completed")
        {
            Publish("state_changed", new { phase = PhaseName.Transfer, status = PhaseStatus.Running });
            return;
        }
        SetStatus(PhaseName.Transfer, PhaseStatus.Approved);
        if (reportVersion is int v)
        {
            services.Phases.SetCurrentVersion(PhaseName.Complete, v);
            services.Phases.SetApproved(PhaseName.Complete, v, null);
            Publish("state_changed", new { phase = PhaseName.Complete, status = PhaseStatus.Approved });
        }
        else
        {
            SetStatus(PhaseName.Complete, PhaseStatus.Approved);
        }
    });

    // ---------------------------------------------------------------- helpers

    private void StartJob(PhaseName phase)
    {
        SetStatus(phase, PhaseStatus.Running);
        var kind = JobKind(phase);
        var alreadyQueued = services.Jobs.Active().Any(j => j.Phase == phase && j.Kind == kind && j.Status == JobStatus.Queued);
        if (!alreadyQueued) services.Jobs.Enqueue(kind, phase);
    }

    private string JobKind(PhaseName phase) =>
        services.Modules.TryGetValue(phase, out var m) ? m.JobKind
        : Phases.DefaultJobKinds.TryGetValue(phase, out var kind) ? kind
        : throw new WorkflowException($"{phase.Text()} has no job.");

    private void MarkStale(PhaseName phase)
    {
        var row = services.Phases.Get(phase);
        if (row.Status is PhaseStatus.Pending or PhaseStatus.Stale) return;
        services.Phases.ClearApproval(phase);
        SetStatus(phase, PhaseStatus.Stale);
    }

    private int StoreArtifact(PhaseName phase, JsonNode payload, string author, string? summary)
    {
        var version = services.Artifacts.NextVersion(phase);
        services.Artifacts.Add(phase, version, payload.ToJsonString(Json.Options), author, summary);
        services.Phases.SetCurrentVersion(phase, version);
        Publish("artifact_created", new { phase, version, author });
        return version;
    }

    private void EnsureTransferPending(string message)
    {
        if (services.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Pending) throw new WorkflowException(message);
    }

    /// <summary>"&lt;src&gt;:&lt;tgt&gt;" from the catalog table; null until both sides are discovered.</summary>
    private string? CatalogFingerprint()
    {
        var rows = services.Db.Query("SELECT side, fingerprint FROM catalog", r => (Side: r.GetString(0), Fp: r.GetString(1)));
        var src = rows.FirstOrDefault(r => r.Side == "src").Fp;
        var tgt = rows.FirstOrDefault(r => r.Side == "tgt").Fp;
        return src is null || tgt is null ? null : $"{src}:{tgt}";
    }

    private void SetStatus(PhaseName phase, PhaseStatus status)
    {
        services.Phases.SetStatus(phase, status);
        Publish("state_changed", new { phase, status });
    }

    private void Publish(string type, object? payload = null) => services.Sink.Publish(type, payload);

    private static string Slashes(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
```

- [ ] **Step 6: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:   138` (DbmServicesTests 5, WorkflowTransitionTests 21, WorkflowNextTests 12, WorkflowPatchTests 12 added).

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(workflow): phase state machine, work packets, patch application, services composition" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.6: Job runner

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Jobs/JobRunner.cs`
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/Wait.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Jobs/JobRunnerTests.cs`

**Interfaces:**
- Consumes: `DbmServices`, `JobRepo` (incl. `TryClaim`), `WorkflowEngine.OnJobDone/OnJobFailed`, `Redactor`.
- Produces (C6): `JobRunner(DbmServices)` with `RunPendingAsync(ct)` and `RunLoopAsync(ct)`, `static TimeSpan PollInterval` (500 ms). Test support: `Wait.UntilAsync(Func<bool>|Func<Task<bool>>, timeoutMs = 10_000)`.

Algorithm (normative): `NextQueued` → `TryClaim` (skip if another runner/process took it) → publish `job_started {id,kind,phase}` → resolve the handler (missing → failure `no handler for <kind>`) → run with a `JobContext` whose `Log` publishes `log {level:"info",message,jobId}` → in ONE transaction `MarkDone` + `Workflow.OnJobDone(job, result.DraftPayload, result.Summary)` → publish `job_done`. Any exception (including one thrown by `OnJobDone`, which rolls the transaction back) → `MarkFailed(scrubbed message)` + `Workflow.OnJobFailed` (which publishes `job_failed`). Messages are scrubbed of the saved connections' secrets. On shutdown cancellation the job stays `running`; `RunLoopAsync` re-queues stale `running` jobs when it starts, then polls every 500 ms.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Support/Wait.cs`:

```csharp
using System.Diagnostics;

namespace Dbm.Tests.Support;

/// <summary>Polling helper for asynchronous effects (job loops, servers, SSE).</summary>
public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition was not met in time");
            await Task.Delay(50);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition was not met in time");
            await Task.Delay(50);
        }
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Jobs/JobRunnerTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Jobs;

public class JobRunnerTests
{
    private sealed class SlowHandler(string kind) : IJobHandler
    {
        private int _runs;
        public string Kind => kind;
        public int Runs => Volatile.Read(ref _runs);

        public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            await Task.Delay(30, ct);
            return new JobResult(null, null);
        }
    }

    [Fact]
    public async Task Discover_then_analyze_run_in_order()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);

        var ran = await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        Assert.Equal(2, ran);
        Assert.Equal(PhaseStatus.Approved, s.Phases.Get(PhaseName.Discovery).Status);
        Assert.Equal(PhaseStatus.Drafting, s.Phases.Get(PhaseName.Analysis).Status);
        Assert.Equal("script", s.Artifacts.Get(PhaseName.Analysis, 0)!.Author);
        var types = s.Events.Since(0, 1000).Select(e => e.Type).ToList();
        Assert.Equal(2, types.Count(t => t == "job_started"));
        Assert.Equal(2, types.Count(t => t == "job_done"));
        Assert.All(s.Jobs.Recent(10), j => Assert.Equal(JobStatus.Done, j.Status));
    }

    [Fact]
    public async Task A_job_without_a_handler_fails_and_stops_the_orchestrator()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws, handlers: [new FakeJobHandler("discover")]);
        FakeServices.SaveConnections(s);

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        var job = s.Jobs.LatestFor(PhaseName.Analysis)!;
        Assert.Equal((JobStatus.Failed, "no handler for analyze"), (job.Status, job.Error));
        Assert.Equal(PhaseStatus.Running, s.Phases.Get(PhaseName.Analysis).Status);
        var next = s.Workflow.Next();
        Assert.Equal(("stop", "job_failed", "no handler for analyze"), (next.Action, next.Reason, next.Summary));
        Assert.Contains(s.Events.Since(0), e => e.Type == "job_failed");
    }

    [Fact]
    public async Task Handler_errors_are_scrubbed_of_saved_secrets()
    {
        using var tw = new TestWorkspace();
        var leaky = new FakeJobHandler("discover", _ => throw new InvalidOperationException("Login failed; password TopSecret99 rejected"));
        using var s = FakeServices.Open(tw.Ws, handlers: [leaky]);
        s.Connections.Save(Side.Src, "Server=a;Database=b;User ID=u;Password=TopSecret99", FakeServices.Meta("a", "b"));
        s.Connections.Save(Side.Tgt, FakeServices.TgtConnection, FakeServices.Meta("t", "ShopV2"));
        s.Workflow.OnConnectionsSaved();

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        Assert.Equal("Login failed; password *** rejected", s.Jobs.LatestFor(PhaseName.Discovery)!.Error);
        Assert.DoesNotContain(s.Events.Since(0, 1000), e => e.PayloadJson.Contains("TopSecret99"));
    }

    [Fact]
    public async Task A_draft_that_the_workflow_rejects_fails_the_job_and_stores_nothing()
    {
        using var tw = new TestWorkspace();
        var noDraft = new FakeJobHandler("analyze", _ => new JobResult(null, null));
        using var s = FakeServices.Open(tw.Ws, handlers: [new FakeJobHandler("discover"), noDraft]);
        FakeServices.SaveConnections(s);

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        var job = s.Jobs.LatestFor(PhaseName.Analysis)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("returned no draft", job.Error);
        Assert.Null(s.Artifacts.Latest(PhaseName.Analysis));
    }

    [Fact]
    public async Task Log_calls_publish_log_events()
    {
        using var tw = new TestWorkspace();
        var chatty = new FakeJobHandler("discover", ctx =>
        {
            ctx.Log("extracting 12 tables");
            return new JobResult(null, null);
        });
        using var s = FakeServices.Open(tw.Ws, handlers: [chatty]);
        FakeServices.SaveConnections(s);

        await new JobRunner(s).RunPendingAsync(CancellationToken.None);

        var log = s.Events.Since(0, 1000).Single(e => e.Type == "log" && e.PayloadJson.Contains("extracting"));
        var payload = JsonNode.Parse(log.PayloadJson)!;
        Assert.Equal("info", payload["level"]!.GetValue<string>());
        Assert.Equal("extracting 12 tables", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_loop_requeues_stale_jobs_and_runs_until_cancelled()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);
        s.Jobs.MarkRunning(s.Jobs.NextQueued()!.Id);   // left "running" by a dead server
        using var cts = new CancellationTokenSource();

        var loop = new JobRunner(s).RunLoopAsync(cts.Token);
        await Wait.UntilAsync(() => s.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.Drafting);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Done, s.Jobs.LatestFor(PhaseName.Discovery)!.Status);
    }

    [Fact]
    public async Task Two_runners_never_run_the_same_job_twice()
    {
        using var tw = new TestWorkspace();
        var slow = new SlowHandler("noop");
        using var a = FakeServices.Open(tw.Ws, handlers: [slow]);
        using var b = FakeServices.Open(tw.Ws, handlers: [slow]);
        for (var i = 0; i < 6; i++) a.Jobs.Enqueue("noop", null);

        var counts = await Task.WhenAll(
            Task.Run(() => new JobRunner(a).RunPendingAsync(CancellationToken.None)),
            Task.Run(() => new JobRunner(b).RunPendingAsync(CancellationToken.None)));

        Assert.Equal(6, counts.Sum());
        Assert.Equal(6, slow.Runs);
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'JobRunner' could not be found`.

- [ ] **Step 3: Implement**

`plugins/db-migrate/engine/Dbm/Core/Jobs/JobRunner.cs`:

```csharp
using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Core.Jobs;

/// <summary>Runs queued jobs one at a time. Safe to run in several processes: jobs are claimed atomically.</summary>
public sealed class JobRunner(DbmServices services)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs queued jobs until none is left; returns how many ran (used by tests and `dbm run-jobs`).</summary>
    public async Task<int> RunPendingAsync(CancellationToken ct)
    {
        var count = 0;
        while (!ct.IsCancellationRequested)
        {
            var next = services.Jobs.NextQueued();
            if (next is null) break;
            if (!services.Jobs.TryClaim(next.Id)) continue;
            await RunOneAsync(services.Jobs.Get(next.Id)!, ct);
            count++;
        }
        return count;
    }

    /// <summary>Server loop: jobs left "running" by a dead process are re-queued, then the queue is polled.</summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        services.Jobs.RequeueStaleRunning();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunPendingAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                services.Sink.Publish("log", new { level = "error", message = $"job runner: {Scrub(ex.Message)}" });
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOneAsync(JobRow job, CancellationToken ct)
    {
        services.Sink.Publish("job_started", new { id = job.Id, kind = job.Kind, phase = job.Phase });
        try
        {
            if (!services.JobHandlers.TryGetValue(job.Kind, out var handler))
                throw new InvalidOperationException($"no handler for {job.Kind}");

            var ctx = new JobContext
            {
                Services = services,
                Job = job,
                Log = message => services.Sink.Publish("log", new { level = "info", message = Scrub(message), jobId = job.Id }),
            };
            var result = await handler.RunAsync(ctx, ct);
            services.Db.InTransaction(() =>
            {
                services.Jobs.MarkDone(job.Id);
                services.Workflow.OnJobDone(job, result.DraftPayload, result.Summary);
            });
            services.Sink.Publish("job_done", new { id = job.Id, kind = job.Kind, phase = job.Phase });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Server shutting down: leave the job "running"; the next server start re-queues it.
            throw;
        }
        catch (Exception ex)
        {
            var message = Scrub(ex.Message);
            services.Jobs.MarkFailed(job.Id, message);
            services.Workflow.OnJobFailed(job, message);
        }
    }

    /// <summary>Removes any password/secret of the saved connections from a message.</summary>
    private string Scrub(string message)
    {
        var secrets = new List<string?>();
        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            try
            {
                var cs = services.Connections.GetConnectionString(side);
                if (cs is not null) secrets.AddRange(Redactor.SecretsOf(cs));
            }
            catch (Exception)
            {
                // unreadable secret: nothing to scrub for this side
            }
        }
        return Redactor.Scrub(message, secrets);
    }
}
```

- [ ] **Step 4: Run the tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:   145` (JobRunnerTests 7 added).

- [ ] **Step 5: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(jobs): background job runner with atomic claims and secret scrubbing" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.7: Web host, server control, SSE, core API

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Web/ServerInfo.cs`, `SelfCommand.cs`, `ServerControl.cs`, `FileLog.cs`
- Create: `plugins/db-migrate/engine/Dbm/Web/WebHost.cs`, `WebState.cs`, `TokenGuard.cs`, `Broadcaster.cs`, `EventPump.cs`, `AgentPresence.cs`, `ApprovalGuards.cs`, `StateView.cs`
- Create: `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs`, `CoreEndpoints.cs`, `ApiResults.cs`
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/WebTestServer.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Web/TokenGuardTests.cs`, `Unit/Web/ServerControlQuoteTests.cs`, `Unit/Web/WebHostTests.cs`, `Integration/Web/ConnectionEndpointTests.cs`

**Interfaces:**
- Consumes: `DbmServices`, `WorkflowEngine`, `JobRunner`, `EventRepo`, `SqlConnect`, `Redactor`, `Workspace`, `Clock`, `Json`.
- Produces (C7): `ServerInfo`, `SelfCommand.Get()`, `ServerControl` (`ReadInfo`, `IsAliveAsync`, `EnsureRunningAsync`, `StopAsync`, `OpenBrowser`), `WebHost.RunAsync(ws, port, ct, Func<Workspace, DbmServices>? servicesFactory = null, Action<ServerInfo>? onStarted = null)`, `Broadcaster(EventRepo)` (+ `Deliver`, `Subscribe()`, `NextSignal()`, `Signal()`, `ClientCount`) and record `SseMessage(long? Id, string Type, string Data)`, `EventPump(EventRepo, Broadcaster, FileLog?)`, `AgentPresence(DbmServices, Broadcaster?)`, `WebState` (C7 minus `Transfer`, which T5.5 adds; plus `FileLog? Log`), `ApprovalGuards.All`, `EndpointRegistry.MapAll`, `CoreEndpoints.Map`, `StateView.Build(WebState)` + `StateView.Extenders` (T2.5 sets `drift`, T5.5 sets `transfer`), `ApiResults` (`Json`, `Ok`, `Error`, `ReadAsync<T>`, `ReadTextAsync`, `WriteAsync`) and `ApiException(status, code, message)` for every endpoint group, `TokenGuard` (`Header`, `HostAllowed`, `TokenMatches`, `InvokeAsync`), `FileLog(path).Write(level, message)`; internal `ServerControl.SpawnOverrides` (test seam keyed by workspace root), `ServerControl.WindowsQuote/PosixQuote`.

Normative details:
- Kestrel listens on `IPAddress.Loopback` only; `port 0` → a free port is picked with a `TcpListener` first. Logging providers are cleared; `FileLog` writes start/stop and unhandled errors to `.dbmigrate/server.log`. `ShutdownTimeout` 5 s.
- Middleware order: error boundary (`ApiException` → its status; `WorkflowException` → 409 `{error:"conflict",message,details}`; others → 500 `{error:"internal"}` + log) → host/token guard (403 `forbidden_host` for any Host other than `127.0.0.1:<port>`/`localhost:<port>`; 401 `unauthorized` for `/api/*` without `X-Dbm-Token`, `?t=` accepted for GET only) → default files → static files (`Cache-Control: no-store`) → endpoints.
- `server.json` is the JSON-serialized `ServerInfo`, exactly `{port,pid,token,startedAt}` (`BaseUrl`/`UiUrl` are `[JsonIgnore]`); it is written atomically on `ApplicationStarted` and deleted on stop only if it still describes this process. `RunAsync` returns at once if `server.json` points to another live server.
- `JobRunner.RunLoopAsync` and `EventPump.RunAsync` run as background tasks for the server's lifetime; services are disposed after both stop. The server's `DbmServices.Sink` is the `Broadcaster`: persisted events go to the `event` table and reach browsers through the pump (so events written by other processes, e.g. `dbm apply`, are delivered too); `persist:false` events go straight to SSE clients.
- SSE: `text/event-stream`, first line `retry: 2000`, each event `id: <n>` / `event: <type>` / `data: <json>`; `Last-Event-ID` header (or `?lastEventId=`) replays the backlog (≤ 1000) and live duplicates are skipped by id; `: ping` every 15 s; ends on client abort or `ApplicationStopping`.
- `/api/agent/await?timeout=<s>`: inside `Presence.Enter()`, loop { take the broadcaster's next signal, `a = Workflow.Next()`; return `a` if `a.Action != "await"` or the timeout passed; 503 `{error:"stopping"}` when the server is stopping; wait for the signal or 1 s }. `agent_presence {online}` is published live-only on 0↔1 transitions. `agentOnline` = open awaits > 0 or `project.agent_seen_at` within 120 s.
- Connections: `/test` probes with a 15 s timeout and answers 200 `{ok:true,meta}` or 200 `{ok:false,error}`; save answers 409 `locked` unless Transfer is `pending`, 400 `connection_failed` when the probe fails, else encrypts + saves, publishes `state_changed` for setup and calls `OnConnectionsSaved`. Every error text passes `Redactor.Scrub(…, SecretsOf(cs))`.
- Artifact bodies (`GET /api/artifact/{phase}` → `current`, and `GET /api/artifact/{phase}/{version}`) are `{version, author, summary, createdAt, payload}` with `payload` as parsed JSON.
- Approve runs `ApprovalGuards.All` first (first non-null → 409 `{error:"guard",message}`), then `Workflow.Approve` (`WorkflowException` → 409 `{error:"blocked",message,details}`). `POST /api/edit/{phase}` always answers 200 with the `ApplyResult` (400 `invalid_patch` for malformed JSON or a phase mismatch). `/api/shutdown` stops the host after the response is sent.
- Detached spawn (critical, verified by the T1.8 integration test): the child must NOT inherit the caller's stdout/stderr pipes, otherwise Claude Code's Bash tool (which reads a command's output until EOF) hangs until the server exits. Windows: `ProcessStartInfo { UseShellExecute = true, WindowStyle = Hidden, WorkingDirectory = ws.Root }` (ShellExecute creates the process without handle inheritance). macOS/Linux: `/bin/sh -c 'nohup "<file>" <args> </dev/null >/dev/null 2>&1 &'`. The spawned command line is `<SelfCommand> serve --workspace <root> --detached`; `EnsureRunningAsync` then waits up to 20 s for a live `server.json`.

- [ ] **Step 1: Write the test support and the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Support/WebTestServer.cs` (runs `WebHost` in-process on a free port; `Services` is the server's own instance):

```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Web;

namespace Dbm.Tests.Support;

/// <summary>Runs WebHost in-process on a free loopback port; HttpClient pre-configured with the token.</summary>
public sealed class WebTestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;

    private WebTestServer(Workspace ws, ServerInfo info, DbmServices services, CancellationTokenSource cts, Task run)
    {
        Ws = ws;
        Info = info;
        Services = services;
        _cts = cts;
        Completion = run;
        Client = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        Client.DefaultRequestHeaders.Add(TokenGuard.Header, info.Token);
    }

    public Workspace Ws { get; }
    public ServerInfo Info { get; }

    /// <summary>The server's own services instance (events published here reach SSE clients).</summary>
    public DbmServices Services { get; }

    public HttpClient Client { get; }
    public Task Completion { get; }

    public static async Task<WebTestServer> StartAsync(Workspace ws, Func<Workspace, DbmServices>? factory = null)
    {
        factory ??= FakeServices.Factory();
        ws.EnsureCreated();
        var cts = new CancellationTokenSource();
        DbmServices? services = null;
        var started = new TaskCompletionSource<ServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(() => WebHost.RunAsync(ws, 0, cts.Token, w => services = factory(w), info => started.TrySetResult(info)));
        var first = await Task.WhenAny(started.Task, run, Task.Delay(TimeSpan.FromSeconds(30)));
        if (first != started.Task)
        {
            cts.Cancel();
            if (run.IsFaulted) await run;
            throw new TimeoutException("test server did not start");
        }
        return new WebTestServer(ws, await started.Task, services!, cts, run);
    }

    public HttpClient Anonymous() => new() { BaseAddress = new Uri(Info.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

    public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    public async Task<JsonNode> GetJsonAsync(string path) => JsonNode.Parse(await Client.GetStringAsync(path))!;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception)
        {
            // already stopped (e.g. /api/shutdown) or failed: nothing to clean up
        }
        Client.Dispose();
        _cts.Dispose();
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Web/TokenGuardTests.cs`:

```csharp
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class TokenGuardTests
{
    [Theory]
    [InlineData("127.0.0.1:5000", true)]
    [InlineData("localhost:5000", true)]
    [InlineData("LOCALHOST:5000", true)]
    [InlineData("127.0.0.1:5001", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("evil.example:5000", false)]
    [InlineData("", false)]
    public void Only_loopback_hosts_on_our_port_are_allowed(string host, bool allowed)
    {
        Assert.Equal(allowed, TokenGuard.HostAllowed(host, 5000));
    }

    [Theory]
    [InlineData("tok", "", true)]
    [InlineData("", "tok", true)]
    [InlineData("bad", "tok", false)]
    [InlineData("", "", false)]
    [InlineData("tok2", "", false)]
    public void Token_comes_from_the_header_or_the_query(string header, string query, bool ok)
    {
        Assert.Equal(ok, TokenGuard.TokenMatches(header, query, "tok"));
    }

    [Fact]
    public void Server_info_urls()
    {
        var info = new ServerInfo(5123, 42, "abc", DateTimeOffset.UnixEpoch);

        Assert.Equal("http://127.0.0.1:5123", info.BaseUrl);
        Assert.Equal("http://127.0.0.1:5123/?t=abc", info.UiUrl);
    }

    [Fact]
    public void Self_command_runs_the_engine_dll_with_dotnet_when_hosted_elsewhere()
    {
        var (file, prefix) = SelfCommand.Get();

        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(file), ignoreCase: true);
        Assert.EndsWith("Dbm.dll", Assert.Single(prefix));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Web/ServerControlQuoteTests.cs`:

```csharp
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class ServerControlQuoteTests
{
    [Theory]
    [InlineData(@"C:\Work\proj", @"C:\Work\proj")]
    [InlineData(@"C:\My Projects\shop", "\"C:\\My Projects\\shop\"")]
    [InlineData(@"C:\My Projects\", "\"C:\\My Projects\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Windows_arguments_follow_command_line_to_argv_rules(string arg, string expected)
    {
        Assert.Equal(expected, ServerControl.WindowsQuote(arg));
    }

    [Theory]
    [InlineData("/home/a b/proj", "'/home/a b/proj'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    public void Posix_arguments_are_single_quoted(string arg, string expected)
    {
        Assert.Equal(expected, ServerControl.PosixQuote(arg));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Web/WebHostTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Web;

public class WebHostTests
{
    /// <summary>Analysis goes straight to awaiting_review (v0) so review endpoints can be exercised without an agent.</summary>
    private static FakeModules ReviewableModules()
    {
        var modules = new FakeModules();
        modules.Analysis.NeedsAgentResult = false;
        return modules;
    }

    private static async Task<WebTestServer> StartInReviewAsync(TestWorkspace tw, FakeModules modules)
    {
        var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(modules));
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.AwaitingReview);
        return server;
    }

    [Fact]
    public async Task Server_json_is_written_while_running_and_removed_on_shutdown()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var info = ServerControl.ReadInfo(tw.Ws)!;
        Assert.Equal(server.Info, info);
        Assert.True(await ServerControl.IsAliveAsync(info));

        var (status, _) = await server.SendAsync(HttpMethod.Post, "/api/shutdown");
        Assert.Equal(HttpStatusCode.OK, status);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(tw.Ws.ServerJsonPath));
    }

    [Fact]
    public async Task Api_requires_the_token_and_a_loopback_host()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        using var anonymous = server.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/state?t={server.Info.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/pause?t={server.Info.Token}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/api/health")).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        foreign.Headers.Host = $"evil.example:{server.Info.Port}";
        foreign.Headers.Add(TokenGuard.Header, server.Info.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task State_view_has_the_documented_shape()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var state = await server.GetJsonAsync("/api/state");

        Assert.Equal("test-project", state["project"]!["name"]!.GetValue<string>());
        Assert.False(state["project"]!["paused"]!.GetValue<bool>());
        Assert.False(state["project"]!["agentOnline"]!.GetValue<bool>());
        Assert.Equal(8, state["phases"]!.AsArray().Count);
        Assert.Equal("awaiting_review", state["phases"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("setup", state["next"]!["reason"]!.GetValue<string>());
        Assert.False(state["connections"]!["src"]!["saved"]!.GetValue<bool>());
        Assert.False(state["drift"]!["tgt"]!.GetValue<bool>());
        Assert.True(state.AsObject().ContainsKey("transfer"));
        Assert.Null(state["transfer"]);
    }

    [Fact]
    public async Task Saved_connections_are_described_without_secrets()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        server.Services.Connections.Save(Side.Src, "Server=src-host;Database=Legacy;User ID=etl;Password=TopSecret99", FakeServices.Meta("src-host", "Legacy"));

        var text = await server.Client.GetStringAsync("/api/state");

        Assert.DoesNotContain("TopSecret99", text);
        var src = JsonNode.Parse(text)!["connections"]!["src"]!;
        Assert.Equal("server=src-host; database=Legacy; auth=sql; user=etl", src["describe"]!.GetValue<string>());
        Assert.Equal("Legacy", src["meta"]!["database"]!.GetValue<string>());
    }

    [Fact]
    public async Task Artifacts_are_served_with_versions_and_payload()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var artifact = await server.GetJsonAsync("/api/artifact/analysis");
        var v0 = await server.GetJsonAsync("/api/artifact/analysis/0");
        var (missing, missingBody) = await server.SendAsync(HttpMethod.Get, "/api/artifact/analysis/5");
        var (unknown, unknownBody) = await server.SendAsync(HttpMethod.Get, "/api/artifact/bogus");

        Assert.Equal("analysis", artifact["phase"]!.GetValue<string>());
        Assert.Equal(0, artifact["versions"]![0]!["version"]!.GetValue<int>());
        Assert.Equal("analyze draft", artifact["current"]!["payload"]!["summary"]!.GetValue<string>());
        Assert.Equal("script", v0["author"]!.GetValue<string>());
        Assert.NotNull(v0["createdAt"]);
        Assert.NotNull(artifact["current"]!["createdAt"]);
        Assert.Equal((HttpStatusCode.NotFound, "not_found"), (missing, missingBody!["error"]!.GetValue<string>()));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_phase"), (unknown, unknownBody!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Feedback_can_be_added_listed_deleted_and_submitted()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var (created, draft) = await server.SendAsync(HttpMethod.Post, "/api/feedback/analysis", new { anchor = "table:src:dbo.CUST", text = "Explain CUST" });
        Assert.Equal(HttpStatusCode.OK, created);
        Assert.Equal("draft", draft!["status"]!.GetValue<string>());
        Assert.Equal(0, draft["version"]!.GetValue<int>());
        Assert.Single((await server.GetJsonAsync("/api/feedback/analysis")).AsArray());

        var id = draft["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Delete, $"/api/feedback/{id}")).Status);
        Assert.Empty((await server.GetJsonAsync("/api/feedback/analysis")).AsArray());

        var (_, second) = await server.SendAsync(HttpMethod.Post, "/api/feedback/analysis", new { text = "General remark" });
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/request-changes")).Status);
        Assert.Equal(PhaseStatus.Reworking, server.Services.Phases.Get(PhaseName.Analysis).Status);
        var (notDraft, body) = await server.SendAsync(HttpMethod.Delete, $"/api/feedback/{second!["id"]!.GetValue<long>()}");
        Assert.Equal((HttpStatusCode.Conflict, "not_draft"), (notDraft, body!["error"]!.GetValue<string>()));
        var (empty, _) = await server.SendAsync(HttpMethod.Post, "/api/feedback/analysis", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, empty);
    }

    [Fact]
    public async Task Request_changes_without_feedback_is_a_conflict()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/request-changes");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approve_blocked_returns_409_with_details()
    {
        using var tw = new TestWorkspace();
        var modules = ReviewableModules();
        modules.Analysis.BlockersFn = _ => ["2 critical findings have no decision"];
        await using var server = await StartInReviewAsync(tw, modules);

        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/approve");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("blocked", body!["error"]!.GetValue<string>());
        Assert.Equal("2 critical findings have no decision", body["details"]![0]!.GetValue<string>());
        Assert.Equal(PhaseStatus.AwaitingReview, server.Services.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Approval_guards_run_before_approval()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());
        Func<WebState, PhaseName, CancellationToken, Task<string?>> guard = (st, _, _) =>
            Task.FromResult(st.Services.Ws.Root == tw.Ws.Root ? "Source schema changed since discovery." : null);
        ApprovalGuards.All.Add(guard);
        try
        {
            var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/approve");

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal(("guard", "Source schema changed since discovery."), (body!["error"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        }
        finally
        {
            ApprovalGuards.All.Remove(guard);
        }

        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/approve")).Status);
        Assert.Equal(PhaseStatus.Approved, server.Services.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Edit_endpoint_creates_a_human_version()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());

        var (status, result) = await server.SendAsync(HttpMethod.Post, "/api/edit/analysis", new
        {
            phase = "analysis",
            baseVersion = 0,
            ops = new[] { new { op = "replace", path = "/summary", value = "edited by me" } },
        });
        var (wrongPhase, _) = await server.SendAsync(HttpMethod.Post, "/api/edit/analysis", new { phase = "mapping", baseVersion = 0 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal(1, result["version"]!.GetValue<int>());
        var artifact = await server.GetJsonAsync("/api/artifact/analysis");
        Assert.Equal("human", artifact["current"]!["author"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, wrongPhase);
    }

    [Fact]
    public async Task Pause_resume_and_retry_endpoints()
    {
        using var tw = new TestWorkspace();
        var failing = new FakeJobHandler("analyze", _ => throw new InvalidOperationException("analyzer crashed"));
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(handlers: [new FakeJobHandler("discover"), failing]));
        var s = server.Services;

        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/pause")).Status);
        Assert.True(s.Project.Get().Paused);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/resume")).Status);
        Assert.False(s.Project.Get().Paused);

        FakeServices.SaveConnections(s);
        await Wait.UntilAsync(() => s.Workflow.Peek().Reason == "job_failed");
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/phase/analysis/retry")).Status);
        await Wait.UntilAsync(() => failing.Runs >= 2);
        var (conflict, _) = await server.SendAsync(HttpMethod.Post, "/api/phase/mapping/retry");
        Assert.Equal(HttpStatusCode.Conflict, conflict);
    }

    [Fact]
    public async Task Connections_are_locked_after_the_transfer_started_and_validated()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var (badSide, _) = await server.SendAsync(HttpMethod.Post, "/api/connections/xyz", new { connectionString = "Server=a" });
        var (noBody, _) = await server.SendAsync(HttpMethod.Post, "/api/connections/src/test", new { connectionString = "" });
        server.Services.Phases.SetStatus(PhaseName.Transfer, PhaseStatus.Running);
        var (locked, body) = await server.SendAsync(HttpMethod.Post, "/api/connections/src", new { connectionString = "Server=a" });

        Assert.Equal(HttpStatusCode.NotFound, badSide);
        Assert.Equal(HttpStatusCode.BadRequest, noBody);
        Assert.Equal((HttpStatusCode.Conflict, "locked"), (locked, body!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Sse_streams_persisted_events_and_replays_after_last_event_id()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        using var http = new HttpClient { BaseAddress = new Uri(server.Info.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

        using var live = await http.GetAsync($"/api/events?t={server.Info.Token}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", live.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await live.Content.ReadAsStreamAsync());
        Assert.Equal("retry: 2000", await reader.ReadLineAsync());

        server.Services.Workflow.SetPaused(true);
        var lines = await ReadUntilAsync(reader, "event: paused");
        Assert.StartsWith("id: ", lines[^3]);
        Assert.Equal("data: {}", lines[^1]);

        using var replay = new HttpRequestMessage(HttpMethod.Get, $"/api/events?t={server.Info.Token}");
        replay.Headers.Add("Last-Event-ID", "0");
        using var replayed = await http.SendAsync(replay, HttpCompletionOption.ResponseHeadersRead);
        using var replayReader = new StreamReader(await replayed.Content.ReadAsStreamAsync());
        Assert.Contains("event: paused", await ReadUntilAsync(replayReader, "event: paused"));
    }

    [Fact]
    public async Task Await_returns_once_the_next_action_changes_and_marks_the_agent_online()
    {
        using var tw = new TestWorkspace();
        await using var server = await StartInReviewAsync(tw, ReviewableModules());
        var s = server.Services;

        var pending = server.Client.GetStringAsync("/api/agent/await");
        await Wait.UntilAsync(async () => (await server.GetJsonAsync("/api/state"))["project"]!["agentOnline"]!.GetValue<bool>());
        Assert.False(pending.IsCompleted);

        s.Feedback.Add(PhaseName.Analysis, 0, null, "More detail please");
        s.Workflow.RequestChanges(PhaseName.Analysis);
        var action = JsonNode.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(10)))!;

        Assert.Equal("agent", action["action"]!.GetValue<string>());
        Assert.Equal("rework", action["mode"]!.GetValue<string>());
        Assert.True(File.Exists(action["packet"]!.GetValue<string>()));
        await Wait.UntilAsync(async () => !(await server.GetJsonAsync("/api/state"))["project"]!["agentOnline"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Await_with_a_timeout_returns_the_current_await_action()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);

        var action = await server.GetJsonAsync("/api/agent/await?timeout=1");

        Assert.Equal(("await", "setup"), (action["action"]!.GetValue<string>(), action["reason"]!.GetValue<string>()));
    }

    private static async Task<List<string>> ReadUntilAsync(StreamReader reader, string wanted)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
            lines.Add(line);
            if (line != wanted) continue;
            lines.Add(await reader.ReadLineAsync(cts.Token) ?? "");
            return lines;
        }
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/Web/ConnectionEndpointTests.cs`:

```csharp
using System.Net;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Integration.Web;

[Trait("Category", "Integration")]
public class ConnectionEndpointTests
{
    [Fact]
    public async Task Test_and_save_probe_the_real_server_and_start_discovery()
    {
        await using var src = await TempDatabase.CreateAsync();
        await using var tgt = await TempDatabase.CreateAsync();
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws, FakeServices.Factory(handlers: []));

        var (testStatus, test) = await server.SendAsync(HttpMethod.Post, "/api/connections/src/test", new { connectionString = src.ConnectionString });
        Assert.Equal(HttpStatusCode.OK, testStatus);
        Assert.True(test!["ok"]!.GetValue<bool>());
        Assert.Equal(src.Name, test["meta"]!["database"]!.GetValue<string>());
        Assert.False(server.Services.Connections.Has(Side.Src));

        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/connections/src", new { connectionString = src.ConnectionString })).Status);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/api/connections/tgt", new { connectionString = tgt.ConnectionString })).Status);

        var state = await server.GetJsonAsync("/api/state");
        Assert.Contains($"database={src.Name}; auth=integrated", state["connections"]!["src"]!["describe"]!.GetValue<string>());
        Assert.Equal(tgt.Name, state["connections"]!["tgt"]!["meta"]!["database"]!.GetValue<string>());
        Assert.Equal(PhaseStatus.Approved, server.Services.Phases.Get(PhaseName.Setup).Status);
        Assert.Equal(PhaseStatus.Running, server.Services.Phases.Get(PhaseName.Discovery).Status);
        Assert.Equal("discover", server.Services.Jobs.LatestFor(PhaseName.Discovery)!.Kind);
    }

    [Fact]
    public async Task Failures_are_reported_without_the_password()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        var cs = new SqlConnectionStringBuilder(SqlTestServer.ConnectionString)
        {
            InitialCatalog = "dbm_missing_" + Guid.NewGuid().ToString("N")[..6],
            IntegratedSecurity = false,
            UserID = "dbm_nobody",
            Password = "Sup3rS3cretPw!",
            ConnectTimeout = 5,
        }.ConnectionString;

        var (testStatus, test) = await server.SendAsync(HttpMethod.Post, "/api/connections/src/test", new { connectionString = cs });
        var (saveStatus, save) = await server.SendAsync(HttpMethod.Post, "/api/connections/src", new { connectionString = cs });

        Assert.Equal(HttpStatusCode.OK, testStatus);
        Assert.False(test!["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(test["error"]!.GetValue<string>()));
        Assert.DoesNotContain("Sup3rS3cretPw!", test.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, saveStatus);
        Assert.Equal("connection_failed", save!["error"]!.GetValue<string>());
        Assert.DoesNotContain("Sup3rS3cretPw!", save.ToJsonString());
        Assert.False(server.Services.Connections.Has(Side.Src));
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS0246: The type or namespace name 'ServerInfo' could not be found` (and `WebHost`, `TokenGuard`, `ServerControl`, …).

- [ ] **Step 3: Implement server discovery and control**

`plugins/db-migrate/engine/Dbm/Web/ServerInfo.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dbm.Web;

/// <summary>Contents of .dbmigrate/server.json — exactly {port, pid, token, startedAt} (written by the running server, deleted when it stops).</summary>
public sealed record ServerInfo(int Port, int Pid, string Token, DateTimeOffset StartedAt)
{
    [JsonIgnore] public string BaseUrl => $"http://127.0.0.1:{Port}";
    [JsonIgnore] public string UiUrl => $"{BaseUrl}/?t={Token}";
}
```

`plugins/db-migrate/engine/Dbm/Web/SelfCommand.cs` (when hosted by something other than `dotnet`/`Dbm` — e.g. the test runner — it runs `Dbm.dll` with the muxer from `DOTNET_HOST_PATH`):

```csharp
namespace Dbm.Web;

/// <summary>How to start this same engine again (used to spawn the detached server).</summary>
public static class SelfCommand
{
    public static (string FileName, IReadOnlyList<string> PrefixArgs) Get()
    {
        var dll = typeof(SelfCommand).Assembly.Location;
        var process = Environment.ProcessPath;
        var name = process is null ? "" : Path.GetFileNameWithoutExtension(process);

        // `dotnet Dbm.dll …` (the launchers) → same muxer, same dll.
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return (process!, [dll]);

        // Apphost (Dbm.exe / ./Dbm) → run it directly.
        if (name.Equals("Dbm", StringComparison.OrdinalIgnoreCase)) return (process!, []);

        // Hosted by something else (e.g. the test runner): run the dll with the dotnet muxer.
        var muxer = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return (string.IsNullOrEmpty(muxer) ? "dotnet" : muxer, [dll]);
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/FileLog.cs`:

```csharp
using Dbm.Core;

namespace Dbm.Web;

/// <summary>Tiny append-only server log (.dbmigrate/server.log). Never pass connection strings to it.</summary>
public sealed class FileLog(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Write(string level, string message)
    {
        try
        {
            lock (_gate) File.AppendAllText(Path, $"{Clock.NowText()} {level.ToUpperInvariant()} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // logging must never take the server down
        }
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/ServerControl.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using Dbm.Core;

namespace Dbm.Web;

/// <summary>Finds, starts (detached) and stops the per-workspace server.</summary>
public static class ServerControl
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Test seam, keyed by workspace root: replaces spawning a real process.</summary>
    internal static readonly ConcurrentDictionary<string, Func<Workspace, CancellationToken, Task>> SpawnOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    public static ServerInfo? ReadInfo(Workspace ws)
    {
        try
        {
            return File.Exists(ws.ServerJsonPath) ? Json.Deserialize<ServerInfo>(File.ReadAllText(ws.ServerJsonPath)) : null;
        }
        catch (Exception)
        {
            return null;   // half-written or corrupt: treat as not running
        }
    }

    /// <summary>GET /api/health with a 1.5 s timeout; true only if the answering process is the one in server.json.</summary>
    public static async Task<bool> IsAliveAsync(ServerInfo info, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.BaseUrl}/api/health");
            request.Headers.Add("X-Dbm-Token", info.Token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return body?["pid"]?.GetValue<int>() == info.Pid;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public static async Task<ServerInfo> EnsureRunningAsync(Workspace ws, CancellationToken ct = default)
    {
        var info = ReadInfo(ws);
        if (info is not null && await IsAliveAsync(info, ct)) return info;

        if (SpawnOverrides.TryGetValue(ws.Root, out var spawn)) await spawn(ws, ct);
        else Spawn(ws);

        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct);
            info = ReadInfo(ws);
            if (info is not null && await IsAliveAsync(info, ct)) return info;
        }
        throw new InvalidOperationException($"The dbm server did not start within {StartTimeout.TotalSeconds:0} s; see {ws.ServerLogPath}");
    }

    /// <summary>POST /api/shutdown, wait ≤ 5 s for the server to remove server.json; kill it if it does not.</summary>
    public static async Task StopAsync(Workspace ws)
    {
        var info = ReadInfo(ws);
        if (info is null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{info.BaseUrl}/api/shutdown");
            request.Headers.Add("X-Dbm-Token", info.Token);
            using var _ = await http.SendAsync(request);
        }
        catch (Exception)
        {
            // not answering: fall through to the kill below
        }

        for (var i = 0; i < 25; i++)
        {
            if (ReadInfo(ws) is not { } current || current.Pid != info.Pid) return;   // stopped cleanly (or replaced)
            await Task.Delay(200);
        }

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.Id != Environment.ProcessId && IsSameServer(process)) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // already exited
        }
        if (ReadInfo(ws) is { } stale && stale.Pid == info.Pid) File.Delete(ws.ServerJsonPath);
    }

    public static void OpenBrowser(string url)
    {
        if (Environment.GetEnvironmentVariable("DBM_NO_BROWSER") == "1" || string.IsNullOrEmpty(url)) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url)?.Dispose();
            else Process.Start("xdg-open", url)?.Dispose();
        }
        catch (Exception)
        {
            // no browser available (headless box): the URL is printed by the caller
        }
    }

    /// <summary>
    /// Starts `serve --workspace &lt;root&gt; --detached` so that it survives the calling command and — critically — does NOT
    /// inherit the caller's stdout/stderr pipes (otherwise a tool that reads the command's output until EOF, such as
    /// Claude Code's Bash tool, would hang until the server exits).
    /// </summary>
    private static void Spawn(Workspace ws)
    {
        var (file, prefix) = SelfCommand.Get();
        var args = prefix.Concat(["serve", "--workspace", ws.Root, "--detached"]).ToList();
        if (OperatingSystem.IsWindows())
        {
            // ShellExecute creates the process with bInheritHandles = FALSE and its own hidden console.
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = ws.Root,
                Arguments = string.Join(' ', args.Select(WindowsQuote)),
            };
            Process.Start(psi)?.Dispose();
        }
        else
        {
            // The shell exits at once; nohup'd child gets /dev/null for all three standard streams.
            var command = $"nohup {PosixQuote(file)} {string.Join(' ', args.Select(PosixQuote))} </dev/null >/dev/null 2>&1 &";
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = ws.Root };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            using var sh = Process.Start(psi);
            sh?.WaitForExit(5000);
        }
    }

    internal static string PosixQuote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
    internal static string WindowsQuote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static bool IsSameServer(Process process)
    {
        try
        {
            var name = process.ProcessName;
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.Equals("Dbm", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Implement the server building blocks**

`plugins/db-migrate/engine/Dbm/Web/TokenGuard.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Dbm.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Dbm.Web;

/// <summary>403 for foreign Host headers (DNS rebinding); 401 for /api/* without the project token.</summary>
public static class TokenGuard
{
    public const string Header = "X-Dbm-Token";

    public static async Task InvokeAsync(HttpContext ctx, RequestDelegate next, ServerInfo info)
    {
        if (!HostAllowed(ctx.Request.Host.Value, info.Port))
        {
            await ApiResults.WriteAsync(ctx, StatusCodes.Status403Forbidden, new { error = "forbidden_host", message = "Use http://127.0.0.1:<port>/ or http://localhost:<port>/." });
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var header = ctx.Request.Headers[Header].ToString();
            var query = HttpMethods.IsGet(ctx.Request.Method) ? ctx.Request.Query["t"].ToString() : "";
            if (!TokenMatches(header, query, info.Token))
            {
                await ApiResults.WriteAsync(ctx, StatusCodes.Status401Unauthorized, new { error = "unauthorized", message = "Missing or wrong project token." });
                return;
            }
        }
        await next(ctx);
    }

    public static bool HostAllowed(string? hostHeader, int port)
    {
        if (string.IsNullOrEmpty(hostHeader)) return false;
        var host = new HostString(hostHeader);
        return host.Port == port
               && (host.Host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Header token, or the ?t= query value (the caller passes "" for non-GET requests).</summary>
    public static bool TokenMatches(string? header, string? query, string expected)
    {
        var candidate = !string.IsNullOrEmpty(header) ? header : query;
        if (string.IsNullOrEmpty(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected));
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/Broadcaster.cs`:

```csharp
using System.Threading.Channels;
using Dbm.Core;
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>One server-sent event. Id is the event-table id (null for live-only events).</summary>
public sealed record SseMessage(long? Id, string Type, string Data);

/// <summary>
/// The server's IEventSink. Persisted events go to the event table (EventPump delivers them to SSE clients, so events
/// written by other processes — e.g. `dbm apply` — reach the browser too); persist:false events go to clients directly.
/// </summary>
public sealed class Broadcaster(EventRepo events) : IEventSink
{
    private readonly object _gate = new();
    private readonly List<Channel<SseMessage>> _clients = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(string type, object? payload = null, bool persist = true)
    {
        if (persist) events.Append(type, payload);
        else Deliver(new SseMessage(null, type, Json.Serialize(payload ?? new { })));
    }

    public int ClientCount
    {
        get
        {
            lock (_gate) return _clients.Count;
        }
    }

    public void Deliver(SseMessage message)
    {
        lock (_gate)
        {
            foreach (var client in _clients) client.Writer.TryWrite(message);
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<SseMessage>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _clients.Add(channel);
        return new Subscription(this, channel);
    }

    /// <summary>Completes on the next <see cref="Signal"/> (EventPump signals after delivering new rows).</summary>
    public Task NextSignal()
    {
        lock (_gate) return _signal.Task;
    }

    public void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    private void Remove(Channel<SseMessage> channel)
    {
        lock (_gate) _clients.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(Broadcaster owner, Channel<SseMessage> channel) : IDisposable
    {
        public ChannelReader<SseMessage> Reader => channel.Reader;
        public void Dispose() => owner.Remove(channel);
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/EventPump.cs`:

```csharp
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>Polls the event table (every 300 ms) and forwards new rows to SSE clients and await waiters.</summary>
public sealed class EventPump(EventRepo events, Broadcaster broadcaster, FileLog? log = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public async Task RunAsync(CancellationToken ct)
    {
        var lastId = events.LastId();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var rows = events.Since(lastId);
                foreach (var row in rows)
                {
                    broadcaster.Deliver(new SseMessage(row.Id, row.Type, row.PayloadJson));
                    lastId = row.Id;
                }
                if (rows.Count > 0) broadcaster.Signal();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Write("error", $"event pump: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/AgentPresence.cs`:

```csharp
using Dbm.Core;

namespace Dbm.Web;

/// <summary>Online = at least one open /api/agent/await, or the CLI touched project.agent_seen_at within 120 s.</summary>
public sealed class AgentPresence(DbmServices services, Broadcaster? broadcaster = null)
{
    public static readonly TimeSpan SeenWindow = TimeSpan.FromSeconds(120);
    private int _open;

    public bool Online
    {
        get
        {
            if (Volatile.Read(ref _open) > 0) return true;
            try
            {
                return services.Project.Get().AgentSeenAt is { } seen && Clock.Now() - seen < SeenWindow;
            }
            catch (InvalidOperationException)
            {
                return false;   // project not initialised
            }
        }
    }

    public IDisposable Enter()
    {
        if (Interlocked.Increment(ref _open) == 1) broadcaster?.Publish("agent_presence", new { online = true }, persist: false);
        return new Exit(this);
    }

    private void Leave()
    {
        if (Interlocked.Decrement(ref _open) == 0) broadcaster?.Publish("agent_presence", new { online = Online }, persist: false);
    }

    private sealed class Exit(AgentPresence owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Leave();
        }
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/ApprovalGuards.cs`:

```csharp
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>
/// Run by POST /api/phase/{phase}/approve before WorkflowEngine.Approve; the first non-null message aborts with
/// 409 {"error":"guard","message":…}. Empty in M1; T2.5 appends the drift guard.
/// </summary>
public static class ApprovalGuards
{
    public static readonly List<Func<WebState, PhaseName, CancellationToken, Task<string?>>> All = new();
}
```

`plugins/db-migrate/engine/Dbm/Web/WebState.cs`:

```csharp
using Dbm.Core;

namespace Dbm.Web;

/// <summary>One per server process, passed to every endpoint group.</summary>
public sealed class WebState
{
    public required DbmServices Services { get; init; }
    public required Broadcaster Broadcaster { get; init; }
    public required AgentPresence Presence { get; init; }
    public required ServerInfo Info { get; init; }
    public FileLog? Log { get; init; }
    // T5.5 adds: public Dbm.Core.Transfer.TransferService? Transfer { get; set; }
}
```

`plugins/db-migrate/engine/Dbm/Web/StateView.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>GET /api/state body (see C7 StateView). Later milestones fill "drift"/"transfer" through <see cref="Extenders"/>.</summary>
public static class StateView
{
    /// <summary>Each extender may replace or add top-level keys (T2.5: "drift", T5.5: "transfer").</summary>
    public static readonly List<Action<WebState, JsonObject>> Extenders = new();

    public static JsonObject Build(WebState state)
    {
        var s = state.Services;
        var project = s.Project.Get();
        var view = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["name"] = project.Name,
                ["paused"] = project.Paused,
                ["agentOnline"] = state.Presence.Online,
            },
            ["phases"] = Json.ToNode(s.Phases.All().Select(p => new
            {
                name = p.Name,
                status = p.Status,
                currentVersion = p.CurrentVersion,
                approvedVersion = p.ApprovedVersion,
            })),
            ["next"] = Json.ToNode(s.Workflow.Peek()),
            ["jobs"] = Json.ToNode(s.Jobs.Recent(20).Select(j => new
            {
                id = j.Id,
                kind = j.Kind,
                phase = j.Phase,
                status = j.Status,
                error = j.Error,
                startedAt = j.StartedAt,
                endedAt = j.EndedAt,
            })),
            ["connections"] = new JsonObject
            {
                ["src"] = Connection(s, Side.Src),
                ["tgt"] = Connection(s, Side.Tgt),
            },
            ["drift"] = new JsonObject { ["src"] = false, ["tgt"] = false },
            ["transfer"] = null,
        };
        foreach (var extend in Extenders) extend(state, view);
        return view;
    }

    private static JsonObject Connection(DbmServices s, Side side)
    {
        if (!s.Connections.Has(side)) return new JsonObject { ["saved"] = false };
        string describe;
        try
        {
            describe = Redactor.Describe(s.Connections.GetConnectionString(side)!);
        }
        catch (Exception)
        {
            describe = "saved (cannot be decrypted by this OS user — enter it again)";
        }
        var meta = s.Connections.GetMeta(side);
        return new JsonObject
        {
            ["saved"] = true,
            ["describe"] = describe,
            ["meta"] = meta is null ? null : Json.ToNode(meta),
        };
    }
}
```

- [ ] **Step 5: Implement the endpoints and the host**

`plugins/db-migrate/engine/Dbm/Web/Endpoints/ApiResults.cs` (every endpoint group should answer through these so JSON always uses `Dbm.Core.Json.Options`):

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Dbm.Web.Endpoints;

/// <summary>Thrown by endpoint code to answer {"error":Code,"message":Message} with Status.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>JSON helpers that always use Dbm.Core.Json.Options (camelCase, snake enums).</summary>
public static class ApiResults
{
    public static IResult Json(object? value, int status = StatusCodes.Status200OK) =>
        Results.Text(Dbm.Core.Json.Serialize(value), "application/json", Encoding.UTF8, status);

    public static IResult Ok() => Json(new { ok = true });

    public static IResult Error(int status, string code, string message, IReadOnlyList<string>? details = null) =>
        Json(new { error = code, message, details }, status);

    public static async Task<T> ReadAsync<T>(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "A JSON body is required.");
        try
        {
            return Dbm.Core.Json.Deserialize<T>(text);
        }
        catch (JsonException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", $"Invalid JSON body: {ex.Message}");
        }
    }

    public static async Task<string> ReadTextAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
    }

    public static Task WriteAsync(HttpContext ctx, int status, object value)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(Dbm.Core.Json.Serialize(value));
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

public static class EndpointRegistry
{
    public static void MapAll(IEndpointRouteBuilder app, WebState state)
    {
        CoreEndpoints.Map(app, state);
        // T2.8: CatalogEndpoints.Map(app, state); ExportEndpoints.Map(app, state);
        // T3.5: MappingEndpoints.Map(app, state);
        // T4.5: SqlEndpoints.Map(app, state);
        // T5.5: TransferEndpoints.Map(app, state);
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/Endpoints/CoreEndpoints.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Patching;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Dbm.Web.Endpoints;

/// <summary>M1 API: health, state, SSE, agent long-poll, pause/resume, connections, artifacts, feedback, review actions.</summary>
public static class CoreEndpoints
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    private sealed record ConnectionBody(string? ConnectionString);
    private sealed record FeedbackBody(string? Anchor, string? Text);

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        var api = app.MapGroup("/api");
        var s = state.Services;

        api.MapGet("/health", () => ApiResults.Json(new { ok = true, pid = Environment.ProcessId }));
        api.MapGet("/state", () => ApiResults.Json(StateView.Build(state)));
        api.MapGet("/events", (HttpContext http, IHostApplicationLifetime life) => StreamEventsAsync(http, life, state));
        api.MapGet("/agent/await", (HttpContext http, IHostApplicationLifetime life, int? timeout) => AwaitAsync(http, life, state, timeout));

        api.MapPost("/pause", () =>
        {
            s.Workflow.SetPaused(true);
            return ApiResults.Ok();
        });
        api.MapPost("/resume", () =>
        {
            s.Workflow.SetPaused(false);
            return ApiResults.Ok();
        });

        api.MapPost("/connections/{side}/test", async (string side, HttpContext http) =>
        {
            ParseSide(side);
            var cs = await ReadConnectionStringAsync(http);
            try
            {
                var meta = await ProbeAsync(cs, http.RequestAborted);
                return ApiResults.Json(new { ok = true, meta });
            }
            catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
            {
                return ApiResults.Json(new { ok = false, error = Redactor.Scrub(ex.Message, Redactor.SecretsOf(cs)) });
            }
        });

        api.MapPost("/connections/{side}", async (string side, HttpContext http) =>
        {
            var which = ParseSide(side);
            if (s.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Pending)
                return ApiResults.Error(StatusCodes.Status409Conflict, "locked", "Connections cannot change after the transfer has started.");
            var cs = await ReadConnectionStringAsync(http);
            ServerMeta meta;
            try
            {
                meta = await ProbeAsync(cs, http.RequestAborted);
            }
            catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "connection_failed", Redactor.Scrub(ex.Message, Redactor.SecretsOf(cs)));
            }
            s.Connections.Save(which, cs, meta);
            s.Sink.Publish("state_changed", new { phase = PhaseName.Setup, status = s.Phases.Get(PhaseName.Setup).Status });
            s.Workflow.OnConnectionsSaved();
            return ApiResults.Json(new { ok = true, meta });
        });

        api.MapGet("/artifact/{phase}", (string phase) =>
        {
            var p = ParsePhase(phase);
            var row = s.Phases.Get(p);
            var current = row.CurrentVersion is int v ? s.Artifacts.Get(p, v) : null;
            return ApiResults.Json(new { phase = p, versions = s.Artifacts.List(p), current = current is null ? null : View(current) });
        });

        api.MapGet("/artifact/{phase}/{version:int}", (string phase, int version) =>
        {
            var a = s.Artifacts.Get(ParsePhase(phase), version)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", $"{phase} v{version} does not exist.");
            return ApiResults.Json(View(a));
        });

        api.MapGet("/feedback/{phase}", (string phase) => ApiResults.Json(s.Feedback.List(ParsePhase(phase))));

        api.MapPost("/feedback/{phase}", async (string phase, HttpContext http) =>
        {
            var p = ParsePhase(phase);
            var body = await ApiResults.ReadAsync<FeedbackBody>(http.Request);
            var text = body.Text?.Trim();
            if (string.IsNullOrEmpty(text)) throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "Feedback text is required.");
            if (text.Length > 4000) throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "Feedback text is limited to 4000 characters.");
            var version = s.Phases.Get(p).CurrentVersion
                          ?? throw new ApiException(StatusCodes.Status409Conflict, "no_artifact", $"{phase} has no version to comment on yet.");
            var anchor = string.IsNullOrWhiteSpace(body.Anchor) ? null : body.Anchor.Trim();
            var row = s.Feedback.Add(p, version, anchor, text);
            s.Sink.Publish("feedback_changed", new { phase = p });
            return ApiResults.Json(row);
        });

        api.MapDelete("/feedback/{id:long}", (long id) =>
        {
            var row = s.Feedback.Get(id) ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", $"Feedback {id} does not exist.");
            if (!s.Feedback.DeleteDraft(id))
                return ApiResults.Error(StatusCodes.Status409Conflict, "not_draft", "Only draft feedback can be deleted.");
            s.Sink.Publish("feedback_changed", new { phase = row.Phase });
            return ApiResults.Ok();
        });

        api.MapPost("/phase/{phase}/request-changes", (string phase) =>
        {
            s.Workflow.RequestChanges(ParsePhase(phase));
            return ApiResults.Ok();
        });

        api.MapPost("/phase/{phase}/approve", async (string phase, HttpContext http) =>
        {
            var p = ParsePhase(phase);
            foreach (var guard in ApprovalGuards.All)
            {
                var message = await guard(state, p, http.RequestAborted);
                if (message is not null) return ApiResults.Error(StatusCodes.Status409Conflict, "guard", message);
            }
            try
            {
                s.Workflow.Approve(p);
                return ApiResults.Ok();
            }
            catch (WorkflowException ex)
            {
                return ApiResults.Error(StatusCodes.Status409Conflict, "blocked", ex.Message, ex.Details);
            }
        });

        api.MapPost("/phase/{phase}/reopen", (string phase) =>
        {
            s.Workflow.Reopen(ParsePhase(phase));
            return ApiResults.Ok();
        });

        api.MapPost("/phase/{phase}/retry", (string phase) =>
        {
            s.Workflow.RetryJob(ParsePhase(phase));
            return ApiResults.Ok();
        });

        api.MapPost("/edit/{phase}", async (string phase, HttpContext http) =>
        {
            var p = ParsePhase(phase);
            Patch patch;
            try
            {
                patch = Patch.Parse(await ApiResults.ReadTextAsync(http.Request));
            }
            catch (PatchException ex)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_patch", ex.Message);
            }
            if (!Phases.TryParse(patch.Phase, out var patchPhase) || patchPhase != p)
                return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_patch", $"patch.phase must be '{p.Text()}'.");
            return ApiResults.Json(s.Workflow.HumanEdit(patch));
        });

        api.MapPost("/shutdown", (HttpContext http, IHostApplicationLifetime life) =>
        {
            http.Response.OnCompleted(() =>
            {
                life.StopApplication();
                return Task.CompletedTask;
            });
            return ApiResults.Ok();
        });
    }

    // ------------------------------------------------------------------ SSE

    private static async Task StreamEventsAsync(HttpContext http, IHostApplicationLifetime life, WebState state)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, life.ApplicationStopping);
        var ct = linked.Token;
        using var subscription = state.Broadcaster.Subscribe();
        long sent = 0;
        try
        {
            await WriteAsync(response, "retry: 2000\n\n", ct);

            var lastEventId = http.Request.Headers["Last-Event-ID"].ToString();
            if (string.IsNullOrEmpty(lastEventId)) lastEventId = http.Request.Query["lastEventId"].ToString();
            if (long.TryParse(lastEventId, out var last))
            {
                sent = last;
                foreach (var e in state.Services.Events.Since(last, 1000))
                {
                    await WriteEventAsync(response, new SseMessage(e.Id, e.Type, e.PayloadJson), ct);
                    sent = e.Id;
                }
            }

            while (!ct.IsCancellationRequested)
            {
                using var ping = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ping.CancelAfter(PingInterval);
                bool more;
                try
                {
                    more = await subscription.Reader.WaitToReadAsync(ping.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, ": ping\n\n", ct);
                    continue;
                }
                if (!more) break;
                while (subscription.Reader.TryRead(out var message))
                {
                    if (message.Id is long id)
                    {
                        if (id <= sent) continue;   // already sent as backlog
                        sent = id;
                    }
                    await WriteEventAsync(response, message, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client went away or server stopping
        }
        catch (IOException)
        {
            // connection reset
        }
    }

    private static Task WriteEventAsync(HttpResponse response, SseMessage m, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (m.Id is long id) sb.Append("id: ").Append(id).Append('\n');
        sb.Append("event: ").Append(m.Type).Append('\n');
        sb.Append("data: ").Append(m.Data.Replace("\n", " ")).Append("\n\n");
        return WriteAsync(response, sb.ToString(), ct);
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.WriteAsync(text, ct);
        await response.Body.FlushAsync(ct);
    }

    // ------------------------------------------------------------------ agent long-poll

    private static async Task<IResult> AwaitAsync(HttpContext http, IHostApplicationLifetime life, WebState state, int? timeout)
    {
        using var presence = state.Presence.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, life.ApplicationStopping);
        DateTimeOffset? deadline = timeout is > 0 ? DateTimeOffset.UtcNow.AddSeconds(timeout.Value) : null;
        while (true)
        {
            var signal = state.Broadcaster.NextSignal();   // taken before Next() so no change can be missed
            var action = state.Services.Workflow.Next();
            if (action.Action != "await") return ApiResults.Json(action);
            if (deadline is { } d && DateTimeOffset.UtcNow >= d) return ApiResults.Json(action);
            if (life.ApplicationStopping.IsCancellationRequested)
                return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "stopping", "The server is stopping.");
            if (http.RequestAborted.IsCancellationRequested) return Results.Empty;
            await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(1), linked.Token));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static object View(ArtifactRow a) =>
        new { version = a.Version, author = a.Author, summary = a.Summary, createdAt = a.CreatedAt, payload = JsonNode.Parse(a.PayloadJson) };

    private static PhaseName ParsePhase(string text) =>
        Phases.TryParse(text, out var p) ? p : throw new ApiException(StatusCodes.Status404NotFound, "unknown_phase", $"Unknown phase '{text}'.");

    private static Side ParseSide(string text) =>
        EnumText.TryParse<Side>(text, out var side) ? side : throw new ApiException(StatusCodes.Status404NotFound, "unknown_side", "Side must be 'src' or 'tgt'.");

    private static async Task<string> ReadConnectionStringAsync(HttpContext http)
    {
        var body = await ApiResults.ReadAsync<ConnectionBody>(http.Request);
        var cs = body.ConnectionString?.Trim();
        return string.IsNullOrEmpty(cs)
            ? throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "connectionString is required.")
            : cs;
    }

    private static async Task<ServerMeta> ProbeAsync(string connectionString, CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            return await SqlConnect.ProbeAsync(connectionString, cts.Token);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            throw new TimeoutException($"The connection test timed out after {ProbeTimeout.TotalSeconds:0} s.");
        }
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/WebHost.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Workflow;
using Dbm.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dbm.Web;

public static class WebHost
{
    /// <summary>
    /// Serves wwwroot + API on 127.0.0.1:<paramref name="port"/> (0 = pick a free port) until <paramref name="ct"/> fires or
    /// POST /api/shutdown. Writes server.json when started, deletes it when stopped. Returns immediately if another live
    /// server already owns the workspace.
    /// </summary>
    /// <param name="servicesFactory">Test hook (fake modules/handlers). Default: DbmServices.Open(ws).</param>
    /// <param name="onStarted">Called once the server listens (ServeCommand prints the URL).</param>
    public static async Task RunAsync(Workspace ws, int port, CancellationToken ct,
        Func<Workspace, DbmServices>? servicesFactory = null, Action<ServerInfo>? onStarted = null)
    {
        var existing = ServerControl.ReadInfo(ws);
        if (existing is not null && existing.Pid != Environment.ProcessId && await ServerControl.IsAliveAsync(existing, ct)) return;

        Directory.CreateDirectory(ws.Dir);
        var log = new FileLog(ws.ServerLogPath);
        var services = (servicesFactory ?? (w => DbmServices.Open(w)))(ws);
        try
        {
            var info = new ServerInfo(port == 0 ? FreePort() : port, Environment.ProcessId, NewToken(), Clock.Now());
            var broadcaster = new Broadcaster(services.Events);
            services.Sink = broadcaster;
            var state = new WebState
            {
                Services = services,
                Broadcaster = broadcaster,
                Presence = new AgentPresence(services, broadcaster),
                Info = info,
                Log = log,
            };

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            });
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, info.Port));

            await using var app = builder.Build();
            app.Use((ctx, next) => ErrorBoundaryAsync(ctx, next, log));
            app.Use((ctx, next) => TokenGuard.InvokeAsync(ctx, next, info));
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-store",
            });
            EndpointRegistry.MapAll(app, state);

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                WriteInfo(ws, info);
                log.Write("info", $"server started on {info.BaseUrl} (pid {info.Pid})");
                onStarted?.Invoke(info);
            });

            using var background = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var jobs = Task.Run(() => new JobRunner(services).RunLoopAsync(background.Token));
            var pump = Task.Run(() => new EventPump(services.Events, broadcaster, log).RunAsync(background.Token));
            try
            {
                await app.StartAsync(ct);
                await app.WaitForShutdownAsync(ct);
            }
            finally
            {
                background.Cancel();
                await Quietly(jobs);
                await Quietly(pump);
                DeleteInfo(ws, info);
                log.Write("info", "server stopped");
            }
        }
        finally
        {
            services.Dispose();
        }
    }

    private static async Task ErrorBoundaryAsync(HttpContext ctx, RequestDelegate next, FileLog log)
    {
        try
        {
            await next(ctx);
        }
        catch (ApiException ex) when (!ctx.Response.HasStarted)
        {
            await ApiResults.WriteAsync(ctx, ex.Status, new { error = ex.Code, message = ex.Message });
        }
        catch (WorkflowException ex) when (!ctx.Response.HasStarted)
        {
            await ApiResults.WriteAsync(ctx, StatusCodes.Status409Conflict, new { error = "conflict", message = ex.Message, details = ex.Details });
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            log.Write("error", $"{ctx.Request.Method} {ctx.Request.Path}: {ex}");
            await ApiResults.WriteAsync(ctx, StatusCodes.Status500InternalServerError, new { error = "internal", message = ex.Message });
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void WriteInfo(Workspace ws, ServerInfo info)
    {
        var tmp = ws.ServerJsonPath + ".tmp";
        File.WriteAllText(tmp, Json.Serialize(info));
        File.Move(tmp, ws.ServerJsonPath, overwrite: true);
    }

    private static void DeleteInfo(Workspace ws, ServerInfo info)
    {
        var current = ServerControl.ReadInfo(ws);
        if (current is not null && current.Pid == info.Pid && current.Token == info.Token)
        {
            try
            {
                File.Delete(ws.ServerJsonPath);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // background loops end with cancellation
        }
    }
}
```

- [ ] **Step 6: Run the unit tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:   181` (TokenGuardTests 14, ServerControlQuoteTests 7, WebHostTests 15 added).

- [ ] **Step 7: Run the integration tests**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:     6` (SqlConnectTests 4, ConnectionEndpointTests 2).

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(web): localhost server with token/host guard, SSE, agent long-poll and core API" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.8: Core CLI commands

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/ProjectCommands.cs` (`InitCommand`, `StatusCommand`, `PauseCommand`, `ResumeCommand`)
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/ServerCommands.cs` (`ServeCommand`, `UiCommand`, `StopCommand`)
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/AgentCommands.cs` (`NextCommand`, `AwaitCommand`, `ApplyCommand`, `ArtifactCommand`, `FeedbackCommand`, `RunJobsCommand`)
- Modify: `plugins/db-migrate/engine/Dbm/Cli/CliContext.cs`, `CliApp.cs`, `CommandRegistry.cs`
- Create (test support): `plugins/db-migrate/engine/Dbm.Tests/Support/CliRunner.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ProjectCommandTests.cs`, `Unit/Cli/AgentCommandTests.cs`, `Unit/Cli/ServerCommandTests.cs`, `Integration/Web/ServerControlTests.cs` (moved here from T1.7: it needs `serve` and `ui`)

**Interfaces:**
- Consumes: everything above.
- Produces (C8): the M1 CLI surface — `init [name] [--no-browser]` → `{ok,created,url,workspace}`; `status` → `{name,paused,phase,status,version,next,url}`; `pause`/`resume` → `{ok,paused}`; `serve [--port n] [--detached]` (prints `{ok,url,pid}` when started in the foreground, `{ok,alreadyRunning:true,url}` when a server already runs); `ui [--no-browser]` → `{ok,url}`; `stop` → `{ok,stopped}`; `next` → `NextAction`; `await [--timeout s]` → `NextAction` (or `{"action":"stop","reason":"server_stopped",…}` when the server was stopped cleanly); `apply <patch.json> [--dry-run]` → `ApplyResult` or exit 1 `{ok:false,error:"patch_rejected",message,errors,warnings}`; `artifact <phase> [--version n] [--path /ptr]` → `{phase,version,author,summary,payload}` or `{phase,version,path,value}`; `feedback <phase> [--status s]` → `[FeedbackRow]`; `run-jobs` → `{ok,ran}`. Additions: `CliContext.ServicesFactory`, `RequireProject()`, `OpenProject()`, `OpenServices(ws)`; internal `CliApp.RunAsync(argv, stdout, stderr, servicesFactory)`; `AwaitCommand.ServerStopped`, `AwaitCommand.MaxReconnects` (3). Errors: `no_project` (no `.dbmigrate/state.db` in the resolved workspace), `usage`, `not_found`, `invalid_patch`, `server_error`, `server_unreachable`.

Behaviour: `next` and `await` start the server if needed (`EnsureRunningAsync`) and touch `project.agent_seen_at`; `await` uses an infinite HTTP timeout, returns `server_stopped` on a 503 or when `server.json` disappears after a lost connection (clean stop — never respawn), and otherwise reconnects (spawning a new server) at most 3 times. `apply` does not need the server.

- [ ] **Step 1: Write the test support and the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Support/CliRunner.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Cli;
using Dbm.Core;

namespace Dbm.Tests.Support;

public sealed record CliResult(int Exit, string Out, string Err)
{
    public JsonNode Json => JsonNode.Parse(Out) ?? throw new InvalidOperationException("empty output");
}

/// <summary>Runs CliApp in-process against a workspace (always passes --workspace), capturing stdout/stderr.</summary>
public static class CliRunner
{
    public static async Task<CliResult> RunAsync(Workspace ws, Func<Workspace, DbmServices>? factory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync([.. args, "--workspace", ws.Root], stdout, stderr, factory);
        return new CliResult(exit, stdout.ToString().Trim(), stderr.ToString());
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ProjectCommandTests.cs` (the `init` test replaces process spawning with an in-process server through `ServerControl.SpawnOverrides`):

```csharp
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class ProjectCommandTests
{
    [Fact]
    public async Task Init_creates_the_project_starts_the_server_and_prints_the_url()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory(initProject: false);
        WebTestServer? server = null;
        ServerControl.SpawnOverrides[tw.Ws.Root] = async (ws, _) => server = await WebTestServer.StartAsync(ws, factory);
        try
        {
            var first = await CliRunner.RunAsync(tw.Ws, factory, "init", "shop", "--no-browser");
            var second = await CliRunner.RunAsync(tw.Ws, factory, "init");

            Assert.Equal(0, first.Exit);
            Assert.True(first.Json["ok"]!.GetValue<bool>());
            Assert.True(first.Json["created"]!.GetValue<bool>());
            Assert.Equal(server!.Info.UiUrl, first.Json["url"]!.GetValue<string>());
            Assert.Equal(tw.Ws.Root, first.Json["workspace"]!.GetValue<string>());
            Assert.True(File.Exists(Path.Combine(tw.Ws.Dir, ".gitignore")));
            Assert.Equal("shop", server.Services.Project.Get().Name);
            Assert.False(second.Json["created"]!.GetValue<bool>());
        }
        finally
        {
            ServerControl.SpawnOverrides.TryRemove(tw.Ws.Root, out _);
            if (server is not null) await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Status_prints_the_current_phase_and_next_action()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }

        var r = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "status");

        Assert.Equal(0, r.Exit);
        Assert.Equal("test-project", r.Json["name"]!.GetValue<string>());
        Assert.Equal("setup", r.Json["phase"]!.GetValue<string>());
        Assert.Equal("awaiting_review", r.Json["status"]!.GetValue<string>());
        Assert.Equal("await", r.Json["next"]!["action"]!.GetValue<string>());
        Assert.Equal("", r.Json["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pause_and_resume_toggle_the_project()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        var paused = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "pause");
        Assert.True(s.Project.Get().Paused);
        var resumed = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "resume");

        Assert.True(paused.Json["paused"]!.GetValue<bool>());
        Assert.False(resumed.Json["paused"]!.GetValue<bool>());
        Assert.False(s.Project.Get().Paused);
        Assert.Contains(s.Events.Since(0), e => e.Type == "resumed");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("next")]
    [InlineData("pause")]
    [InlineData("apply", "x.json")]
    public async Task Project_commands_fail_with_no_project_in_an_empty_folder(params string[] args)
    {
        using var tw = new TestWorkspace();
        File.WriteAllText(Path.Combine(tw.Root, "x.json"), "{\"phase\":\"analysis\",\"baseVersion\":0}");

        var r = await CliRunner.RunAsync(tw.Ws, null, args.Select(a => a == "x.json" ? Path.Combine(tw.Root, a) : a).ToArray());

        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", r.Json["error"]!.GetValue<string>());
        Assert.False(File.Exists(tw.Ws.StateDbPath));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/AgentCommandTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Cli;

public class AgentCommandTests
{
    private static string WritePatch(TestWorkspace tw, string json)
    {
        var path = Path.Combine(tw.Root, $"patch-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Next_prints_the_agent_action_and_touches_the_agent()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.Drafting);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "next");

        Assert.Equal(0, r.Exit);
        Assert.Equal("agent", r.Json["action"]!.GetValue<string>());
        Assert.Equal("schema-analyst", r.Json["agent"]!.GetValue<string>());
        Assert.True(File.Exists(r.Json["packet"]!.GetValue<string>()));
        Assert.NotNull(server.Services.Project.Get().AgentSeenAt);
    }

    [Fact]
    public async Task Apply_accepts_a_valid_patch_and_rejects_the_same_patch_twice()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.DriveToAnalysisDraft(s);
        var patch = WritePatch(tw, """{"phase":"analysis","baseVersion":0,"ops":[{"op":"replace","path":"/summary","value":"v1"}],"responses":[]}""");

        var dry = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", patch, "--dry-run");
        var ok = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", patch);
        var again = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", patch);

        Assert.Equal(0, dry.Exit);
        Assert.Null(dry.Json["version"]);
        Assert.Equal(0, ok.Exit);
        Assert.Equal(1, ok.Json["version"]!.GetValue<int>());
        Assert.Equal(1, again.Exit);
        Assert.Equal("patch_rejected", again.Json["error"]!.GetValue<string>());
        Assert.Contains("awaiting_review", again.Json["errors"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_reports_missing_and_malformed_files()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }

        var missing = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", Path.Combine(tw.Root, "nope.json"));
        var malformed = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply", WritePatch(tw, "{oops"));
        var usage = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "apply");

        Assert.Equal("not_found", missing.Json["error"]!.GetValue<string>());
        Assert.Equal("invalid_patch", malformed.Json["error"]!.GetValue<string>());
        Assert.Equal("usage", usage.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Artifact_prints_the_payload_or_one_slice()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.DriveToAnalysisDraft(s);
        var factory = FakeServices.Factory();

        var whole = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis");
        var slice = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--path", "/items/b");
        var missingVersion = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--version", "9");
        var missingPath = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "analysis", "--path", "/nope");
        var badPhase = await CliRunner.RunAsync(tw.Ws, factory, "artifact", "bogus");

        Assert.Equal("script draft", whole.Json["payload"]!["summary"]!.GetValue<string>());
        Assert.Equal("script", whole.Json["author"]!.GetValue<string>());
        Assert.Equal(2, slice.Json["value"]!.GetValue<int>());
        Assert.Equal("/items/b", slice.Json["path"]!.GetValue<string>());
        Assert.Equal("not_found", missingVersion.Json["error"]!.GetValue<string>());
        Assert.Equal("not_found", missingPath.Json["error"]!.GetValue<string>());
        Assert.Equal("usage", badPhase.Json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Feedback_lists_items_optionally_by_status()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws))
        {
            s.Feedback.Add(PhaseName.Analysis, 0, "narrative", "Shorter please");
            s.Feedback.Add(PhaseName.Analysis, 0, null, "Another");
            s.Feedback.SubmitDrafts(PhaseName.Analysis);
            s.Feedback.Add(PhaseName.Analysis, 0, null, "Still a draft");
        }

        var all = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "feedback", "analysis");
        var open = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "feedback", "analysis", "--status", "open");

        Assert.Equal(3, all.Json.AsArray().Count);
        Assert.Equal(2, open.Json.AsArray().Count);
        Assert.Equal("narrative", open.Json[0]!["anchor"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_jobs_runs_everything_queued()
    {
        using var tw = new TestWorkspace();
        using (var s = FakeServices.Open(tw.Ws)) FakeServices.SaveConnections(s);

        var r = await CliRunner.RunAsync(tw.Ws, FakeServices.Factory(), "run-jobs");

        Assert.Equal(2, r.Json["ran"]!.GetValue<int>());
        using var check = FakeServices.Open(tw.Ws);
        Assert.Equal(PhaseStatus.Drafting, check.Phases.Get(PhaseName.Analysis).Status);
    }

    [Fact]
    public async Task Await_returns_the_next_action_after_a_human_change()
    {
        using var tw = new TestWorkspace();
        var modules = new FakeModules();
        modules.Analysis.NeedsAgentResult = false;
        var factory = FakeServices.Factory(modules);
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);
        FakeServices.SaveConnections(server.Services);
        await Wait.UntilAsync(() => server.Services.Phases.Get(PhaseName.Analysis).Status == PhaseStatus.AwaitingReview);

        var waiting = CliRunner.RunAsync(tw.Ws, factory, "await", "--timeout", "30");
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);
        server.Services.Feedback.Add(PhaseName.Analysis, 0, null, "Explain the heap");
        server.Services.Workflow.RequestChanges(PhaseName.Analysis);
        var r = await waiting.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, r.Exit);
        Assert.Equal(("agent", "rework"), (r.Json["action"]!.GetValue<string>(), r.Json["mode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Await_timeout_returns_the_current_await_action()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "await", "--timeout", "1");

        Assert.Equal(new NextAction("await", Reason: "setup", Phase: "setup", Url: server.Info.UiUrl),
            Dbm.Core.Json.Deserialize<NextAction>(r.Out));
    }

    [Fact]
    public async Task Await_reports_server_stopped_when_the_server_shuts_down()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var waiting = CliRunner.RunAsync(tw.Ws, factory, "await");
        await Task.Delay(500);
        await server.DisposeAsync();
        var r = await waiting.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, r.Exit);
        Assert.Equal(("stop", "server_stopped"), (r.Json["action"]!.GetValue<string>(), r.Json["reason"]!.GetValue<string>()));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/ServerCommandTests.cs`:

```csharp
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Unit.Cli;

public class ServerCommandTests
{
    [Fact]
    public async Task Stop_without_a_server_reports_nothing_stopped()
    {
        using var tw = new TestWorkspace();

        var r = await CliRunner.RunAsync(tw.Ws, null, "stop");

        Assert.Equal(0, r.Exit);
        Assert.False(r.Json["stopped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stop_shuts_down_a_running_server()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var r = await CliRunner.RunAsync(tw.Ws, factory, "stop");

        Assert.True(r.Json["stopped"]!.GetValue<bool>());
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(ServerControl.ReadInfo(tw.Ws));
    }

    [Fact]
    public async Task Ui_and_serve_reuse_the_running_server()
    {
        using var tw = new TestWorkspace();
        var factory = FakeServices.Factory();
        await using var server = await WebTestServer.StartAsync(tw.Ws, factory);

        var ui = await CliRunner.RunAsync(tw.Ws, factory, "ui", "--no-browser");
        var serve = await CliRunner.RunAsync(tw.Ws, factory, "serve");

        Assert.Equal(server.Info.UiUrl, ui.Json["url"]!.GetValue<string>());
        Assert.True(serve.Json["alreadyRunning"]!.GetValue<bool>());
        Assert.Equal(server.Info.UiUrl, serve.Json["url"]!.GetValue<string>());
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/Web/ServerControlTests.cs` — spawns real detached servers from the test output folder (`Dbm.dll` + `Dbm.runtimeconfig.json` are copied there by the project reference). The second test is the regression test for the pipe-inheritance hang:

```csharp
using System.Diagnostics;
using Dbm.Tests.Support;
using Dbm.Web;

namespace Dbm.Tests.Integration.Web;

/// <summary>Spawns a real detached `dotnet Dbm.dll serve` process from the test output folder.</summary>
[Trait("Category", "Integration")]
public class ServerControlTests
{
    [Fact]
    public async Task EnsureRunning_spawns_one_detached_server_and_StopAsync_stops_it()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }   // creates the project

        var info = await ServerControl.EnsureRunningAsync(tw.Ws);
        try
        {
            Assert.NotEqual(Environment.ProcessId, info.Pid);
            Assert.True(await ServerControl.IsAliveAsync(info));
            var again = await ServerControl.EnsureRunningAsync(tw.Ws);
            Assert.Equal(info.Pid, again.Pid);
        }
        finally
        {
            await ServerControl.StopAsync(tw.Ws);
        }

        Assert.Null(ServerControl.ReadInfo(tw.Ws));
        Assert.False(await ServerControl.IsAliveAsync(info));
        Assert.Contains("server started", File.ReadAllText(tw.Ws.ServerLogPath));
    }

    /// <summary>
    /// The Claude Code Bash tool reads a command's stdout until EOF. If the detached server inherited the CLI's stdout
    /// pipe, `dbm ui` would never "finish". This runs the CLI exactly like a tool would and requires EOF within 30 s
    /// while the server it started is still alive.
    /// </summary>
    [Fact]
    public async Task A_cli_command_that_starts_the_server_releases_its_output_pipe()
    {
        using var tw = new TestWorkspace();
        using (FakeServices.Open(tw.Ws)) { }
        var (file, prefix) = SelfCommand.Get();
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in prefix.Concat(["ui", "--no-browser", "--workspace", tw.Root])) psi.ArgumentList.Add(a);
        psi.Environment["DBM_NO_BROWSER"] = "1";

        try
        {
            using var cli = Process.Start(psi)!;
            var stdout = cli.StandardOutput.ReadToEndAsync();
            var stderr = cli.StandardError.ReadToEndAsync();
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(30));   // EOF ⇒ no inherited pipe
            await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, cli.ExitCode);
            Assert.Contains("\"url\":\"http://127.0.0.1:", output);
            var info = ServerControl.ReadInfo(tw.Ws)!;
            Assert.True(await ServerControl.IsAliveAsync(info));
        }
        finally
        {
            await ServerControl.StopAsync(tw.Ws);
        }
    }
}
```

- [ ] **Step 2: Run the tests — they must fail to compile**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: build FAILS with `error CS1501: No overload for method 'RunAsync' takes 4 arguments` (in `CliRunner.cs`).

- [ ] **Step 3: Extend `CliContext` and `CliApp`**

Replace the whole of `plugins/db-migrate/engine/Dbm/Cli/CliContext.cs` with:

```csharp
using Dbm.Core;

namespace Dbm.Cli;

public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }

    /// <summary>From the global --workspace option.</summary>
    public string? WorkspaceOverride { get; init; }

    /// <summary>Test seam: how commands open services. Null = <see cref="DbmServices.Open"/> with the real registries.</summary>
    public Func<Dbm.Core.Workspace, DbmServices>? ServicesFactory { get; init; }

    public Dbm.Core.Workspace Workspace() => Dbm.Core.Workspace.Resolve(WorkspaceOverride);

    /// <summary>The resolved workspace, or CliFailure("no_project") when it has no project yet.</summary>
    public Dbm.Core.Workspace RequireProject()
    {
        var ws = Workspace();
        if (!ws.Exists) throw new CliFailure("no_project", $"No db-migrate project in {ws.Root}. Run: dbm init");
        return ws;
    }

    public DbmServices OpenProject() => OpenServices(RequireProject());

    public DbmServices OpenServices(Dbm.Core.Workspace ws) => ServicesFactory?.Invoke(ws) ?? DbmServices.Open(ws);
}
```

Replace the whole of `plugins/db-migrate/engine/Dbm/Cli/CliApp.cs` with (the public signature is unchanged; the internal overload passes the factory into the context):

```csharp
using System.Text;
using Dbm.Core;

namespace Dbm.Cli;

public static class CliApp
{
    public static Task<int> RunAsync(string[] argv, TextWriter? stdout = null, TextWriter? stderr = null) =>
        RunAsync(argv, stdout, stderr, servicesFactory: null);

    /// <summary>Test entry point: lets CliRunner inject a services factory (fake modules/handlers).</summary>
    internal static async Task<int> RunAsync(string[] argv, TextWriter? stdout, TextWriter? stderr,
        Func<Workspace, DbmServices>? servicesFactory)
    {
        if (stdout is null) UseUtf8Console();
        var (rest, workspace) = ExtractWorkspace(argv);
        var ctx = new CliContext
        {
            Out = stdout ?? Console.Out,
            Err = stderr ?? Console.Error,
            WorkspaceOverride = workspace,
            ServicesFactory = servicesFactory,
        };

        try
        {
            if (workspace == "") throw new CliFailure("usage", "--workspace expects a directory");
            var (command, words) = Match(rest);
            if (rest.Count == 0) (command, words) = (CommandRegistry.All().First(c => c.Name == "help"), 0);
            if (command is null)
            {
                var typed = string.Join(' ', rest.TakeWhile(w => !w.StartsWith('-')).Take(2));
                return Output.Fail(ctx, "unknown_command", $"Unknown command '{typed}'. Run: dbm help");
            }
            return await command.RunAsync(Args.Parse(rest.Skip(words)), ctx);
        }
        catch (CliFailure f)
        {
            return Output.Fail(ctx, f.Code, f.Message);
        }
        catch (Exception ex)
        {
            return Output.Fail(ctx, "internal", ex.Message);
        }
    }

    /// <summary>Removes the global "--workspace dir" / "--workspace=dir" from anywhere in argv.</summary>
    internal static (List<string> Remaining, string? Workspace) ExtractWorkspace(IReadOnlyList<string> argv)
    {
        var rest = new List<string>();
        string? workspace = null;
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            if (a == "--workspace")
            {
                workspace = i + 1 < argv.Count ? argv[++i] : "";
            }
            else if (a.StartsWith("--workspace=", StringComparison.Ordinal))
            {
                workspace = a["--workspace=".Length..];
            }
            else
            {
                rest.Add(a);
            }
        }
        return (rest, workspace);
    }

    /// <summary>Longest match first: a two-word command ("sql validate") wins over a one-word one ("sql").</summary>
    internal static (ICommand? Command, int Words) Match(IReadOnlyList<string> rest) => Match(rest, CommandRegistry.All());

    internal static (ICommand? Command, int Words) Match(IReadOnlyList<string> rest, IReadOnlyList<ICommand> commands)
    {
        var words = rest.TakeWhile(w => !w.StartsWith('-')).Take(2).ToList();
        for (var n = words.Count; n >= 1; n--)
        {
            var name = string.Join(' ', words.Take(n));
            var match = commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (match, n);
        }
        return (null, 0);
    }

    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // No console attached (detached server): nothing to configure.
        }
    }
}
```

- [ ] **Step 4: Implement the commands**

`plugins/db-migrate/engine/Dbm/Cli/Commands/ProjectCommands.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm init [name] [--no-browser]` — create the project (if new), start the server, open the browser.</summary>
public sealed class InitCommand : ICommand
{
    public string Name => "init";
    public string Help => "Create (or reuse) the project in this folder, start the UI server and open the browser";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.Workspace();
        ws.EnsureCreated();
        bool created;
        using (var services = ctx.OpenServices(ws))
        {
            created = !services.Project.Exists();
            if (created) services.Project.Init(args.Positionals.FirstOrDefault() ?? new DirectoryInfo(ws.Root).Name);
        }
        var info = await ServerControl.EnsureRunningAsync(ws);
        if (!args.Flag("no-browser")) ServerControl.OpenBrowser(info.UiUrl);
        return Output.Ok(ctx, new { ok = true, created, url = info.UiUrl, workspace = ws.Root });
    }
}

/// <summary>`dbm status` — one-line project/phase status.</summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Help => "Project name, current phase and status, next action, UI URL";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        using var services = ctx.OpenServices(ws);
        var project = services.Project.Get();
        var phases = services.Phases.All();
        var current = phases.FirstOrDefault(p => p.Status != PhaseStatus.Approved) ?? phases[^1];
        var info = ServerControl.ReadInfo(ws);
        var url = info is not null && await ServerControl.IsAliveAsync(info) ? info.UiUrl : "";
        return Output.Ok(ctx, new
        {
            name = project.Name,
            paused = project.Paused,
            phase = current.Name,
            status = current.Status,
            version = current.CurrentVersion,
            next = services.Workflow.Peek(),
            url,
        });
    }
}

/// <summary>`dbm pause` — the agent stops taking new work; the UI shows a Resume banner.</summary>
public sealed class PauseCommand : ICommand
{
    public string Name => "pause";
    public string Help => "Pause the project (agent work stops after the current step)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        services.Workflow.SetPaused(true);
        return Task.FromResult(Output.Ok(ctx, new { ok = true, paused = true }));
    }
}

/// <summary>`dbm resume`.</summary>
public sealed class ResumeCommand : ICommand
{
    public string Name => "resume";
    public string Help => "Resume a paused project";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        services.Workflow.SetPaused(false);
        return Task.FromResult(Output.Ok(ctx, new { ok = true, paused = false }));
    }
}
```

`plugins/db-migrate/engine/Dbm/Cli/Commands/ServerCommands.cs` (a detached server silences the console: nobody reads its output once the spawning command exits):

```csharp
using Dbm.Core;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm serve [--port n] [--detached]` — run the web server (ServerControl spawns it with --detached).</summary>
public sealed class ServeCommand : ICommand
{
    public string Name => "serve";
    public string Help => "Run the UI/API server in the foreground (normally started detached by other commands)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        var existing = ServerControl.ReadInfo(ws);
        if (existing is not null && await ServerControl.IsAliveAsync(existing))
            return Output.Ok(ctx, new { ok = true, alreadyRunning = true, url = existing.UiUrl });

        var detached = args.Flag("detached");
        if (detached)
        {
            // Nobody reads our stdout/stderr once the spawning command has exited.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
        var output = detached ? TextWriter.Null : ctx.Out;
        await WebHost.RunAsync(ws, args.Int("port", 0), CancellationToken.None, ctx.ServicesFactory, info =>
        {
            output.WriteLine(Json.Serialize(new { ok = true, url = info.UiUrl, pid = info.Pid }));
            output.Flush();
        });
        return 0;
    }
}

/// <summary>`dbm ui [--no-browser]` — make sure the server runs and open the browser.</summary>
public sealed class UiCommand : ICommand
{
    public string Name => "ui";
    public string Help => "Start the UI server if needed and open the browser";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        var info = await ServerControl.EnsureRunningAsync(ws);
        if (!args.Flag("no-browser")) ServerControl.OpenBrowser(info.UiUrl);
        return Output.Ok(ctx, new { ok = true, url = info.UiUrl });
    }
}

/// <summary>`dbm stop` — stop the detached server of this workspace.</summary>
public sealed class StopCommand : ICommand
{
    public string Name => "stop";
    public string Help => "Stop the UI server of this workspace";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.Workspace();
        var wasRunning = ServerControl.ReadInfo(ws) is not null;
        await ServerControl.StopAsync(ws);
        return Output.Ok(ctx, new { ok = true, stopped = wasRunning });
    }
}
```

`plugins/db-migrate/engine/Dbm/Cli/Commands/AgentCommands.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>`dbm next` — the orchestrator's next action (writes the work packet for agent actions).</summary>
public sealed class NextCommand : ICommand
{
    public string Name => "next";
    public string Help => "Print the next action for the orchestrator (agent | await | stop)";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var ws = ctx.RequireProject();
        await ServerControl.EnsureRunningAsync(ws);
        using var services = ctx.OpenServices(ws);
        services.Project.TouchAgent();
        return Output.Ok(ctx, services.Workflow.Next());
    }
}

/// <summary>`dbm await [--timeout s]` — long-polls the server until the next action is not "await".</summary>
public sealed class AwaitCommand : ICommand
{
    public const int MaxReconnects = 3;

    public static readonly NextAction ServerStopped = new("stop", Reason: "server_stopped",
        Summary: "The db-migrate server was stopped. Run /db-migrate resume to continue.");

    public string Name => "await";
    public string Help => "Block until a human action changes the next step (--timeout s, 0 = forever); prints the next action";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var timeout = args.Int("timeout", 0);
        var ws = ctx.RequireProject();
        using (var services = ctx.OpenServices(ws)) services.Project.TouchAgent();

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reconnects = 0;
        while (true)
        {
            var info = await ServerControl.EnsureRunningAsync(ws);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.BaseUrl}/api/agent/await?timeout={timeout}");
                request.Headers.Add(TokenGuard.Header, info.Token);
                using var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return Output.Ok(ctx, Json.Deserialize<NextAction>(await response.Content.ReadAsStringAsync()));
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    return Output.Ok(ctx, ServerStopped);   // clean shutdown (dbm stop / UI): never respawn
                return Output.Fail(ctx, "server_error", $"await failed with HTTP {(int)response.StatusCode}");
            }
            catch (HttpRequestException)
            {
                // server died or restarted: handled below
            }
            catch (IOException)
            {
                // connection reset: handled below
            }

            // Connection lost. Wait (≤ 3 s) for the old server to remove server.json: a clean stop removes it, a crash does not.
            for (var i = 0; i < 15 && ServerControl.ReadInfo(ws) is { } still && still.Pid == info.Pid; i++) await Task.Delay(200);
            if (ServerControl.ReadInfo(ws) is null) return Output.Ok(ctx, ServerStopped);
            if (++reconnects > MaxReconnects)
                return Output.Fail(ctx, "server_unreachable", $"Lost the connection to the dbm server {MaxReconnects} times; see {ws.ServerLogPath}");
        }
    }
}

/// <summary>`dbm apply &lt;patch.json&gt; [--dry-run]` — validate an agent patch and create the next version.</summary>
public sealed class ApplyCommand : ICommand
{
    public string Name => "apply";
    public string Help => "Apply an agent patch file (--dry-run: validate only); exit 1 when rejected";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var path = args.Positionals.FirstOrDefault() ?? throw new CliFailure("usage", "usage: dbm apply <patch.json> [--dry-run]");
        if (!File.Exists(path)) throw new CliFailure("not_found", $"Patch file not found: {path}");
        Patch patch;
        try
        {
            patch = Patch.Parse(File.ReadAllText(path));
        }
        catch (PatchException ex)
        {
            throw new CliFailure("invalid_patch", ex.Message);
        }

        using var services = ctx.OpenProject();
        var result = services.Workflow.ApplyPatch(patch, args.Flag("dry-run"));
        if (result.Ok) return Task.FromResult(Output.Ok(ctx, result));
        return Task.FromResult(Output.Write(ctx, new
        {
            ok = false,
            error = "patch_rejected",
            message = string.Join("; ", result.Errors),
            errors = result.Errors,
            warnings = result.Warnings,
        }, 1));
    }
}

/// <summary>`dbm artifact &lt;phase&gt; [--version n] [--path /json/pointer]` — print an artifact or one slice of it.</summary>
public sealed class ArtifactCommand : ICommand
{
    public string Name => "artifact";
    public string Help => "Print a phase artifact (--version n, --path /json/pointer for one slice)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var phase = ParsePhaseArg(args, "usage: dbm artifact <phase> [--version n] [--path /pointer]");
        using var services = ctx.OpenProject();
        var version = args.Opt("version") is null
            ? services.Phases.Get(phase).CurrentVersion ?? throw new CliFailure("not_found", $"{phase.Text()} has no artifact yet")
            : args.Int("version", 0);
        var artifact = services.Artifacts.Get(phase, version)
                       ?? throw new CliFailure("not_found", $"{phase.Text()} v{version} does not exist");
        var payload = JsonNode.Parse(artifact.PayloadJson)!;

        var pointer = args.Opt("path");
        if (pointer is null)
            return Task.FromResult(Output.Ok(ctx, new { phase, version, author = artifact.Author, summary = artifact.Summary, payload }));

        JsonNode? value;
        try
        {
            value = JsonPatch.Resolve(payload, pointer);
        }
        catch (PatchException ex)
        {
            throw new CliFailure("usage", ex.Message);
        }
        if (value is null) throw new CliFailure("not_found", $"{pointer} does not exist in {phase.Text()} v{version}");
        return Task.FromResult(Output.Ok(ctx, new { phase, version, path = pointer, value }));
    }

    internal static PhaseName ParsePhaseArg(Args args, string usage)
    {
        var text = args.Positionals.FirstOrDefault() ?? throw new CliFailure("usage", usage);
        return Phases.TryParse(text, out var phase) ? phase : throw new CliFailure("usage", $"unknown phase '{text}'");
    }
}

/// <summary>`dbm feedback &lt;phase&gt; [--status open]` — list feedback items with responses.</summary>
public sealed class FeedbackCommand : ICommand
{
    public string Name => "feedback";
    public string Help => "List feedback items of a phase (--status draft|open|addressed|declined)";

    public Task<int> RunAsync(Args args, CliContext ctx)
    {
        var phase = ArtifactCommand.ParsePhaseArg(args, "usage: dbm feedback <phase> [--status s]");
        FeedbackStatus? status = null;
        if (args.Opt("status") is { } s)
            status = EnumText.TryParse<FeedbackStatus>(s, out var fs) ? fs : throw new CliFailure("usage", $"unknown status '{s}'");
        using var services = ctx.OpenProject();
        return Task.FromResult(Output.Ok(ctx, services.Feedback.List(phase, status)));
    }
}

/// <summary>`dbm run-jobs` — run queued jobs in this process (no server needed).</summary>
public sealed class RunJobsCommand : ICommand
{
    public string Name => "run-jobs";
    public string Help => "Run all queued jobs in this process and exit";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
        return Output.Ok(ctx, new { ok = true, ran });
    }
}
```

Replace the whole of `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` with:

```csharp
using Dbm.Cli.Commands;

namespace Dbm.Cli;

public static class CommandRegistry
{
    public static IReadOnlyList<ICommand> All() =>
    [
        // M0
        new HelpCommand(),
        new VersionCommand(),
        new DoctorCommand(),
        // M1
        new InitCommand(),
        new StatusCommand(),
        new PauseCommand(),
        new ResumeCommand(),
        new ServeCommand(),
        new UiCommand(),
        new StopCommand(),
        new NextCommand(),
        new AwaitCommand(),
        new ApplyCommand(),
        new ArtifactCommand(),
        new FeedbackCommand(),
        new RunJobsCommand(),
        // Later milestones append their commands below this line.
    ];
}
```

- [ ] **Step 5: Run the unit tests — they pass**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:   200` (ProjectCommandTests 7, AgentCommandTests 9, ServerCommandTests 3 added).

- [ ] **Step 6: Run the integration tests**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"
```

Expected: `Passed!  - Failed:     0, Passed:     8` (ServerControlTests 2 added). The command must return promptly — if it hangs after printing the summary, a detached server is holding the console pipe: find it with `Get-CimInstance Win32_Process | Where-Object CommandLine -match 'Dbm.dll serve'`, kill it, and fix `ServerControl.Spawn` before continuing.

- [ ] **Step 7: Manual check from a real shell**

Rebuild the launcher output and run the M1 commands against a scratch workspace (Git Bash):

If a publish ever fails with "file is being used by another process", a server started from `engine/dist` is still running: run `dbm stop` in its workspace first.

```bash
DBM_REBUILD=1 plugins/db-migrate/bin/dbm version
mkdir -p "$TEMP/dbm-m1" && cd "$TEMP/dbm-m1"
dbm() { "$OLDPWD/plugins/db-migrate/bin/dbm" "$@"; }
dbm init "M1 check" --no-browser
dbm status
dbm next
dbm await --timeout 2
dbm pause && dbm next && dbm resume
dbm stop
ls -a .dbmigrate
cd "$OLDPWD"
```

Expected, one JSON line per command and exit code 0 each:
- `init` returns within a few seconds (even though it started a server — the pipe-inheritance guarantee) with `{"ok":true,"created":true,"url":"http://127.0.0.1:<port>/?t=<token>","workspace":"<TEMP>\\dbm-m1"}`.
- `status` → `{"name":"M1 check","paused":false,"phase":"setup","status":"awaiting_review","next":{"action":"await","reason":"setup","phase":"setup","url":"<same url>"},"url":"<same url>"}`.
- `next` → `{"action":"await","reason":"setup","phase":"setup","url":"<same url>"}`; `await --timeout 2` prints the same after about 2–3 s.
- `pause` → `{"ok":true,"paused":true}`, `next` → `{"action":"await","reason":"paused"}`, `resume` → `{"ok":true,"paused":false}`.
- `stop` → `{"ok":true,"stopped":true}`; `.dbmigrate` then contains `.gitignore`, `exports`, `server.log`, `state.db`, `work` and no `server.json`.

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(cli): init/status/pause/resume, serve/ui/stop, next/await/apply/artifact/feedback/run-jobs" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---
### Task 1.9: UI shell, review loop UI, setup view

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/index.html`, `wwwroot/css/app.css`
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/lib/dom.js`, `js/lib/diff.js`, `js/api.js`, `js/app.js`
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/components/core.js`, `js/components/review.js`
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/views/setup.js`, `js/views/pending.js`
- Test: `plugins/db-migrate/engine/Dbm.Tests/js/dom.test.cjs`, `js/diff.test.cjs`, `Unit/Web/StaticFilesTests.cs`

**Interfaces:**
- Consumes: the C7 HTTP API (`/api/state`, `/api/events`, `/api/artifact`, `/api/feedback`, `/api/phase/*`, `/api/connections/*`, `/api/pause|resume`).
- Produces (C9): `DBM.h`, `DBM.esc`, `DBM.fmt.{num,pct,mb,dur,ts,rel}`, `DBM.mdLite`, `DBM.diff.lines`; `DBM.api.{get,post,put,del,events}` and `DBM.ApiError`; `DBM.components.{stepper,topbar,badge,kpi,table,progress,sparkline,modal,toast,drawer,emptyState,reviewBar}`; `DBM.views.setup`, `DBM.views.pending`; the `ctx` object; the full CSS token set and class vocabulary. Additions (use them in later views): `DBM.clear(el)`; `DBM.diff.collapse(ops, context)` / `DBM.diff.stats(ops)`; `DBM.api.url(path)` (GET link with token, for downloads), `DBM.api.token()`, `DBM.api.EVENT_TYPES` (push extra SSE event types here before `app.js` runs); `DBM.phaseTitle(name)`, `DBM.statusLabel(status)`, `DBM.anchorLabel(anchor, fallback)`; components `icon(name)`, `banner(kind, content, action)`, `notice(kind, content)`, `field(label, control, hint)`, `busy(button, action)`, `errorText(err)`; `DBM.review.{openFeedback, openHistory, refresh, isOpen}`; `DBM.app.{refresh, select, state}`; `ctx.artifact` is the version being shown: `{version, author, summary, createdAt, payload}` (payload = parsed JSON; `null` before the first version); `ctx` additions `phase`, `phaseRow`, `versions` (`[ArtifactMeta]`, ascending), `latestVersion` (the phase's current version number), `feedback`, `logs`, `export` (the full `DBM_EXPORT.payload` in export mode, else `null`), `setVersion(v|null)`, `openFeedback(anchor, label)`, `openHistory()`. Extra CSS tokens `--border-strong --radius-sm --shadow-lg --focus --accent-soft --ok-soft --warn-soft --err-soft --info-soft`; extra classes `.tbl-wrap .field .field-label .field-hint .input-group .notice .notice-{ok,err,warn,info} .banner .banner-{info,warn,err} .review-bar .review-summary .review-actions .fb-* .version-list .comment-btn .spinner .progress .progress-label .diff-skip .stack-sm .prose .wrap-anywhere .icon .icon-btn dl.meta .pill-{ok,off,warn}`. Severity classes: `.sev-critical .sev-high .sev-medium .sev-low .sev-info`.

Rules later views rely on:
- Script order and markers are fixed by `index.html`: lib scripts go after `js/lib/diff.js` at the `<!-- lib scripts … -->` marker (graph.js then highlight.js); view scripts go at the `<!-- view scripts … -->` marker before `js/app.js`.
- Library files (`js/lib/*`) end with `})(globalThis.DBM = globalThis.DBM || {});` so they load under Node for tests; all other files use `window.DBM`.
- View selection (`app.js pickView`): Setup → `setup`; Analysis/Mapping/SQL use `DBM.views[phase]` only while `awaiting_review`, `reworking` or `approved` with a version, else `pending`; Discovery/Ready/Transfer/Complete use their view unless `pending`/`stale`. The pending view doubles as a raw-JSON review screen for a phase whose view is not loaded yet.
- The view is re-rendered only when its inputs change (phase row, feedback, jobs of the phase, connections, pause/agent flags, `drift`, `transfer`), so inputs keep focus while unrelated events arrive; `view.onEvent(evt, ctx)` receives every SSE event (`{id, type, data}`).
- `ctx.readOnly` is true in export mode, for any status other than `awaiting_review`, and while an older version is shown. `ctx.commentable(el, anchor, label)` must be given a block/inline element (for table rows pass a cell): it adds `.commentable` + a hover comment button (and `.has-feedback` when the anchor has draft/open items).
- Export mode (C9 standalone export inlines `app.css`, `js/lib/*`, `js/components/*`, the view script and `app.js` — **not** `api.js`): nothing in those files touches `DBM.api` at load time. With `window.DBM_EXPORT = {view, title, payload}` and `payload.artifact = {version, author, summary, createdAt, payload}` (optional `payload.state`), `app.js` builds any missing shell element, renders `DBM.views[view]` read-only with `ctx.artifact = payload.artifact`, `ctx.export = payload`, `ctx.api = null`, no SSE, `ctx.commentable` a no-op, and `reviewBar` showing only version/status/author/summary/date (no actions).

- [ ] **Step 1: Write the failing JS tests**

`plugins/db-migrate/engine/Dbm.Tests/js/dom.test.cjs`:

```js
// node --test plugins/db-migrate/engine/Dbm.Tests/js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

globalThis.DBM = globalThis.DBM || {};
const file = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'dom.js');
vm.runInThisContext(fs.readFileSync(file, 'utf8'), { filename: file });
const DBM = globalThis.DBM;

test('esc escapes the five HTML-significant characters and nulls', () => {
  assert.equal(DBM.esc(`<a href="x">Tom & 'Jerry'</a>`), '&lt;a href=&quot;x&quot;&gt;Tom &amp; &#39;Jerry&#39;&lt;/a&gt;');
  assert.equal(DBM.esc(null), '');
  assert.equal(DBM.esc(42), '42');
});

test('fmt.num groups thousands and keeps two decimals', () => {
  assert.equal(DBM.fmt.num(1234567.891), '1,234,567.89');
  assert.equal(DBM.fmt.num(0), '0');
  assert.equal(DBM.fmt.num(null), '—');
});

test('fmt.pct, fmt.mb and fmt.dur', () => {
  assert.equal(DBM.fmt.pct(0.734), '73%');
  assert.equal(DBM.fmt.pct(0.734, 1), '73.4%');
  assert.equal(DBM.fmt.mb(0.42), '0.4 MB');
  assert.equal(DBM.fmt.mb(12.6), '13 MB');
  assert.equal(DBM.fmt.mb(1536), '1.5 GB');
  assert.equal(DBM.fmt.mb(3 * 1024 * 1024), '3.0 TB');
  assert.equal(DBM.fmt.dur(850), '850 ms');
  assert.equal(DBM.fmt.dur(12300), '12.3 s');
  assert.equal(DBM.fmt.dur(245000), '4m 05s');
  assert.equal(DBM.fmt.dur(3720000), '1h 02m');
});

test('fmt.ts is local "yyyy-mm-dd hh:mm" and fmt.rel is relative', () => {
  assert.match(DBM.fmt.ts('2026-09-11T10:30:00Z'), /^2026-09-1[01] \d\d:\d\d$/);
  const now = Date.parse('2026-09-11T12:00:00Z');
  assert.equal(DBM.fmt.rel('2026-09-11T11:59:30Z', now), 'just now');
  assert.equal(DBM.fmt.rel('2026-09-11T11:55:00Z', now), '5 min ago');
  assert.equal(DBM.fmt.rel('2026-09-11T09:00:00Z', now), '3 h ago');
  assert.equal(DBM.fmt.rel('2026-09-09T12:00:00Z', now), '2 d ago');
  assert.equal(DBM.fmt.rel(null), '—');
});

test('mdLite renders paragraphs, bullet lists, bold and code', () => {
  const html = DBM.mdLite('Two **critical** risks:\n- heap `dbo.AUDIT_LOG`\n* orphan rows\n\nSecond paragraph\ncontinues.');
  assert.equal(html,
    '<p>Two <strong>critical</strong> risks:</p>' +
    '<ul><li>heap <code>dbo.AUDIT_LOG</code></li><li>orphan rows</li></ul>' +
    '<p>Second paragraph continues.</p>');
});

test('mdLite escapes HTML everywhere, including inside code and bold', () => {
  const html = DBM.mdLite('<script>alert(1)</script> **<b>x</b>** `<i>y</i>`');
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('&lt;script&gt;'));
  assert.ok(html.includes('<strong>&lt;b&gt;x&lt;/b&gt;</strong>'));
  assert.ok(html.includes('<code>&lt;i&gt;y&lt;/i&gt;</code>'));
  assert.equal(DBM.mdLite(''), '');
  assert.equal(DBM.mdLite(null), '');
});
```

`plugins/db-migrate/engine/Dbm.Tests/js/diff.test.cjs`:

```js
// node --test plugins/db-migrate/engine/Dbm.Tests/js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

globalThis.DBM = globalThis.DBM || {};
const file = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'diff.js');
vm.runInThisContext(fs.readFileSync(file, 'utf8'), { filename: file });
const { lines, collapse, stats } = globalThis.DBM.diff;

const apply = (ops) => ops.filter((o) => o.op !== 'del').map((o) => o.text).join('\n');
const original = (ops) => ops.filter((o) => o.op !== 'add').map((o) => o.text).join('\n');

test('identical texts are all eq', () => {
  const ops = lines('a\nb', 'a\nb');
  assert.deepEqual(ops, [{ op: 'eq', text: 'a' }, { op: 'eq', text: 'b' }]);
});

test('a changed middle line is one del and one add', () => {
  const ops = lines('a\nb\nc', 'a\nB\nc');
  assert.deepEqual(ops.map((o) => o.op), ['eq', 'del', 'add', 'eq']);
  assert.deepEqual(stats(ops), { add: 1, del: 1 });
});

test('insertions and deletions reconstruct both sides', () => {
  const a = '{\n  "x": 1,\n  "y": 2,\n  "z": 3\n}';
  const b = '{\n  "w": 0,\n  "x": 1,\n  "z": 4\n}';
  const ops = lines(a, b);
  assert.equal(original(ops), a);
  assert.equal(apply(ops), b);
  assert.deepEqual(stats(ops), { add: 2, del: 2 });
});

test('empty inputs and CRLF are handled', () => {
  assert.deepEqual(lines('', ''), []);
  assert.deepEqual(lines('', 'a').map((o) => o.op), ['add']);
  assert.deepEqual(lines('a\r\nb', 'a\nb').map((o) => o.op), ['eq', 'eq']);
});

test('collapse keeps context around changes and counts skipped lines', () => {
  const a = Array.from({ length: 20 }, (_, i) => `line ${i}`).join('\n');
  const b = a.replace('line 10', 'LINE 10');
  const out = collapse(lines(a, b), 2);
  assert.deepEqual(out[0], { op: 'skip', count: 8 });
  assert.deepEqual(out.slice(1, 7).map((o) => o.op), ['eq', 'eq', 'del', 'add', 'eq', 'eq']);
  assert.deepEqual(out[7], { op: 'skip', count: 7 });
});
```

- [ ] **Step 2: Run them — they fail**

Node 24 treats a directory argument as a file, so always pass the glob:

```bash
node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"
```

Expected: `ℹ fail 2` with `Error: ENOENT: no such file or directory, open '…\wwwroot\js\lib\dom.js'` (and `diff.js`).

- [ ] **Step 3: Implement the two libraries**

`plugins/db-migrate/engine/Dbm/wwwroot/js/lib/dom.js`:

```js
/* DOM + formatting helpers. Classic script: attaches to globalThis.DBM (window.DBM in the browser); runs under Node for tests. */
(function (DBM) {
  'use strict';

  var ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

  function esc(value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) { return ESCAPES[c]; });
  }

  function appendChildren(el, children) {
    for (var i = 0; i < children.length; i++) {
      var c = children[i];
      if (c == null || c === false || c === true) continue;
      if (Array.isArray(c)) appendChildren(el, c);
      else if (typeof c === 'string' || typeof c === 'number') el.appendChild(document.createTextNode(String(c)));
      else el.appendChild(c);
    }
  }

  /**
   * h('div', {class: 'card', style: {'--v': 0.5}, on: {click: fn}, data: {id: 3}, html: trustedMarkup}, ...children)
   * Strings become text nodes (never parsed as HTML). null/false attributes and children are skipped.
   */
  function h(tag, attrs) {
    var el = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (key) {
        var value = attrs[key];
        if (value == null || value === false) return;
        if (key === 'class') {
          el.className = Array.isArray(value) ? value.filter(Boolean).join(' ') : value;
        } else if (key === 'style' && typeof value === 'object') {
          Object.keys(value).forEach(function (k) {
            if (k.indexOf('--') === 0) el.style.setProperty(k, String(value[k]));
            else el.style[k] = value[k];
          });
        } else if (key === 'on') {
          Object.keys(value).forEach(function (ev) { el.addEventListener(ev, value[ev]); });
        } else if (key === 'data') {
          Object.keys(value).forEach(function (k) { el.dataset[k] = value[k]; });
        } else if (key === 'html') {
          el.innerHTML = value;
        } else if (value === true) {
          el.setAttribute(key, '');
        } else {
          el.setAttribute(key, String(value));
        }
      });
    }
    appendChildren(el, Array.prototype.slice.call(arguments, 2));
    return el;
  }

  function clear(el) {
    while (el.firstChild) el.removeChild(el.firstChild);
    return el;
  }

  var numberFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });

  function pad(n) { return n < 10 ? '0' + n : String(n); }

  var fmt = {
    /** 1234567.891 → "1,234,567.89"; null → "—" */
    num: function (n) { return n == null || isNaN(n) ? '—' : numberFormat.format(Number(n)); },
    /** 0.734 → "73%"; digits = decimals */
    pct: function (v, digits) { return v == null || isNaN(v) ? '—' : (Number(v) * 100).toFixed(digits || 0) + '%'; },
    /** megabytes → "0.4 MB" | "12 MB" | "1.5 GB" | "2.3 TB" */
    mb: function (v) {
      if (v == null || isNaN(v)) return '—';
      v = Number(v);
      if (v >= 1024 * 1024) return (v / 1024 / 1024).toFixed(1) + ' TB';
      if (v >= 1024) return (v / 1024).toFixed(1) + ' GB';
      if (v >= 10) return Math.round(v) + ' MB';
      return v.toFixed(1) + ' MB';
    },
    /** milliseconds → "850 ms" | "12.3 s" | "4m 05s" | "1h 02m" */
    dur: function (ms) {
      if (ms == null || isNaN(ms)) return '—';
      ms = Math.max(0, Number(ms));
      if (ms < 1000) return Math.round(ms) + ' ms';
      var s = ms / 1000;
      if (s < 60) return s.toFixed(1) + ' s';
      var m = Math.floor(s / 60);
      if (m < 60) return m + 'm ' + pad(Math.floor(s % 60)) + 's';
      return Math.floor(m / 60) + 'h ' + pad(m % 60) + 'm';
    },
    /** ISO timestamp → local "2026-09-11 14:03" */
    ts: function (iso) {
      if (!iso) return '—';
      var d = new Date(iso);
      if (isNaN(d.getTime())) return String(iso);
      return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    },
    /** ISO timestamp → "just now" | "5 min ago" | "3 h ago" | "2 d ago" */
    rel: function (iso, now) {
      if (!iso) return '—';
      var t = new Date(iso).getTime();
      if (isNaN(t)) return String(iso);
      var s = Math.round(((now == null ? Date.now() : now) - t) / 1000);
      if (s < 45) return 'just now';
      var m = Math.round(s / 60);
      if (m < 60) return m + ' min ago';
      var hrs = Math.round(m / 60);
      if (hrs < 24) return hrs + ' h ago';
      return Math.round(hrs / 24) + ' d ago';
    },
  };

  function inline(text) {
    return text.split(/(`[^`]+`)/g).map(function (part) {
      if (part.length > 1 && part.charAt(0) === '`' && part.charAt(part.length - 1) === '`') {
        return '<code>' + esc(part.slice(1, -1)) + '</code>';
      }
      return esc(part).replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    }).join('');
  }

  /**
   * Tiny, safe Markdown subset for agent-written text: blank-line paragraphs, "-"/"*" bullet lists,
   * **bold** and `code`. Everything else is escaped. Returns an HTML string.
   */
  function mdLite(text) {
    var lines = String(text == null ? '' : text).replace(/\r\n?/g, '\n').split('\n');
    var out = [];
    var para = [];
    var list = [];
    function flushPara() {
      if (para.length) out.push('<p>' + para.map(inline).join(' ') + '</p>');
      para = [];
    }
    function flushList() {
      if (list.length) out.push('<ul>' + list.map(function (i) { return '<li>' + inline(i) + '</li>'; }).join('') + '</ul>');
      list = [];
    }
    lines.forEach(function (line) {
      var bullet = /^\s*[-*]\s+(.*)$/.exec(line);
      if (bullet) {
        flushPara();
        list.push(bullet[1]);
      } else if (!line.trim()) {
        flushPara();
        flushList();
      } else {
        flushList();
        para.push(line.trim());
      }
    });
    flushPara();
    flushList();
    return out.join('');
  }

  DBM.esc = esc;
  DBM.h = h;
  DBM.clear = clear;
  DBM.fmt = fmt;
  DBM.mdLite = mdLite;
})(globalThis.DBM = globalThis.DBM || {});
```

`plugins/db-migrate/engine/Dbm/wwwroot/js/lib/diff.js`:

```js
/* Line diff (LCS) for version comparison. Classic script; runs under Node for tests. */
(function (DBM) {
  'use strict';

  var MAX_CELLS = 4000000;   // beyond this the middle section is shown as a full replace

  function split(text) {
    if (text == null || text === '') return [];
    return String(text).replace(/\r\n?/g, '\n').split('\n');
  }

  /** lines(a, b) → [{op: 'eq'|'add'|'del', text}] turning text a into text b. */
  function lines(a, b) {
    var A = split(a);
    var B = split(b);
    var start = 0;
    while (start < A.length && start < B.length && A[start] === B[start]) start++;
    var endA = A.length;
    var endB = B.length;
    while (endA > start && endB > start && A[endA - 1] === B[endB - 1]) { endA--; endB--; }

    var ops = [];
    var i;
    for (i = 0; i < start; i++) ops.push({ op: 'eq', text: A[i] });

    var a = A.slice(start, endA);
    var b = B.slice(start, endB);
    var n = a.length;
    var m = b.length;
    if ((n + 1) * (m + 1) > MAX_CELLS) {
      a.forEach(function (t) { ops.push({ op: 'del', text: t }); });
      b.forEach(function (t) { ops.push({ op: 'add', text: t }); });
    } else {
      // dp[x][y] = LCS length of a[x..] and b[y..], stored row-major in one typed array.
      var w = m + 1;
      var dp = new Uint32Array((n + 1) * w);
      for (var x = n - 1; x >= 0; x--) {
        for (var y = m - 1; y >= 0; y--) {
          dp[x * w + y] = a[x] === b[y] ? dp[(x + 1) * w + y + 1] + 1 : Math.max(dp[(x + 1) * w + y], dp[x * w + y + 1]);
        }
      }
      var p = 0;
      var q = 0;
      while (p < n && q < m) {
        if (a[p] === b[q]) { ops.push({ op: 'eq', text: a[p] }); p++; q++; }
        else if (dp[(p + 1) * w + q] >= dp[p * w + q + 1]) { ops.push({ op: 'del', text: a[p] }); p++; }
        else { ops.push({ op: 'add', text: b[q] }); q++; }
      }
      while (p < n) ops.push({ op: 'del', text: a[p++] });
      while (q < m) ops.push({ op: 'add', text: b[q++] });
    }

    for (i = endA; i < A.length; i++) ops.push({ op: 'eq', text: A[i] });
    return ops;
  }

  /** Keeps `context` unchanged lines around each change; longer unchanged runs become {op: 'skip', count}. */
  function collapse(ops, context) {
    var ctx = context == null ? 3 : context;
    var keep = ops.map(function () { return false; });
    ops.forEach(function (o, i) {
      if (o.op === 'eq') return;
      for (var k = Math.max(0, i - ctx); k <= Math.min(ops.length - 1, i + ctx); k++) keep[k] = true;
    });
    var out = [];
    var skipped = 0;
    ops.forEach(function (o, i) {
      if (keep[i]) {
        if (skipped) { out.push({ op: 'skip', count: skipped }); skipped = 0; }
        out.push(o);
      } else {
        skipped++;
      }
    });
    if (skipped) out.push({ op: 'skip', count: skipped });
    return out;
  }

  function stats(ops) {
    var s = { add: 0, del: 0 };
    ops.forEach(function (o) { if (o.op === 'add') s.add++; else if (o.op === 'del') s.del++; });
    return s;
  }

  DBM.diff = { lines: lines, collapse: collapse, stats: stats };
})(globalThis.DBM = globalThis.DBM || {});
```

- [ ] **Step 4: Run the JS tests — they pass**

```bash
node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"
```

Expected: `ℹ tests 11`, `ℹ pass 11`, `ℹ fail 0`.

- [ ] **Step 5: Write the failing static-files test**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Web/StaticFilesTests.cs` (the `wwwroot` content of the referenced Dbm project is copied into the test output folder):

```csharp
using System.Net;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

public class StaticFilesTests
{
    [Fact]
    public async Task Index_and_scripts_are_served_without_caching_and_without_a_token()
    {
        using var tw = new TestWorkspace();
        await using var server = await WebTestServer.StartAsync(tw.Ws);
        using var anonymous = server.Anonymous();

        using var index = await anonymous.GetAsync("/");
        var html = await index.Content.ReadAsStringAsync();
        using var app = await anonymous.GetAsync("/js/app.js");
        using var css = await anonymous.GetAsync("/css/app.css");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("<div id=\"app\" class=\"app\">", html);
        Assert.Equal("no-store", index.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Index_loads_scripts_in_the_c9_order()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        string[] order = ["js/lib/dom.js", "js/lib/diff.js", "js/api.js", "js/components/core.js", "js/components/review.js",
            "js/views/setup.js", "js/views/pending.js", "js/app.js"];

        var positions = order.Select(s => html.IndexOf($"<script src=\"{s}\"></script>", StringComparison.Ordinal)).ToList();

        Assert.All(positions, p => Assert.True(p > 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("<!-- lib scripts:", html);
        Assert.Contains("<!-- view scripts:", html);
    }
}
```

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~StaticFilesTests"
```

Expected: 2 failures — `Assert.Equal() Failure: Values differ … Expected: OK Actual: NotFound` and `System.IO.DirectoryNotFoundException … wwwroot\index.html`.

- [ ] **Step 6: Create the page and the stylesheet**

`plugins/db-migrate/engine/Dbm/wwwroot/index.html` (the inline script applies the saved theme before first paint):

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>db-migrate</title>
  <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='7' fill='%234f46e5'/%3E%3Cpath d='M9 11h14M9 16h14M9 21h9' stroke='white' stroke-width='2.5' stroke-linecap='round'/%3E%3C/svg%3E">
  <link rel="stylesheet" href="css/app.css">
  <script>
    (function () {
      try {
        var t = localStorage.getItem('dbm.theme');
        if (t === 'light' || t === 'dark') document.documentElement.setAttribute('data-theme', t);
      } catch (e) { /* storage blocked: follow the OS theme */ }
    })();
  </script>
</head>
<body>
  <a class="skip-link" href="#view">Skip to content</a>
  <div id="app" class="app">
    <header id="topbar" class="topbar"></header>
    <div id="banners" class="banners" aria-live="polite"></div>
    <nav id="stepper" class="stepper" aria-label="Migration phases"></nav>
    <main id="view" class="main" tabindex="-1">
      <div class="page"><p class="muted">Loading…</p></div>
    </main>
  </div>
  <aside id="drawer" class="drawer" aria-hidden="true" aria-labelledby="drawer-title" role="dialog"></aside>
  <div id="toasts" class="toasts" role="status" aria-live="polite"></div>

  <script src="js/lib/dom.js"></script>
  <script src="js/lib/diff.js"></script>
  <!-- lib scripts: later tasks add tags here, in this order: js/lib/graph.js (T2.8), js/lib/highlight.js (T4.5) -->
  <script src="js/api.js"></script>
  <script src="js/components/core.js"></script>
  <script src="js/components/review.js"></script>
  <script src="js/views/setup.js"></script>
  <script src="js/views/pending.js"></script>
  <!-- view scripts: later tasks add tags here (js/views/analysis.js T2.8, mapping.js T3.5, sql.js T4.5, execute.js + report.js T5.6) -->
  <script src="js/app.js"></script>
</body>
</html>
```

`plugins/db-migrate/engine/Dbm/wwwroot/css/app.css` — the complete token set (light, `[data-theme="dark"]`, and `prefers-color-scheme: dark` unless `data-theme="light"`) and the C9 class vocabulary. Status colours are scoped to `.badge.st-*` so the stepper's `.step.st-*` classes do not pick up badge backgrounds:

```css
/* db-migrate UI — calm operations console. Tokens first; every view uses the class vocabulary below (C9). */

:root {
  --bg: #f6f7f9;
  --surface: #ffffff;
  --surface-2: #f1f3f6;
  --border: #e2e5ea;
  --border-strong: #cfd4dc;
  --text: #1b1f24;
  --text-muted: #5c6470;
  --accent: #4f46e5;
  --accent-contrast: #ffffff;
  --accent-soft: rgba(79, 70, 229, 0.10);
  --ok: #15803d;
  --warn: #b45309;
  --err: #b91c1c;
  --info: #1d4ed8;
  --ok-soft: #dcfce7;
  --warn-soft: #fef3c7;
  --err-soft: #fee2e2;
  --info-soft: #dbeafe;
  --radius: 8px;
  --radius-sm: 6px;
  --shadow: 0 1px 2px rgba(16, 24, 40, 0.06), 0 1px 3px rgba(16, 24, 40, 0.10);
  --shadow-lg: 0 16px 40px rgba(16, 24, 40, 0.18);
  --focus: 0 0 0 3px rgba(79, 70, 229, 0.35);
  --font-sans: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  --font-mono: ui-monospace, "Cascadia Code", "SF Mono", Consolas, monospace;
  --topbar-h: 56px;
  --nav-w: 248px;
  --drawer-w: 440px;
  color-scheme: light;
}

[data-theme="dark"] {
  --bg: #0f1115;
  --surface: #171a21;
  --surface-2: #1e222b;
  --border: #2a2f3a;
  --border-strong: #3a4150;
  --text: #e6e8ec;
  --text-muted: #9aa3b2;
  --accent: #818cf8;
  --accent-contrast: #0f1115;
  --accent-soft: rgba(129, 140, 248, 0.16);
  --ok: #4ade80;
  --warn: #fbbf24;
  --err: #f87171;
  --info: #60a5fa;
  --ok-soft: rgba(74, 222, 128, 0.14);
  --warn-soft: rgba(251, 191, 36, 0.14);
  --err-soft: rgba(248, 113, 113, 0.14);
  --info-soft: rgba(96, 165, 250, 0.14);
  --shadow: 0 1px 2px rgba(0, 0, 0, 0.4);
  --shadow-lg: 0 16px 40px rgba(0, 0, 0, 0.55);
  --focus: 0 0 0 3px rgba(129, 140, 248, 0.45);
  color-scheme: dark;
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #0f1115;
    --surface: #171a21;
    --surface-2: #1e222b;
    --border: #2a2f3a;
    --border-strong: #3a4150;
    --text: #e6e8ec;
    --text-muted: #9aa3b2;
    --accent: #818cf8;
    --accent-contrast: #0f1115;
    --accent-soft: rgba(129, 140, 248, 0.16);
    --ok: #4ade80;
    --warn: #fbbf24;
    --err: #f87171;
    --info: #60a5fa;
    --ok-soft: rgba(74, 222, 128, 0.14);
    --warn-soft: rgba(251, 191, 36, 0.14);
    --err-soft: rgba(248, 113, 113, 0.14);
    --info-soft: rgba(96, 165, 250, 0.14);
    --shadow: 0 1px 2px rgba(0, 0, 0, 0.4);
    --shadow-lg: 0 16px 40px rgba(0, 0, 0, 0.55);
    --focus: 0 0 0 3px rgba(129, 140, 248, 0.45);
    color-scheme: dark;
  }
}

/* ------------------------------------------------------------------ base */

*, *::before, *::after { box-sizing: border-box; }

html, body { margin: 0; padding: 0; }

body {
  background: var(--bg);
  color: var(--text);
  font: 14px/1.5 var(--font-sans);
  -webkit-font-smoothing: antialiased;
}

a { color: var(--accent); text-decoration: none; }
a:hover { text-decoration: underline; }

:focus-visible { outline: none; box-shadow: var(--focus); border-radius: var(--radius-sm); }

code, kbd, pre { font-family: var(--font-mono); font-size: 12.5px; }
:not(pre) > code {
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 0 4px;
}

hr { border: 0; border-top: 1px solid var(--border); margin: 16px 0; }

.skip-link { position: absolute; left: -9999px; top: 8px; z-index: 100; }
.skip-link:focus { left: 8px; background: var(--surface); padding: 8px 12px; border-radius: var(--radius); }

.icon { display: inline-flex; width: 16px; height: 16px; flex: none; }
.icon svg { width: 100%; height: 100%; stroke: currentColor; fill: none; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }

/* ------------------------------------------------------------------ app shell */

.app {
  display: grid;
  grid-template-columns: var(--nav-w) minmax(0, 1fr);
  grid-template-rows: var(--topbar-h) auto 1fr;
  grid-template-areas: "top top" "banner banner" "nav main";
  min-height: 100vh;
}

.topbar {
  grid-area: top;
  position: sticky;
  top: 0;
  z-index: 20;
  background: var(--surface);
  border-bottom: 1px solid var(--border);
}

.topbar-inner { display: flex; align-items: center; gap: 12px; height: var(--topbar-h); padding: 0 16px; }

.brand { display: flex; align-items: center; gap: 10px; min-width: 0; }
.brand-mark {
  display: inline-grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 7px;
  background: var(--accent);
  color: var(--accent-contrast);
  font: 700 11px/1 var(--font-mono);
  letter-spacing: -0.02em;
}
.brand-name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.brand-sub { color: var(--text-muted); font-size: 12px; }

.banners { grid-area: banner; }
.banner {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border);
  font-size: 13.5px;
}
.banner-info { background: var(--info-soft); color: var(--text); }
.banner-warn { background: var(--warn-soft); color: var(--text); }
.banner-err { background: var(--err-soft); color: var(--text); }
.banner .icon { color: var(--text-muted); }
.banner-warn .icon { color: var(--warn); }
.banner-err .icon { color: var(--err); }
.banner-info .icon { color: var(--info); }

.stepper {
  grid-area: nav;
  border-right: 1px solid var(--border);
  background: var(--surface);
  padding: 16px 12px;
}
.stepper-list { list-style: none; margin: 0; padding: 0; position: sticky; top: calc(var(--topbar-h) + 16px); }
.stepper-list li { margin: 0 0 2px; }

.step {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 8px 10px;
  border: 0;
  border-radius: var(--radius);
  background: transparent;
  color: var(--text);
  text-align: left;
  font: inherit;
  cursor: pointer;
}
.step:hover { background: var(--surface-2); }
.step.is-active { background: var(--accent-soft); }
.step.is-active .step-name { color: var(--accent); }
.step-dot {
  display: inline-grid;
  place-items: center;
  flex: none;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  border: 1.5px solid var(--border-strong);
  color: var(--text-muted);
  font: 600 11px/1 var(--font-sans);
  font-variant-numeric: tabular-nums;
}
.step-text { display: flex; flex-direction: column; min-width: 0; }
.step-name { font-weight: 600; font-size: 13.5px; }
.step-status { color: var(--text-muted); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.step.st-approved .step-dot { background: var(--ok); border-color: var(--ok); color: var(--surface); }
.step.st-running .step-dot,
.step.st-drafting .step-dot,
.step.st-reworking .step-dot { border-color: var(--accent); color: var(--accent); }
.step.st-awaiting_review .step-dot { background: var(--accent); border-color: var(--accent); color: var(--accent-contrast); }
.step.st-stale .step-dot { border-color: var(--warn); color: var(--warn); border-style: dashed; }

.main { grid-area: main; min-width: 0; outline: none; }

/* ------------------------------------------------------------------ layout vocabulary */

.page { max-width: 1280px; margin: 0 auto; padding: 24px; }
.page-h { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 12px; margin-bottom: 20px; }
.page-h > :first-child { flex: 1 1 320px; min-width: 0; }
.page-h p { margin: 4px 0 0; }

.stack { display: flex; flex-direction: column; gap: 16px; }
.stack-sm { display: flex; flex-direction: column; gap: 8px; }
.row { display: flex; align-items: center; gap: 8px; }
.row-wrap { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.spacer { flex: 1 1 auto; }
.grid-2 { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.grid-3 { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; }
.grid-kpi { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 12px; }
.split { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 16px; align-items: start; }
.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  margin-bottom: 16px;
}

/* ------------------------------------------------------------------ surfaces */

.card { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); box-shadow: var(--shadow); min-width: 0; }
.card-h { display: flex; align-items: center; gap: 8px; padding: 12px 16px; border-bottom: 1px solid var(--border); font-weight: 600; }
.card-h .h3 { margin: 0; }
.card-b { padding: 16px; }
.card-b > :first-child { margin-top: 0; }
.card-b > :last-child { margin-bottom: 0; }
.panel { background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius); padding: 12px 14px; }
.empty { text-align: center; padding: 40px 16px; color: var(--text-muted); }
.empty .h3 { color: var(--text); margin: 0 0 6px; }
.empty .icon { width: 28px; height: 28px; margin-bottom: 8px; color: var(--text-muted); }

/* ------------------------------------------------------------------ text */

.h1 { font-size: 22px; line-height: 1.25; font-weight: 650; margin: 0; letter-spacing: -0.01em; }
.h2 { font-size: 17px; line-height: 1.3; font-weight: 650; margin: 0 0 8px; }
.h3 { font-size: 14px; line-height: 1.35; font-weight: 650; margin: 0 0 6px; }
.muted { color: var(--text-muted); }
.small { font-size: 12px; }
.mono { font-family: var(--font-mono); font-size: 12.5px; }
.num { font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; }
.ellipsis { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.wrap-anywhere { overflow-wrap: anywhere; }
.prose p { margin: 0 0 8px; }
.prose ul { margin: 0 0 8px; padding-left: 20px; }
.prose > :last-child { margin-bottom: 0; }

/* ------------------------------------------------------------------ controls */

.btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  height: 34px;
  padding: 0 14px;
  border: 1px solid var(--border-strong);
  border-radius: var(--radius);
  background: var(--surface);
  color: var(--text);
  font: 500 13.5px/1 var(--font-sans);
  white-space: nowrap;
  cursor: pointer;
  transition: background-color 0.12s ease, border-color 0.12s ease;
}
.btn:hover { background: var(--surface-2); }
.btn:disabled, .btn.is-disabled { opacity: 0.5; cursor: not-allowed; }
.btn-primary { background: var(--accent); border-color: var(--accent); color: var(--accent-contrast); }
.btn-primary:hover { background: var(--accent); filter: brightness(1.08); }
.btn-danger { background: var(--err); border-color: var(--err); color: #fff; }
.btn-danger:hover { background: var(--err); filter: brightness(1.08); }
.btn-ghost { background: transparent; border-color: transparent; }
.btn-ghost:hover { background: var(--surface-2); }
.btn-sm { height: 28px; padding: 0 10px; font-size: 12.5px; }
.icon-btn { width: 34px; padding: 0; }
.btn-sm.icon-btn { width: 28px; }
.btn.is-loading { position: relative; color: transparent !important; pointer-events: none; }
.btn.is-loading::after {
  content: "";
  position: absolute;
  width: 14px;
  height: 14px;
  border: 2px solid var(--text-muted);
  border-right-color: transparent;
  border-radius: 50%;
  animation: dbm-spin 0.7s linear infinite;
}
.btn-primary.is-loading::after { border-color: var(--accent-contrast); border-right-color: transparent; }

.input, .textarea, .select {
  width: 100%;
  min-height: 34px;
  padding: 7px 10px;
  border: 1px solid var(--border-strong);
  border-radius: var(--radius);
  background: var(--surface);
  color: var(--text);
  font: 13.5px/1.4 var(--font-sans);
}
.input.mono, .textarea.mono { font-family: var(--font-mono); font-size: 12.5px; }
.textarea { min-height: 84px; resize: vertical; }
.select { width: auto; padding-right: 28px; }
.input:focus, .textarea:focus, .select:focus { outline: none; border-color: var(--accent); box-shadow: var(--focus); }
.check { display: inline-flex; align-items: center; gap: 6px; cursor: pointer; }
.check input { accent-color: var(--accent); width: 15px; height: 15px; }
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-weight: 600; font-size: 13px; }
.field-hint { color: var(--text-muted); font-size: 12px; }
.input-group { display: flex; gap: 6px; }
.input-group .input { flex: 1 1 auto; min-width: 0; }

/* ------------------------------------------------------------------ data */

.tbl-wrap { overflow-x: auto; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface); }
.tbl { width: 100%; border-collapse: collapse; font-size: 13px; }
.tbl th, .tbl td { padding: 8px 12px; border-bottom: 1px solid var(--border); text-align: left; vertical-align: top; }
.tbl th { color: var(--text-muted); font-weight: 600; font-size: 12px; background: var(--surface-2); white-space: nowrap; }
.tbl tr:last-child td { border-bottom: 0; }
.tbl-compact th, .tbl-compact td { padding: 5px 10px; }
.tbl-sticky th { position: sticky; top: 0; z-index: 1; }
.tr-click { cursor: pointer; }
.tr-click:hover td { background: var(--surface-2); }
.tr-click.is-active td { background: var(--accent-soft); }

.kpi { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 12px 14px; box-shadow: var(--shadow); }
.kpi-v { font-size: 22px; font-weight: 650; font-variant-numeric: tabular-nums; line-height: 1.2; }
.kpi-l { color: var(--text-muted); font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; }
.kpi-s { color: var(--text-muted); font-size: 12px; margin-top: 2px; }

.tag, .chip, .pill, .badge {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 100%;
  font-size: 12px;
  line-height: 1;
  white-space: nowrap;
}
.tag { padding: 3px 7px; border-radius: 4px; background: var(--surface-2); border: 1px solid var(--border); color: var(--text-muted); font-family: var(--font-mono); font-size: 11.5px; }
.chip { padding: 4px 8px; border-radius: 999px; background: var(--accent-soft); color: var(--accent); font-weight: 500; }
.chip button { border: 0; background: none; color: inherit; cursor: pointer; padding: 0; font: inherit; line-height: 1; }
.pill { padding: 5px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface); color: var(--text-muted); font-weight: 500; }
.pill .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--text-muted); }
.pill-ok .dot { background: var(--ok); box-shadow: 0 0 0 3px var(--ok-soft); }
.pill-warn .dot { background: var(--warn); }
.pill-off .dot { background: var(--text-muted); }

.badge { padding: 4px 8px; border-radius: 999px; font-weight: 600; background: var(--surface-2); color: var(--text-muted); }
/* Status colours apply to badges only (.step.st-* is styled with the stepper). */
.badge.st-pending, .badge.st-queued, .badge.st-draft { background: var(--surface-2); color: var(--text-muted); }
.badge.st-running, .badge.st-drafting, .badge.st-reworking, .badge.st-open { background: var(--info-soft); color: var(--info); }
.badge.st-awaiting_review { background: var(--accent-soft); color: var(--accent); }
.badge.st-approved, .badge.st-done, .badge.st-completed, .badge.st-addressed { background: var(--ok-soft); color: var(--ok); }
.badge.st-stale, .badge.st-paused, .badge.st-declined { background: var(--warn-soft); color: var(--warn); }
.badge.st-failed, .badge.st-cancelled { background: var(--err-soft); color: var(--err); }

.sev-critical { color: var(--err); font-weight: 650; }
.sev-high { color: var(--err); }
.sev-medium { color: var(--warn); }
.sev-low { color: var(--info); }
.sev-info { color: var(--text-muted); }
.badge.sev-critical { background: var(--err); color: #fff; }
.badge.sev-high { background: var(--err-soft); }
.badge.sev-medium { background: var(--warn-soft); }
.badge.sev-low { background: var(--info-soft); }
.badge.sev-info { background: var(--surface-2); }

.bar {
  --v: 0;
  position: relative;
  height: 6px;
  min-width: 48px;
  border-radius: 999px;
  background: var(--surface-2);
  overflow: hidden;
}
.bar::before {
  content: "";
  position: absolute;
  inset: 0 auto 0 0;
  width: calc(var(--v) * 100%);
  border-radius: inherit;
  background: var(--accent);
  transition: width 0.3s ease;
}
.bar.bar-ok::before { background: var(--ok); }
.bar.bar-warn::before { background: var(--warn); }
.bar.bar-err::before { background: var(--err); }
.bar.bar-indeterminate::before { width: 35%; animation: dbm-slide 1.2s ease-in-out infinite; }
.progress { display: flex; align-items: center; gap: 10px; }
.progress .bar { flex: 1 1 auto; }
.progress-label { font-size: 12px; color: var(--text-muted); font-variant-numeric: tabular-nums; min-width: 42px; text-align: right; }

.spark { display: block; overflow: visible; }
.spark polyline { fill: none; stroke: var(--accent); stroke-width: 1.5; stroke-linejoin: round; stroke-linecap: round; }
.spark .spark-area { fill: var(--accent-soft); stroke: none; }

dl.meta { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 6px 16px; margin: 0; font-size: 13px; }
dl.meta dt { color: var(--text-muted); }
dl.meta dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }

/* ------------------------------------------------------------------ code & diff */

.code {
  margin: 0;
  padding: 12px 0;
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  font: 12.5px/1.55 var(--font-mono);
  overflow: auto;
  max-height: 70vh;
  counter-reset: none;
}
.code .line { display: block; padding: 0 14px; white-space: pre; min-height: 1.55em; }
.code .line.commentable { position: relative; }
.code .ln {
  display: inline-block;
  width: 3.5em;
  margin-right: 12px;
  color: var(--text-muted);
  text-align: right;
  user-select: none;
  opacity: 0.7;
}
.code.plain { padding: 12px 14px; white-space: pre-wrap; }
.tok-kw { color: #7c3aed; font-weight: 600; }
.tok-str { color: #047857; }
.tok-num { color: #b45309; }
.tok-com { color: var(--text-muted); font-style: italic; }
.tok-id { color: #0e7490; }
.tok-fn { color: #1d4ed8; }
.tok-op { color: var(--text-muted); }
[data-theme="dark"] .tok-kw { color: #c4b5fd; }
[data-theme="dark"] .tok-str { color: #6ee7b7; }
[data-theme="dark"] .tok-num { color: #fcd34d; }
[data-theme="dark"] .tok-id { color: #67e8f9; }
[data-theme="dark"] .tok-fn { color: #93c5fd; }
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) .tok-kw { color: #c4b5fd; }
  :root:not([data-theme="light"]) .tok-str { color: #6ee7b7; }
  :root:not([data-theme="light"]) .tok-num { color: #fcd34d; }
  :root:not([data-theme="light"]) .tok-id { color: #67e8f9; }
  :root:not([data-theme="light"]) .tok-fn { color: #93c5fd; }
}

.diff { margin: 0; padding: 8px 0; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface); font: 12px/1.5 var(--font-mono); overflow: auto; max-height: 60vh; }
.diff > div { padding: 0 12px 0 28px; white-space: pre; position: relative; }
.diff > div::before { position: absolute; left: 10px; color: var(--text-muted); }
.diff-add { background: var(--ok-soft); }
.diff-add::before { content: "+"; color: var(--ok) !important; }
.diff-del { background: var(--err-soft); }
.diff-del::before { content: "\2212"; color: var(--err) !important; }
.diff-eq { color: var(--text-muted); }
.diff-skip { color: var(--text-muted); font-style: italic; background: var(--surface-2); padding-top: 2px !important; padding-bottom: 2px !important; }

/* ------------------------------------------------------------------ feedback & drawer */

.commentable { position: relative; }
.comment-btn {
  position: absolute;
  top: 4px;
  right: 4px;
  display: inline-grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--surface);
  color: var(--text-muted);
  box-shadow: var(--shadow);
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.12s ease;
}
.comment-btn .icon { width: 14px; height: 14px; }
.commentable:hover > .comment-btn,
.comment-btn:focus-visible,
.has-feedback > .comment-btn { opacity: 1; }
.comment-btn:hover { color: var(--accent); border-color: var(--accent); }
.has-feedback { box-shadow: inset 3px 0 0 var(--accent); }
.has-feedback > .comment-btn { color: var(--accent); border-color: var(--accent); }
@media (hover: none) { .comment-btn { opacity: 1; } }

.drawer {
  position: fixed;
  top: 0;
  right: 0;
  bottom: 0;
  z-index: 40;
  width: min(var(--drawer-w), 100vw);
  display: flex;
  flex-direction: column;
  background: var(--surface);
  border-left: 1px solid var(--border);
  box-shadow: var(--shadow-lg);
  transform: translateX(100%);
  visibility: hidden;
  transition: transform 0.2s ease, visibility 0.2s;
}
.drawer.drawer-open { transform: none; visibility: visible; }
.drawer-h { display: flex; align-items: center; gap: 8px; padding: 12px 16px; border-bottom: 1px solid var(--border); }
.drawer-h .h2 { margin: 0; flex: 1 1 auto; }
.drawer-tabs { display: flex; gap: 4px; padding: 8px 16px 0; border-bottom: 1px solid var(--border); }
.drawer-tab { border: 0; background: none; font: 500 13px var(--font-sans); color: var(--text-muted); padding: 8px 10px; border-bottom: 2px solid transparent; cursor: pointer; }
.drawer-tab.is-active { color: var(--accent); border-bottom-color: var(--accent); }
.drawer-b { flex: 1 1 auto; overflow: auto; padding: 16px; }

.fb-item { border: 1px solid var(--border); border-radius: var(--radius); padding: 10px 12px; background: var(--surface); }
.fb-item + .fb-item { margin-top: 8px; }
.fb-head { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; margin-bottom: 6px; }
.fb-text { overflow-wrap: anywhere; }
.fb-response { margin-top: 8px; padding: 8px 10px; border-left: 3px solid var(--ok); background: var(--surface-2); border-radius: 0 var(--radius-sm) var(--radius-sm) 0; }
.fb-response.is-declined { border-left-color: var(--warn); }
.fb-section { margin-bottom: 18px; }
.fb-section > .h3 { display: flex; align-items: center; gap: 6px; }

.version-list { list-style: none; margin: 0; padding: 0; }
.version-list li { display: flex; align-items: flex-start; gap: 10px; padding: 8px 0; border-bottom: 1px solid var(--border); }
.version-list li:last-child { border-bottom: 0; }

/* ------------------------------------------------------------------ review bar */

.review-bar {
  position: sticky;
  top: var(--topbar-h);
  z-index: 10;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 10px;
  padding: 10px 12px;
  margin-bottom: 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  box-shadow: var(--shadow);
}
.review-summary { flex: 1 1 200px; min-width: 0; color: var(--text-muted); }
.review-actions { display: flex; flex-wrap: wrap; gap: 8px; }

/* ------------------------------------------------------------------ modal & toast */

.modal-backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: 16px;
  background: rgba(15, 17, 21, 0.45);
}
.modal { width: min(480px, 100%); max-height: calc(100vh - 32px); overflow: auto; background: var(--surface); border: 1px solid var(--border); border-radius: 12px; box-shadow: var(--shadow-lg); }
.modal-h { padding: 16px 20px 0; }
.modal-b { padding: 12px 20px; }
.modal-f { display: flex; justify-content: flex-end; gap: 8px; padding: 12px 20px 16px; }

.toasts { position: fixed; right: 16px; bottom: 16px; z-index: 80; display: flex; flex-direction: column; gap: 8px; max-width: min(380px, calc(100vw - 32px)); }
.toast { display: flex; gap: 8px; align-items: flex-start; padding: 10px 14px; border-radius: var(--radius); border: 1px solid var(--border); background: var(--surface); box-shadow: var(--shadow-lg); font-size: 13.5px; }
.toast-ok { border-left: 4px solid var(--ok); }
.toast-err { border-left: 4px solid var(--err); }
.toast-warn { border-left: 4px solid var(--warn); }
.toast-info { border-left: 4px solid var(--info); }

.notice { display: flex; gap: 10px; align-items: flex-start; padding: 10px 12px; border-radius: var(--radius); font-size: 13.5px; }
.notice-ok { background: var(--ok-soft); }
.notice-err { background: var(--err-soft); }
.notice-warn { background: var(--warn-soft); }
.notice-info { background: var(--info-soft); }
.notice .icon { margin-top: 2px; }
.notice-ok .icon { color: var(--ok); }
.notice-err .icon { color: var(--err); }
.notice-warn .icon { color: var(--warn); }
.notice-info .icon { color: var(--info); }

/* ------------------------------------------------------------------ view: setup */

.setup-card .card-b { display: flex; flex-direction: column; gap: 14px; }
.setup-describe { font-family: var(--font-mono); font-size: 12.5px; overflow-wrap: anywhere; }

/* ------------------------------------------------------------------ view: pending */

.pending-log { max-height: 320px; }
.pending-log .line { white-space: pre-wrap; }
.log-warn { color: var(--warn); }
.log-error { color: var(--err); }

/* ------------------------------------------------------------------ states */

.is-disabled { opacity: 0.5; pointer-events: none; }
.is-loading { cursor: progress; }
.spinner { width: 16px; height: 16px; border: 2px solid var(--border-strong); border-right-color: var(--accent); border-radius: 50%; animation: dbm-spin 0.8s linear infinite; flex: none; }

@keyframes dbm-spin { to { transform: rotate(360deg); } }
@keyframes dbm-slide { 0% { transform: translateX(-100%); } 100% { transform: translateX(300%); } }

@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { animation-duration: 0.001ms !important; animation-iteration-count: 1 !important; transition-duration: 0.001ms !important; }
}

/* ------------------------------------------------------------------ responsive */

@media (max-width: 1024px) {
  .grid-3 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .split { grid-template-columns: minmax(0, 1fr); }
}

@media (max-width: 760px) {
  .app { grid-template-columns: minmax(0, 1fr); grid-template-rows: var(--topbar-h) auto auto 1fr; grid-template-areas: "top" "banner" "nav" "main"; }
  .stepper { border-right: 0; border-bottom: 1px solid var(--border); padding: 8px; overflow-x: auto; }
  .stepper-list { position: static; display: flex; gap: 4px; }
  .stepper-list li { margin: 0; flex: none; }
  .step { padding: 6px 10px 6px 6px; }
  .step-status { display: none; }
  .page { padding: 16px 12px; }
  .grid-2, .grid-3 { grid-template-columns: minmax(0, 1fr); }
  .topbar-inner { gap: 8px; padding: 0 10px; }
  .pill-label { display: none; }
  .review-bar { top: var(--topbar-h); }
  .banner { flex-wrap: wrap; }
}

.is-export .app { grid-template-columns: minmax(0, 1fr); grid-template-areas: "top" "banner" "main"; }
.is-export .stepper { display: none; }

@media print {
  .topbar, .stepper, .banners, .drawer, .toasts, .comment-btn, .review-actions { display: none !important; }
  .app { display: block; }
  .card, .kpi { box-shadow: none; }
}
```

- [ ] **Step 7: Create the API client and the components**

`plugins/db-migrate/engine/Dbm/wwwroot/js/api.js`:

```js
/* HTTP + SSE client. The project token comes from ?t= once and is kept in sessionStorage for this tab. */
(function (DBM) {
  'use strict';

  var KEY = 'dbm.token';

  function readToken() {
    var fromQuery = null;
    try { fromQuery = new URLSearchParams(window.location.search).get('t'); } catch (e) { fromQuery = null; }
    if (fromQuery) {
      try { sessionStorage.setItem(KEY, fromQuery); } catch (e) { /* storage blocked */ }
      return fromQuery;
    }
    try { return sessionStorage.getItem(KEY) || ''; } catch (e) { return ''; }
  }

  var TOKEN = window.DBM_EXPORT ? '' : readToken();

  function ApiError(status, code, message, details) {
    var err = new Error(message || code || 'Request failed');
    err.name = 'ApiError';
    err.status = status;
    err.code = code;
    err.details = details || [];
    Object.setPrototypeOf(err, ApiError.prototype);
    return err;
  }
  ApiError.prototype = Object.create(Error.prototype);
  ApiError.prototype.constructor = ApiError;

  function request(method, path, body) {
    var headers = { 'X-Dbm-Token': TOKEN, 'Accept': 'application/json' };
    var init = { method: method, headers: headers, cache: 'no-store' };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    return fetch(path, init).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        if (text) {
          try { data = JSON.parse(text); } catch (e) { data = { message: text }; }
        }
        if (!res.ok) {
          throw new ApiError(res.status, (data && data.error) || 'http_' + res.status,
            (data && data.message) || res.statusText, data && data.details);
        }
        return data;
      });
    }, function (networkError) {
      throw new ApiError(0, 'network', 'The db-migrate server is not reachable. Run "dbm ui" to restart it.', [String(networkError)]);
    });
  }

  /** Event types the UI listens for. Later milestones may push more before app.js starts. */
  var EVENT_TYPES = [
    'state_changed', 'artifact_created', 'feedback_changed', 'job_started', 'job_done', 'job_failed',
    'paused', 'resumed', 'agent_presence', 'drift_detected', 'log',
    'transfer_run_changed', 'transfer_task_changed', 'transfer_progress',
  ];

  /**
   * events(onEvent, onStatus) — onEvent({id, type, data}); onStatus(connected). EventSource reconnects by itself
   * (retry: 2000, Last-Event-ID); if the browser gives up (e.g. server restarted) we reconnect every 3 s.
   */
  function events(onEvent, onStatus) {
    var source = null;
    var closed = false;
    var timer = null;
    var lastId = null;

    function connect() {
      var url = '/api/events?t=' + encodeURIComponent(TOKEN) + (lastId ? '&lastEventId=' + encodeURIComponent(lastId) : '');
      source = new EventSource(url);
      source.onopen = function () { if (onStatus) onStatus(true); };
      source.onerror = function () {
        if (onStatus) onStatus(false);
        if (source.readyState === EventSource.CLOSED) {
          source.close();
          if (!closed) timer = setTimeout(connect, 3000);
        }
      };
      EVENT_TYPES.forEach(function (type) {
        source.addEventListener(type, function (e) {
          if (e.lastEventId) lastId = e.lastEventId;
          var data = {};
          try { data = e.data ? JSON.parse(e.data) : {}; } catch (err) { data = { raw: e.data }; }
          onEvent({ id: e.lastEventId ? Number(e.lastEventId) : null, type: type, data: data });
        });
      });
    }

    connect();
    return {
      close: function () {
        closed = true;
        clearTimeout(timer);
        if (source) source.close();
      },
    };
  }

  DBM.ApiError = ApiError;
  DBM.api = {
    token: function () { return TOKEN; },
    /** Same-origin URL with the token, for GET links such as downloads. */
    url: function (path) { return path + (path.indexOf('?') >= 0 ? '&' : '?') + 't=' + encodeURIComponent(TOKEN); },
    get: function (path) { return request('GET', path); },
    post: function (path, body) { return request('POST', path, body === undefined ? {} : body); },
    put: function (path, body) { return request('PUT', path, body === undefined ? {} : body); },
    del: function (path) { return request('DELETE', path); },
    events: events,
    EVENT_TYPES: EVENT_TYPES,
  };
})(window.DBM = window.DBM || {});
```

`plugins/db-migrate/engine/Dbm/wwwroot/js/components/core.js`:

```js
/* Shared UI components (C9). Everything renders with DBM.h, so text is never parsed as HTML. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components = DBM.components || {};

  var PHASE_TITLES = {
    setup: 'Setup', discovery: 'Discovery', analysis: 'Analysis', mapping: 'Mapping', sql: 'SQL',
    ready: 'Execute', transfer: 'Transfer', complete: 'Report',
  };
  var STATUS_LABELS = {
    pending: 'Pending', running: 'Running', drafting: 'Drafting', awaiting_review: 'Awaiting review',
    reworking: 'Reworking', approved: 'Approved', stale: 'Stale', queued: 'Queued', done: 'Done', failed: 'Failed',
    draft: 'Draft', open: 'Sent to Claude', addressed: 'Addressed', declined: 'Declined', paused: 'Paused',
    completed: 'Completed', cancelled: 'Cancelled',
  };

  DBM.phaseTitle = function (name) { return PHASE_TITLES[name] || name; };
  DBM.statusLabel = function (status) { return STATUS_LABELS[status] || (status ? String(status).replace(/_/g, ' ') : ''); };

  /* Inline SVG icons (trusted markup, 24x24 stroke icons). */
  var ICONS = {
    comment: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
    monitor: '<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/>',
    check: '<path d="M20 6 9 17l-5-5"/>',
    x: '<path d="M18 6 6 18M6 6l12 12"/>',
    alert: '<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h16.9a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/>',
    info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
    pause: '<rect x="6" y="4" width="4" height="16" rx="1"/><rect x="14" y="4" width="4" height="16" rx="1"/>',
    play: '<path d="M6 4l14 8-14 8z"/>',
    refresh: '<path d="M21 12a9 9 0 1 1-2.6-6.4L21 8"/><path d="M21 3v5h-5"/>',
    eye: '<path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/>',
    eyeOff: '<path d="M17.9 17.9A10.1 10.1 0 0 1 12 20C5 20 1 12 1 12a18.5 18.5 0 0 1 5.1-5.9M9.9 4.2A9.1 9.1 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.2 3.2M1 1l22 22"/><path d="M14.1 14.1a3 3 0 1 1-4.2-4.2"/>',
    history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5M12 7v5l4 2"/>',
    database: '<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5"/><path d="M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/>',
    clock: '<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>',
  };

  C.icon = function (name) {
    return h('span', { class: 'icon', 'aria-hidden': 'true', html: '<svg viewBox="0 0 24 24">' + (ICONS[name] || '') + '</svg>' });
  };

  C.badge = function (status, label) {
    return h('span', { class: 'badge st-' + status }, label || DBM.statusLabel(status));
  };

  /** Left phase list. opts: {current, onSelect(name)} */
  C.stepper = function (state, opts) {
    opts = opts || {};
    var list = h('ol', { class: 'stepper-list' });
    state.phases.forEach(function (p, i) {
      var active = p.name === opts.current;
      var status = DBM.statusLabel(p.status) + (p.currentVersion != null && p.status !== 'pending' ? ' · v' + p.currentVersion : '');
      list.appendChild(h('li', null,
        h('button', {
          type: 'button',
          class: ['step', 'st-' + p.status, active && 'is-active'],
          'aria-current': active ? 'step' : null,
          title: DBM.phaseTitle(p.name) + ': ' + status,
          on: { click: function () { if (opts.onSelect) opts.onSelect(p.name); } },
        },
        h('span', { class: 'step-dot', 'aria-hidden': 'true' }, p.status === 'approved' ? '✓' : String(i + 1)),
        h('span', { class: 'step-text' },
          h('span', { class: 'step-name' }, DBM.phaseTitle(p.name)),
          h('span', { class: 'step-status' }, status)))));
    });
    return list;
  };

  /** Top bar. opts: {live, theme ('system'|'light'|'dark'), onPause, onResume, onTheme, title} */
  C.topbar = function (state, opts) {
    opts = opts || {};
    var project = (state && state.project) || { name: opts.title || 'db-migrate', paused: false, agentOnline: false };
    var themeIcon = opts.theme === 'dark' ? 'moon' : opts.theme === 'light' ? 'sun' : 'monitor';
    return h('div', { class: 'topbar-inner' },
      h('div', { class: 'brand' },
        h('span', { class: 'brand-mark', 'aria-hidden': 'true' }, 'dbm'),
        h('span', { class: 'brand-name' }, project.name)),
      h('span', { class: 'spacer' }),
      state ? h('span', {
        class: ['pill', project.agentOnline ? 'pill-ok' : 'pill-off'],
        title: project.agentOnline ? 'Claude is connected and waiting for you' : 'Claude is not connected',
      }, h('span', { class: 'dot' }), h('span', { class: 'pill-label' }, project.agentOnline ? 'Agent online' : 'Agent offline')) : null,
      state && opts.live === false ? h('span', { class: 'pill pill-warn', title: 'Live updates interrupted' },
        h('span', { class: 'dot' }), h('span', { class: 'pill-label' }, 'Reconnecting…')) : null,
      state ? (project.paused
        ? h('button', { type: 'button', class: 'btn btn-primary btn-sm', on: { click: opts.onResume } }, C.icon('play'), 'Resume')
        : h('button', { type: 'button', class: 'btn btn-sm', on: { click: opts.onPause } }, C.icon('pause'), 'Pause')) : null,
      h('button', {
        type: 'button',
        class: 'btn btn-ghost btn-sm icon-btn',
        'aria-label': 'Theme: ' + (opts.theme || 'system') + ' (click to change)',
        title: 'Theme: ' + (opts.theme || 'system'),
        on: { click: opts.onTheme },
      }, C.icon(themeIcon)));
  };

  C.banner = function (kind, content, action) {
    var icon = kind === 'err' || kind === 'warn' ? 'alert' : 'info';
    return h('div', { class: 'banner banner-' + kind, role: kind === 'err' ? 'alert' : null },
      C.icon(icon), h('div', { class: 'spacer' }, content), action || null);
  };

  C.notice = function (kind, content) {
    var icon = kind === 'ok' ? 'check' : kind === 'info' ? 'info' : 'alert';
    return h('div', { class: 'notice notice-' + kind }, C.icon(icon), h('div', { class: 'wrap-anywhere' }, content));
  };

  C.kpi = function (label, value, sub) {
    return h('div', { class: 'kpi' },
      h('div', { class: 'kpi-l' }, label),
      h('div', { class: 'kpi-v' }, value == null ? '—' : value),
      sub ? h('div', { class: 'kpi-s' }, sub) : null);
  };

  /**
   * columns: [{key, label, num?, class?, render?(row) → node|string}]
   * opts: {compact, sticky, onRowClick(row), rowClass(row), empty, activeRow(row) → bool}
   */
  C.table = function (columns, rows, opts) {
    opts = opts || {};
    var thead = h('thead', null, h('tr', null, columns.map(function (c) {
      return h('th', { class: [c.num && 'num', c.class], scope: 'col' }, c.label);
    })));
    var tbody = h('tbody');
    if (!rows.length) {
      tbody.appendChild(h('tr', null, h('td', { colspan: columns.length, class: 'muted' }, opts.empty || 'Nothing to show.')));
    }
    rows.forEach(function (row) {
      var tr = h('tr', {
        class: [opts.onRowClick && 'tr-click', opts.rowClass && opts.rowClass(row), opts.activeRow && opts.activeRow(row) && 'is-active'],
        tabindex: opts.onRowClick ? '0' : null,
        on: opts.onRowClick ? {
          click: function () { opts.onRowClick(row); },
          keydown: function (e) { if (e.key === 'Enter') opts.onRowClick(row); },
        } : null,
      });
      columns.forEach(function (c) {
        var value = c.render ? c.render(row) : row[c.key];
        tr.appendChild(h('td', { class: [c.num && 'num', c.class] }, value == null ? '' : value));
      });
      tbody.appendChild(tr);
    });
    return h('div', { class: 'tbl-wrap' },
      h('table', { class: ['tbl', opts.compact && 'tbl-compact', opts.sticky && 'tbl-sticky'] }, thead, tbody));
  };

  /** opts: {label (string|true for %), kind: 'ok'|'warn'|'err', indeterminate} */
  C.progress = function (value, max, opts) {
    opts = opts || {};
    var ratio = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
    var bar = h('div', {
      class: ['bar', opts.kind && 'bar-' + opts.kind, opts.indeterminate && 'bar-indeterminate'],
      style: { '--v': ratio.toFixed(4) },
      role: 'progressbar',
      'aria-valuemin': '0',
      'aria-valuemax': String(max || 0),
      'aria-valuenow': opts.indeterminate ? null : String(value || 0),
    });
    if (!opts.label) return bar;
    return h('div', { class: 'progress' }, bar,
      h('span', { class: 'progress-label' }, opts.label === true ? DBM.fmt.pct(ratio) : opts.label));
  };

  /** Small line chart; values: numbers. Returns an SVG element. */
  C.sparkline = function (values, opts) {
    opts = opts || {};
    var w = opts.width || 120;
    var hh = opts.height || 28;
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'spark');
    svg.setAttribute('width', String(w));
    svg.setAttribute('height', String(hh));
    svg.setAttribute('viewBox', '0 0 ' + w + ' ' + hh);
    svg.setAttribute('aria-hidden', 'true');
    var vals = (values || []).filter(function (v) { return typeof v === 'number' && isFinite(v); });
    if (vals.length < 2) return svg;
    var max = Math.max.apply(null, vals);
    var min = Math.min.apply(null, vals.concat([0]));
    var span = max - min || 1;
    var pts = vals.map(function (v, i) {
      return (i * (w / (vals.length - 1))).toFixed(1) + ',' + (hh - 2 - ((v - min) / span) * (hh - 4)).toFixed(1);
    });
    var area = document.createElementNS(ns, 'polygon');
    area.setAttribute('class', 'spark-area');
    area.setAttribute('points', '0,' + hh + ' ' + pts.join(' ') + ' ' + w + ',' + hh);
    var line = document.createElementNS(ns, 'polyline');
    line.setAttribute('points', pts.join(' '));
    svg.appendChild(area);
    svg.appendChild(line);
    return svg;
  };

  /** modal({title, body (node|string), confirmText, cancelText, danger, requireText}) → Promise<bool> */
  C.modal = function (o) {
    return new Promise(function (resolve) {
      var previous = document.activeElement;
      var input = o.requireText ? h('input', { class: 'input mono', type: 'text', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Type ' + o.requireText + ' to confirm' }) : null;
      var confirm = h('button', { type: 'button', class: ['btn', o.danger ? 'btn-danger' : 'btn-primary'], disabled: !!o.requireText }, o.confirmText || 'OK');
      var cancel = h('button', { type: 'button', class: 'btn' }, o.cancelText || 'Cancel');
      var titleId = 'modal-title-' + Date.now();
      var dialog = h('div', { class: 'modal', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId },
        h('div', { class: 'modal-h' }, h('h2', { class: 'h2', id: titleId }, o.title || 'Confirm')),
        h('div', { class: 'modal-b stack-sm' },
          typeof o.body === 'string' ? h('p', { style: { margin: '0' } }, o.body) : o.body,
          o.requireText ? h('label', { class: 'field' },
            h('span', { class: 'field-hint' }, 'Type ', h('code', null, o.requireText), ' to confirm'), input) : null),
        h('div', { class: 'modal-f' }, o.cancelText === null ? null : cancel, confirm));
      var backdrop = h('div', { class: 'modal-backdrop' }, dialog);

      function close(result) {
        document.removeEventListener('keydown', onKey, true);
        backdrop.remove();
        if (previous && previous.focus) previous.focus();
        resolve(result);
      }
      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); close(false); }
        if (e.key === 'Tab') {
          var focusables = dialog.querySelectorAll('button:not([disabled]), input');
          if (!focusables.length) return;
          var first = focusables[0];
          var last = focusables[focusables.length - 1];
          if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
          else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
      }
      if (input) {
        input.addEventListener('input', function () { confirm.disabled = input.value !== o.requireText; });
        input.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !confirm.disabled) close(true); });
      }
      confirm.addEventListener('click', function () { close(true); });
      cancel.addEventListener('click', function () { close(false); });
      backdrop.addEventListener('mousedown', function (e) { if (e.target === backdrop) close(false); });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(backdrop);
      (input || confirm).focus();
    });
  };

  /** toast(message, kind: 'ok'|'err'|'warn'|'info') */
  C.toast = function (message, kind) {
    var host = document.getElementById('toasts');
    if (!host) return;
    kind = kind || 'info';
    var el = h('div', { class: 'toast toast-' + kind }, C.icon(kind === 'ok' ? 'check' : kind === 'info' ? 'info' : 'alert'), h('div', null, message));
    host.appendChild(el);
    setTimeout(function () { el.remove(); }, kind === 'err' ? 8000 : 4000);
  };

  /** The single right-hand drawer. open(title, content, {tabs}) ; close() ; isOpen() */
  C.drawer = (function () {
    var onCloseHandler = null;
    function el() { return document.getElementById('drawer'); }
    function onKey(e) { if (e.key === 'Escape') api.close(); }
    var api = {
      open: function (title, content, options) {
        var d = el();
        options = options || {};
        DBM.clear(d);
        d.appendChild(h('div', { class: 'drawer-h' },
          h('h2', { class: 'h2', id: 'drawer-title' }, title),
          h('button', { type: 'button', class: 'btn btn-ghost btn-sm icon-btn', 'aria-label': 'Close panel', on: { click: function () { api.close(); } } }, C.icon('x'))));
        if (options.tabs) d.appendChild(options.tabs);
        d.appendChild(h('div', { class: 'drawer-b' }, content));
        d.classList.add('drawer-open');
        d.setAttribute('aria-hidden', 'false');
        document.addEventListener('keydown', onKey);
        onCloseHandler = options.onClose || null;
        var focusTarget = d.querySelector('textarea, input, .drawer-b button') || d.querySelector('button');
        if (focusTarget && !options.keepFocus) focusTarget.focus();
      },
      close: function () {
        var d = el();
        if (!d.classList.contains('drawer-open')) return;
        d.classList.remove('drawer-open');
        d.setAttribute('aria-hidden', 'true');
        document.removeEventListener('keydown', onKey);
        if (onCloseHandler) { var fn = onCloseHandler; onCloseHandler = null; fn(); }
      },
      isOpen: function () { var d = el(); return !!d && d.classList.contains('drawer-open'); },
    };
    return api;
  })();

  C.emptyState = function (title, text, action) {
    return h('div', { class: 'empty' },
      C.icon('info'), h('div', { class: 'h3' }, title), text ? h('p', { class: 'muted', style: { margin: '0 0 12px' } }, text) : null, action || null);
  };

  /** Labelled form field. */
  C.field = function (label, control, hint) {
    return h('label', { class: 'field' }, h('span', { class: 'field-label' }, label), control, hint ? h('span', { class: 'field-hint' }, hint) : null);
  };

  /** Runs an async action while the button shows a spinner; returns the action's promise. */
  C.busy = function (button, action) {
    button.classList.add('is-loading');
    button.disabled = true;
    return Promise.resolve().then(action).finally(function () {
      button.classList.remove('is-loading');
      button.disabled = false;
    });
  };

  /** Human text for an ApiError (includes blocker details). */
  C.errorText = function (err) {
    if (!err) return 'Something went wrong.';
    var details = err.details && err.details.length ? ' ' + err.details.join(' · ') : '';
    return (err.message || String(err)) + details;
  };
})(window.DBM = window.DBM || {});
```

`plugins/db-migrate/engine/Dbm/wwwroot/js/components/review.js` (review bar, feedback drawer with anchored drafts and agent responses, version history with a collapsed diff of pretty-printed payloads):

```js
/* Review loop UI: review bar, feedback drawer (anchored comments + agent responses), version history with diff. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var R = DBM.review = {};
  var REVIEWABLE = { analysis: true, mapping: true, sql: true };
  var drawerState = null;      // {kind: 'feedback'|'history', anchor, label, text, from, to}
  var payloadCache = {};       // "phase:version" → payload

  /** Human label for a feedback anchor (C5 anchor grammar). */
  DBM.anchorLabel = function (anchor, fallback) {
    if (!anchor) return fallback || 'General';
    var side = function (s) { return s === 'src' ? 'Source' : s === 'tgt' ? 'Target' : s; };
    var m;
    if ((m = /^finding:(.+)$/.exec(anchor))) return 'Finding ' + m[1];
    if ((m = /^table:(src|tgt):(.+)$/.exec(anchor))) return side(m[1]) + ' table ' + m[2];
    if ((m = /^column:(src|tgt):(.+)$/.exec(anchor))) return side(m[1]) + ' column ' + m[2];
    if (anchor === 'narrative') return 'Narrative';
    if ((m = /^tablemap:(.+)$/.exec(anchor))) return 'Mapping of ' + m[1];
    if ((m = /^colmap:(.+)$/.exec(anchor))) return 'Column mapping ' + m[1];
    if ((m = /^task:(.+)$/.exec(anchor))) return 'Task ' + m[1];
    if ((m = /^sql:([^:]+):(\d+)$/.exec(anchor))) return 'Task ' + m[1] + ', line ' + m[2];
    return fallback || anchor;
  };

  function transferPending(ctx) {
    var t = ctx.state && ctx.state.phases.filter(function (p) { return p.name === 'transfer'; })[0];
    return !t || t.status === 'pending';
  }

  function counts(ctx) {
    var c = { draft: 0, open: 0, all: ctx.feedback.length };
    ctx.feedback.forEach(function (f) { if (c[f.status] != null) c[f.status]++; });
    return c;
  }

  /* ------------------------------------------------------------------ review bar */

  C.reviewBar = function (ctx) {
    var row = ctx.phaseRow;
    var versions = ctx.versions || [];
    var shown = ctx.artifact;                 // {version, author, summary, createdAt, payload}
    var latest = ctx.latestVersion;
    if (window.DBM_EXPORT) return exportBar(row, shown);
    var viewingOld = !!shown && latest != null && shown.version !== latest;
    var n = counts(ctx);
    var canReview = row.status === 'awaiting_review' && !viewingOld;

    var picker = h('select', {
      class: 'select',
      name: 'version',
      'aria-label': 'Version',
      on: { change: function (e) { ctx.setVersion(Number(e.target.value)); } },
    }, versions.slice().reverse().map(function (v) {
      var label = 'v' + v.version + ' · ' + v.author + (v.version === latest ? ' (current)' : '');
      return h('option', { value: String(v.version), selected: shown && v.version === shown.version }, label);
    }));

    var approveBtn = h('button', { type: 'button', class: 'btn btn-primary' }, C.icon('check'), 'Approve');
    approveBtn.addEventListener('click', function () { approve(ctx, approveBtn); });
    var changesBtn = h('button', {
      type: 'button',
      class: 'btn',
      disabled: n.draft === 0,
      title: n.draft === 0 ? 'Add at least one comment first' : 'Send your comments to Claude',
    }, 'Request changes' + (n.draft ? ' (' + n.draft + ')' : ''));
    changesBtn.addEventListener('click', function () { requestChanges(ctx, changesBtn, n.draft); });

    var reopenBtn = null;
    if (row.status === 'approved' && REVIEWABLE[ctx.phase] && transferPending(ctx) && !window.DBM_EXPORT) {
      reopenBtn = h('button', { type: 'button', class: 'btn btn-sm' }, 'Reopen');
      reopenBtn.addEventListener('click', function () { reopen(ctx, reopenBtn); });
    }

    return h('div', { class: 'review-bar' },
      versions.length ? picker : null,
      C.badge(row.status),
      shown ? h('span', { class: 'tag', title: 'Author of this version' }, shown.author) : null,
      h('span', { class: 'review-summary ellipsis', title: (shown && shown.summary) || '' }, (shown && shown.summary) || ''),
      h('div', { class: 'review-actions' },
        h('button', { type: 'button', class: 'btn btn-ghost', on: { click: function () { ctx.openFeedback(null, null); } } },
          C.icon('comment'), 'Feedback' + (n.all ? ' (' + n.all + ')' : '')),
        h('button', { type: 'button', class: 'btn btn-ghost', on: { click: function () { ctx.openHistory(); } } }, C.icon('history'), 'History'),
        reopenBtn,
        canReview ? changesBtn : null,
        canReview ? approveBtn : null),
      viewingOld ? h('div', { style: { flexBasis: '100%' } }, C.notice('info', h('span', null,
        'You are viewing v' + shown.version + '. The current version is v' + latest + '. ',
        h('a', { href: '#', on: { click: function (e) { e.preventDefault(); ctx.setVersion(null); } } }, 'Show current')))) : null);
  };

  /** Export mode: facts only, no actions. */
  function exportBar(row, shown) {
    return h('div', { class: 'review-bar' },
      shown && shown.version != null ? h('span', { class: 'tag' }, 'v' + shown.version) : null,
      C.badge(row.status),
      shown && shown.author ? h('span', { class: 'tag' }, shown.author) : null,
      h('span', { class: 'review-summary ellipsis', title: (shown && shown.summary) || '' }, (shown && shown.summary) || ''),
      shown && shown.createdAt ? h('span', { class: 'small muted' }, DBM.fmt.ts(shown.createdAt)) : null);
  }

  function approve(ctx, btn) {
    C.busy(btn, function () {
      return ctx.api.post('/api/phase/' + ctx.phase + '/approve').then(function () {
        C.toast(DBM.phaseTitle(ctx.phase) + ' approved.', 'ok');
        ctx.refresh();
      }, function (err) {
        if (err.code === 'blocked' || err.code === 'guard') {
          return C.modal({
            title: 'Not ready to approve',
            body: h('div', { class: 'stack-sm' },
              h('p', { style: { margin: '0' } }, err.message),
              err.details && err.details.length ? h('ul', { style: { margin: '0', paddingLeft: '20px' } },
                err.details.map(function (d) { return h('li', null, d); })) : null),
            confirmText: 'OK',
            cancelText: null,
          });
        }
        C.toast(C.errorText(err), 'err');
      });
    });
  }

  function requestChanges(ctx, btn, drafts) {
    C.busy(btn, function () {
      return ctx.api.post('/api/phase/' + ctx.phase + '/request-changes').then(function () {
        C.toast('Sent ' + drafts + ' comment' + (drafts === 1 ? '' : 's') + ' to Claude.', 'ok');
        ctx.refresh();
      }, function (err) { C.toast(C.errorText(err), 'err'); });
    });
  }

  function reopen(ctx, btn) {
    var later = ctx.state.phases
      .filter(function (p) { return REVIEWABLE[p.name] && p.name !== ctx.phase && p.status !== 'pending'; })
      .filter(function (p) { return ctx.state.phases.indexOf(p) > ctx.state.phases.map(function (x) { return x.name; }).indexOf(ctx.phase); })
      .map(function (p) { return DBM.phaseTitle(p.name); });
    C.modal({
      title: 'Reopen ' + DBM.phaseTitle(ctx.phase) + '?',
      body: later.length
        ? 'You can edit and re-approve it. ' + later.join(' and ') + ' will be marked stale and must be approved again.'
        : 'You can add feedback and approve it again.',
      confirmText: 'Reopen',
    }).then(function (ok) {
      if (!ok) return;
      C.busy(btn, function () {
        return ctx.api.post('/api/phase/' + ctx.phase + '/reopen').then(function () { ctx.refresh(); },
          function (err) { C.toast(C.errorText(err), 'err'); });
      });
    });
  }

  /* ------------------------------------------------------------------ drawer */

  R.openFeedback = function (ctx, anchor, label) {
    drawerState = { kind: 'feedback', anchor: anchor || null, label: label || null, text: drawerState && drawerState.text || '' };
    render(ctx, false);
  };

  R.openHistory = function (ctx) {
    drawerState = { kind: 'history', from: null, to: null, text: drawerState && drawerState.text || '' };
    render(ctx, false);
  };

  /** Called by app.js after every refresh so an open drawer shows fresh data. */
  R.refresh = function (ctx) {
    if (drawerState && C.drawer.isOpen()) render(ctx, true);
  };

  R.isOpen = function () { return !!drawerState && C.drawer.isOpen(); };

  function render(ctx, keepFocus) {
    var active = document.activeElement;
    var activeId = active && active.closest && active.closest('#drawer') ? active.id : null;
    var caret = activeId && typeof active.selectionStart === 'number' ? active.selectionStart : null;
    var tabs = h('div', { class: 'drawer-tabs', role: 'tablist' },
      tab(ctx, 'feedback', 'Feedback'), tab(ctx, 'history', 'History'));
    var body = drawerState.kind === 'history' ? historyPanel(ctx) : feedbackPanel(ctx);
    C.drawer.open(DBM.phaseTitle(ctx.phase) + ' review', body, {
      tabs: tabs,
      keepFocus: keepFocus,
      onClose: function () { drawerState = null; },
    });
    if (activeId) {
      var again = document.getElementById(activeId);
      if (again) {
        again.focus();
        if (caret != null && again.setSelectionRange) again.setSelectionRange(caret, caret);
      }
    }
  }

  function tab(ctx, kind, label) {
    return h('button', {
      type: 'button',
      role: 'tab',
      class: ['drawer-tab', drawerState.kind === kind && 'is-active'],
      'aria-selected': drawerState.kind === kind ? 'true' : 'false',
      on: { click: function () { drawerState.kind = kind; render(ctx, true); } },
    }, label);
  }

  function feedbackPanel(ctx) {
    var canComment = !ctx.readOnly && ctx.phaseRow.currentVersion != null && !window.DBM_EXPORT;
    var composer;
    if (canComment) {
      var text = h('textarea', {
        id: 'fb-text',
        class: 'textarea',
        rows: '4',
        placeholder: 'What should change? Be specific — Claude only sees your comments and the element they point at.',
        'aria-label': 'Comment',
      });
      text.value = drawerState.text || '';
      text.addEventListener('input', function () { drawerState.text = text.value; });
      var add = h('button', { type: 'submit', class: 'btn btn-primary btn-sm' }, 'Add comment');
      var form = h('form', { class: 'stack-sm fb-section' },
        drawerState.anchor
          ? h('div', { class: 'row' }, h('span', { class: 'chip', title: drawerState.anchor },
            DBM.anchorLabel(drawerState.anchor, drawerState.label),
            h('button', {
              type: 'button',
              'aria-label': 'Comment on the whole version instead',
              on: { click: function () { drawerState.anchor = null; drawerState.label = null; render(ctx, true); } },
            }, '×')))
          : h('div', { class: 'small muted' }, 'General comment on v' + ctx.phaseRow.currentVersion),
        text,
        h('div', { class: 'row' },
          h('span', { class: 'small muted spacer' }, 'Comments stay drafts until you click Request changes.'),
          add));
      form.addEventListener('submit', function (e) {
        e.preventDefault();
        var value = text.value.trim();
        if (!value) { text.focus(); return; }
        C.busy(add, function () {
          return ctx.api.post('/api/feedback/' + ctx.phase, { anchor: drawerState.anchor, text: value }).then(function () {
            drawerState.text = '';
            ctx.refresh();
          }, function (err) { C.toast(C.errorText(err), 'err'); });
        });
      });
      text.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) form.requestSubmit ? form.requestSubmit() : add.click();
      });
      composer = form;
    } else {
      composer = h('div', { class: 'fb-section' }, C.notice('info',
        ctx.phaseRow.status === 'reworking' ? 'Claude is working on the comments below.'
          : ctx.phaseRow.status === 'approved' ? 'This phase is approved. Reopen it to add comments.'
            : 'Comments can be added while the phase awaits your review.'));
    }

    var focus = drawerState.anchor;
    var sorted = ctx.feedback.slice().sort(function (a, b) {
      var fa = a.anchor === focus ? 0 : 1;
      var fb = b.anchor === focus ? 0 : 1;
      return fa - fb || b.id - a.id;
    });
    var groups = [
      { title: 'Drafts', hint: 'not sent yet', items: sorted.filter(function (f) { return f.status === 'draft'; }) },
      { title: 'Sent to Claude', hint: 'being worked on', items: sorted.filter(function (f) { return f.status === 'open'; }) },
      { title: 'Answered', hint: '', items: sorted.filter(function (f) { return f.status === 'addressed' || f.status === 'declined'; }) },
    ];

    return h('div', null, composer,
      ctx.feedback.length ? null : C.emptyState('No comments yet', 'Hover any element in the page and click the comment icon, or write a general comment above.'),
      groups.filter(function (g) { return g.items.length; }).map(function (g) {
        return h('section', { class: 'fb-section' },
          h('h3', { class: 'h3' }, g.title, h('span', { class: 'badge' }, String(g.items.length)), g.hint ? h('span', { class: 'small muted' }, g.hint) : null),
          g.items.map(function (f) { return feedbackItem(ctx, f); }));
      }));
  }

  function feedbackItem(ctx, f) {
    var del = null;
    if (f.status === 'draft' && !ctx.readOnly) {
      del = h('button', { type: 'button', class: 'btn btn-ghost btn-sm icon-btn', 'aria-label': 'Delete draft comment' }, C.icon('x'));
      del.addEventListener('click', function () {
        C.busy(del, function () {
          return ctx.api.del('/api/feedback/' + f.id).then(function () { ctx.refresh(); },
            function (err) { C.toast(C.errorText(err), 'err'); });
        });
      });
    }
    return h('article', { class: 'fb-item' },
      h('div', { class: 'fb-head' },
        C.badge(f.status),
        h('span', { class: 'tag', title: f.anchor || 'general' }, DBM.anchorLabel(f.anchor)),
        h('span', { class: 'spacer' }),
        h('span', { class: 'small muted', title: DBM.fmt.ts(f.createdAt) }, 'v' + f.version + ' · ' + DBM.fmt.rel(f.createdAt)),
        del),
      h('div', { class: 'fb-text prose', html: DBM.mdLite(f.text) }),
      f.response ? h('div', { class: ['fb-response', f.status === 'declined' && 'is-declined'] },
        h('div', { class: 'small muted' }, (f.status === 'declined' ? 'Declined' : 'Addressed') + ' by Claude in v' + f.respondedVersion),
        h('div', { class: 'prose', html: DBM.mdLite(f.response) })) : null);
  }

  function historyPanel(ctx) {
    var versions = ctx.versions || [];
    if (!versions.length) return C.emptyState('No versions yet', 'Versions appear once the first draft exists.');
    var last = versions[versions.length - 1].version;
    if (drawerState.to == null) drawerState.to = last;
    if (drawerState.from == null) drawerState.from = versions.length > 1 ? versions[versions.length - 2].version : last;

    var list = h('ul', { class: 'version-list' }, versions.slice().reverse().map(function (v) {
      return h('li', null,
        h('span', { class: 'tag' }, 'v' + v.version),
        h('div', { class: 'spacer', style: { minWidth: '0' } },
          h('div', { class: 'ellipsis', title: v.summary || '' }, v.summary || h('span', { class: 'muted' }, 'No summary')),
          h('div', { class: 'small muted' }, v.author + ' · ' + DBM.fmt.rel(v.createdAt))),
        h('button', {
          type: 'button',
          class: 'btn btn-ghost btn-sm',
          on: { click: function () { ctx.setVersion(v.version === last ? null : v.version); C.drawer.close(); } },
        }, 'View'));
    }));

    function versionSelect(key, label) {
      return h('label', { class: 'row small' }, label, h('select', {
        class: 'select',
        on: { change: function (e) { drawerState[key] = Number(e.target.value); render(ctx, true); } },
      }, versions.map(function (v) {
        return h('option', { value: String(v.version), selected: v.version === drawerState[key] }, 'v' + v.version);
      })));
    }

    var diffHost = h('div', { class: 'stack-sm' }, h('div', { class: 'row small muted' }, h('span', { class: 'spinner' }), 'Loading diff…'));
    loadDiff(ctx, drawerState.from, drawerState.to, diffHost);

    return h('div', { class: 'stack' },
      h('section', null, h('h3', { class: 'h3' }, 'Versions'), list),
      h('section', { class: 'stack-sm' },
        h('div', { class: 'row-wrap' }, h('h3', { class: 'h3', style: { margin: '0' } }, 'Compare'),
          h('span', { class: 'spacer' }), versionSelect('from', 'from'), versionSelect('to', 'to')),
        diffHost));
  }

  function payloadOf(ctx, version) {
    var key = ctx.phase + ':' + version;
    if (payloadCache[key]) return Promise.resolve(payloadCache[key]);
    return ctx.api.get('/api/artifact/' + ctx.phase + '/' + version).then(function (a) {
      payloadCache[key] = a.payload;
      return a.payload;
    });
  }

  function loadDiff(ctx, from, to, host) {
    Promise.all([payloadOf(ctx, from), payloadOf(ctx, to)]).then(function (pair) {
      var ops = DBM.diff.lines(JSON.stringify(pair[0], null, 2), JSON.stringify(pair[1], null, 2));
      var s = DBM.diff.stats(ops);
      var rows = DBM.diff.collapse(ops, 3).map(function (o) {
        if (o.op === 'skip') return h('div', { class: 'diff-skip' }, '… ' + o.count + ' unchanged line' + (o.count === 1 ? '' : 's'));
        return h('div', { class: 'diff-' + o.op }, o.text || ' ');
      });
      DBM.clear(host);
      host.appendChild(h('div', { class: 'row small' },
        h('span', { class: 'sev-low' }, 'v' + from + ' → v' + to),
        h('span', { class: 'spacer' }),
        h('span', { style: { color: 'var(--ok)' } }, '+' + s.add),
        h('span', { style: { color: 'var(--err)' } }, '−' + s.del)));
      host.appendChild(from === to || (!s.add && !s.del)
        ? C.notice('info', 'No differences.')
        : h('div', { class: 'diff', role: 'region', 'aria-label': 'Differences' }, rows));
    }, function (err) {
      DBM.clear(host);
      host.appendChild(C.notice('err', C.errorText(err)));
    });
  }
})(window.DBM = window.DBM || {});
```

- [ ] **Step 8: Create the views and the app shell**

`plugins/db-migrate/engine/Dbm/wwwroot/js/views/setup.js` (connection strings are held only in this tab's memory until saved; password-type input with a show/hide toggle):

```js
/* SETUP: enter, test and save the two connection strings. Secrets live only in this tab's memory until saved. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var SIDES = [
    { key: 'src', title: 'Source', hint: 'The database you migrate from.' },
    { key: 'tgt', title: 'Target', hint: 'The existing database you migrate into.' },
  ];
  var local = {
    src: { editing: false, value: '', reveal: false, result: null },
    tgt: { editing: false, value: '', reveal: false, result: null },
  };

  function metaList(meta) {
    if (!meta) return null;
    return h('dl', { class: 'meta' },
      h('dt', null, 'Server'), h('dd', { class: 'mono' }, meta.server),
      h('dt', null, 'Database'), h('dd', { class: 'mono' }, meta.database),
      h('dt', null, 'Version'), h('dd', null, meta.version),
      h('dt', null, 'Edition'), h('dd', null, meta.edition),
      h('dt', null, 'Collation'), h('dd', { class: 'mono' }, meta.databaseCollation +
        (meta.serverCollation && meta.serverCollation !== meta.databaseCollation ? ' (server ' + meta.serverCollation + ')' : '')),
      h('dt', null, 'Compatibility'), h('dd', null, String(meta.compatLevel)),
      h('dt', null, 'Sign-in'), h('dd', null, meta.authSummary));
  }

  function sideCard(ctx, side) {
    var conn = (ctx.state.connections && ctx.state.connections[side.key]) || { saved: false };
    var st = local[side.key];
    var transfer = ctx.state.phases.filter(function (p) { return p.name === 'transfer'; })[0];
    var locked = transfer && transfer.status !== 'pending';

    if (conn.saved && !st.editing) {
      return h('section', { class: 'card setup-card', 'aria-label': side.title + ' connection' },
        h('div', { class: 'card-h' }, C.icon('database'), h('span', { class: 'spacer' }, side.title), C.badge('approved', 'Saved')),
        h('div', { class: 'card-b' },
          h('div', { class: 'setup-describe' }, conn.describe),
          metaList(conn.meta),
          locked ? h('p', { class: 'small muted' }, 'Connections are locked while a transfer exists.')
            : h('div', { class: 'row' }, h('button', {
              type: 'button',
              class: 'btn btn-sm',
              on: { click: function () { st.editing = true; st.result = null; ctx.refresh(); } },
            }, 'Change'))));
    }

    var input = h('input', {
      id: 'cs-' + side.key,
      class: 'input mono',
      type: st.reveal ? 'text' : 'password',
      autocomplete: 'off',
      spellcheck: 'false',
      placeholder: 'Server=…;Database=…;Integrated Security=true;TrustServerCertificate=true',
      'aria-describedby': 'cs-hint-' + side.key,
    });
    input.value = st.value;
    input.addEventListener('input', function () { st.value = input.value; });

    var reveal = h('button', {
      type: 'button',
      class: 'btn icon-btn',
      'aria-label': st.reveal ? 'Hide connection string' : 'Show connection string',
      'aria-pressed': st.reveal ? 'true' : 'false',
      on: { click: function () { st.reveal = !st.reveal; input.type = st.reveal ? 'text' : 'password'; DBM.clear(reveal).appendChild(C.icon(st.reveal ? 'eyeOff' : 'eye')); } },
    }, C.icon(st.reveal ? 'eyeOff' : 'eye'));

    var resultHost = h('div', { 'aria-live': 'polite' }, renderResult(st.result));
    var testBtn = h('button', { type: 'button', class: 'btn' }, 'Test');
    var saveBtn = h('button', { type: 'button', class: 'btn btn-primary' }, 'Save');

    function value() {
      var v = input.value.trim();
      if (!v) { input.focus(); C.toast('Paste a connection string first.', 'warn'); }
      return v;
    }

    testBtn.addEventListener('click', function () {
      var cs = value();
      if (!cs) return;
      C.busy(testBtn, function () {
        return ctx.api.post('/api/connections/' + side.key + '/test', { connectionString: cs }).then(function (r) {
          st.result = r.ok ? { ok: true, meta: r.meta } : { ok: false, error: r.error };
        }, function (err) {
          st.result = { ok: false, error: C.errorText(err) };
        }).then(function () {
          DBM.clear(resultHost).appendChild(renderResult(st.result));
        });
      });
    });

    saveBtn.addEventListener('click', function () {
      var cs = value();
      if (!cs) return;
      C.busy(saveBtn, function () {
        return ctx.api.post('/api/connections/' + side.key, { connectionString: cs }).then(function () {
          st.value = '';
          st.editing = false;
          st.reveal = false;
          st.result = null;
          C.toast(side.title + ' connection saved.', 'ok');
          ctx.refresh();
        }, function (err) {
          st.result = { ok: false, error: C.errorText(err) };
          DBM.clear(resultHost).appendChild(renderResult(st.result));
        });
      });
    });

    // A real <form> (Enter = Test) keeps password managers and screen readers happy; nothing is ever submitted.
    var form = h('form', { class: 'card-b', autocomplete: 'off', on: { submit: function (e) { e.preventDefault(); testBtn.click(); } } },
        h('div', { class: 'field' },
          h('label', { class: 'field-label', for: 'cs-' + side.key }, 'Connection string'),
          h('div', { class: 'input-group' }, input, reveal),
          h('span', { class: 'field-hint', id: 'cs-hint-' + side.key }, side.hint + ' SQL login, Windows integrated and every Microsoft Entra ID mode work as-is.')),
        h('div', { class: 'row-wrap' }, testBtn, saveBtn,
          conn.saved ? h('button', {
            type: 'button',
            class: 'btn btn-ghost',
            on: { click: function () { st.editing = false; st.value = ''; st.result = null; ctx.refresh(); } },
          }, 'Cancel') : null),
        resultHost);

    return h('section', { class: 'card setup-card', 'aria-label': side.title + ' connection' },
      h('div', { class: 'card-h' }, C.icon('database'), h('span', { class: 'spacer' }, side.title),
        conn.saved ? C.badge('stale', 'Editing') : C.badge('pending', 'Not set')),
      form);
  }

  function renderResult(result) {
    if (!result) return h('span');
    if (result.ok) {
      return h('div', { class: 'stack-sm' },
        C.notice('ok', 'Connected to ' + result.meta.server + ' / ' + result.meta.database + '.'),
        metaList(result.meta));
    }
    return C.notice('err', result.error || 'Connection failed.');
  }

  DBM.views = DBM.views || {};
  DBM.views.setup = {
    title: 'Setup',
    render: function (root, ctx) {
      var c = ctx.state.connections || {};
      var both = c.src && c.src.saved && c.tgt && c.tgt.saved;
      root.appendChild(h('div', { class: 'page' },
        h('div', { class: 'page-h' }, h('div', null,
          h('h1', { class: 'h1' }, 'Connect the two databases'),
          h('p', { class: 'muted' }, 'Connection strings are encrypted on this computer and never shown to Claude, logs or reports.'))),
        h('div', { class: 'grid-2' }, SIDES.map(function (side) { return sideCard(ctx, side); })),
        both ? h('div', { style: { marginTop: '16px' } }, C.notice('info',
          'Both databases are connected. Discovery runs automatically — the next phases appear on the left as they become ready.')) : null));
    },
  };
})(window.DBM = window.DBM || {});
```

`plugins/db-migrate/engine/Dbm/wwwroot/js/views/pending.js`:

```js
/* Fallback view: phases without their own view, or waiting on a job / on Claude. Shows job status, live log, retry,
   and — when an artifact exists but no dedicated view is loaded yet — the raw artifact with the review bar. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var logHost = null;
  var logEmpty = null;

  var MESSAGES = {
    pending: ['Waiting for earlier phases', 'This phase starts automatically once the phases before it are approved.'],
    stale: ['Needs to run again', 'An earlier phase changed. This phase re-runs when that phase is approved again.'],
    running: ['Working…', 'A background job is preparing this phase. You can close the browser; it keeps running.'],
    drafting: ['Claude is preparing the first version', 'The script draft is ready; Claude is turning it into a reviewable version.'],
    reworking: ['Claude is working on your feedback', 'A new version will appear here with a response to each comment.'],
    approved: ['Approved', 'Nothing to do here.'],
    awaiting_review: ['Ready for your review', 'Review the version below, then approve it or request changes.'],
  };

  function latestJob(ctx) {
    return (ctx.state.jobs || []).filter(function (j) { return j.phase === ctx.phase; })[0] || null;
  }

  function logLine(entry) {
    return h('span', { class: ['line', entry.level === 'warn' && 'log-warn', entry.level === 'error' && 'log-error'] },
      h('span', { class: 'muted' }, DBM.fmt.ts(entry.ts).slice(11) + '  '), entry.message);
  }

  function jobCard(ctx, job) {
    if (!job) return null;
    var failed = job.status === 'failed';
    var retry = null;
    if (failed && ctx.phaseRow.status === 'running') {
      retry = h('button', { type: 'button', class: 'btn btn-primary btn-sm' }, C.icon('refresh'), 'Retry');
      retry.addEventListener('click', function () {
        C.busy(retry, function () {
          return ctx.api.post('/api/phase/' + ctx.phase + '/retry').then(function () { ctx.refresh(); },
            function (err) { C.toast(C.errorText(err), 'err'); });
        });
      });
    }
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('span', { class: 'spacer' }, 'Job ', h('span', { class: 'mono' }, job.kind)), C.badge(job.status)),
      h('div', { class: 'card-b stack-sm' },
        job.status === 'running' || job.status === 'queued' ? C.progress(0, 1, { indeterminate: true }) : null,
        h('dl', { class: 'meta' },
          h('dt', null, 'Started'), h('dd', null, job.startedAt ? DBM.fmt.rel(job.startedAt) : 'queued'),
          job.endedAt ? h('dt', null, 'Ended') : null, job.endedAt ? h('dd', null, DBM.fmt.ts(job.endedAt)) : null),
        failed ? C.notice('err', job.error || 'The job failed.') : null,
        retry ? h('div', { class: 'row' }, retry) : null));
  }

  function logCard(ctx) {
    var lines = (ctx.logs || []).slice(-200);
    logEmpty = h('p', { class: 'muted small', hidden: lines.length > 0 }, 'Log messages from background jobs appear here.');
    logHost = h('pre', { class: 'code pending-log', 'aria-label': 'Live log', hidden: lines.length === 0 }, lines.map(logLine));
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, 'Live log'),
      h('div', { class: 'card-b' }, logEmpty, logHost));
  }

  function artifactCard(ctx) {
    var current = ctx.artifact;
    if (!current) return null;
    var pre = h('pre', { class: 'code plain' }, JSON.stringify(current.payload, null, 2));
    var card = h('section', { class: 'card' },
      h('div', { class: 'card-h' }, 'Artifact v' + current.version, h('span', { class: 'spacer' }),
        h('span', { class: 'small muted' }, 'Raw view — the dedicated screen for this phase arrives in a later release.')),
      h('div', { class: 'card-b' }, pre));
    ctx.commentable(card.querySelector('.card-b'), null, 'this version');
    return card;
  }

  DBM.views = DBM.views || {};
  DBM.views.pending = {
    title: 'Status',
    render: function (root, ctx) {
      var status = ctx.phaseRow.status;
      var job = latestJob(ctx);
      var msg = MESSAGES[status] || [DBM.statusLabel(status), ''];
      if (status === 'running' && job && job.status === 'failed') msg = ['The job failed', 'Fix the cause (see the error below) and retry.'];
      var agentHint = (status === 'drafting' || status === 'reworking') && !ctx.state.project.agentOnline
        ? C.notice('warn', h('span', null, 'Claude is not connected. In Claude Code run ', h('code', null, '/db-migrate resume'), ' to continue.'))
        : null;
      var hasArtifact = !!ctx.artifact && (status === 'awaiting_review' || status === 'approved' || status === 'reworking');
      var jobFailed = !!job && job.status === 'failed';
      var busy = !hasArtifact && !jobFailed && (status === 'running' || status === 'drafting' || status === 'reworking');

      root.appendChild(h('div', { class: 'page' },
        h('div', { class: 'page-h' },
          h('div', null, h('h1', { class: 'h1' }, DBM.phaseTitle(ctx.phase)), h('p', { class: 'muted' }, msg[1])),
          C.badge(status)),
        hasArtifact ? C.reviewBar(ctx) : null,
        h('div', { class: 'stack' },
          agentHint,
          busy ? h('div', { class: 'row muted' }, h('span', { class: 'spinner' }), msg[0]) : null,
          hasArtifact ? artifactCard(ctx) : null,
          jobCard(ctx, job),
          status === 'running' || (job && job.status !== 'done') ? logCard(ctx) : null)));
    },
    onEvent: function (evt, ctx) {
      if (evt.type !== 'log' || !logHost || !logHost.isConnected) return;
      logHost.hidden = false;
      if (logEmpty) logEmpty.hidden = true;
      logHost.appendChild(logLine({ ts: new Date().toISOString(), level: evt.data.level, message: evt.data.message }));
      while (logHost.childNodes.length > 200) logHost.removeChild(logHost.firstChild);
      logHost.scrollTop = logHost.scrollHeight;
    },
  };
})(window.DBM = window.DBM || {});
```

`plugins/db-migrate/engine/Dbm/wwwroot/js/app.js`:

```js
/* App shell: routing over the phase stepper, live refresh from SSE, banners, theme, export (read-only) mode. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var REVIEWABLE = { analysis: true, mapping: true, sql: true };
  var REFRESH_EVENTS = {
    state_changed: 1, artifact_created: 1, feedback_changed: 1, job_started: 1, job_done: 1, job_failed: 1,
    paused: 1, resumed: 1, agent_presence: 1, drift_detected: 1, transfer_run_changed: 1,
  };
  var THEMES = ['system', 'light', 'dark'];

  var S = {
    state: null,
    current: null,        // phase shown
    picked: false,        // user chose a phase explicitly
    pickedDefault: null,  // the default phase at the moment the user picked
    artifact: null,       // GET /api/artifact/{phase}
    viewed: null,         // {version, author, summary, payload} being shown (null = current)
    viewedVersion: null,  // explicit older version, or null for current
    feedback: [],
    logs: [],
    live: true,
    viewKey: null,
    view: null,
    ctx: null,
    refreshTimer: null,
    loading: null,
  };

  /* ------------------------------------------------------------------ theme */

  function theme() {
    try { var t = localStorage.getItem('dbm.theme'); return THEMES.indexOf(t) >= 0 ? t : 'system'; } catch (e) { return 'system'; }
  }

  function setTheme(t) {
    try { localStorage.setItem('dbm.theme', t); } catch (e) { /* storage blocked: session only */ }
    if (t === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', t);
  }

  function cycleTheme() {
    setTheme(THEMES[(THEMES.indexOf(theme()) + 1) % THEMES.length]);
    renderChrome();
  }

  /* ------------------------------------------------------------------ routing */

  function defaultPhase(state) {
    var open = state.phases.filter(function (p) { return p.status !== 'approved'; })[0];
    return open ? open.name : 'complete';
  }

  function phaseRow(name) {
    return S.state.phases.filter(function (p) { return p.name === name; })[0] || S.state.phases[0];
  }

  /** Which view renders a phase (C9): dedicated view when it has something to show, else the pending view. */
  function pickView(row) {
    if (row.name === 'setup') return DBM.views.setup;
    var custom = DBM.views[row.name];
    if (REVIEWABLE[row.name]) {
      var reviewable = (row.status === 'awaiting_review' || row.status === 'reworking' || row.status === 'approved') && row.currentVersion != null;
      return custom && reviewable ? custom : DBM.views.pending;
    }
    return custom && row.status !== 'pending' && row.status !== 'stale' ? custom : DBM.views.pending;
  }

  function select(name) {
    if (name === S.current) return;
    S.current = name;
    S.picked = name !== defaultPhase(S.state);
    S.pickedDefault = defaultPhase(S.state);
    S.viewedVersion = null;
    if (window.location.hash !== '#/' + name) window.history.replaceState(null, '', '#/' + name);
    refresh(true);
  }

  /* ------------------------------------------------------------------ data */

  function loadPhaseData(row) {
    if (row.currentVersion == null || row.name === 'setup') {
      S.artifact = null;
      S.viewed = null;
      S.feedback = [];
      return Promise.resolve();
    }
    var base = '/api/artifact/' + row.name;
    return Promise.all([
      DBM.api.get(base),
      DBM.api.get('/api/feedback/' + row.name),
      S.viewedVersion != null ? DBM.api.get(base + '/' + S.viewedVersion) : Promise.resolve(null),
    ]).then(function (r) {
      S.artifact = r[0];
      S.feedback = r[1] || [];
      S.viewed = r[2] || (r[0] && r[0].current) || null;
    });
  }

  function refresh(force) {
    if (S.loading) { S.loading.again = S.loading.again || force || true; return S.loading.promise; }
    var job = { again: false };
    job.promise = DBM.api.get('/api/state').then(function (state) {
      S.state = state;
      var def = defaultPhase(state);
      if (!S.current || !S.picked || S.pickedDefault !== def) {
        if (S.current !== def) S.viewedVersion = null;
        S.current = def;
        S.picked = false;
      }
      var row = phaseRow(S.current);
      return loadPhaseData(row).then(function () {
        renderChrome();
        var key = viewKey(row);
        if (force === true || key !== S.viewKey) {
          S.viewKey = key;
          renderView(row);
        }
        DBM.review.refresh(S.ctx);
      });
    }).catch(function (err) {
      renderChrome();
      if (err && err.status === 401) {
        renderFatal('This page needs its access link.', 'Open the UI again from Claude Code with /db-migrate ui (or run "dbm ui").');
      } else {
        C.toast(C.errorText(err), 'err');
      }
    }).then(function () {
      S.loading = null;
      if (job.again) return refresh(job.again === true ? false : job.again);
    });
    S.loading = job;
    return job.promise;
  }

  function scheduleRefresh() {
    clearTimeout(S.refreshTimer);
    S.refreshTimer = setTimeout(function () { refresh(false); }, 150);
  }

  /** Re-render the view only when something it shows changed (keeps inputs and focus stable). */
  function viewKey(row) {
    var st = S.state;
    return JSON.stringify([
      row.name, row.status, row.currentVersion, row.approvedVersion, S.viewedVersion,
      S.feedback.map(function (f) { return [f.id, f.status]; }),
      st.project.paused, st.project.agentOnline,
      st.connections && [st.connections.src, st.connections.tgt],
      (st.jobs || []).filter(function (j) { return j.phase === row.name; }).map(function (j) { return [j.id, j.status]; }),
      st.phases.map(function (p) { return p.status; }),
      st.drift, st.transfer,
    ]);
  }

  /* ------------------------------------------------------------------ rendering */

  function makeCtx(row) {
    var art = S.artifact;
    var ctx = {
      state: S.state,
      phase: row.name,
      phaseRow: row,
      artifact: S.viewed || null,                     // {version, author, summary, createdAt, payload} being shown
      versions: art ? art.versions || [] : [],        // [ArtifactMeta], ascending
      latestVersion: art && art.current ? art.current.version : null,
      version: S.viewed ? S.viewed.version : null,
      export: null,
      feedback: S.feedback,
      logs: S.logs,
      readOnly: row.status !== 'awaiting_review' || S.viewedVersion != null,
      api: DBM.api,
      refresh: function () { return refresh(true); },
      toast: C.toast,
      commentable: function (el, anchor, label) { return commentable(ctx, el, anchor, label); },
      setVersion: function (v) {
        var latest = art && art.current ? art.current.version : null;
        S.viewedVersion = v == null || v === latest ? null : v;
        refresh(true);
      },
      openFeedback: function (anchor, label) { DBM.review.openFeedback(ctx, anchor, label); },
      openHistory: function () { DBM.review.openHistory(ctx); },
    };
    return ctx;
  }

  function commentable(ctx, el, anchor, label) {
    if (!el || window.DBM_EXPORT) return el;
    var pending = ctx.feedback.some(function (f) { return (f.anchor || null) === (anchor || null) && (f.status === 'draft' || f.status === 'open'); });
    if (pending && anchor) el.classList.add('has-feedback');
    if (ctx.readOnly) return el;
    el.classList.add('commentable');
    el.appendChild(h('button', {
      type: 'button',
      class: 'comment-btn',
      title: 'Comment on ' + (label || DBM.anchorLabel(anchor)),
      'aria-label': 'Comment on ' + (label || DBM.anchorLabel(anchor)),
      on: { click: function (e) { e.preventDefault(); e.stopPropagation(); ctx.openFeedback(anchor, label); } },
    }, C.icon('comment')));
    return el;
  }

  function renderChrome() {
    var top = document.getElementById('topbar');
    DBM.clear(top).appendChild(C.topbar(S.state, {
      live: S.live,
      theme: theme(),
      onTheme: cycleTheme,
      onPause: function () { DBM.api.post('/api/pause').then(function () { refresh(false); }, function (e) { C.toast(C.errorText(e), 'err'); }); },
      onResume: function () { DBM.api.post('/api/resume').then(function () { refresh(false); }, function (e) { C.toast(C.errorText(e), 'err'); }); },
    }));
    if (!S.state) return;
    var stepper = document.getElementById('stepper');
    DBM.clear(stepper).appendChild(C.stepper(S.state, { current: S.current, onSelect: select }));
    var active = stepper.querySelector('.step.is-active');
    if (active && stepper.scrollWidth > stepper.clientWidth) active.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    renderBanners();
    document.title = S.state.project.name + ' · ' + DBM.phaseTitle(S.current) + ' — db-migrate';
  }

  function renderBanners() {
    var host = DBM.clear(document.getElementById('banners'));
    var st = S.state;
    var next = st.next || {};
    if (st.project.paused) {
      host.appendChild(C.banner('info', 'Paused — Claude will not start new work until you resume.',
        h('button', { type: 'button', class: 'btn btn-primary btn-sm', on: { click: function () { DBM.api.post('/api/resume').then(function () { refresh(false); }); } } }, 'Resume')));
    }
    if (next.action === 'agent' && !st.project.agentOnline) {
      host.appendChild(C.banner('warn', h('span', null, 'Agent offline — run ', h('code', null, '/db-migrate resume'), ' in Claude Code to let Claude continue ', DBM.phaseTitle(next.phase), '.')));
    }
    if (next.action === 'stop' && next.reason === 'job_failed') {
      var retry = h('button', { type: 'button', class: 'btn btn-sm' }, C.icon('refresh'), 'Retry');
      retry.addEventListener('click', function () {
        C.busy(retry, function () {
          return DBM.api.post('/api/phase/' + next.phase + '/retry').then(function () { refresh(false); }, function (e) { C.toast(C.errorText(e), 'err'); });
        });
      });
      host.appendChild(C.banner('err', h('span', null, 'A background job failed in ', h('strong', null, DBM.phaseTitle(next.phase)), ': ', next.summary || 'unknown error'), retry));
    }
  }

  function renderView(row) {
    var root = DBM.clear(document.getElementById('view'));
    var view = pickView(row);
    S.view = view;
    S.ctx = makeCtx(row);
    try {
      view.render(root, S.ctx);
    } catch (err) {
      root.appendChild(h('div', { class: 'page' }, C.notice('err', 'This screen failed to render: ' + (err && err.message))));
      if (window.console) console.error(err);
    }
  }

  function renderFatal(title, text) {
    DBM.clear(document.getElementById('view')).appendChild(h('div', { class: 'page' }, C.emptyState(title, text)));
  }

  /* ------------------------------------------------------------------ events */

  function onEvent(evt) {
    if (evt.type === 'log') {
      S.logs.push({ ts: new Date().toISOString(), level: evt.data.level || 'info', message: evt.data.message || '' });
      if (S.logs.length > 300) S.logs.splice(0, S.logs.length - 300);
    }
    if (S.view && S.view.onEvent) {
      try { S.view.onEvent(evt, S.ctx); } catch (err) { if (window.console) console.error(err); }
    }
    if (REFRESH_EVENTS[evt.type]) scheduleRefresh();
  }

  function onStatus(connected) {
    if (S.live === connected) return;
    S.live = connected;
    renderChrome();
    if (connected) scheduleRefresh();
  }

  /* ------------------------------------------------------------------ export mode */

  /** A standalone export may not contain index.html's skeleton: build whatever container is missing. */
  function ensureShell() {
    if (!document.getElementById('app')) {
      document.body.appendChild(h('div', { id: 'app', class: 'app' },
        h('header', { id: 'topbar', class: 'topbar' }),
        h('div', { id: 'banners', class: 'banners' }),
        h('nav', { id: 'stepper', class: 'stepper', 'aria-label': 'Migration phases' }),
        h('main', { id: 'view', class: 'main', tabindex: '-1' })));
    }
    if (!document.getElementById('drawer')) document.body.appendChild(h('aside', { id: 'drawer', class: 'drawer', 'aria-hidden': 'true' }));
    if (!document.getElementById('toasts')) document.body.appendChild(h('div', { id: 'toasts', class: 'toasts', role: 'status' }));
  }

  /**
   * DBM_EXPORT = {view, title, payload: {artifact: {version, author, summary, createdAt, payload}, state?, …}}.
   * Read-only, no API (api.js is not part of an export), no SSE, no review actions.
   */
  function renderExport(x) {
    ensureShell();
    document.body.classList.add('is-export');
    DBM.clear(document.getElementById('topbar')).appendChild(C.topbar(null, { title: x.title, theme: theme(), onTheme: function () { cycleTheme(); renderExport(x); } }));
    var view = DBM.views[x.view] || DBM.views.pending;
    var root = DBM.clear(document.getElementById('view'));
    var data = x.payload || {};
    var a = data.artifact || {};
    var artifact = {
      version: a.version == null ? null : a.version,
      author: a.author || null,
      summary: a.summary || null,
      createdAt: a.createdAt || null,
      payload: a.payload === undefined ? null : a.payload,
    };
    var row = { name: x.view, status: 'approved', currentVersion: artifact.version, approvedVersion: artifact.version };
    var ctx = {
      state: data.state || {
        project: { name: x.title, paused: false, agentOnline: false }, phases: [row], jobs: [], connections: {},
        drift: { src: false, tgt: false }, transfer: null, next: {},
      },
      phase: x.view,
      phaseRow: row,
      artifact: artifact,
      versions: [],
      latestVersion: artifact.version,
      version: artifact.version,
      export: data,
      feedback: [],
      logs: [],
      readOnly: true,
      api: null,
      refresh: function () { return Promise.resolve(); },
      toast: C.toast,
      commentable: function (el) { return el; },
      setVersion: function () {},
      openFeedback: function () {},
      openHistory: function () {},
    };
    document.title = x.title;
    view.render(root, ctx);
  }

  /* ------------------------------------------------------------------ start */

  function start() {
    if (window.DBM_EXPORT) {
      renderExport(window.DBM_EXPORT);
      return;
    }
    var fromHash = /^#\/([a-z_]+)$/.exec(window.location.hash || '');
    if (fromHash) { S.current = fromHash[1]; S.picked = true; }
    window.addEventListener('hashchange', function () {
      var m = /^#\/([a-z_]+)$/.exec(window.location.hash || '');
      if (m && S.state) select(m[1]);
    });
    renderChrome();
    refresh(true).then(function () {
      if (S.picked) S.pickedDefault = defaultPhase(S.state);
    });
    DBM.api.events(onEvent, onStatus);
  }

  DBM.app = { refresh: refresh, select: select, state: function () { return S.state; } };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})(window.DBM = window.DBM || {});
```

- [ ] **Step 9: Run all automated tests — they pass**

```bash
node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
```

Expected: node `ℹ pass 11`; dotnet `Passed!  - Failed:     0, Passed:   202` (StaticFilesTests 2 added).

- [ ] **Step 10: Manual verification in the browser**

The server serves `engine/dist/wwwroot`, so rebuild the launcher output first. In PowerShell:

```powershell
$env:DBM_REBUILD = '1'; plugins/db-migrate/bin/dbm.cmd version; Remove-Item Env:DBM_REBUILD
$ws = "$env:TEMP\dbm-ui-check"; New-Item -ItemType Directory -Force $ws | Out-Null
plugins/db-migrate/bin/dbm.cmd init "LegacyShop to ShopV2" --workspace $ws
```

The browser opens `http://127.0.0.1:<port>/?t=<token>`. Use a desktop width first, then repeat the layout checks at ~400 px (DevTools device toolbar, 400 × 800) and in both themes (theme button at the top right cycles system → light → dark; also DevTools "Rendering → Emulate prefers-color-scheme").

1. **Shell:** top bar shows the `dbm` mark, the project name, "Agent offline" pill, Pause button and theme button; the left stepper lists Setup … Report with numbered dots, Setup highlighted as "Awaiting review"; no errors in the DevTools console.
2. **Setup:** two cards (Source, Target) with a masked connection-string field, eye toggle (shows/hides the text), Test and Save. Paste `Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true` into Source and press Test: a green "Connected to … / master." notice and a metadata list (server, database, version, edition, collation, compatibility 170/160…, sign-in `integrated`). Enter a wrong database name and Test: a red notice with the SQL error, no password shown. Save Source, then Target with `Database=tempdb`: each card switches to the saved view (describe line without secrets, metadata, Change button) and a toast confirms.
3. **Jobs and banners (M1 has no discover handler yet):** right after the second save the UI selects Discovery by itself; a red banner reads "A background job failed in Discovery: no handler for discover" with Retry; the page shows the failed `discover` job card with the same error. Retry re-queues the job (it fails again — expected until T2.5). The stepper shows Setup with a green ✓.
4. **Review loop:** seed a reviewable Analysis version. Save this script as `$env:TEMP\dbm-seed.cjs` (Node 24 ships `node:sqlite`; the experimental warning is harmless):
   ```js
   const { DatabaseSync } = require('node:sqlite');
   const db = new DatabaseSync(process.argv[2]);
   const now = new Date().toISOString();
   const v0 = { summary: 'Rules found 3 risks', findings: [{ id: 'heap:dbo.AUDIT_LOG', severity: 'medium' }], stats: { tables: 8, rows: 16207 } };
   const v1 = { ...v0, summary: 'Two tables need attention: AUDIT_LOG is a heap and ORD_HDR has orphan rows.', narrative: 'The source is small.' };
   db.exec("UPDATE phase SET status='approved', approved_version=0 WHERE name='discovery'");
   const add = db.prepare("INSERT OR REPLACE INTO artifact (phase, version, payload_json, author, summary, created_at) VALUES ('analysis', ?, ?, ?, ?, ?)");
   add.run(0, JSON.stringify(v0), 'script', v0.summary, now);
   add.run(1, JSON.stringify(v1), 'agent', v1.summary, now);
   db.exec("UPDATE phase SET status='awaiting_review', current_version=1 WHERE name='analysis'");
   db.prepare("INSERT INTO feedback (phase, version, anchor, text, status, response, responded_version, created_at) VALUES ('analysis', 0, 'narrative', ?, 'addressed', ?, 1, ?)")
     .run('Explain **why** the heap matters for `resume`.', 'Added a paragraph:\n- heaps cannot resume mid-table\n- they reload in one transaction', now);
   db.prepare("INSERT INTO feedback (phase, version, anchor, text, status, created_at) VALUES ('analysis', 1, 'table:src:dbo.ORD_HDR', ?, 'draft', ?)")
     .run('List the two orphan order ids.', now);
   db.prepare("INSERT INTO event (ts, type, payload_json) VALUES (?, 'state_changed', '{}')").run(now);
   console.log('seeded');
   ```
   ```powershell
   node "$env:TEMP\dbm-seed.cjs" "$ws\.dbmigrate\state.db"
   ```
   Within about a second and without a reload (SSE → EventPump) the UI moves to Analysis: Discovery ✓, Analysis "Awaiting review · v1"; a sticky review bar with the version picker "v1 · agent (current)", an "Awaiting review" badge, the `agent` tag, the summary, Feedback (2), History, "Request changes (1)" and Approve; below it the raw artifact card. Hovering the artifact body shows a comment button in its top-right corner.
5. **Feedback drawer:** Feedback (2) opens a right drawer (full width at 400 px) with tabs Feedback/History, a comment box ("General comment on v1"), "Drafts 1" (chip "Source table dbo.ORD_HDR", × deletes it) and "Answered 1" rendering **bold**, `code` and a bullet list inside a green "Addressed by Claude in v1" block. Clicking the artifact's comment button pre-fills a "General" target; add a comment (Ctrl+Enter works): it appears under Drafts and the bar shows "Request changes (2)". Delete it: back to (1). Esc closes the drawer.
6. **History and diff:** History lists v1 (agent) and v0 (script); Compare v0 → v1 shows red/green lines, a "… 3 unchanged lines" separator and `+3 −2`. "View" on v0 shows a notice "You are viewing v0. The current version is v1." with "Show current"; Approve/Request changes are hidden while an old version is shown.
7. **Actions:** Approve opens a "Not ready to approve" dialog ("No module is registered for phase analysis." — expected until T2.7); Esc closes it and returns focus. Request changes shows the toast "Sent 1 comment to Claude." and the phase becomes "Reworking" (read-only; the drawer shows "Claude is working on the comments below.").
8. **Pause/Resume and presence:** Pause → blue banner "Paused — Claude will not start new work until you resume." and a Resume button (banner and top bar); Resume removes it. In a second terminal run `plugins/db-migrate/bin/dbm.cmd await --timeout 20 --workspace $ws`: the pill turns green "Agent online" within a second (it stays online up to 2 minutes after the command ends — `agent_seen_at`).
9. **Themes and layout:** in light and dark the text, muted text, badges (indigo awaiting, green approved/addressed, red failed), code block and diff are all readable; the theme choice survives a reload. At 400 px the stepper becomes a horizontal strip that scrolls (active step visible), the page itself never scrolls sideways (`document.documentElement.scrollWidth` equals `400` in the console), the review bar wraps onto several lines and the setup cards stack.
10. **Keyboard:** Tab reaches the skip link, top bar buttons, stepper steps (Enter switches phase), review bar controls and drawer controls, always with a visible focus ring.
11. **Stop:** `plugins/db-migrate/bin/dbm.cmd stop --workspace $ws` → the page shows the amber "Reconnecting…" pill; `dbm.cmd ui --workspace $ws` starts a new server and opens a fresh tab.

- [ ] **Step 11: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/wwwroot plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(ui): app shell, review loop (feedback drawer, history diff), setup and pending views" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

### Task 1.10: Orchestrator skill + M1 end-to-end loop test

**Files:**
- Create: `plugins/db-migrate/skills/db-migrate/SKILL.md`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Workflow/M1LoopTests.cs`

**Interfaces:**
- Consumes: the whole engine (T1.2–T1.6) through `FakeServices`; the CLI surface of T1.8 (in the skill).
- Produces: the `/db-migrate` skill (`name: db-migrate`, `argument-hint: "[resume|status|ui|stop]"`) — the loop contract later milestones rely on: `dbm init` → `dbm next` → `agent` = dispatch `db-migrate:<agent>` with `Work packet: <packet>\nWrite your patch to: <patchPath>\nFollow your playbook. Reply with one line.` then `dbm apply <patchPath>` (one retry with the errors appended) / `await` = background `dbm await` and end the turn / `stop` = report. Agents (T2.7, T3.4, T4.4) must honour exactly that prompt shape.

- [ ] **Step 1: Write the end-to-end loop test**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Workflow/M1LoopTests.cs` — drives init → connections → jobs → agent draft → review → feedback → rework → approve for Analysis, Mapping and SQL, reading the real packet files and writing real patch files, until `Next()` is `await/execute`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

/// <summary>
/// The whole M1 orchestration loop at engine level, exactly as SKILL.md drives it:
/// init → connections → jobs → agent draft → review → feedback → rework → approve … → await execute.
/// </summary>
public class M1LoopTests
{
    private static async Task RunJobs(DbmServices s) => await new JobRunner(s).RunPendingAsync(CancellationToken.None);

    /// <summary>What a subagent does: read the packet, write a patch answering every feedback item.</summary>
    private static Patch AgentPatch(NextAction action, string summary)
    {
        var packet = JsonNode.Parse(File.ReadAllText(action.Packet!))!;
        var responses = packet["feedback"]!.AsArray()
            .Select(f => new FeedbackResponse(f!["id"]!.GetValue<long>(), "addressed", $"Handled: {f["text"]!.GetValue<string>()}"))
            .ToList();
        var patch = new Patch(packet["phase"]!.GetValue<string>(), packet["baseVersion"]!.GetValue<int>(),
            [new PatchOp("replace", "/summary", JsonValue.Create(summary))], responses, summary);
        File.WriteAllText(action.PatchPath!, Json.Serialize(patch));
        return Patch.Parse(File.ReadAllText(action.PatchPath!));
    }

    [Fact]
    public async Task Full_review_loop_through_all_three_phases_until_execution_awaits()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        using var s = FakeServices.Open(tw.Ws);                       // `dbm init`

        Assert.Equal("setup", s.Workflow.Next().Reason);

        FakeServices.SaveConnections(s);                              // UI: both connections saved
        Assert.Equal(new NextAction("await", Reason: "job", Phase: "discovery"), s.Workflow.Next());
        await RunJobs(s);                                             // server jobs: discover + analyze

        foreach (var (phase, agent) in new[] { (PhaseName.Analysis, "schema-analyst"), (PhaseName.Mapping, "mapping-architect"), (PhaseName.Sql, "sql-engineer") })
        {
            var name = phase.Text();

            var draft = s.Workflow.Next();
            Assert.Equal(("agent", agent, name, "draft"), (draft.Action, draft.Agent, draft.Phase, draft.Mode));
            var v1 = s.Workflow.ApplyPatch(AgentPatch(draft, $"{name} v1"));
            Assert.True(v1.Ok, string.Join("; ", v1.Errors));
            Assert.Equal(("await", "review", name), (s.Workflow.Next().Action, s.Workflow.Next().Reason, s.Workflow.Next().Phase));

            var item = s.Feedback.Add(phase, v1.Version!.Value, $"narrative", $"Tighten the {name} summary");
            s.Workflow.RequestChanges(phase);
            var rework = s.Workflow.Next();
            Assert.Equal(("agent", "rework"), (rework.Action, rework.Mode));

            var v2 = s.Workflow.ApplyPatch(AgentPatch(rework, $"{name} v2"));
            Assert.True(v2.Ok, string.Join("; ", v2.Errors));
            Assert.Equal(FeedbackStatus.Addressed, s.Feedback.Get(item.Id)!.Status);
            Assert.Equal("review", s.Workflow.Next().Reason);

            s.Workflow.Approve(phase);
            Assert.Equal(2, s.Phases.Get(phase).ApprovedVersion);
            await RunJobs(s);                                         // automap / sqlgen after approval
        }

        Assert.Equal(new NextAction("await", Reason: "execute", Phase: "ready", Url: ""), s.Workflow.Next());
        Assert.All(new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql },
            p => Assert.Equal(PhaseStatus.Approved, s.Phases.Get(p).Status));
    }
}
```

- [ ] **Step 2: Run it**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~M1LoopTests"
```

Expected: `Passed!  - Failed:     0, Passed:     1`. This test only composes T1.2–T1.6, so it passes on the first run; it pins the integrated loop. If it fails, the bug is in the engine — fix the engine, never the test.

- [ ] **Step 3: Write the skill**

`plugins/db-migrate/skills/db-migrate/SKILL.md`:

```markdown
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

- NEVER ask for, accept, print or repeat a connection string. Connections are entered only in the browser UI.
- Do not read `.dbmigrate/state.db`, `server.json` or work packets yourself — subagents read their packet.
- Do not edit artifacts yourself; changes come only from subagent patches applied with `dbm apply`.

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
```

- [ ] **Step 4: Validate the plugin**

```bash
claude plugin validate plugins/db-migrate
claude plugin validate .
```

Expected: `✔ Validation passed` twice (the skill's frontmatter is part of the check).

- [ ] **Step 5: Optional smoke test of the skill wiring**

In a scratch folder start Claude Code with the local plugin (`claude --plugin-dir <repo>/plugins/db-migrate`) and type `/db-migrate`. Expected: Claude runs `dbm init`, says in one line that the UI opened and the connection strings go into the browser, runs `dbm await` in the background and ends its turn. It must never ask for a connection string in chat. Stop with `/db-migrate stop`. The full install-from-git flow is verified in T6.4.

- [ ] **Step 6: Run the complete M1 test suite**

```bash
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"
dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"
node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"
```

Expected: `Passed: 203` (unit), `Passed: 8` (integration), `pass 11` (node), zero failures, no build warnings, and each command returns promptly.

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/skills plugins/db-migrate/engine/Dbm.Tests
git commit -m "feat(skill): /db-migrate orchestrator loop and M1 end-to-end engine test" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49"
```

---

## Contract notes

**Moved between tasks / files not in the file map**
- `DbmServices.cs`, `ModuleRegistry.cs`, `JobRegistry.cs`, `DbEventSink.cs` (new file) and `Jobs/IJobHandler.cs` are created in **T1.5**, not T1.6, because `WorkflowEngine` and `DbmServices` need them; T1.6 only adds `JobRunner`.
- `Json.cs` (T0.1) and `UserHome.cs` (T0.2) are not recreated in T1.1.
- `Integration/Web/ServerControlTests.cs` lives in **T1.8** (it spawns `serve`/`ui`, which T1.8 creates).
- New files: `Dbm/Core/State/ReaderExtensions.cs`, `Dbm/Core/DbEventSink.cs`, `Dbm/Web/FileLog.cs`, `Dbm/Web/StateView.cs`, `Dbm/Web/Endpoints/ApiResults.cs`, test support `ProcessStateCollection.cs`, `Wait.cs`, `WebTestServer.cs`.

**Contract members not yet present (added by their owners):** `DbmServices.Catalog` (T2.5), `DbmServices.Transfers` (T5.1), `WebState.Transfer` (T5.5, the commented line in `WebState.cs` shows where). `StateView` already emits `"drift":{"src":false,"tgt":false}` and `"transfer":null`; T2.5/T5.5 should fill them by appending to `StateView.Extenders` instead of editing `Build`.

**Added members (all additive)**
- C1: `EnumText.ToText(Enum)`, `EnumText.TryParse<T>`, `Workspace.DirName`.
- C2: `StateDb.Path`, `StateDb.ToDb(object?)`, `StateDb.SchemaResource`; `JobRepo.TryClaim(id)` (used by `JobRunner` instead of `MarkRunning` so two runners/processes never run one job twice) and `JobRepo.Recent(limit)`; `ReaderExtensions`.
- C3: `Redactor.AuthMode(string)`; `DpapiProtector.Prefix`, `AesGcmFileKeyProtector.Prefix/KeyPath`.
- C4: `Patch.Parse(string)`, `JsonPatch.Resolve(JsonNode, string)`.
- C5: `WorkflowEngine.Peek()`, `WorkflowEngine.CarryOver(PhaseName)`, `WorkflowEngine.PatchRules`, `ApplyResult.Fail(...)`, helper class `Phases`; stop reason `server_stopped` (produced only by `dbm await` when the server was stopped cleanly — SKILL.md handles it).
- C7: `WebHost.RunAsync(…, servicesFactory = null, onStarted = null)`; `Broadcaster(EventRepo)` + `Deliver/Subscribe/NextSignal/Signal/ClientCount` and `SseMessage`; `EventPump(EventRepo, Broadcaster, FileLog?)`; `AgentPresence(DbmServices, Broadcaster?)`; `WebState.Log`; `StateView.Build/Extenders`; `ApiResults`/`ApiException`; `TokenGuard.Header/HostAllowed/TokenMatches/InvokeAsync`; internal `ServerControl.SpawnOverrides`, `WindowsQuote`, `PosixQuote`.
- C8: `CliContext.ServicesFactory/RequireProject/OpenProject/OpenServices`; internal 4-argument `CliApp.RunAsync`; `serve --detached`; `AwaitCommand.ServerStopped/MaxReconnects`.
- C9: see the T1.9 Interfaces list (`ctx.artifact = {version, author, summary, createdAt, payload}`, ctx additions `versions`/`latestVersion`/`export`/…, helper functions, extra tokens/classes, severity names `critical|high|medium|low|info`).

**Cross-milestone requests implemented (from the M2–M4 writers)**
- Validate-then-store in `ApplyPatch`/`HumanEdit` (same `JsonNode` instance stored after `Validate`), with a test.
- `Json.Options` sets no `DictionaryKeyPolicy` (keys round-trip exactly, test); global `--workspace` accepted anywhere in argv, also after two-word commands (test).
- `server.json` is exactly `{port,pid,token,startedAt}`; `dbm version` → `{version,runtime}`; `DoctorCommand` early-returns for `--quiet` and ends with `return Output.Ok(ctx, …)`.
- `TestWorkspace` public API: `Ws`, `Root`, `OpenServices(modules = null, jobs = null)` (initialises the project as `"test"` on first open; T1.5).
- `SqlTestServer.MasterConnectionString`/`ForDatabase(db)`; `TempDatabase.CreateAsync(prefix = "dbm_test")`, `Name`, `ConnectionString`, `ExecAsync` (GO-split), `ScalarAsync<T>`, `DisposeAsync`.
- `WorkflowEngine.RetryJob` works for `running` (failed job) and `drafting` phases (test).
- `ModuleRegistry`/`JobRegistry` bodies end with `// milestone registrations below`; append `yield return new X(s);` lines after it.
- Artifact bodies carry `createdAt`; export mode renders without `api.js` (see T1.9 rules); `modal({title, body (string|Node), confirmText, requireText})` → `Promise<bool>`; `ctx.commentable` adds `.commentable` (no-op in export/read-only screens).
- PARSEONLY is not used anywhere in M0/M1.

**Deviations and clarifications — please review**
- **Detached spawn on Windows uses `UseShellExecute = true` + `WindowStyle = Hidden`, not the verified `UseShellExecute = false, CreateNoWindow = true`.** While writing this plan the verified configuration left the spawned server holding the *caller's* stdout pipe: a `dotnet test` run that spawned a server hung for 10 minutes after all tests had passed, until the stray server was killed. Claude Code's Bash tool reads command output to EOF, so `dbm init` would hang the same way. ShellExecute creates the child without handle inheritance; T1.8's integration test `A_cli_command_that_starts_the_server_releases_its_output_pipe` proves the CLI's output pipe reaches EOF while the server lives on. Survival after the calling tool exits (job-object behaviour is unchanged) should be re-confirmed in T6.4. The detached marker moved from an environment variable to the `--detached` flag because ShellExecute cannot pass environment variables.
- Approvals are cleared when a phase goes stale, so "job handlers receive the previous approved artifact as carry-over" is realised as `WorkflowEngine.CarryOver(phase)` = the phase's latest artifact at job start (M3/M4 handlers should call it).
- `ApplyPatch` in `drafting` rejects any `responses` (there are no open items); `HumanEdit` ignores `responses`. A job result is ignored when the phase is no longer `running` or a newer job exists. `Next()` returns `await/job` for a leading `pending`/`stale` phase (transient). `Url` is only set on the setup/review/execute/transfer awaits.
- HTTP details not fixed by C7: connection test failures are 200 `{ok:false,error}`, save failures 400 `connection_failed`, locked 409 `locked`; `/api/edit/{phase}` answers 200 with the `ApplyResult` even when `ok` is false; `/api/agent/await` answers 503 `{error:"stopping"}` during shutdown; unknown phase/side → 404 `unknown_phase`/`unknown_side`; `GET /api/state` job list = the 20 most recent jobs (with `startedAt`/`endedAt`); `StateView.phases` omit null version fields (WhenWritingNull).
- Work packets are written compact (not pretty) to save agent tokens; writing a packet deletes any existing file at its `patchPath`.
- `node --test <directory>` does not work on Node 24; the plan uses the glob form everywhere.

## Self-review

Spec coverage for M1 ("State store, crypto, connections, server + SSE + UI shell, pause/resume plumbing, `dbm next/await/apply`"):
- §2.5 storage: `.dbmigrate/` with `state.db`, `server.json`, `server.log`, `work/`, `exports/`, `.gitignore` = `*` (T1.1/T1.2/T1.7); key in the user profile (DPAPI or `~/.dbmigrate/key` 0600, T1.3).
- §3.1 state machine and statuses, §3.2 review loop (script v0 → agent v1, anchored feedback, per-item responses, versions kept, human edits, reopen → stale cascade, approval blockers, approved fingerprint) — T1.5 with a test per transition; drift checks are T2.5 (the `ApprovalGuards` hook exists).
- §3.3 pause/resume: `SetPaused`, `await/paused`, patches still accepted while paused, UI banner + Resume, CLI `pause/resume` (T1.5/T1.7/T1.8/T1.9); transfer-side pause is M5.
- §3.4 handshake: `dbm await` long-poll with presence (online dot), background use in the skill, zero-token waiting, reconnect on server restart, no respawn after a clean stop; detached server that survives the caller and — new — does not hold its pipes (T1.7/T1.8/T1.10).
- §4.1 orchestrator loop and sub-commands (T1.10); compact packets with feedback + module `data` (T1.5).
- §5.4 patch format and validation incl. "every open feedback item has a response" (T1.4/T1.5).
- §6 CLI: `init`, `ui`, `status`, `next`, `await`, `apply` (+ `artifact`, `feedback`, `run-jobs`, `serve`, `stop`, `pause`, `resume`) (T1.8).
- §7 state model (C2 schema incl. transfer tables used by `Next()`), `PRAGMA user_version` migration (T1.2).
- §8 UI shell: stepper with status badges, top bar with project name/agent dot/Pause-Resume, right drawer with feedback thread and version history with diff, setup screen with masked fields + Test + detected server info, SSE live updates, light/dark, keyboard, responsive (T1.9). §8.1 API rows owned by T1.7 are implemented; others belong to M2–M5.
- §10 security: 127.0.0.1 only, token on every API call (header for mutations), host check, secrets encrypted at rest, redacted in UI/state/errors/job messages, never in packets or CLI output (T1.3/T1.6/T1.7).
- §11 unit tests for patch validation, state-machine transitions (stale cascade, pause), crypto round-trip, redaction; integration tests against LocalDB (probe, connection endpoints, real detached server).
- TDD caveat: T1.10's end-to-end test passes on first run by design (it composes finished parts); the markdown skill has no automated test beyond `claude plugin validate`.
