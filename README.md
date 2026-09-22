# db-migrate

A Claude Code plugin that moves data from one SQL Server database into another **existing SQL Server database with a
different schema**. A .NET 8 engine (`dbm`) does the deterministic work, Claude does the judgement, and you approve
every step in a local web page that keeps working whether Claude was started from the CLI, the Desktop app, or is not
running at all.

Pause at any point, close Claude Code, come back tomorrow: everything is stored in your project folder and the engine
knows where to continue.

This repository is both the plugin's source and a Claude Code plugin marketplace that lists it.

## What it does

| Phase | Who does the work | What comes out |
|---|---|---|
| **Setup** | You, in the browser | Two ADO.NET connection strings, tested, encrypted at rest, never shown to Claude |
| **Discovery** | Engine (server job) | Normalised catalog of both databases, data profile, local vector index, schema fingerprints |
| **Analysis** ⟲ | Engine rules + `schema-analyst` subagent | Analysis report: inventory, findings by severity, risks, recommendations |
| **Mapping** ⟲ | Engine auto-mapper + `mapping-architect` subagent | Table and column mappings, T-SQL transforms, splits, merges, lookups, defaults, explicit drops |
| **SQL** ⟲ | Engine templating + `sql-engineer` subagent | Ordered migration plan, per-table SQL (direct or staging + MERGE), pre/post scripts, downloadable script pack |
| **Execute / Transfer** | You start it; the engine runs it | Chunked, checkpointed, resumable copy with live progress; row-level errors bisected and logged |
| **Report** | Engine | Final report: source vs target vs rejected counts, checksums, duration, throughput, error samples |

⟲ marks a review loop: the engine writes a draft, the subagent turns it into the first reviewable version, you
approve it or leave feedback (general or anchored to a table, column, finding, mapping row, SQL task or SQL line), the
subagent answers every item — *addressed*, or *declined* with a reason — and a new version appears with a diff. Every
version and every piece of feedback is kept; approving locks the version.

### Design principles

- **Scripts do the deterministic work; Claude spends tokens only on judgement.** Catalog extraction, profiling, rule
  checks, auto-mapping, SQL templating and validation, the transfer and every rendered page are engine code that runs as
  server jobs, costs no tokens and keeps running when Claude Code is closed. Each subagent reads one compact work packet
  — confident items summarised, only the uncertain ones in detail — and returns one small JSON patch; it looks things up
  with a local vector search instead of loading whole schemas, and rework sends only your feedback and the affected
  slice.
- **The engine owns the state machine.** `dbm next` tells the orchestrator what to do, so the skill is a short loop.
  Waiting for you is a background `dbm await` that costs nothing.
- **The UI is always available.** The web server runs detached from Claude and survives it exiting. You can browse,
  leave feedback, approve, pause and run the transfer with no agent session open; a banner tells you when a Claude step
  is pending.
- **Pause and resume anywhere.** A transfer pauses at a chunk boundary and resumes from its checkpoint, mid-table, in a
  new run or after a crash.
