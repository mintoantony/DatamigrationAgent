# db-migrate Implementation Plan — Overview & Shared Contracts

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `db-migrate`, a Claude Code plugin + .NET 8 engine (`dbm`) that migrates data between two SQL Server databases with different schemas through human-approved analysis → mapping → SQL → transfer phases, driven from a local web UI.

**Architecture:** One .NET 8 app (`dbm`) is a CLI for Claude, a detached localhost web server (Kestrel + SSE + vanilla-JS UI) and a SQLite state store. The engine owns the phase state machine and runs all deterministic work (catalog extraction, profiling, rules, auto-mapping, SQL templating/validation, transfer) as server jobs; Claude only performs judgement steps via three subagents that read compact work packets and return JSON patches.

**Tech Stack:** .NET 8 (C# latest), ASP.NET Core minimal APIs (shared framework), Microsoft.Data.SqlClient 7.0.3 (+ Extensions.Azure 7.0.3), Microsoft.Data.Sqlite 10.0.12, xUnit 2.9.3, vanilla HTML/CSS/JS, Claude Code plugin (skills, agents, hooks, bin).

**Spec:** `docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md` (read it before starting any task).

## Plan files (execute in order)

| File | Milestone |
|---|---|
| `00-overview.md` | This file: constraints, file map, shared contracts (normative for all milestones) |
| `m0-skeleton.md` | M0 — solution, CLI skeleton, plugin scaffold, `dbm doctor` |
| `m1-core-platform.md` | M1 — workspace, state store, crypto, workflow engine, jobs, web server, CLI, UI shell, orchestrator skill |
| `m2-discovery-analysis.md` | M2 — test fixtures, catalog, profiling, vector search, discovery job, rules, analysis loop + UI |
| `m3-mapping.md` | M3 — type compatibility, mapping model/validator, auto-mapper, mapping loop + UI |
| `m4-sql.md` | M4 — SQL generation, validation, SQL loop + UI, script pack |
| `m5-transfer.md` | M5 — transfer engine, bisection, preflight, validation, final report, execute/report UI |
| `m6-packaging.md` | M6 — demo, release build, docs, end-to-end install verification |

## Global Constraints

- Target framework `net8.0`, `<RollForward>Major</RollForward>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`.
- Runtime NuGet packages — exactly: `Microsoft.Data.SqlClient` 7.0.3, `Microsoft.Data.SqlClient.Extensions.Azure` 7.0.3, `Microsoft.Data.Sqlite` 10.0.12. Web via `Microsoft.NET.Sdk.Web` (shared framework, not a package). No other runtime dependencies.
- Test-only NuGet: `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.10.0. JS unit tests use `node --test` (dev-only, Node is not a runtime dependency).
- No npm packages, no CDN, no MCP server. UI = classic `<script>` files (no ES modules) that attach to `window.DBM`.
- Server binds `127.0.0.1` only; every API call requires the project token (`X-Dbm-Token` header; `?t=` query accepted only for GET and SSE); requests whose `Host` is not `127.0.0.1:<port>` or `localhost:<port>` are rejected with 403.
- Connection strings are never written to logs, events, work packets, exports, CLI output or the UI after save. Use `Redactor`.
- CLI prints compact single-line JSON to stdout (exit 0 success, 1 failure: `{"error":"<code>","message":"..."}`), except `dbm show`, `dbm search` and `dbm help` which print compact text unless `--json`.
- JSON: property names camelCase; enums as `snake_case_lower` strings (e.g. `awaiting_review`) in JSON **and** in SQLite text columns.
- Timestamps: UTC `DateTimeOffset`, stored in SQLite as ISO-8601 `"O"` strings.
- Cross-platform: any Windows-only API is guarded by `OperatingSystem.IsWindows()` with a macOS/Linux path.
- Engine root: `plugins/db-migrate/engine` (all paths in this plan are relative to the repository root `D:\DatamigrationAgent`).
- Unit tests: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`. Integration tests are marked `[Trait("Category","Integration")]` and use env `DBM_TEST_SQL` (default `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`).
- Every commit message ends with these two lines:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
  ```
- Work on branch `feat/db-migrate` (create it from `main` before Task 0.1).

## Verified environment facts (2026-09-11)

- A completed background Bash command re-invokes the Claude Code session (CLI, Windows).
- A process started from a tool command survives the command ending (Windows). **But** `UseShellExecute=false` makes the child inherit the caller's stdout pipe, so the calling command never returns (found in T1.7/T1.8 verification). The server is therefore spawned with `UseShellExecute=true` + hidden window and `serve --detached`; an integration test asserts the pipe is released and `dbm init` returns in ~2 s.
- `Microsoft.Data.SqlClient` 7.0.3 has a `net8.0` build; it connects with integrated auth to `localhost` (SQL Server 2025 Developer) and `(localdb)\MSSQLLocalDB`.
- Entra ID auth modes in SqlClient 7 require `Microsoft.Data.SqlClient.Extensions.Azure`. With that package referenced, `SqlAuthenticationProvider.GetProvider(m)` returns `Microsoft.Data.SqlClient.ActiveDirectoryAuthenticationProvider` for every `ActiveDirectory*` method automatically — no registration code needed. Task 1.3 keeps a test asserting this so the package can't be dropped by accident.

## File map

```
.claude-plugin/marketplace.json                          T0.2
plugins/db-migrate/
  .claude-plugin/plugin.json                              T0.2
  bin/dbm  bin/dbm.cmd                                     T0.2
  hooks/hooks.json                                         T0.2
  skills/db-migrate/SKILL.md                               T1.10 (polished T6.3)
  skills/db-migrate/reference/transfer.md                  T5.5
  skills/db-migrate/reference/troubleshooting.md           T6.3
  agents/schema-analyst.md     (phase playbook embedded in the agent body)   T2.7
  agents/mapping-architect.md  (phase playbook embedded in the agent body)   T3.4
  agents/sql-engineer.md       (phase playbook embedded in the agent body)   T4.4
  samples/legacyshop-to-shopv2/legacyshop.sql  shopv2.sql  seed.sql  README.md   T2.1 (embedded into Dbm.dll as resources)
  engine/
    Directory.Build.props                                  T0.1
    Dbm.sln                                                T0.1
    release.ps1  release.sh                                T6.2
    dist/                                                  T6.2 (committed build output)
    Dbm/Dbm.csproj                                         T0.1
    Dbm/Program.cs                                         T0.1
    Dbm/Cli/Args.cs  CliApp.cs  CliContext.cs  ICommand.cs  Output.cs  CommandRegistry.cs   T0.1
    Dbm/Cli/Commands/HelpCommand.cs VersionCommand.cs      T0.1
    Dbm/Cli/Commands/DoctorCommand.cs                      T0.2
    Dbm/Cli/Commands/ProjectCommands.cs  (init, status, pause, resume)          T1.8
    Dbm/Cli/Commands/ServerCommands.cs   (serve, ui, stop)                      T1.8
    Dbm/Cli/Commands/AgentCommands.cs    (next, await, apply, artifact, feedback, run-jobs)  T1.8
    Dbm/Cli/Commands/CatalogCommands.cs  (discover, search, show)               T2.5
    Dbm/Cli/Commands/MapCommands.cs      (map auto)                             T3.3
    Dbm/Cli/Commands/SqlCommands.cs      (sql gen, sql validate)                T4.3
    Dbm/Cli/Commands/TransferCommands.cs (transfer start|pause|resume|cancel|status)  T5.5
    Dbm/Cli/Commands/DemoCommand.cs  ExportCommand.cs      T6.1
    Dbm/Core/Json.cs  EnumText.cs  Workspace.cs  UserHome.cs  IEventSink.cs  Clock.cs   T1.1
    Dbm/Core/DbmServices.cs  ModuleRegistry.cs  JobRegistry.cs                  T1.6
    Dbm/Core/State/StateDb.cs  schema.sql  Rows.cs  ProjectRepo.cs  PhaseRepo.cs  ArtifactRepo.cs
                   FeedbackRepo.cs  EventRepo.cs  JobRepo.cs                    T1.2
    Dbm/Core/State/ConnectionRepo.cs                       T1.3
    Dbm/Core/State/CatalogRepo.cs                          T2.5
    Dbm/Core/State/TransferRepo.cs                         T5.1
    Dbm/Core/Crypto/ISecretProtector.cs DpapiProtector.cs AesGcmFileKeyProtector.cs SecretProtector.cs  T1.3
    Dbm/Core/Sql/Redactor.cs  SqlConnect.cs  ServerMeta.cs  SqlQuote.cs          T1.3 (SqlQuote T4.1)
    Dbm/Core/Patching/Patch.cs  JsonPatch.cs               T1.4
    Dbm/Core/Workflow/Phases.cs  NextAction.cs  IPhaseModule.cs  WorkflowEngine.cs  T1.5
    Dbm/Core/Jobs/IJobHandler.cs  JobRunner.cs             T1.6
    Dbm/Core/Samples/SampleSql.cs                          T2.1
    Dbm/Core/Catalog/CatalogModel.cs  CatalogExtractor.cs  T2.2
    Dbm/Core/Catalog/ValueSignature.cs  Profiler.cs        T2.3
    Dbm/Core/Catalog/Fingerprint.cs  DriftChecker.cs  DiscoverJob.cs   T2.5
    Dbm/Core/Matching/SparseVector.cs NameNormalizer.cs Synonyms.cs synonyms.json
                      NgramTfidfVectorizer.cs VectorIndex.cs              T2.4
    Dbm/Core/Matching/TypeCompat.cs                        T3.1
    Dbm/Core/Matching/AutoMapper.cs  AutomapJob.cs         T3.3
    Dbm/Core/Analysis/Finding.cs  AnalysisPayload.cs  Rules.cs  Analyzer.cs  AnalyzeJob.cs  T2.6
    Dbm/Core/Analysis/AnalysisModule.cs                    T2.7
    Dbm/Core/Mapping/MappingPayload.cs  MappingValidator.cs  T3.2
    Dbm/Core/Mapping/MappingModule.cs                      T3.4
    Dbm/Core/SqlGen/SqlPlanPayload.cs  TopoSort.cs         T4.1
    Dbm/Core/SqlGen/SqlGenerator.cs                        T4.2
    Dbm/Core/SqlGen/SqlValidator.cs  SqlGenJob.cs          T4.3
    Dbm/Core/SqlGen/SqlModule.cs  ScriptPack.cs            T4.4
    Dbm/Core/Transfer/TransferOptions.cs  ControlTable.cs  ChunkPlanner.cs      T5.1
    Dbm/Core/Transfer/BulkLoader.cs  Bisector.cs           T5.2
    Dbm/Core/Transfer/TransferEngine.cs  TaskRunner.cs     T5.3
    Dbm/Core/Transfer/Preflight.cs  RunValidator.cs  FinalReport.cs             T5.4
    Dbm/Web/ServerInfo.cs  ServerControl.cs  SelfCommand.cs                     T1.7
    Dbm/Web/WebHost.cs  WebState.cs  TokenGuard.cs  Broadcaster.cs  EventPump.cs  AgentPresence.cs  ApprovalGuards.cs  T1.7
    Dbm/Web/Endpoints/EndpointRegistry.cs  CoreEndpoints.cs  T1.7
    Dbm/Web/Endpoints/CatalogEndpoints.cs  ExportEndpoints.cs  WebExport.cs      T2.8
    Dbm/Web/Endpoints/MappingEndpoints.cs                  T3.5
    Dbm/Web/Endpoints/SqlEndpoints.cs                      T4.5
    Dbm/Web/Endpoints/TransferEndpoints.cs                 T5.5
    Dbm/wwwroot/index.html                                 T1.9
    Dbm/wwwroot/css/app.css                                T1.9
    Dbm/wwwroot/js/lib/dom.js  diff.js                     T1.9
    Dbm/wwwroot/js/lib/graph.js                            T2.8
    Dbm/wwwroot/js/lib/highlight.js                        T4.5
    Dbm/wwwroot/js/api.js  app.js                          T1.9
    Dbm/wwwroot/js/components/core.js  review.js           T1.9
    Dbm/wwwroot/js/views/setup.js  pending.js              T1.9
    Dbm/wwwroot/js/views/analysis.js                       T2.8
    Dbm/wwwroot/js/views/mapping.js                        T3.5
    Dbm/wwwroot/js/views/sql.js                            T4.5
    Dbm/wwwroot/js/views/execute.js  report.js             T5.6
    Dbm.Tests/Dbm.Tests.csproj                             T0.1
    Dbm.Tests/Support/TestWorkspace.cs  CliRunner.cs       T1.1 / T1.8
    Dbm.Tests/Support/SqlTestServer.cs  TempDatabase.cs    T1.3
    Dbm.Tests/Support/SampleDatabases.cs                   T2.1
    Dbm.Tests/Support/FakeModule.cs                        T1.5
    Dbm.Tests/Unit/**  Dbm.Tests/Integration/**           (per task)
    Dbm.Tests/js/*.test.cjs                                (per UI task, run with `node --test`)
docs/README.md (user guide)                               T6.3
README.md (repo root)                                     T6.3
```

## Shared contracts (normative)

Every milestone MUST use these exact names and signatures. Adding members is allowed; renaming or changing signatures is not. Namespaces follow folders (`Dbm.Core.State`, `Dbm.Web`, …).

### C1. Core utilities (T1.1)

```csharp
namespace Dbm.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options;   // camelCase props, JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), DefaultIgnoreCondition = WhenWritingNull, PropertyNameCaseInsensitive = true
    public static readonly JsonSerializerOptions Pretty;    // Options + WriteIndented
    public static string Serialize<T>(T value, bool pretty = false);
    public static T Deserialize<T>(string json);            // throws JsonException on null result
    public static JsonNode ToNode<T>(T value);
    public static T FromNode<T>(JsonNode node);
}

public static class EnumText
{
    public static string ToText<T>(T value) where T : struct, Enum;   // PascalCase -> snake_case_lower ("AwaitingReview" -> "awaiting_review")
    public static T Parse<T>(string text) where T : struct, Enum;     // inverse; throws ArgumentException
}

public static class Clock
{
    public static Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;   // tests may replace
    public static string NowText() => Now().ToString("O");
}

public sealed class Workspace
{
    public string Root { get; }
    public string Dir { get; }            // Root/.dbmigrate
    public string StateDbPath { get; }    // Dir/state.db
    public string ServerJsonPath { get; } // Dir/server.json
    public string ServerLogPath { get; }  // Dir/server.log
    public string ExportsDir { get; }     // Dir/exports
    public string WorkDir { get; }        // Dir/work
    public bool Exists { get; }           // Directory.Exists(Dir) && File.Exists(StateDbPath)
    public Workspace(string root);
    public static Workspace Resolve(string? explicitRoot);  // explicit > env DBM_WORKSPACE > nearest ancestor of cwd containing .dbmigrate/state.db > cwd
    public void EnsureCreated();          // creates Dir, ExportsDir, WorkDir and Dir/.gitignore containing "*\n"
    public string Relative(string absolutePath); // path relative to Root using '/' separators
}

public static class UserHome
{
    public static string Dir { get; }     // env DBM_HOME or ~/.dbmigrate ; created on first access
}

public interface IEventSink
{
    void Publish(string type, object? payload = null, bool persist = true);
}
```

### C2. State store (T1.2, T1.3)

SQLite schema (`Dbm/Core/State/schema.sql`, embedded resource, applied when `PRAGMA user_version` < 1; sets `user_version = 1`). Pragmas on open: `journal_mode=WAL`, `busy_timeout=5000`, `foreign_keys=ON`.

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

(The spec's `catalog_object`/`profile`/`vector` tables are realised as `catalog` (one JSON snapshot per side, profiles embedded) + `vector`.)

```csharp
namespace Dbm.Core.State;

// Thread-safe: every public member takes an internal lock (Monitor, re-entrant); InTransaction holds it for the whole
// transaction. The server shares one DbmServices (and so one StateDb) across requests, the job runner and the transfer engine.
public sealed class StateDb : IDisposable
{
    public static StateDb Open(string path);                         // creates file, applies pragmas + schema
    public SqliteConnection Connection { get; }
    public void InTransaction(Action work);                           // BEGIN IMMEDIATE; nested calls join the outer tx
    public T InTransaction<T>(Func<T> work);
    public int Execute(string sql, object? args = null);              // args: anonymous object; property X binds to $X
    public T? Scalar<T>(string sql, object? args = null);
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, object? args = null);
}

public enum Side { Src, Tgt }                                          // text: "src" / "tgt"
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
public sealed record PhaseRow(PhaseName Name, int Ordinal, PhaseStatus Status, int? CurrentVersion, int? ApprovedVersion, string? ApprovedFingerprint, DateTimeOffset UpdatedAt);
public sealed record ArtifactRow(long Id, PhaseName Phase, int Version, string PayloadJson, string Author, string? Summary, DateTimeOffset CreatedAt);
public sealed record ArtifactMeta(int Version, string Author, string? Summary, DateTimeOffset CreatedAt);
public sealed record FeedbackRow(long Id, PhaseName Phase, int Version, string? Anchor, string Text, FeedbackStatus Status, string? Response, int? RespondedVersion, DateTimeOffset CreatedAt);
public sealed record EventRow(long Id, DateTimeOffset Ts, string Type, string PayloadJson);
public sealed record JobRow(long Id, string Kind, PhaseName? Phase, JobStatus Status, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

public sealed class ProjectRepo(StateDb db)
{
    public bool Exists();
    public void Init(string name);            // project row + one phase row per PhaseName (Setup=awaiting_review, others=pending)
    public ProjectRow Get();
    public void SetPaused(bool paused);
    public void TouchAgent();                 // agent_seen_at = now
    public ProjectSettings GetSettings();
    public void SaveSettings(ProjectSettings settings);
}
public sealed class PhaseRepo(StateDb db)
{
    public PhaseRow Get(PhaseName phase);
    public IReadOnlyList<PhaseRow> All();     // ordered by ordinal
    public void SetStatus(PhaseName phase, PhaseStatus status);
    public void SetCurrentVersion(PhaseName phase, int? version);
    public void SetApproved(PhaseName phase, int version, string? fingerprint);   // also status = approved
    public void ClearApproval(PhaseName phase);
}
public sealed class ArtifactRepo(StateDb db)
{
    public int NextVersion(PhaseName phase);  // 0 when none, else max+1
    public ArtifactRow Add(PhaseName phase, int version, string payloadJson, string author, string? summary);   // author: "script" | "agent" | "human"
    public ArtifactRow? Get(PhaseName phase, int version);
    public ArtifactRow? Latest(PhaseName phase);
    public IReadOnlyList<ArtifactMeta> List(PhaseName phase);   // ascending version, no payload
}
public sealed class FeedbackRepo(StateDb db)
{
    public FeedbackRow Add(PhaseName phase, int version, string? anchor, string text);   // status draft
    public FeedbackRow? Get(long id);
    public int SubmitDrafts(PhaseName phase);                  // draft -> open ; returns count
    public IReadOnlyList<FeedbackRow> List(PhaseName phase, FeedbackStatus? status = null);
    public void Respond(long id, FeedbackStatus status, string response, int respondedVersion);
    public bool DeleteDraft(long id);
}
public sealed class EventRepo(StateDb db)
{
    public long Append(string type, object? payload = null);
    public IReadOnlyList<EventRow> Since(long afterId, int limit = 500);
    public long LastId();
}
public sealed class JobRepo(StateDb db)
{
    public long Enqueue(string kind, PhaseName? phase);
    public JobRow? Get(long id);
    public JobRow? NextQueued();
    public JobRow? LatestFor(PhaseName phase);
    public void MarkRunning(long id);
    public void MarkDone(long id);
    public void MarkFailed(long id, string error);
    public IReadOnlyList<JobRow> Active();                    // queued or running
    public int RequeueStaleRunning();                         // running -> queued (server start)
}
public sealed class ConnectionRepo(StateDb db, Dbm.Core.Crypto.ISecretProtector protector)    // T1.3
{
    public void Save(Side side, string connectionString, Dbm.Core.Sql.ServerMeta meta);
    public string? GetConnectionString(Side side);
    public Dbm.Core.Sql.ServerMeta? GetMeta(Side side);
    public bool Has(Side side);
}
```

### C3. Crypto & SQL helpers (T1.3)

```csharp
namespace Dbm.Core.Crypto;
public interface ISecretProtector { string Protect(string plaintext); string Unprotect(string protectedText); }
public sealed class DpapiProtector : ISecretProtector { }                 // Windows only; P/Invoke crypt32 CryptProtectData (CurrentUser); output "dpapi:" + base64
public sealed class AesGcmFileKeyProtector(string keyPath) : ISecretProtector { }   // 32-byte key file (created 0600); output "aesgcm:" + base64(nonce12 | tag16 | cipher)
public static class SecretProtector { public static ISecretProtector ForCurrentUser(); }  // Windows -> Dpapi ; else AesGcm(Path.Combine(UserHome.Dir, "key"))

namespace Dbm.Core.Sql;
public sealed record ServerMeta(string Server, string Database, string Version, string ProductVersion, int MajorVersion,
    string Edition, string ServerCollation, string DatabaseCollation, int CompatLevel, string AuthSummary);
public static class SqlConnect
{
    public static string Normalize(string connectionString);                      // sets Application Name=dbm if absent
    public static Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct);
    public static Task<ServerMeta> ProbeAsync(string connectionString, CancellationToken ct);
}
public static class Redactor
{
    public static string Describe(string connectionString);                        // "server=X; database=Y; auth=<mode>[; user=U]" — never secrets
    public static string Scrub(string text, IEnumerable<string?> secrets);          // replaces each secret value (len >= 4) with "***"
    public static IEnumerable<string> SecretsOf(string connectionString);           // password / client secret values
}
public static class SqlQuote                                                        // T4.1
{
    public static string Ident(string name);                  // "[" + name.Replace("]", "]]") + "]"
    public static string Table(string schema, string name);   // "[schema].[name]"
    public static string TableKey(string key);                // "dbo.Customer" -> "[dbo].[Customer]" (split on first '.')
    public static string Literal(string value);               // N'...' with quotes doubled
}
```

### C4. Patches (T1.4)

```csharp
namespace Dbm.Core.Patching;
public sealed record PatchOp(string Op, string Path, JsonNode? Value = null);   // op: "add" | "replace" | "remove"
public sealed record FeedbackResponse(long FeedbackId, string Status, string Note); // status: "addressed" | "declined"
public sealed record Patch(string Phase, int BaseVersion, List<PatchOp> Ops, List<FeedbackResponse> Responses, string? Summary = null);
public sealed class PatchException(string message) : Exception(message);
public static class JsonPatch
{
    public static JsonNode Apply(JsonNode document, IEnumerable<PatchOp> ops);   // returns a modified deep clone; JSON-pointer (~0 ~1); "add" creates missing intermediate objects; array index or "-" supported; errors name the op index and path
}
```

Patch file example (what subagents write):

```json
{"phase":"mapping","baseVersion":3,
 "ops":[{"op":"replace","path":"/tables/dbo.Customers/columns/Email/expr","value":"LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))"}],
 "responses":[{"feedbackId":12,"status":"addressed","note":"Email is now trimmed and lower-cased."}],
 "summary":"Normalised email; mapped FAX to drop list."}
```

### C5. Workflow (T1.5)

```csharp
namespace Dbm.Core.Workflow;

public sealed record NextAction(string Action, string? Reason = null, string? Agent = null, string? Phase = null,
    string? Mode = null, string? Packet = null, string? PatchPath = null, string? Summary = null, string? Url = null);
// Action: "agent" | "await" | "stop"
// Reason for await: "setup" | "job" | "review" | "execute" | "transfer" | "transfer_paused" | "paused"
// Reason for stop:  "complete" | "job_failed" | "transfer_failed" | "transfer_cancelled"

public enum PacketMode { Draft, Rework }
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
public interface IPhaseModule
{
    PhaseName Phase { get; }                 // Analysis | Mapping | Sql
    string Agent { get; }                    // "schema-analyst" | "mapping-architect" | "sql-engineer"
    string JobKind { get; }                  // "analyze" | "automap" | "sqlgen"
    bool NeedsAgent(JsonNode draft);         // false -> script draft goes straight to awaiting_review
    JsonNode BuildPacket(ModuleContext ctx, PacketMode mode);   // module-specific "data" section of the packet
    PayloadCheck Validate(ModuleContext ctx, JsonNode payload); // run on every agent/human patch; errors reject it
    IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload);
    string Summarize(JsonNode payload);      // one line
}
public sealed record ApplyResult(bool Ok, int? Version, List<string> Errors, List<string> Warnings);

public sealed class WorkflowEngine(DbmServices services)
{
    public static readonly PhaseName[] Order;            // Setup..Complete
    public NextAction Next();                            // writes packet file for "agent" actions
    public void OnConnectionsSaved();                    // both sides saved -> Setup approved, Discovery running, enqueue "discover", downstream (if any approved) -> stale
    public void Rediscover();                            // Discovery running + enqueue "discover"; Analysis..Ready -> stale (only when Transfer is pending)
    public void OnJobDone(JobRow job, JsonNode? draftPayload, string? summary);
    public void OnJobFailed(JobRow job, string error);
    public void RetryJob(PhaseName phase);               // re-enqueue the phase's job
    public ApplyResult ApplyPatch(Patch patch, bool dryRun = false);   // author "agent"
    public ApplyResult HumanEdit(Patch patch);                         // author "human"; phase must be awaiting_review
    public void RequestChanges(PhaseName phase);         // submit drafts; requires >= 1 open item -> reworking
    public void Approve(PhaseName phase);                // throws WorkflowException with blockers; records approved_fingerprint = catalog fingerprints ("<src>:<tgt>", read from the `catalog` table; null if empty)
    public void Reopen(PhaseName phase);                 // approved -> awaiting_review; later phases up to Ready -> stale; only when Transfer pending
    public void SetPaused(bool paused);
    public void OnTransferStarted();                     // Ready approved, Transfer running
    public void OnTransferFinished(string runStatus, int? reportVersion);  // "completed" -> Transfer approved, Complete approved (report artifact already stored)
}
public sealed class WorkflowException(string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public IReadOnlyList<string> Details { get; } = details ?? [];
}
```

**Transitions (normative):**

| Trigger | Effect |
|---|---|
| `ProjectRepo.Init` | Setup `awaiting_review`, all others `pending` |
| `OnConnectionsSaved` (both sides present) | Setup `approved`; Discovery `running`; enqueue `discover`; any approved/drafting/awaiting phase after Discovery → `stale` |
| job `discover` done | Discovery `approved` (fingerprint = src+tgt fingerprints); Analysis `running`; enqueue `analyze` |
| job of a module done | store draft artifact (author `script`, version = `NextVersion`), set current_version; `NeedsAgent` ? `drafting` : `awaiting_review` |
| job failed | phase stays `running`; event `job_failed`; `Next()` → stop `job_failed` with the error as summary |
| `ApplyPatch` | phase must be `drafting` or `reworking`; `BaseVersion == current_version`; in `reworking` every `open` feedback needs a response; ops applied to base payload; module `Validate` must pass; new version (author `agent`) stores **the same `JsonNode` instance after `Validate` returned** (modules may annotate the payload during validation, e.g. SqlModule sets `Custom`/`Errors`/`Warnings`); feedback marked addressed/declined; phase `awaiting_review`. `HumanEdit` follows the same validate-then-store rule. |
| `RequestChanges` | drafts → open; requires ≥1 open item; phase `reworking` |
| `Approve(Analysis)` | no blockers → Analysis `approved`; Mapping `running`; enqueue `automap` |
| `Approve(Mapping)` | → Sql `running`; enqueue `sqlgen` |
| `Approve(Sql)` | → Ready `awaiting_review` |
| `OnTransferStarted` | Ready `approved`; Transfer `running` |
| `OnTransferFinished("completed")` | Transfer `approved`; Complete `approved` |
| `Reopen(p)` | p `awaiting_review` (current = approved version), approval cleared; every later phase up to Ready that is not `pending` → `stale`, approvals cleared |
| Approve of a phase whose successor is `stale` | successor re-runs exactly like a first run (`running` + job); job handlers receive the previous approved artifact as carry-over |

**`Next()` algorithm (normative):** if project paused → `await/paused`. Otherwise find the first phase in `Order` whose status is not `approved`:
- Setup → `await/setup` (with `Url`)
- `running`: Discovery/Analysis/Mapping/Sql → if `JobRepo.LatestFor(phase)` failed → `stop/job_failed` (Summary = error) else `await/job`; Transfer → by latest `transfer_run.status`: `running` → `await/transfer`, `paused` → `await/transfer_paused`, `failed` → `stop/transfer_failed`, `cancelled` → `stop/transfer_cancelled`
- `drafting` → `agent` (Mode `draft`); `reworking` → `agent` (Mode `rework`) — Agent = module.Agent, Phase = phase text, Packet/PatchPath = **absolute** paths `<ws>/.dbmigrate/work/<phase>-v<base>-<mode>.json` / `.patch.json`
- `awaiting_review` → `await/review` (Ready → `await/execute`) with `Url`
- if all approved → `stop/complete` with Summary = the Complete artifact summary.

**Work packet envelope** (engine writes; `data` comes from `IPhaseModule.BuildPacket`):

```json
{"phase":"mapping","mode":"rework","baseVersion":3,
 "patchPath":"D:/work/proj/.dbmigrate/work/mapping-v3-rework.patch.json",
 "agent":"mapping-architect",
 "feedback":[{"id":12,"anchor":"column:tgt:dbo.Customers.Email","text":"Lower-case the email"}],
 "rules":"Write a Patch JSON to patchPath: ops (add|replace|remove, JSON pointer into the artifact), a response for every feedback id, and a one-line summary.",
 "data":{}}
```

**Anchors** (feedback targets, used by UI and packets): `general` (null), `finding:<id>`, `table:<side>:<schema.table>`, `column:<side>:<schema.table>.<column>`, `narrative`, `tablemap:<target>`, `colmap:<target>.<column>`, `task:<taskId>`, `sql:<taskId>:<line>`.

### C6. Services, jobs, events (T1.6)

```csharp
namespace Dbm.Core;
public sealed class DbmServices : IDisposable
{
    public static DbmServices Open(Workspace ws, IEventSink? sink = null,
        Func<DbmServices, IEnumerable<IPhaseModule>>? modules = null,     // default ModuleRegistry.Create (tests pass fakes)
        Func<DbmServices, IEnumerable<IJobHandler>>? jobs = null);        // default JobRegistry.Create
    // sink default: DbEventSink
    public Workspace Ws { get; }
    public StateDb Db { get; }
    public ISecretProtector Protector { get; }
    public ProjectRepo Project { get; }  public PhaseRepo Phases { get; }  public ArtifactRepo Artifacts { get; }
    public FeedbackRepo Feedback { get; } public EventRepo Events { get; } public JobRepo Jobs { get; }
    public ConnectionRepo Connections { get; }
    public CatalogRepo Catalog { get; }          // T2.5 adds
    public TransferRepo Transfers { get; }       // T5.1 adds
    public IEventSink Sink { get; set; }
    public WorkflowEngine Workflow { get; }
    public IReadOnlyDictionary<PhaseName, IPhaseModule> Modules { get; }     // from ModuleRegistry.Create(this)
    public IReadOnlyDictionary<string, IJobHandler> JobHandlers { get; }      // from JobRegistry.Create(this)
    public string UiUrl();                        // from server.json, "" if unknown
}
public sealed class DbEventSink(EventRepo events) : IEventSink { }            // persists; ignores persist:false events
public static class ModuleRegistry { public static IEnumerable<IPhaseModule> Create(DbmServices s); }   // each milestone appends its module
public static class JobRegistry   { public static IEnumerable<IJobHandler> Create(DbmServices s); }    // each milestone appends its handler(s)

namespace Dbm.Core.Jobs;
public sealed record JobResult(JsonNode? DraftPayload, string? Summary);
public sealed class JobContext
{
    public required DbmServices Services { get; init; }
    public required JobRow Job { get; init; }
    public required Action<string> Log { get; init; }       // publishes "log" events
}
public interface IJobHandler { string Kind { get; } Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct); }
public sealed class JobRunner(DbmServices services)
{
    public Task<int> RunPendingAsync(CancellationToken ct);  // runs queued jobs until none; returns count (used by tests and `dbm run-jobs`)
    public Task RunLoopAsync(CancellationToken ct);          // server: RequeueStaleRunning, then poll every 500 ms
}
```

**Event types:** `state_changed {phase,status}`, `artifact_created {phase,version,author}`, `feedback_changed {phase}`, `job_started|job_done|job_failed {id,kind,phase,error?}`, `paused`, `resumed`, `agent_presence {online}`, `drift_detected {side}`, `log {level,message}`, `transfer_run_changed {runId,status}`, `transfer_task_changed {runId,taskId,status}`, `transfer_progress {…}` (persist:false).

### C7. Web server (T1.7)

```csharp
namespace Dbm.Web;
public sealed record ServerInfo(int Port, int Pid, string Token, DateTimeOffset StartedAt)
{
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string UiUrl => $"{BaseUrl}/?t={Token}";
}
public static class SelfCommand { public static (string FileName, IReadOnlyList<string> PrefixArgs) Get(); }  // dotnet + Dbm.dll, or the apphost exe
public static class ServerControl
{
    public static ServerInfo? ReadInfo(Workspace ws);
    public static Task<bool> IsAliveAsync(ServerInfo info, CancellationToken ct = default);   // GET /api/health, 1.5 s timeout
    public static Task<ServerInfo> EnsureRunningAsync(Workspace ws, CancellationToken ct = default); // spawn detached `serve --workspace <root>`; wait up to 20 s
    public static Task StopAsync(Workspace ws);
    public static void OpenBrowser(string url);
}
public static class WebHost { public static Task RunAsync(Workspace ws, int port, CancellationToken ct); }   // writes server.json, starts JobRunner loop + EventPump, serves wwwroot + API, calls EndpointRegistry.MapAll
public sealed class Broadcaster : IEventSink { }     // persist -> EventRepo ; persist:false -> SSE clients directly
public sealed class AgentPresence { public bool Online { get; } public IDisposable Enter(); }   // open awaits counter; Online also true when project.agent_seen_at is within 120 s

public sealed class WebState                                  // one per server process, passed to every endpoint group
{
    public required DbmServices Services { get; init; }
    public required Broadcaster Broadcaster { get; init; }
    public required AgentPresence Presence { get; init; }
    public required ServerInfo Info { get; init; }
    public Dbm.Core.Transfer.TransferService? Transfer { get; set; }   // set in T5.5
}

// Dbm/Web/Endpoints/EndpointRegistry.cs (T1.7) — later milestones append one line each:
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
// Every endpoint group: public static class XEndpoints { public static void Map(IEndpointRouteBuilder app, WebState state); }

// Dbm/Web/ApprovalGuards.cs (T1.7) — run by POST /api/phase/{phase}/approve before WorkflowEngine.Approve;
// the first non-null message aborts with 409 {"error":"guard","message":…}. T2.5 appends the drift guard.
public static class ApprovalGuards
{
    public static readonly List<Func<WebState, PhaseName, CancellationToken, Task<string?>>> All = new();
}
```

**HTTP API** (all JSON; mutations require `X-Dbm-Token`):

| Method & path | Task | Body → Response |
|---|---|---|
| `GET /api/health` | T1.7 | → `{ok:true,pid}` |
| `GET /api/state` | T1.7 | → `StateView` (below) |
| `GET /api/events?t=` | T1.7 | SSE; `event: <type>\ndata: <json>\n\n`; `id:` = event id; `Last-Event-ID` resumes |
| `GET /api/agent/await?timeout=<s>` | T1.7 | long-poll → `NextAction` when `Next().Action != "await"` or timeout (then returns current await action) |
| `POST /api/pause`, `POST /api/resume` | T1.7 | → `{ok}` |
| `POST /api/connections/{side}/test` | T1.7 | `{connectionString}` → `{ok, meta?, error?}` (nothing saved) |
| `POST /api/connections/{side}` | T1.7 | `{connectionString}` → `{ok, meta}` (probe, encrypt, save; calls `OnConnectionsSaved`) |
| `GET /api/artifact/{phase}` | T1.7 | → `{phase, versions:[ArtifactMeta], current:{version,author,summary,payload}}` |
| `GET /api/artifact/{phase}/{version}` | T1.7 | → `{version,author,summary,payload}` |
| `GET /api/feedback/{phase}` | T1.7 | → `[FeedbackRow]` |
| `POST /api/feedback/{phase}` | T1.7 | `{anchor?, text}` → `FeedbackRow` (draft on current version) |
| `DELETE /api/feedback/{id}` | T1.7 | → `{ok}` (drafts only) |
| `POST /api/phase/{phase}/request-changes` | T1.7 | → `{ok}` |
| `POST /api/phase/{phase}/approve` | T1.7 | → `{ok}` or 409 `{error:"blocked", details:[…]}` (drift check added in T2.5) |
| `POST /api/phase/{phase}/reopen` | T1.7 | → `{ok}` |
| `POST /api/phase/{phase}/retry` | T1.7 | → `{ok}` |
| `POST /api/edit/{phase}` | T1.7 | `Patch` → `ApplyResult` (human edit) |
| `POST /api/shutdown` | T1.7 | → `{ok}` then process exits |
| `GET /api/catalog/{side}` , `GET /api/catalog/{side}/table/{key}` , `POST /api/rediscover` , `GET /api/search?q=&side=&k=` | T2.8 | |
| `GET /api/export/{what}` (`analysis`, `mapping`, `sql`, `report` → standalone HTML; `sqlpack` → zip) | T2.8 / T4.4 / T5.6 | |
| `POST /api/transfer/preflight`, `POST /api/transfer/start`, `POST /api/transfer/{pause|resume|cancel}`, `GET /api/transfer`, `GET /api/transfer/errors?task=` | T5.5 | |

`StateView`:
```json
{"project":{"name":"x","paused":false,"agentOnline":true},
 "phases":[{"name":"analysis","status":"awaiting_review","currentVersion":2,"approvedVersion":null}],
 "next":{"action":"await","reason":"review"},
 "jobs":[{"id":3,"kind":"analyze","phase":"analysis","status":"done","error":null}],
 "connections":{"src":{"saved":true,"describe":"server=A; database=Legacy; auth=integrated","meta":{}},"tgt":{"saved":false}},
 "drift":{"src":false,"tgt":false},
 "transfer":null}
```

### C8. CLI (T0.1, T1.8)

```csharp
namespace Dbm.Cli;
public sealed class Args
{
    public static Args Parse(IEnumerable<string> argv);   // "--name value", "--name=value", "--flag"; "-k 5" short options allowed; everything else positional
    public IReadOnlyList<string> Positionals { get; }
    public string? Opt(string name);
    public bool Flag(string name);
    public int Int(string name, int defaultValue);
}
public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    public string? WorkspaceOverride { get; init; }         // from global --workspace
    public Workspace Workspace() => Dbm.Core.Workspace.Resolve(WorkspaceOverride);
}
public interface ICommand
{
    string Name { get; }         // one or two words, e.g. "next", "sql validate"
    string Help { get; }         // one line
    Task<int> RunAsync(Args args, CliContext ctx);   // args.Positionals exclude the command words
}
public static class Output
{
    public static int Ok(CliContext ctx, object payload);                       // compact JSON, exit 0
    public static int Fail(CliContext ctx, string code, string message);        // {"error","message"}, exit 1
    public static int Text(CliContext ctx, string text);                        // exit 0
}
public static class CommandRegistry { public static IReadOnlyList<ICommand> All(); }   // each milestone appends
public static class CliApp { public static Task<int> RunAsync(string[] argv, TextWriter? stdout = null, TextWriter? stderr = null); }  // matches the longest command name; unknown -> Fail("unknown_command")
```

**CLI surface:** `help`, `version`, `doctor [--quiet] [--rebuild]` (M0); `init [name]`, `status`, `pause`, `resume`, `serve --workspace <dir> [--port n]`, `ui`, `stop`, `next`, `await [--timeout s]`, `apply <patch.json> [--dry-run]`, `artifact <phase> [--version n] [--path /ptr]`, `feedback <phase>`, `run-jobs` (M1); `discover [--inline]`, `search <query> [--side src|tgt] [-k n]`, `show <object> [--side src|tgt]` (M2); `map auto` (M3); `sql gen`, `sql validate [--task id]` (M4); `transfer start|pause|resume|cancel|status` (M5); `demo --server "<conn>" [--scale n]`, `export <analysis|mapping|sql|report|sqlpack>` (M6).

### C9. UI conventions (T1.9)

- Files are classic scripts loaded in this order by `index.html`: `lib/dom.js`, `lib/diff.js`, `lib/graph.js`, `lib/highlight.js`, `api.js`, `components/core.js`, `components/review.js`, `views/*.js`, `app.js`. Each file is an IIFE: `(function (DBM) { … })(window.DBM = window.DBM || {});`. Library files must also run under Node (`globalThis`) for `node --test`.
- `DBM.h(tag, attrs, ...children)` creates elements (`attrs`: `class`, `style` object, `on: {event: fn}`, `data: {k: v}`, any other attribute; `html` for trusted markup only); string children are text nodes. `DBM.esc(s)`; `DBM.fmt.{num, pct, mb, dur, ts, rel}`.
- `DBM.api.{get, post, put, del}(path, body?)` → parsed JSON, throws `DBM.ApiError {status, code, message, details}`; `DBM.api.events(onEvent)` wraps `EventSource` with auto-reconnect; token read once from `?t=` and kept in `sessionStorage`.
- `DBM.views[phaseName] = { title, render(root, ctx), onEvent?(evt, ctx) }` where `ctx = { state, artifact, version, readOnly, api, refresh(), toast(msg, kind), commentable(el, anchor, label) }`. `readOnly` is true for exports and for approved phases unless reopened.
- `DBM.components`: `stepper(state)`, `topbar(state)`, `badge(status)`, `kpi(label, value, sub)`, `table(columns, rows, opts)`, `progress(value, max, opts)`, `sparkline(values)`, `modal({title, body, confirmText, requireText})` → Promise<bool>, `toast(msg, kind)`, `drawer`, `emptyState(title, text)`.
- `DBM.components.reviewBar(ctx)` renders version picker, status badge, author, "Request changes (n)" and "Approve"; the feedback drawer (list, add, delete drafts, responses) and version diff (`DBM.diff.lines`) live in `components/review.js`.
- `DBM.diff.lines(a, b)` → `[{op:'eq'|'add'|'del', text}]`; `DBM.highlight.sql(text)` → escaped HTML string; `DBM.graph.fkSvg(nodes, edges, opts)` → SVG element.
- CSS custom properties defined in `app.css` `:root` and overridden under `[data-theme="dark"]` and `@media (prefers-color-scheme: dark)` (unless `data-theme="light"`): `--bg --surface --surface-2 --border --text --text-muted --accent --accent-contrast --ok --warn --err --info --radius --shadow --font-sans --font-mono`. Status badge classes `.badge.st-<status>`; severity classes `.sev-<severity>`.
- **Visual direction:** calm operations console — neutral surfaces, one accent (indigo `#4f46e5` light / `#818cf8` dark), system font stack (`--font-sans: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`; `--font-mono: ui-monospace, "Cascadia Code", "SF Mono", Consolas, monospace`), 4/8 px spacing grid, 8 px radius, subtle 1 px borders, no gradients, tabular numerals for figures. Motion only for progress and drawer slide (respect `prefers-reduced-motion`).
- **CSS class vocabulary (defined in T1.9 `app.css`; later views must use these, adding view-scoped classes prefixed with the view name, e.g. `.map-grid`):** layout `.page .page-h .stack .row .row-wrap .spacer .grid-2 .grid-3 .grid-kpi .split .toolbar`; surfaces `.card .card-h .card-b .panel .empty`; text `.muted .small .mono .num .ellipsis .h1 .h2 .h3`; controls `.btn .btn-primary .btn-danger .btn-ghost .btn-sm .input .textarea .select .check`; data `.tbl .tbl-compact .tbl-sticky .tr-click .kpi .kpi-v .kpi-l .kpi-s .tag .chip .pill .badge .st-<status> .sev-<severity> .bar` (confidence/progress bar; set `style="--v:0.73"`) `.spark`; code `.code` (pre) `.code .ln` (line-number gutter) `.code .line` `.tok-kw .tok-str .tok-num .tok-com .tok-id .tok-fn .tok-op`; diff `.diff .diff-add .diff-del .diff-eq`; feedback `.commentable` (hover shows a comment button) `.has-feedback` (element with open feedback) `.drawer .drawer-open`; state `.is-active .is-disabled .is-loading`.
- Standalone export (T2.8 `WebExport`) inlines `app.css` + all `lib/*`, `components/*`, the view script and `app.js` into one HTML file with `window.DBM_EXPORT = {view, title, payload}`; `app.js` renders that view read-only when `DBM_EXPORT` is present.

### C10. Catalog model (T2.2 – T2.5)

```csharp
namespace Dbm.Core.Catalog;
public sealed record CatalogSnapshot(ServerMeta Server, List<TableInfo> Tables, ObjectCounts Objects, DateTimeOffset ExtractedAt)
{
    public TableInfo? FindTable(string key);                  // "schema.name", case-insensitive
}
public sealed record ObjectCounts(int Views, int Procedures, int Functions, int Triggers, int Synonyms);
public sealed record TableInfo(string Schema, string Name, long Rows, double SizeMb, List<ColumnInfo> Columns,
    List<IndexInfo> Indexes, List<ForeignKeyInfo> ForeignKeys, int TriggerCount, string? TemporalType, string? Description)
{
    [JsonIgnore] public string Key => $"{Schema}.{Name}";
    [JsonIgnore] public IndexInfo? PrimaryKey => Indexes.FirstOrDefault(i => i.IsPrimaryKey);
    [JsonIgnore] public bool IsHeap => !Indexes.Any(i => i.IsClustered);
    public ColumnInfo? FindColumn(string name);                // case-insensitive
    public IReadOnlyList<string>? BestKey();                    // PK columns, else first unique index whose columns are all NOT NULL, else null
}
public sealed record ColumnInfo(string Name, int Ordinal, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable,
    bool IsIdentity, bool IsComputed, bool IsRowVersion, string? DefaultDefinition, string? Collation, string? Description)
{
    public ColumnProfile? Profile { get; init; }
    [JsonIgnore] public string TypeDisplay { get; }            // "nvarchar(50)", "varchar(max)", "decimal(19,4)", "int"
}
// DataType = sys.types name, lower-case. MaxLength = characters for char/varchar/nchar/nvarchar, bytes for binary/varbinary, -1 = max, 0 for other types.
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
public static class CatalogExtractor { public static Task<CatalogSnapshot> ExtractAsync(SqlConnection conn, ServerMeta meta, CancellationToken ct); }
public sealed record ProfileOptions(int SampleRows, bool SampleValues, int PatternRows = 1000);
public static class Profiler { public static Task<CatalogSnapshot> ProfileAsync(SqlConnection conn, CatalogSnapshot snapshot, ProfileOptions options, Action<string>? log, CancellationToken ct); }
public static class ValueSignature
{
    public static string Pattern(string value);        // letters -> 'a'/'A', digits -> '9', other chars kept, runs collapsed: "John.Smith@x.com" -> "Aa.Aa@a.a"
    public static string? Classify(IReadOnlyCollection<string> values);   // >= 80% match: email | phone | url | guid | date | datetime | integer | decimal | postal_code | flag | country_code ; else "text" ; null when no values
}
public static class Fingerprint { public static string Compute(CatalogSnapshot snapshot); }   // lower-hex SHA-256 over canonical structure; excludes rows, sizes, profiles, ExtractedAt
public sealed record DriftResult(bool SrcChanged, bool TgtChanged) { public bool Any => SrcChanged || TgtChanged; }
public static class DriftChecker { public static Task<DriftResult> CheckAsync(DbmServices services, CancellationToken ct); }   // re-extracts structure (no profiling) and compares fingerprints with CatalogRepo
```

```csharp
namespace Dbm.Core.State;
public sealed record VectorRow(Side Side, string Kind, string Key, string Text, Dbm.Core.Matching.SparseVector Vec);   // Kind "table"|"column"; Key "schema.table" | "schema.table.column"
public sealed class CatalogRepo(StateDb db)
{
    public void Save(Side side, CatalogSnapshot snapshot, string fingerprint);
    public CatalogSnapshot? Get(Side side);
    public string? Fingerprint(Side side);
    public void SaveVectors(Side side, IEnumerable<VectorRow> rows);   // replaces all rows for the side
    public IReadOnlyList<VectorRow> Vectors(Side? side = null, string? kind = null);
}
```

### C11. Matching (T2.4, T3.1, T3.3)

```csharp
namespace Dbm.Core.Matching;
public sealed class SparseVector
{
    public Dictionary<string, double> W { get; init; } = new();
    public double Dot(SparseVector other);
    public static SparseVector Normalized(Dictionary<string, double> weights);   // L2-normalised copy
    [JsonIgnore] public bool IsEmpty => W.Count == 0;
}
public sealed class Synonyms
{
    public static Synonyms Default();                              // embedded Dbm/Core/Matching/synonyms.json
    public static Synonyms Load(params string?[] extraJsonPaths);  // Default merged with existing files (e.g. UserHome/synonyms.json, <ws>/.dbmigrate/synonyms.json)
    public IReadOnlyList<string> Expand(string token);             // abbreviation expansion then group canonicalisation; unknown token -> [token]
}
// synonyms.json: {"abbreviations":{"cust":["customer"],"dob":["birth","date"],...},"groups":[["customer","client"],["updated","modified","changed"],...]} — group canonical = first member
public static class NameNormalizer
{
    public static IReadOnlyList<string> Split(string identifier);   // "CUST_NM" -> [cust, nm]; "EmailAddress" -> [email, address]; "Line1" -> [line, 1]
    public static IReadOnlyList<string> Tokens(string identifier, Synonyms synonyms, IReadOnlyCollection<string>? contextTokens = null);
    // Split -> drop noise prefix (tbl, tb, vw, fld, col) when other tokens remain -> Expand -> Singular -> drop tokens contained in contextTokens (e.g. owning table's tokens) when other tokens remain
    public static string Singular(string token);                    // addresses->address, categories->category, statuses->status, orders->order; "ss"/"us"/"is" endings unchanged
}
public interface IVectorizer
{
    void Fit(IEnumerable<IReadOnlyList<string>> documents);
    SparseVector Transform(IReadOnlyList<string> tokens);
}
public sealed class NgramTfidfVectorizer(int n = 3, double wordWeight = 1.0, double ngramWeight = 0.5) : IVectorizer { }
// features: "w:<token>" (weight wordWeight*idf) + "g:<char n-gram of '#' + joined tokens + '#'>" (weight ngramWeight*idf); idf = ln((1+N)/(1+df))+1; output L2-normalised
public sealed record SearchHit(Side Side, string Kind, string Key, double Score, string Text);
public sealed class VectorIndex
{
    public static (List<VectorRow> Rows, VectorIndex Index) Build(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms);
    public static VectorIndex FromRows(IReadOnlyList<VectorRow> rows, Synonyms synonyms);
    public IReadOnlyList<SearchHit> Search(string query, Side? side = null, string? kind = null, int k = 8);
}
public enum CompatLevel { Exact, Widening, Risky, Incompatible }
public sealed record ColumnType(string DataType, int MaxLength, int Precision, int Scale) { public static ColumnType From(ColumnInfo c); }
public sealed record TypeCompatResult(CompatLevel Level, double Score, string? Risk);   // Score: exact 1.0, widening 0.9, risky 0.5, incompatible 0.0
public static class TypeCompat { public static TypeCompatResult Check(ColumnType src, ColumnType tgt, ColumnProfile? srcProfile = null); }
public sealed record MatchOptions(double AutoAccept = 0.85, double Candidate = 0.50, int TopK = 3);
public static class AutoMapper { public static MappingPayload Map(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms, MatchOptions options, MappingPayload? carryOver = null); }
```

Scoring (spec §4.4): column score = `0.45·name + 0.20·type + 0.15·structure + 0.20·profile` (when the source column has no profile, its 0.20 weight moves to name). Table score = `0.5·tableName + 0.4·columnSetSimilarity + 0.1·fkDegreeSimilarity`, where columnSetSimilarity = mean over target columns of the best column-name cosine in the source table.

### C12. Mapping payload (T3.2)

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
    public string? From { get; set; }                     // optional full FROM clause (without FROM), e.g. "[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]"
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
public static class MappingValidator
{
    public static List<string> Errors(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt);    // unknown target tables/columns, unknown sources, invalid Kind, SourceColumns not in catalog
    public static List<string> Blockers(MappingPayload m, CatalogSnapshot src, CatalogSnapshot tgt);  // every target table has a TableMap; every source column is covered by some SourceColumns or Drops (column or whole table); in non-skip maps every target column that is NOT NULL, not identity, not computed, not rowversion and has no default has Expr or Default
    public static List<string> Attention(MappingPayload m, MatchOptions options);                     // column/table maps with Confidence < AutoAccept and Method in (Fuzzy, Vector)
}
```

### C13. SQL plan payload (T4.1 – T4.3)

```csharp
namespace Dbm.Core.SqlGen;
public sealed class SqlPlanPayload
{
    public List<string> PreSql { get; set; } = new();
    public List<string> PostSql { get; set; } = new();
    public List<string> Order { get; set; } = new();                        // task ids in execution order
    public Dictionary<string, TaskPlan> Tasks { get; set; } = new();        // key = task id "T01", "T02", … (numbered in Order)
    public List<string> Warnings { get; set; } = new();
}
public sealed class TaskPlan
{
    public string Target { get; set; } = "";              // "app.Orders"
    public string Mode { get; set; } = "direct";          // "direct" | "staging_merge"
    public string SourceQuery { get; set; } = "";         // "SELECT <expr> AS [TargetCol], …, s.[k] AS [__k0] FROM … [WHERE …]" (no ORDER BY)
    public List<string> KeyColumns { get; set; } = new(); // key aliases in SourceQuery ("__k0", "__k1"); empty = single-transaction load
    public List<ColumnBinding> Columns { get; set; } = new();
    public bool IdentityInsert { get; set; }
    public string? StagingDdl { get; set; }               // staging_merge only: "CREATE TABLE #stg (…)"
    public string? MergeSql { get; set; }                 // staging_merge only: moves #stg rows into Target
    public List<string> PreSql { get; set; } = new();
    public List<string> PostSql { get; set; } = new();
    public string CountSql { get; set; } = "";            // "SELECT COUNT_BIG(*) FROM (<SourceQuery>) AS q"
    public List<string> DependsOn { get; set; } = new();  // FK-parent task ids (cycle edges excluded)
    public int? ChunkSize { get; set; }                   // generator sets 5000 when any mapped column is LOB/(max)
    public bool Custom { get; set; }                      // true once an agent/human edited this task's SQL
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();     // from the last validation
}
public sealed record ColumnBinding(string Source, string Target);   // Source = alias in SourceQuery (equals the target column name for generated tasks)
public sealed record TopoResult(List<string> Order, List<List<string>> Cycles, List<(string Child, string Parent)> CycleEdges);
public static class TopoSort { public static TopoResult Sort(IReadOnlyCollection<string> nodes, IReadOnlyCollection<(string Child, string Parent)> edges); }  // parents first; deterministic (ordinal tie-break)
public static class SqlGenerator { public static SqlPlanPayload Generate(MappingPayload mapping, CatalogSnapshot src, CatalogSnapshot tgt, SqlPlanPayload? carryOver = null); }
public sealed record ValidationReport(bool Ok, Dictionary<string, List<string>> TaskErrors, Dictionary<string, List<string>> TaskWarnings, List<string> GlobalErrors);
public static class SqlValidator { public static Task<ValidationReport> ValidateAsync(SqlPlanPayload plan, string sourceCs, string targetCs, CatalogSnapshot tgt, string? onlyTaskId, CancellationToken ct); }
public static class ScriptPack { public static byte[] BuildZip(SqlPlanPayload plan, string projectName); }
```

FK cycles: for each cycle edge the generator adds `ALTER TABLE <child> NOCHECK CONSTRAINT <fk>;` to `PreSql` and `ALTER TABLE <child> WITH CHECK CHECK CONSTRAINT <fk>;` to `PostSql`.

### C14. Transfer records (T5.1)

`transfer_run.status` text values (read directly by `WorkflowEngine.Next()` in M1): `pending | running | paused | completed | failed | cancelled`. `transfer_task.status`: `pending | running | paused | done | failed`.

```csharp
namespace Dbm.Core.Transfer;
public sealed record TransferOptions
{
    public int ChunkSize { get; init; } = 100_000;
    public int Parallelism { get; init; } = 4;
    public string ErrorMode { get; init; } = "stop";     // "stop" | "skip"
    public bool TruncateTarget { get; init; }
    public bool TableLock { get; init; }
    public bool ValidateChecksums { get; init; } = true;
    public bool FireTriggers { get; init; }
    public bool KeepControlTable { get; init; }
}
public enum RunStatus { Pending, Running, Paused, Completed, Failed, Cancelled }
public enum TransferTaskStatus { Pending, Running, Paused, Done, Failed }
```

### C15. Sample databases (T2.1) — used by integration tests in M2–M5 and by `dbm demo`

`LegacyShop` (source, schema `dbo`):

```sql
CREATE TABLE dbo.CUST (CUST_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CUST PRIMARY KEY, CUST_NM varchar(100) NOT NULL,
  EMAIL_ADDR varchar(120) NULL, PHONE_NO varchar(30) NULL, DOB datetime NULL,
  CRT_DT datetime NOT NULL CONSTRAINT DF_CUST_CRT_DT DEFAULT (getdate()), FAX_NO varchar(30) NULL, NOTES text NULL);
CREATE TABLE dbo.ADDR (ADDR_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ADDR PRIMARY KEY,
  CUST_ID int NOT NULL CONSTRAINT FK_ADDR_CUST REFERENCES dbo.CUST (CUST_ID), LINE1 varchar(200) NOT NULL,
  CITY varchar(80) NOT NULL, ZIP varchar(12) NULL, CTRY_CD char(2) NOT NULL);
CREATE TABLE dbo.PROD (PROD_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PROD PRIMARY KEY, PROD_NM varchar(150) NOT NULL,
  PROD_DESC varchar(500) NULL, UNIT_PRC money NOT NULL, ACTIVE_FLG char(1) NOT NULL);
CREATE TABLE dbo.ORD_STATUS (STATUS_ID tinyint NOT NULL CONSTRAINT PK_ORD_STATUS PRIMARY KEY, STATUS_CD varchar(10) NOT NULL,
  STATUS_DESC varchar(50) NOT NULL);
CREATE TABLE dbo.ORD_HDR (ORD_ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ORD_HDR PRIMARY KEY, CUST_ID int NOT NULL,
  ORD_DT datetime NOT NULL, STATUS_ID tinyint NOT NULL CONSTRAINT FK_ORD_STATUS REFERENCES dbo.ORD_STATUS (STATUS_ID),
  SHIP_ADDR_ID int NULL, TOTAL_AMT money NOT NULL, CMNT varchar(500) NULL);
CREATE TABLE dbo.ORD_LINE (ORD_ID int NOT NULL CONSTRAINT FK_LINE_ORD REFERENCES dbo.ORD_HDR (ORD_ID), LINE_NO smallint NOT NULL,
  PROD_ID int NOT NULL CONSTRAINT FK_LINE_PROD REFERENCES dbo.PROD (PROD_ID), QTY int NOT NULL, UNIT_PRC money NOT NULL,
  CONSTRAINT PK_ORD_LINE PRIMARY KEY (ORD_ID, LINE_NO));
CREATE TABLE dbo.AUDIT_LOG (LOG_TS datetime NOT NULL, USR varchar(50) NOT NULL, ACTION_TXT varchar(200) NOT NULL);   -- heap
CREATE TABLE dbo.TMP_IMPORT (X int NULL);                                                                           -- empty, to be dropped
-- after seeding: ALTER TABLE dbo.ORD_HDR WITH NOCHECK ADD CONSTRAINT FK_ORD_CUST FOREIGN KEY (CUST_ID) REFERENCES dbo.CUST (CUST_ID);  -- untrusted
```

Seed (`seed.sql`, parameter `$(scale)` substituted by `SampleSql.Seed(int scale)`, deterministic, set-based from a numbers CTE): CUST = 1000×scale (CUST_NM = "First{n} Last{n}", ~10% NULL EMAIL_ADDR, emails `first{n}.last{n}@example.com`), ADDR = 1500×scale (1–2 per customer), PROD = 200, ORD_STATUS = 4 rows (1 NEW, 2 PAID, 3 SHIPPED, 4 CANCELLED), ORD_HDR = 3000×scale, ORD_LINE = 3 per order, AUDIT_LOG = 5000×scale, TMP_IMPORT empty. **Fixed anomalies (independent of scale):** 2 ORD_HDR rows with CUST_ID values that do not exist (orphans, inserted before FK_ORD_CUST is added WITH NOCHECK), each with 1 ORD_LINE; 3 ORD_HDR rows whose CMNT is 300 characters **and which have no ORD_LINE rows**; 1 ORD_LINE with QTY = 0. **Exact counts:** CUST 1000·s, ADDR 1500·s, PROD 200, ORD_STATUS 4, ORD_HDR 3000·s + 5, ORD_LINE 9000·s + 2, AUDIT_LOG 5000·s, TMP_IMPORT 0.

`ShopV2` (target, schema `app`):

```sql
CREATE SCHEMA app;
CREATE TABLE app.Customers (CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
  FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NOT NULL, Email nvarchar(120) NULL, Phone nvarchar(30) NULL,
  BirthDate date NULL, CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (sysutcdatetime()),
  PrimaryAddressId int NULL, Notes nvarchar(max) NULL, DisplayName AS (FirstName + N' ' + LastName));
CREATE TABLE app.Addresses (AddressId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Addresses PRIMARY KEY,
  CustomerId int NOT NULL CONSTRAINT FK_Addresses_Customers REFERENCES app.Customers (CustomerId),
  Line1 nvarchar(200) NOT NULL, City nvarchar(80) NOT NULL, PostalCode nvarchar(12) NULL, CountryCode char(2) NOT NULL);
ALTER TABLE app.Customers ADD CONSTRAINT FK_Customers_PrimaryAddress FOREIGN KEY (PrimaryAddressId) REFERENCES app.Addresses (AddressId);  -- FK cycle
CREATE TABLE app.Products (ProductId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY, Name nvarchar(150) NOT NULL,
  Description nvarchar(500) NULL, UnitPrice decimal(19,4) NOT NULL, IsActive bit NOT NULL, RowVer rowversion NOT NULL);
CREATE TABLE app.Orders (OrderId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
  CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES app.Customers (CustomerId), OrderDate datetime2(0) NOT NULL,
  StatusCode varchar(10) NOT NULL, ShippingAddressId int NULL CONSTRAINT FK_Orders_Addresses REFERENCES app.Addresses (AddressId),
  TotalAmount decimal(19,4) NOT NULL, Comment nvarchar(200) NULL);
CREATE TABLE app.OrderLines (OrderId int NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES app.Orders (OrderId),
  LineNumber smallint NOT NULL, ProductId int NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES app.Products (ProductId),
  Quantity int NOT NULL CONSTRAINT CK_OrderLines_Quantity CHECK (Quantity > 0), UnitPrice decimal(19,4) NOT NULL,
  CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));
CREATE TABLE app.AuditEvents (EventTime datetime2(3) NOT NULL, UserName nvarchar(50) NOT NULL, Action nvarchar(200) NOT NULL);   -- heap
GO
CREATE TRIGGER app.trg_Orders_Audit ON app.Orders AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
```

**Ground-truth mapping** (the "approved" mapping used by M4/M5 tests, built in code by `SampleMappings.Approved()` in `Dbm.Tests/Support/SampleMappings.cs`, T3.2):

| Target | Source / expression |
|---|---|
| app.Customers | `dbo.CUST AS s`: CustomerId←`s.[CUST_ID]` (identity insert); FirstName←`LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)`; LastName←`LTRIM(SUBSTRING(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') + 1, 100))`; Email←`s.[EMAIL_ADDR]`; Phone←`s.[PHONE_NO]`; BirthDate←`CAST(s.[DOB] AS date)`; CreatedAt←`s.[CRT_DT]`; PrimaryAddressId←`(SELECT MIN(a.[ADDR_ID]) FROM [dbo].[ADDR] AS a WHERE a.[CUST_ID] = s.[CUST_ID])`; Notes←`CAST(s.[NOTES] AS nvarchar(max))` |
| app.Addresses | `dbo.ADDR AS s`: AddressId←ADDR_ID (identity insert), CustomerId←CUST_ID, Line1←LINE1, City←CITY, PostalCode←ZIP, CountryCode←CTRY_CD |
| app.Products | `dbo.PROD AS s`: ProductId←PROD_ID (identity insert), Name←PROD_NM, Description←PROD_DESC, UnitPrice←UNIT_PRC, IsActive←`CASE WHEN s.[ACTIVE_FLG] = 'Y' THEN 1 ELSE 0 END` |
| app.Orders | kind `merge`, From `[dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]`: OrderId←ORD_ID (identity insert), CustomerId←CUST_ID, OrderDate←ORD_DT, StatusCode←`st.[STATUS_CD]`, ShippingAddressId←SHIP_ADDR_ID, TotalAmount←TOTAL_AMT, Comment←CMNT |
| app.OrderLines | `dbo.ORD_LINE AS s`: OrderId←ORD_ID, LineNumber←LINE_NO, ProductId←PROD_ID, Quantity←QTY, UnitPrice←UNIT_PRC |
| app.AuditEvents | `dbo.AUDIT_LOG AS s`: EventTime←LOG_TS, UserName←USR, Action←ACTION_TXT |
| Drops | `dbo.TMP_IMPORT` (whole table), `dbo.CUST.FAX_NO`, `dbo.ORD_STATUS.STATUS_ID`, `dbo.ORD_STATUS.STATUS_DESC` (ORD_STATUS.STATUS_CD is covered via the Orders merge) |

**Expected transfer outcome at any scale with `ErrorMode = "skip"`:** rejected rows = 2 orphan orders (FK) + 3 long comments (truncation) + 1 zero-quantity line (CHECK) + the 2 lines of the orphan orders (FK) = **8**; all other rows load; FK cycle Customers↔Addresses handled by NOCHECK/WITH CHECK.

**Auto-mapper baseline** (T3.3 test on scale 1): all six target tables pair with the correct primary source; these column pairs are the top candidate: Customers.CustomerId←CUST_ID, Email←EMAIL_ADDR, Phone←PHONE_NO, BirthDate←DOB; Addresses.AddressId←ADDR_ID, CustomerId←CUST_ID, Line1←LINE1, City←CITY, PostalCode←ZIP, CountryCode←CTRY_CD; Products.ProductId←PROD_ID, UnitPrice←UNIT_PRC; Orders.OrderId←ORD_ID, CustomerId←CUST_ID, OrderDate←ORD_DT, TotalAmount←TOTAL_AMT, Comment←CMNT; OrderLines.OrderId←ORD_ID, LineNumber←LINE_NO, ProductId←PROD_ID, Quantity←QTY, UnitPrice←UNIT_PRC.

## Task index

| Task | Title | File |
|---|---|---|
| 0.1 | Solution scaffold + CLI core | m0 |
| 0.2 | Plugin scaffold, launchers, `dbm doctor` | m0 |
| 1.1 | Core utilities (Json, EnumText, Workspace, UserHome, Clock) | m1 |
| 1.2 | State store + repositories | m1 |
| 1.3 | Crypto, redaction, connections, SqlConnect | m1 |
| 1.4 | JSON patch | m1 |
| 1.5 | Workflow engine | m1 |
| 1.6 | Services composition + job runner | m1 |
| 1.7 | Web host, server control, SSE, core API | m1 |
| 1.8 | Core CLI commands | m1 |
| 1.9 | UI shell, review loop UI, setup view | m1 |
| 1.10 | Orchestrator skill + M1 end-to-end loop test | m1 |
| 2.1 | Sample databases + SQL test fixtures | m2 |
| 2.2 | Catalog model + extractor | m2 |
| 2.3 | Value signatures + profiler | m2 |
| 2.4 | Name normalisation, vectoriser, vector index | m2 |
| 2.5 | Fingerprint, drift, catalog repo, discover job, `discover/search/show` | m2 |
| 2.6 | Rules, analyzer, analyze job | m2 |
| 2.7 | Analysis module + schema-analyst agent | m2 |
| 2.8 | Analysis UI, catalog endpoints, standalone export | m2 |
| 3.1 | Type compatibility | m3 |
| 3.2 | Mapping payload + validator | m3 |
| 3.3 | Auto-mapper + automap job | m3 |
| 3.4 | Mapping module + mapping-architect agent | m3 |
| 3.5 | Mapping UI + human edits | m3 |
| 4.1 | SqlQuote, plan payload, topological sort | m4 |
| 4.2 | SQL generator | m4 |
| 4.3 | SQL validator + sqlgen job + `sql` commands | m4 |
| 4.4 | SQL module + sql-engineer agent + script pack | m4 |
| 4.5 | SQL UI (highlighter, line comments, diff) | m4 |
| 5.1 | Transfer repo, options, control table, chunk planner | m5 |
| 5.2 | Bulk loader + bisection | m5 |
| 5.3 | Transfer engine (deps, parallelism, pause/resume/cancel, crash recovery) | m5 |
| 5.4 | Preflight, run validation, final report | m5 |
| 5.5 | Transfer API + CLI + transfer playbook | m5 |
| 5.6 | Execute & report UI | m5 |
| 5.7 | Full end-to-end migration test | m5 |
| 6.1 | `dbm demo` + `dbm export` | m6 |
| 6.2 | Release build, dist, launcher/doctor hardening | m6 |
| 6.3 | Docs + skill polish + troubleshooting | m6 |
| 6.4 | Installed-plugin end-to-end verification (CLI + Desktop) | m6 |
