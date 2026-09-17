# db-migrate — SQL Server to SQL Server migration agent for Claude Code

A Claude Code plugin, backed by a .NET 8 engine (`dbm`), that migrates data from one SQL Server database into
another **existing** SQL Server database with a **different schema**. The work happens in four human-approved
phases — schema analysis, source-to-target mapping, migration SQL, then a chunked and resumable transfer — and the
human reviews every phase in a local web UI that keeps working whether Claude was started from the CLI, the Desktop
app, or is not running at all.

This repository is both the plugin's source and a Claude Code plugin marketplace that lists it.

## What it does

| Phase | Who does the work | What comes out |
|---|---|---|
| **Setup** | You, in the browser | Two ADO.NET connection strings, tested, encrypted at rest, never shown to Claude |
| **Discovery** | Engine (server job) | Normalised catalog of both databases, data profile, local vector index, schema fingerprints |
| **Analysis** ⟲ | Engine rules + `schema-analyst` subagent | Analysis report: inventory, findings by severity, risks, recommendations |
| **Mapping** ⟲ | Engine auto-mapper + `mapping-architect` subagent | Table and column mappings, T-SQL transforms, splits, merges, lookups, defaults, explicit drops |
| **SQL** ⟲ | Engine templating + `sql-engineer` subagent | Ordered migration plan, per-table SQL (direct or staging + MERGE), pre/post scripts, downloadable script pack |
| **Ready / Transfer** | You start it; the engine runs it | Chunked, checkpointed, resumable copy with live progress; row-level errors bisected and logged |
| **Complete** | Engine | Final report: source vs target vs rejected counts, checksums, duration, throughput, error samples |

⟲ marks a review loop: the engine writes a draft, the subagent turns it into the first reviewable version, you
approve it or leave feedback (general or anchored to a table, column, finding, mapping row or SQL line), the subagent
answers every item with a patch and a response, and a new version appears with a diff. Every version and every piece
of feedback is kept. Reopening an approved phase marks everything downstream stale.

### Design principles

- **Scripts do the deterministic work; Claude spends tokens only on judgement.** Catalog extraction, profiling, rule
  checks, candidate matching, SQL templating, validation, the transfer and all rendering are engine code. Subagents
  read one compact work packet and return one JSON patch.
- **The engine owns the state machine.** `dbm next` tells the orchestrator what to do; the skill is a short loop.
- **The UI is always available.** The web server runs detached from Claude and survives it exiting. You can browse,
  leave feedback, approve, pause and run the transfer with no agent session open; a banner tells you when an agent
  step is pending.
- **Pause and resume anywhere.** Every phase can be paused from the UI. A transfer pauses at a chunk boundary and
  resumes from its checkpoint, mid-table, in a new run or after a crash.
- **Secrets stay local.** Connection strings are encrypted with Windows DPAPI or an AES-GCM key in the user profile,
  and are redacted from logs, events, packets, exports and the UI after save.
- **Minimal dependencies.** Three NuGet packages at runtime (`Microsoft.Data.SqlClient`, its Azure extension for
  Entra ID auth, `Microsoft.Data.Sqlite`). No npm, no CDN, no MCP server; the browser UI is vanilla HTML, CSS and JS.

## Requirements

- **Claude Code** (CLI or Desktop, local sessions).
- **ASP.NET Core Runtime 8 or later** on the machine that runs the engine. The launcher prints the one-line install
  command for your OS if it is missing (`winget install Microsoft.DotNet.AspNetCore.8` on Windows,
  `brew install --cask dotnet` on macOS, `aspnetcore-runtime-8.0` on Linux).
- The **.NET SDK** only if `engine/dist/` has not been built for your checkout; the launcher builds it once.
- Network access from the machine to **both** SQL Server instances. Source and target may be on different servers;
  no linked server is needed. Any authentication mode `Microsoft.Data.SqlClient` supports works verbatim: SQL login,
  Windows integrated, Kerberos, and all Entra ID modes.

## Install

```text
/plugin marketplace add https://github.com/mintoantony/DatamigrationAgent
/plugin install db-migrate@db-migrate
```

Updates flow from git. A `SessionStart` hook runs `dbm doctor --quiet` on every session: it verifies the runtime,
builds the engine if needed, and stays silent when healthy.

## Use

In a Claude Code session, in the folder that should hold the migration project:

```text
/db-migrate
```

The engine creates `.dbmigrate/` in that folder (SQLite state, server discovery file, exports; it writes its own
`.gitignore`), starts the local web server on `127.0.0.1` with a per-project token, and opens the browser. Paste the
two connection strings in the Setup screen. From there the orchestrator relays between the engine, the three
subagents and you, keeping each message to one line; the browser is where you read and decide.

Other forms:

| Command | Effect |
|---|---|
| `/db-migrate resume` | Continue a paused or interrupted project in a new session |
| `/db-migrate status` | One line: current phase and status |
| `/db-migrate ui` | Re-open the browser on the running project |
| `/db-migrate stop` | Stop the project's UI server |

### The `dbm` command line

The plugin puts `dbm` on the session PATH. Claude uses it; you can too. Every command prints compact JSON unless
noted, exit code 0 on success and 1 with `{"error":"<code>","message":"…"}` on failure. Global option:
`--workspace <dir>`.