- **Secrets stay local.** See [Security](#security).
- **Minimal dependencies.** Three NuGet packages at runtime (`Microsoft.Data.SqlClient`, its Azure extension for
  Entra ID auth, `Microsoft.Data.Sqlite`). No npm, no CDN, no MCP server; the browser UI is vanilla HTML, CSS and JS.

## Requirements

| What | Details |
|---|---|
| Claude Code | CLI or the Desktop app (Code tab, **local** session). Cloud sessions are not supported. |
| ASP.NET Core Runtime 8 or later | One installer that also contains the .NET runtime. Windows: `winget install Microsoft.DotNet.AspNetCore.8` · macOS: `brew install --cask dotnet` · Debian/Ubuntu: `sudo apt-get install -y aspnetcore-runtime-8.0` · Fedora/RHEL: `sudo dnf install aspnetcore-runtime-8.0`. Check with `dotnet --list-runtimes` (look for `Microsoft.AspNetCore.App 8.x` or later). |
| .NET SDK 8 or later | Only to build the engine yourself (a development checkout, or a copy without `engine/dist`). Releases ship the build. |
| SQL Server source and target | Both reachable from **your machine**. They may live on different servers; no linked server is needed, because the data streams through the local engine. Developed against SQL Server 2025 and LocalDB. |
| Permissions | Source: read access plus `VIEW DEFINITION` (and `VIEW DATABASE STATE` for size estimates). Target: `db_datareader`, `db_datawriter` and `db_ddladmin`, or equivalent: `INSERT` and `ALTER` on the target tables (identity insert, constraint `NOCHECK`) plus `CREATE TABLE` for the `dbo.__dbm_checkpoint` control table. |
| A browser | Any modern browser. The UI has no CDN and works offline. |
| Windows only: Git for Windows | The plugin's session-start hook runs under `sh`, which Claude Code on Windows takes from Git Bash (the same Git Bash its Bash tool uses). |

Every authentication mode Microsoft.Data.SqlClient supports works verbatim: SQL logins, Windows integrated/Kerberos and
the Entra ID modes (`Active Directory Default`, `Interactive`, `Integrated`, `Service Principal`, `Device Code Flow`,
`Managed Identity`, `Workload Identity`).

## Install

In Claude Code:

```
/plugin marketplace add <git-url-of-this-repo>
/plugin install db-migrate@db-migrate
```

`<git-url-of-this-repo>` is the HTTPS or SSH URL you would clone this repository from; on GitHub, `owner/repo` works
too — for this repository, `/plugin marketplace add mintoantony/DatamigrationAgent`. It is a private repository, so the
person installing needs access to it (the same access `git clone` would need). From a local clone use its path with
forward slashes, e.g. `/plugin marketplace add D:/src/DatamigrationAgent`. Start a new Claude Code session afterwards.

The same from a terminal: `claude plugin marketplace add <git-url-of-this-repo>` then
`claude plugin install db-migrate@db-migrate`. Updates: `claude plugin marketplace update db-migrate` and
`claude plugin update db-migrate@db-migrate`, then restart Claude Code.

Each session start runs a health check (`dbm doctor --quiet` through the plugin's `hooks/session-start.sh`). It is
silent when everything is healthy; otherwise it shows you a `db-migrate:` message saying exactly what is wrong (missing
runtime, unusable encryption, stale engine build) and passes the same text to Claude. It never blocks the session.

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
   Only give Claude a string like these, with Windows authentication and no secret in it. If the server needs a SQL
   login, run `dbm demo --server "…" --attach` yourself in a terminal outside Claude Code, in the project folder — not
   with `!` in Claude Code, whose command and output land in the conversation — so the password never reaches the chat.
   `dbm` is on the PATH only inside Claude Code, so first ask Claude for the launcher's full path (it holds no secret);
   in PowerShell or cmd use the `dbm.cmd` next to it.
   `--attach` saves both connections into the project, so there is nothing to paste. Useful flags: `--force` (drop and
   recreate exactly those two demo databases — anything in them is lost), `--prefix Team_` (other names), `--scale 10`
   (ten times the rows).
   Without `--attach`, `dbm demo` only prints the two connection strings, with any password replaced by `***`; with SQL
   authentication you would have to put the password back in yourself (in the browser, never in the chat), so prefer
   `--attach`, which saves the real strings encrypted.
4. In the browser, discovery runs by itself. Then review each phase: **Analysis** (comment on a finding, press *Request
   changes*, then *Approve* the reworked version), **Mapping** (check the auto-mapped columns, fix a transform, approve),
   **SQL** (read the generated tasks, approve).
5. On **Execute**, press *Run pre-flight*, choose *Skip and log bad rows*, press **Execute…**, type the target database
   name and press *Start transfer*. With the intended mapping the final report shows **8 rejected rows**: 2 orphan
   orders, their 2 order lines, 3 comments too long for the target column and 1 zero-quantity line. They are wrong on
   purpose, to show how bad rows are captured.

Afterwards, run `/db-migrate stop` (or `dbm stop` in the project folder) so the local server releases its connections,
then drop the two demo databases, e.g.
`sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "DROP DATABASE DbmDemo_LegacyShop; DROP DATABASE DbmDemo_ShopV2"`
(with `--prefix`, use your prefix instead of `DbmDemo_`).

## Using it on real databases

1. Run `/db-migrate` in your project folder.
2. In the browser **Setup** screen paste the source and target ADO.NET connection strings, press **Test**, then **Save**.
   Never paste connection strings into the Claude chat — Claude never needs them.
3. Review and approve Analysis → Mapping → SQL, then run the transfer from **Execute**. The
   [user guide](docs/README.md) walks through every screen.

The engine creates `.dbmigrate/` in that folder (SQLite state, server discovery file and log, exports; it writes its own
`.gitignore`), starts the local web server on `127.0.0.1` with a per-project token, and opens the browser. From there
the orchestrator relays between the engine, the three subagents and you, keeping each message to one line; the browser
is where you read and decide.

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

- **Workflow**: `SETUP → DISCOVERY → ANALYSIS ⟲ → MAPPING ⟲ → SQL ⟲ → READY → TRANSFER → COMPLETE` (the UI calls
  READY *Execute* and COMPLETE *Report*).
- **Three subagents**: `schema-analyst` writes the analysis narrative, `mapping-architect` resolves uncertain mappings
  and writes transforms, `sql-engineer` writes custom SQL.
- **Reopen and staleness.** Reopening an approved phase marks the later phases up to Execute *stale*; each is
  regenerated and must be approved again. Analysis, Mapping and SQL can be reopened before the first run and after a
  run that completed, was cancelled or failed (a failed run is cancelled by the reopen); not while a run is running or
  paused. After re-approval, Execute starts a new run; earlier runs' final reports stay on the Report screen.
- **Drift check.** Schema fingerprints are re-checked before approval and in the pre-flight before execution; if a
  database changed, approval is refused until you press **Re-run discovery** on the Analysis screen.
- **Pause / resume.** **Pause** in the top bar stops agent work: Claude finishes the patch it is writing and takes no
  new step until **Resume**. The transfer has its own **Pause** on the Execute screen. If Claude Code was closed,
  `/db-migrate resume` in any new session picks up exactly where the engine stopped.

### The transfer

- Tasks follow the target's foreign-key order: up to *Parallel tasks* load at once, and a task starts only when the
  tables it depends on are loaded. FK cycles load with the cut constraints `NOCHECK` and re-enable them `WITH CHECK` in
  a post-script. **Cancel** runs that post-script too, best effort, and the run's notes say per constraint what was
  restored (a constraint the loaded rows violate is re-enabled without the check and reported as not trusted). A paused
  or failed run keeps the pre-script in force - Resume expects it - and the Execute screen names it.
- Rows stream from a `SqlDataReader` on the source into `SqlBulkCopy` on the target; Claude is never in the data path.
- Keyed tables are copied in key-ordered chunks (100 000 rows by default, 5 000 for tasks with LOB columns). Each chunk
  commits in its own target transaction together with its checkpoint row in `dbo.__dbm_checkpoint`, so a crash or a
  pause resumes exactly where it stopped. Tables with no usable key load in a single transaction and are flagged in the
  pre-flight.
- `staging_merge` tasks bulk-copy each chunk into a session temp table and run the task's MERGE inside the same
  transaction.
- A failed bulk batch is bisected to isolate the bad rows; they are recorded with the reason, and the task stops or
  skips-and-logs per the run's options.
- Pre-flight checks connectivity, schema drift against the approved fingerprints, target row counts, permissions and
  the estimated volume, and - for a task whose source query joins - that its chunk key is unique (a repeated key would
  lose rows at chunk boundaries; it blocks the run). Running the transfer requires typing the target database name. A
  new run into target tables that already hold rows also needs *Truncate target first* or a confirmation naming those
  tables; pre-flight says which of them have no key (their rows would be loaded twice).
- Over a target that was not empty, the report's headline says "target tables were not empty before this run; counts
  compare rows added" instead of "validated".
- After the run, validation compares source and target row counts per task and, where possible, per-column checksums,
  and the final report says in words what it could not check and why.

## The `dbm` command line

The plugin puts `dbm` on the session PATH. Claude uses it; you can too. Every command prints one line of compact JSON
unless noted: exit code 0 on success, and 1 with `{"error":"<code>","message":"…"}` on failure. Global option:
`--workspace <dir>` (default: the current folder).

| Command | Purpose |
|---|---|
| `dbm doctor [--quiet] [--rebuild]` | Health checks (see Troubleshooting); `--quiet` is silent when healthy; `--rebuild` makes the launcher rebuild the engine first |
| `dbm init [name] [--no-browser]` | Create or reuse the project in this folder, start the UI server, open the browser |
| `dbm ui [--no-browser]` / `dbm stop` / `dbm serve` | Open the UI; stop the server; run the server in the foreground |
| `dbm status` | Project, phase, status, next action, UI URL |
| `dbm next` / `dbm await [--timeout s]` | The orchestrator's next action; block until it changes (`0` = no timeout, the default) |
| `dbm pause` / `dbm resume` | Pause or resume agent work |
| `dbm config sample-values [off]` | Show whether the column profiles Claude reads include sample values, or switch them off; switching them back on is done only on the Setup screen (see Security) |
| `dbm discover [--inline]` | Extract and profile both catalogs, rebuild the vector index |
| `dbm search <query> [--side src\|tgt] [--kind table\|column] [-k n] [--json]` | Vector search over both catalogs (text output unless `--json`) |
| `dbm show <schema.table[.column]\|F001> [--side src\|tgt] [--json]` | Compact view of a table, column or finding |
| `dbm map auto [--inline]` | Re-run the auto-mapper |
| `dbm sql gen [--inline]` / `dbm sql validate [--task <id>] [--patch <file>]` | Regenerate the plan; validate it against both databases (bare carriage returns are reported offline too). Without `--patch`, a whole-plan pass over a version with no recorded validation stores it as a new version while SQL awaits review, like the screen's Validate live |
| `dbm apply <patch.json> [--dry-run]` | Validate and apply a subagent patch as a new version |
| `dbm artifact <phase> [--version n] [--path /pointer]` / `dbm feedback <phase> [--status s]` | Read a phase artifact or its feedback items |
| `dbm transfer start --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]` | Start the transfer (normally done from the UI) |
| `dbm transfer pause\|resume\|cancel\|status` | Control or inspect the running transfer |
| `dbm demo --server "<conn>" [--attach] [--force] [--prefix p] [--scale n]` | Create the LegacyShop/ShopV2 sample databases |
| `dbm export <analysis\|sql\|sqlpack\|report> [--out path]` | Write a standalone export (see the [user guide](docs/README.md#exports)) |
| `dbm run-jobs` | Run queued server jobs in this process and exit |
| `dbm version` / `dbm help` | Engine and runtime versions; command list |

## Security

- **Local only.** The server binds to `127.0.0.1`, requires a random per-project token on every API call and rejects
  foreign `Host` headers. The tokenised URL appears only in the output of local `dbm` commands and in
  `.dbmigrate/server.json`.
- **Connection strings** are entered in the browser (or saved by `dbm demo --attach`) and encrypted at rest: DPAPI for
  the current user on Windows, AES-GCM with a key in `~/.dbmigrate/key` (mode 600) elsewhere. They are masked after
  saving and never appear in logs, events, work packets, exports, CLI output (except `dbm demo` without `--attach`,
  which prints them with any password replaced by `***`) or anything sent to Claude. The one way a secret could reach
  the chat is you typing it there — so never do, not even for the demo.
- **What Claude sees:** schema metadata (names, types, keys, row counts, sizes), column profiles (null share, distinct
  ratio, lengths, value patterns and a few sample values while the project's sample-values setting is on, which is the
  default), rule findings, the mapping, the SQL plan and your feedback. Switch sample values off under **Privacy** on the
  Setup screen or with `dbm config sample-values off`: the sample values and text min/max already collected are removed
  from the project and its work packets, so no work packet and no `dbm show` carries any, and later discoveries
  collect none. The state database is overwritten as far as SQLite allows; raw pages freed before the switch can still
  hold old values until they are reused. Files you exported earlier to `.dbmigrate/exports` keep the values they
  were written with. Claude still sees the statistics and patterns, so its analysis and mapping suggestions may be less
  precise. Switching it back on is done only on the Setup screen (the CLI refuses it) and takes effect at the next
  discovery (**Re-run discovery**).
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
| `dbm: warning: … running the previous engine/dist … instead` | A rebuild of the engine failed and your command ran on the previous build; it is retried next time. If the reason is `engine/dist is in use by a running dbm server`, run `dbm stop` in the other project folder whose server uses this engine. |
| `engine build failed` or `engine/dist could not be replaced` (exit 4) | There was no previous build to fall back to. If the message says the previous build "is in <folder> - rename it to dist", rename that folder to `engine/dist`. Otherwise run the command again; for a build failure, `dotnet build plugins/db-migrate/engine/Dbm.sln` shows the compiler errors. |
| Session start shows `db-migrate: …` | The health check found a problem; the message names it (see `dbm doctor` below). Claude sees it too. |
| The browser did not open | Run `/db-migrate ui` (or `dbm ui`) and open the printed `http://127.0.0.1:<port>/?t=<token>` URL. |
| "This page needs its access link." in the browser | The server restarted with a new token: run `/db-migrate ui` (or `dbm ui`) for a fresh link. |
| "Agent offline" banner | No Claude session is running the loop: type `/db-migrate resume`. |
| A connection test fails with a certificate error | Add `TrustServerCertificate=True` (test servers) or install the server's CA certificate. |
| `demo_exists` | Add `--force` to recreate the two demo databases (anything in them is lost), or use another `--prefix`. |
| Anything else | Claude reads `plugins/db-migrate/skills/db-migrate/reference/troubleshooting.md` when a command fails; you can read it too. |

`dbm doctor` prints a health report as JSON, eight checks: runtime, ASP.NET Core, SqlClient, SQLite, user profile,
encryption round-trip, this project's server and whether the engine build matches the plugin version.
`dbm doctor --quiet` skips the server check, prints nothing when healthy and otherwise one line per failing check.

## Development

```
.claude-plugin/marketplace.json       marketplace listing "db-migrate"
plugins/db-migrate/
  .claude-plugin/plugin.json          plugin manifest
  skills/db-migrate/SKILL.md          the /db-migrate orchestrator loop (+ reference/ playbooks)
  agents/                             schema-analyst, mapping-architect, sql-engineer
  hooks/                              SessionStart → session-start.sh → dbm doctor --quiet
  bin/dbm, bin/dbm.cmd                launchers (sh; Windows)
  samples/legacyshop-to-shopv2/       LegacyShop → ShopV2 sample pair used by the tests and the demo
  engine/
    Dbm/                              the engine (.NET 8): Cli/, Core/, Web/, wwwroot/
    Dbm.Tests/                        xUnit unit and integration tests; js/*.test.cjs for the UI libraries
    dist/                             committed release build (framework-dependent)
docs/                                 user guide; superpowers/specs (design spec), superpowers/plans (implementation plan)
```

- **Build:** `dotnet build plugins/db-migrate/engine/Dbm.sln` (.NET SDK 8 or later).
- **Unit tests:** `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"`.
- **Integration tests:** need a SQL Server. `DBM_TEST_SQL` selects it (default
  `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`). Without LocalDB, run
  `docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Your_str0ng_pw' -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest`
  and set `DBM_TEST_SQL="Server=localhost,1433;User ID=sa;Password=Your_str0ng_pw;TrustServerCertificate=true"`. Then
  `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration"`.
- **UI tests:** `node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"` (Node 21 or later; Node is a
  development-only dependency).
- **Sample pair:** `plugins/db-migrate/samples/legacyshop-to-shopv2/` seeds a source with renames, splits, merges, type
  changes, orphan rows, a heap and an FK cycle, and a target that rejects exactly eight rows with the reference mapping.
- **Run the plugin from the working tree:** `claude --plugin-dir plugins/db-migrate`. The launcher rebuilds
  `engine/dist` whenever `dist/VERSION` differs from `plugin.json`, when `DBM_REBUILD=1` is set, or on
  `dbm doctor --rebuild`.
- **Validate:** `claude plugin validate plugins/db-migrate` and `claude plugin validate .`.
- **Release:**
  1. Bump `version` in `plugins/db-migrate/.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` and
     `<Version>` in `plugins/db-migrate/engine/Directory.Build.props` (the release script checks they agree).
  2. Run `pwsh plugins/db-migrate/engine/release.ps1` (or `sh plugins/db-migrate/engine/release.sh`): unit tests, UI
     tests, Release publish with a `VERSION` file, a contents check (no `.pdb`, `.exe`, `.user`, `.suo`, `.mdb` or
     `appsettings.*.json`), smoke test, swap into `engine/dist`, plugin validation, size summary.
  3. Commit `engine/dist` and tag with `claude plugin tag plugins/db-migrate --push` (creates `db-migrate--v<version>`).

  `engine/dist` is committed build output and the launcher rebuilds it only when `VERSION` changes, not when the source
  changes at the same version. Run the release script and commit `engine/dist` as the last step before any version bump
  or merge to `main`, or users without the .NET SDK run old code.

  Teammates update with `claude plugin update db-migrate@db-migrate`.

Design spec: [docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md](docs/superpowers/specs/2026-09-11-db-migrate-agent-design.md).
The implementation plan is under `docs/superpowers/plans/`, one file per milestone.

## Status

| Milestone | State |
|---|---|
| M0 Skeleton, M1 Core platform | done |
| M2 Discovery and analysis loop | done |
| M3 Mapping loop | done |
| M4 SQL loop and script pack | done |
| M5 Transfer engine and report | done (including the end-to-end LegacyShop → ShopV2 test) |
| M6 Packaging, demo, docs | done — `dbm demo`/`dbm export`, doctor checks, launchers, release scripts, these docs and the committed `engine/dist` build |
| Final review fixes | done — a completed run always reaches Complete, Reopen after a run, Cancel restores the plan's pre-load SQL, the new-run guard, pageable work packets, the chunk-key check, a reproducible `engine/dist` |

As of 2026-09-18. Each task is implemented and reviewed on its own branch before it reaches `main`.

### Out of scope for v1

Engines other than SQL Server (the adapter seam exists), creating or redesigning the target schema, incremental or
continuous sync, cut-over orchestration, multi-user editing of one project, and Claude Code cloud sessions.

## Author

Xanalys. Built with Claude Code.