| Command | Purpose |
|---|---|
| `dbm doctor [--quiet] [--rebuild]` | Check runtime, drivers and user profile; build the engine if needed |
| `dbm init` | Create or reuse the project in this folder, start the UI server, open the browser |
| `dbm ui` / `dbm stop` / `dbm serve` | Open the UI; stop the server; run the server in the foreground |
| `dbm status` | Project, phase, status, next action, UI URL |
| `dbm next` / `dbm await [--timeout s]` | The orchestrator's next action; block until a human action changes it |
| `dbm pause` / `dbm resume` | Pause or resume agent work |
| `dbm discover [--inline]` | Extract and profile both catalogs, rebuild the vector index |
| `dbm search <query> [--side src\|tgt] [--kind table\|column] [-k n]` | Vector search over both catalogs (text output; `--json`) |
| `dbm show <schema.table[.column]\|F001> [--side src\|tgt]` | Compact view of a table, column or finding |
| `dbm map auto [--inline]` | Re-run the auto-mapper |
| `dbm sql gen [--inline]` / `dbm sql validate [--task <id>] [--patch <file>]` | Regenerate the plan; validate it live against both databases |
| `dbm apply <patch.json> [--dry-run]` | Validate and apply a subagent patch as a new version |
| `dbm artifact <phase> [--version n] [--path /pointer]` / `dbm feedback <phase>` | Read a phase artifact or its feedback items |
| `dbm run-jobs` | Run queued server jobs in this process and exit |
| `dbm version` / `dbm help` | Engine and runtime versions; command list |

Transfer control (`dbm transfer start|pause|resume|cancel|status`), `dbm export` and `dbm demo` arrive with the
milestones still in progress (see Status).

## How the transfer works

- Tasks are ordered by a topological sort of the target FK graph and run in dependency levels with parallel
  workers. FK cycles load with constraints `NOCHECK` and re-enable them `WITH CHECK` in a post-script.
- Rows stream from a `SqlDataReader` on the source into `SqlBulkCopy` on the target; Claude is never in the data path.
- Keyed tables are copied in keyset chunks. Each chunk commits in its own target transaction together with its
  checkpoint row in a control table in the target, so a chunk is either committed with its checkpoint or not at all.
  Resume reads that checkpoint. Tables with no usable key load in a single transaction and are flagged in analysis.
- `staging_merge` tasks bulk-copy each chunk into a session temp table and run the task's MERGE inside the same
  transaction.
- A failed bulk batch is bisected to isolate the bad rows; they are recorded with the reason, and the task stops or
  skips-and-logs per the run's options.
- Pre-flight checks connectivity, schema drift against the approved fingerprints, target row counts, permissions
  and estimated volume before a run; running the transfer requires typing the target database name.
- After the run, validation compares source and target row counts per task and, where possible, per-column
  checksums, and the final report says in words what it could not check and why.

## Repository layout

```text
.claude-plugin/marketplace.json       marketplace listing for "db-migrate"
plugins/db-migrate/
  .claude-plugin/plugin.json          plugin manifest
  skills/db-migrate/SKILL.md          the /db-migrate orchestrator loop
  agents/                             schema-analyst, mapping-architect, sql-engineer
  hooks/hooks.json                    SessionStart → dbm doctor --quiet
  bin/dbm, bin/dbm.cmd                launchers (sh; Windows)
  samples/legacyshop-to-shopv2/       LegacyShop → ShopV2 sample pair used by tests and the demo
  engine/
    Dbm/                              the engine: Cli/, Core/, Web/, wwwroot/
    Dbm.Tests/                        xUnit unit and integration tests, node --test for the browser helpers
    dist/                             framework-dependent build output (committed on release)
docs/superpowers/specs/               the design spec
docs/superpowers/plans/               the implementation plan, one file per milestone
```

## Building and testing from source

```text
dotnet build plugins/db-migrate/engine/Dbm.sln
dotnet test  plugins/db-migrate/engine/Dbm.sln --no-build
node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"
```

Integration tests need a SQL Server. They read `DBM_TEST_SQL` and default to
`Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`; Docker's
`mcr.microsoft.com/mssql/server` works too. Unit tests alone: add `--filter "Category!=Integration"`.
The sample pair under `plugins/db-migrate/samples/legacyshop-to-shopv2/` seeds a source with renames, splits, merges,
type changes, orphan rows, a heap and an FK cycle, and a target that rejects exactly eight rows with the reference
mapping.

## Status

| Milestone | State |
|---|---|
| M0 Skeleton, M1 Core platform | done |
| M2 Discovery and analysis loop | done |
| M3 Mapping loop | done |
| M4 SQL loop and script pack | done |
| M5 Transfer engine and report | in progress — repository, bulk loader, task runner, preflight, validation and final report are merged; the transfer service, API, CLI, Execute and Final report screens and the end-to-end test follow |
| M6 Packaging, demo, docs | not started |

Each task is implemented and reviewed on its own branch before it reaches `main`. The design spec is
`docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md`; the plan is under `docs/superpowers/plans/`.

### Out of scope for v1

Engines other than SQL Server (the adapter seam exists), creating or redesigning the target schema, incremental or
continuous sync, cut-over orchestration, multi-user editing of one project, and Claude Code cloud sessions.

## Security notes

- The web server binds `127.0.0.1` only and requires the project token on every request; requests with a foreign
  `Host` header are rejected.
- Connection strings are encrypted at rest and never appear in logs, events, work packets, exports or CLI output.
- Work packets carry no credentials and no row data beyond profile samples, which can be disabled per project.
- Destructive actions (truncating the target, running the transfer) require typed confirmation.

## Author

Xanalys. Built with Claude Code.
