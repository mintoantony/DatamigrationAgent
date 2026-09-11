# db-migrate — SQL Server Migration Agent (Design Spec)

- **Date:** 2026-09-11
- **Status:** Approved in brainstorming, pending written-spec review
- **Author:** minto.antony@xanalys.com (with Claude)

## 1. Goal

A reusable, easily installable Claude Code plugin that migrates data from one SQL Server database to another **existing SQL Server database with a different schema**, via a human-in-the-loop workflow:

1. Identify both databases, extract and analyse schemas, produce an analysis report → iterate on human feedback until approved.
2. Produce a source→target mapping → iterate until approved.
3. Generate migration SQL → iterate until approved.
4. Execute the transfer from a web screen with real-time progress, then show a detailed final report.

The human interacts **through a local web UI** that is always available, whether the agent was started from the Claude Code CLI or the Desktop app. Any stage can be paused and resumed; all state is persisted. Deterministic work is done by scripts so Claude spends tokens only on judgement.

### Confirmed requirements

| Topic | Decision |
|---|---|
| Engines | SQL Server → SQL Server only (v1). Engine access is behind an adapter interface for future engines. |
| Target | Target DB already exists with its own (redesigned) schema; mapping is the core problem. |
| Topology | Source and target on **different servers**; no linked server assumed. Data is streamed through the tool. |
| Auth | User supplies **two ADO.NET connection strings**; any auth mode SqlClient supports must work verbatim (SQL login, Windows integrated/SSPI, Kerberos, all Entra ID modes). |
| Volume | Unknown / any size → chunked, checkpointed, resumable mid-table from day one. |
| Secrets | Connection strings encrypted locally; never shown to Claude, never logged, never in reports. |
| Audience | Team, cross-platform (Windows, macOS, Linux). |
| Stack | .NET 8 engine (approach A). |

### Out of scope (v1)

- Non-SQL-Server engines (the adapter seam exists; no implementations).
- Creating or redesigning the target schema.
- Incremental/CDC sync, continuous replication, cut-over orchestration.
- Multi-user concurrent editing of one project.
- Claude Code cloud sessions (plugins with `bin/` target local sessions).

## 2. Architecture

### 2.1 Components

```
Claude Code session (CLI or Desktop, local)
  │  /db-migrate skill (orchestrator) + 3 subagents
  │  calls `dbm …` via Bash (plugin bin/ is on PATH)
  ▼
dbm (single .NET 8 app)
  ├── CLI            — compact JSON in/out for Claude and scripts
  ├── Core           — state store, workflow state machine, catalog extractor,
  │                    profiler, rules, matcher + vector index, SQL generator,
  │                    transfer engine, crypto
  └── Web (Kestrel)  — detached local server: REST API + SSE + static UI (wwwroot)
        ▲
        │ http://127.0.0.1:<port>/?t=<token>
      Browser (vanilla HTML/CSS/JS, no CDN, works offline)
```

The web server runs **detached** from Claude: it survives Claude exiting, keeps serving the UI, and runs the transfer. Claude is never in the data path.

### 2.2 Repository / plugin layout

The repository is both a plugin marketplace and the plugin source.

```
DatamigrationAgent/                         (git repo)
├── .claude-plugin/marketplace.json         marketplace listing "db-migrate"
├── plugins/db-migrate/
│   ├── .claude-plugin/plugin.json
│   ├── skills/db-migrate/
│   │   ├── SKILL.md                        /db-migrate orchestrator loop (short)
│   │   └── reference/                      per-phase playbooks, read on demand
│   │       ├── analysis.md  mapping.md  sql.md  transfer.md  troubleshooting.md
│   ├── agents/
│   │   ├── schema-analyst.md
│   │   ├── mapping-architect.md
│   │   └── sql-engineer.md
│   ├── hooks/hooks.json                    SessionStart → `dbm doctor --quiet`
│   ├── bin/dbm  bin/dbm.cmd                launchers (sh / Windows)
│   └── engine/
│       ├── Dbm/                            the app (Cli/, Core/, Web/, wwwroot/)
│       ├── Dbm.Tests/                      xUnit
│       └── dist/                           framework-dependent build output (committed on release)
├── docs/
└── samples/legacyshop-to-shopv2/           demo + integration-test fixture SQL
```

### 2.3 Dependencies

- **Runtime prerequisite:** ASP.NET Core Runtime 8+ (single installer; includes .NET runtime).
- **NuGet:** `Microsoft.Data.SqlClient` 7.x, `Microsoft.Data.SqlClient.Extensions.Azure` 7.x (SqlClient 7 moved the Entra ID auth modes into this package), `Microsoft.Data.Sqlite`. Nothing else at runtime.
- **Test-only:** xUnit; optional Playwright smoke test.
- **No npm, no CDN, no MCP server.** A CLI on PATH is cheaper than MCP (tool schemas would occupy context every turn).

### 2.4 Distribution & first run

- Install: `/plugin marketplace add <git-url>` then `/plugin install db-migrate@db-migrate`. Updates flow from git.
- `bin/dbm` / `bin/dbm.cmd`: run `engine/dist/Dbm.dll` via `dotnet`; if `dist/` is missing or stale and the .NET SDK is present, build it once.
- SessionStart hook runs `dbm doctor --quiet`: verifies runtime (prints install guidance if missing), builds if needed. Idempotent, fast when healthy.

### 2.5 Storage

| What | Where |
|---|---|
| Project state (SQLite) | `<workspace>/.dbmigrate/state.db` |
| Server discovery | `<workspace>/.dbmigrate/server.json` (port, pid, token) |
| Exports (HTML/JSON/SQL packs) | `<workspace>/.dbmigrate/exports/` |
| Work packets / patches (transient) | `<workspace>/.dbmigrate/work/` |
| Encryption key | User profile only: Windows DPAPI (CurrentUser); macOS/Linux `~/.dbmigrate/key` (0600) used with AES-GCM |

`.dbmigrate/` should be git-ignored by users by default (`dbm init` writes a `.gitignore` inside it covering `state.db`, `work/`, `server.json`).

## 3. Workflow

### 3.1 Phase state machine (owned by the engine)

```
SETUP → DISCOVERY → ANALYSIS ⟲ → MAPPING ⟲ → SQL ⟲ → READY → TRANSFER → COMPLETE
```

| Phase | Actor | Output |
|---|---|---|
| SETUP | Human (UI) | Two connection strings entered, tested, encrypted; server metadata captured (version, edition, collation, compat level). |
| DISCOVERY | Script | Normalised catalog of both DBs + data profile + vector index + schema fingerprints. |
| ANALYSIS ⟲ | Script findings + schema-analyst | Analysis report artifact. |
| MAPPING ⟲ | Script auto-mapper + mapping-architect | Mapping artifact. |
| SQL ⟲ | Script templating + sql-engineer | Migration plan + SQL artifact. |
| READY | Human (UI) | Pre-flight checks passed, execution options chosen. |
| TRANSFER | Engine | Running / paused / failed / cancelled transfer run. |
| COMPLETE | Engine | Final report artifact. |

Script steps (discovery, rule findings, auto-mapping, SQL templating and validation) run as **server jobs** triggered by state transitions: they run even when Claude is offline and cost no tokens. Claude is needed only for agent (judgement) steps.

Phase statuses: `pending → running` (server job) `→ drafting` (agent must produce the first reviewable version) `→ awaiting_review ⇄ reworking → approved`; an approved or in-progress phase becomes `stale` when an upstream phase is reopened.

### 3.2 Review loop (ANALYSIS, MAPPING, SQL)

1. The server job writes a script draft as **v0** (internal); the agent turns it into **v1**, the first version shown for review (status `awaiting_review`).
2. Human reviews in the UI and either **Approves** or adds **feedback items**: general, or **anchored** to an element (table, column, finding, mapping row, SQL task/line).
3. On "Request changes", phase goes `reworking`; Claude receives only the feedback items plus the affected slice.
4. Claude returns a patch plus a **response per feedback item** (`addressed` with note, or `declined` with reason). Engine creates **vN+1**; UI shows the per-item responses and a diff vN→vN+1.
5. Repeat until Approved. Approval locks the version and records its schema fingerprint.

Rules:
- All versions and feedback are kept (audit trail).
- The human may also **edit directly** in the Mapping screen; direct edits create a new version authored by `human`.
- **Reopen** an approved phase → all downstream phases become `stale` and must be re-approved (e.g. mapping change invalidates SQL).
- **Drift check:** fingerprints are recomputed before approval and before execution; on mismatch the UI warns and offers re-discovery.
- Mapping approval is **blocked** while any source column is unmapped without an explicit `drop` decision, or any non-nullable target column without default lacks a source/expression.

### 3.3 Pause / resume

- A **Pause** control is available in every phase (UI top bar).
- Agent side: while paused, `dbm next` returns `{"action":"await","reason":"paused"}`; `dbm apply` still accepts an in-flight patch so no work is lost. Claude reports "Paused" in one line and keeps a background `dbm await` open (zero tokens), so clicking **Resume** continues automatically while the session is alive.
- Transfer side: workers finish the current chunk, commit, checkpoint, then stop.
- **Resume:** UI button (if agent online) or `/db-migrate resume` in any new Claude session; the engine state determines exactly where to continue.
- If Claude is not running, the UI still works: browse, queue feedback, approve, pause, run the transfer. A banner shows "Agent offline — run `/db-migrate resume` in Claude Code" whenever an agent action is pending.

### 3.4 Agent ↔ UI handshake

- `dbm await` long-polls `GET /api/agent/await` on the local server (starting the server if needed). While connected the server marks the agent **online**.
- The orchestrator runs `dbm await` as a **background** Bash command so waiting costs no tokens; the user's action completes the command and wakes the session.
- **Verified (2026-09-11, CLI on Windows):** a completed background command re-invokes the session. Desktop is re-checked in M6. **Fallback** for any surface that doesn't: foreground `dbm await --timeout 540` in a loop (a few tokens per ~9 minutes idle).
- **Verified (2026-09-11, Windows):** processes started from a tool command survive the command ending, so `dbm` can spawn the detached server directly (`setsid`/`nohup` equivalent used on macOS/Linux).

## 4. Agents & token strategy

### 4.1 Orchestrator skill (`/db-migrate`)

`SKILL.md` is a short loop; the engine decides what happens next:

```
dbm next  →  {"action": "...", ...}
  agent  → dispatch the named subagent with the packet path; `dbm apply` its patch; loop
  await  → run `dbm await` in background (returns the next action once state changes); loop
  stop   → complete or failed; report the one-line summary and end turn
```

Sub-commands: `/db-migrate` (start or continue in current workspace), `/db-migrate resume`, `/db-migrate status`, `/db-migrate ui` (re-open browser). Phase playbooks in `reference/` are read only when that phase's agent step needs them.

### 4.2 Subagents

All three: tools limited to `Bash, Read, Write`; read one work packet; may call `dbm search` / `dbm show` for more context; write one patch file; return one line (the patch path + counts).

| Agent | Input packet | Output patch |
|---|---|---|
| schema-analyst | Script-computed findings digest (stats, rule hits by severity, top risks), plus feedback items on rework | Executive summary, risk narrative, recommendations, per-finding commentary |
| mapping-architect | Auto-mapping result: confident matches (summary only) + low-confidence/unmapped items with top-k candidates and profiles; feedback items | Resolved table/column mappings, transforms (T-SQL expressions), splits/merges, lookups, defaults, drop decisions, rationale |
| sql-engineer | Template-generated plan, the tasks needing custom SQL, validation errors; feedback items | Custom source queries/expressions, staging/MERGE logic, pre/post scripts; must pass `dbm sql validate` before returning |

Model per agent is set in agent frontmatter (default: inherit) and can be tuned by the team.

### 4.3 Token-saving techniques

1. **Scripts do deterministic work:** catalog extraction, profiling, rule checks, candidate matching, SQL templating, validation, transfer, all rendering. Claude never writes HTML.
2. **Engine-driven state machine:** `dbm next` keeps the skill tiny and prevents drift.
3. **Compact work packets:** JSON with short keys and a legend; confident items summarised, only uncertain items detailed.
4. **Local vector index:** character 3-gram + word-token TF-IDF sparse vectors over table/column names (after abbreviation/synonym expansion), comments, and value-pattern signatures, stored in SQLite; cosine similarity in memory. `dbm search "customer email" --side tgt -k 8` returns top-k compact rows, so no agent ever loads a full schema. Embedding models can be added later behind `IVectorizer`.
5. **Incremental rework:** only feedback items plus the affected slice are sent; outputs are patches, not full artifacts.
6. **Zero-cost waiting:** background `dbm await`.

### 4.4 Auto-mapper scoring

Per candidate pair (table–table, then column–column within matched tables):

`score = 0.45·name + 0.20·type + 0.15·structure + 0.20·profile`

- **name:** vector cosine after normalisation (case, underscores, prefixes like `tbl_`/`fld_`), abbreviation dictionary (`cust→customer`, `addr→address`, `dob→birth date`, …, extensible per team in `~/.dbmigrate/synonyms.json` and per project).
- **type:** compatibility matrix (exact, widening, narrowing-with-risk, incompatible).
- **structure:** PK/FK role, position in FK graph, parent-table match.
- **profile:** null %, distinct ratio, length distribution, value-pattern signature overlap (e.g. email/date/phone shapes).

Bands: ≥ 0.85 auto-accepted (still reviewable), 0.50–0.85 sent to mapping-architect with top-k candidates, < 0.50 unmapped. Weights and thresholds are configurable.

## 5. Artifacts & formats

### 5.1 Analysis artifact

Server/DB metadata for both sides; object inventory (tables, columns, rows, sizes, indexes, FKs, triggers, views, procs, computed/identity/rowversion columns, temporal tables); findings by rule and severity, e.g.:
- heaps / tables without PK or unique key (affects chunked resume),
- deprecated/LOB types (`text`, `ntext`, `image`, `xml`, `(n)varchar(max)`),
- collation differences, orphaned FK data, triggers on target, identity columns,
- data-quality: null-heavy columns, out-of-range values vs target types, truncation risk (source max length > target length),
- estimated transfer size and time;

plus the agent-written summary, narrative and recommendations.

### 5.2 Mapping artifact

Table mappings (1→1, 1→N split, N→1 merge, lookup); per target column: source expression, transform, default, confidence, method (`exact|fuzzy|vector|agent|human`), rationale; explicit drop list for source columns; unresolved list (must be empty to approve).

### 5.3 SQL artifact (migration plan)

Global pre/post scripts (e.g. disable triggers/constraints, re-enable `WITH CHECK`); ordered task list — one per target table:

```
{ target, mode: "direct" | "staging_merge", sourceQuery, keyColumns,
  columnMap, identityInsert, preSql[], postSql[], validationSql[] }
```

Downloadable as a DBA-readable script pack (`.sql` files + README).

### 5.4 Patch format

```json
{
  "phase": "mapping",
  "baseVersion": 3,
  "ops": [ { "op": "replace", "path": "/tables/dbo.Customer/columns/Email/expr", "value": "LOWER(TRIM(s.EMAIL_ADDR))" } ],
  "responses": [ { "feedbackId": 12, "status": "addressed", "note": "Normalised email per your comment." } ]
}
```

`ops` use a JSON Patch (RFC 6902) subset (`add`, `replace`, `remove`). `dbm apply` validates the schema, the `baseVersion`, and that **every open feedback item has a response**, then writes vN+1.

## 6. CLI surface (`dbm`)

All commands print compact JSON unless `--human`.

| Command | Purpose |
|---|---|
| `dbm doctor [--quiet]` | Check runtime, build if needed, report health |
| `dbm init [name]` | Create `.dbmigrate/` project, start server, open browser |
| `dbm ui` | Ensure server running; open browser |
| `dbm status` | One-line project/phase status |
| `dbm next` | Next action for the orchestrator |
| `dbm await [--timeout s]` | Block until a human event (approve, feedback, pause, resume, reopen) |
| `dbm discover` | Extract catalogs, profile, build vector index, fingerprint |
| `dbm analyze` | Run rules → findings digest |
| `dbm map auto` | Auto-mapper → draft mapping |
| `dbm sql gen` / `dbm sql validate [--task T]` | Template SQL / validate against real DBs (`SET PARSEONLY`, `sp_describe_first_result_set`, target column compatibility) |
| `dbm packet <phase>` | Write work packet, print its path |
| `dbm apply <patch.json>` | Validate patch, create new version |
| `dbm search <query> [--side src\|tgt] [-k N]` | Vector search over catalog |
| `dbm show <object>` | Compact view of one table/column/finding/task |
| `dbm transfer start\|pause\|resume\|cancel\|status` | Transfer control (normally UI-driven) |
| `dbm export <analysis\|mapping\|sql\|report> [--html\|--json\|--sql]` | Standalone exports |
| `dbm demo --server "<conn>"` | Create LegacyShop / ShopV2 sample pair for a trial run |

## 7. State model (SQLite)

| Table | Key fields |
|---|---|
| `project` | id, name, created_at, paused |
| `connection` | side (src/tgt), encrypted_conn, server_meta_json, fingerprint |
| `phase` | name, status (`pending\|running\|drafting\|awaiting_review\|reworking\|approved\|stale`), current_version, approved_version, approved_fingerprint |
| `artifact` | id, phase, version, payload_json, author (`script\|agent\|human`), created_at |
| `feedback` | id, artifact_id, anchor, text, status (`draft\|open\|addressed\|declined`), response, created_at |
| `job` | id, kind, phase, status (`queued\|running\|done\|failed`), error, started_at, ended_at |
| `event` | id, ts, type, payload_json (audit log + SSE feed) |
| `catalog_object` | id, side, kind, schema, name, parent_id, meta_json |
| `profile` | object_id, stats_json |
| `vector` | object_id, sparse_vector_blob |
| `transfer_run` | id, started_at, ended_at, status, options_json |
| `transfer_task` | run_id, task_id, status, rows_source, rows_done, rows_error, last_key, heartbeat_at |
| `error_row` | run_id, task_id, key_json, row_json (redacted per config), error |

Schema migrations of `state.db` itself are versioned (`PRAGMA user_version`).

## 8. Web UI

Single page, vanilla JS modules, SSE for live updates, no framework, no CDN. Clean, restrained design; light/dark themes; keyboard accessible; responsive.

- **Shell:** left phase stepper with status badges; top bar with project name, agent online/offline dot, Pause/Resume; right drawer with feedback thread and version history (with diff).
- **Setup:** two masked connection-string fields, Test buttons, detected server info.
- **Analysis:** KPI tiles (tables, columns, rows, size, risks), source vs target side by side, risks by severity, per-table drill-down, FK dependency graph (inline SVG), data-quality findings. Every element offers "comment" for anchored feedback. Approve / Request changes. Export to a single self-contained HTML file.
- **Mapping:** table grid (source → target, confidence bar, method badge), expandable column mappings with transform editor, unmapped-source and unresolved-target panels (blocking), direct editing.
- **SQL:** tasks in execution order, syntax-highlighted code (tiny built-in highlighter), pre/post/validation scripts, line-anchored comments, version diff, script-pack download.
- **Execute:** pre-flight checklist (connectivity, fingerprint unchanged, target row counts, permissions, estimated volume), options (batch/chunk size, parallelism, stop-on-error vs skip-and-log, truncate target first), **Execute** button with typed confirmation of the target DB name. Live view: overall and per-task progress bars, rows/sec, ETA, throughput sparkline, error count, log tail; Pause / Resume / Cancel.
- **Final report:** per task source vs target vs rejected counts, duration, throughput, validation results (counts; column checksums), errors with sample rows; export HTML/JSON.

### 8.1 HTTP API (127.0.0.1 only; token required on every request)

`GET /api/state` · `GET /api/artifact/{phase}[/{version}]` · `GET /api/diff/{phase}?from=&to=` · `POST /api/feedback` · `POST /api/approve/{phase}` · `POST /api/request-changes/{phase}` · `POST /api/reopen/{phase}` · `POST /api/pause` · `POST /api/resume` · `POST /api/connections/{side}` (test + save) · `PUT /api/mapping` (direct edits) · `POST /api/transfer/{start|pause|resume|cancel}` · `GET /api/transfer` · `GET /api/agent/await` (long-poll) · `GET /api/events` (SSE) · `GET /api/export/{what}`.

## 9. Transfer engine

- Runs inside the `dbm` server process; independent of Claude.
- **Ordering:** topological sort of target FK graph; tasks run in dependency levels with N parallel workers (default 4). FK cycles → load with constraints `NOCHECK`, re-enable `WITH CHECK` in post-script.
- **Copy path:** `SqlDataReader` (source, `CommandBehavior.SequentialAccess`) → `SqlBulkCopy` (target, `EnableStreaming`, `BulkCopyTimeout = 0`, `CheckConstraints`, `KeepNulls`, `KeepIdentity` when `identityInsert`, `NotifyAfter` for progress events).
- **Modes:** `direct` (bulk copy into target table) or `staging_merge` (bulk copy into a staging table, then `MERGE`/`INSERT…SELECT` with target-side lookups).
- **Chunking & checkpoints:** keyset chunks on the source key (`WHERE key > @last ORDER BY key`, default 100k rows); each chunk commits in its own target transaction together with its checkpoint.
- **Exactly-once chunks:** the checkpoint (`last_key`, counts) is written to a control table in the target (`dbo.__dbm_checkpoint`) **inside the same transaction** as the chunk's rows, so a chunk is either committed with its checkpoint or not at all; resume reads the target checkpoint (SQLite keeps a mirror for the UI). Staging mode bulk-copies each chunk into a session `#stg` table and runs the task's merge SQL inside that same transaction. Tables with no usable key load in a single transaction (restart from scratch on failure; flagged as a risk in analysis). The control table is dropped when the run completes.
- **Crash recovery:** tasks in `running` with a stale heartbeat are treated as `paused` on server start.
- **Validation:** per task, source row count (after filters) vs target rows added by the run; optional per-column aggregate checksums (source expression cast to the target type vs the target column) when the target table was empty before the run; LOB/unsupported types are excluded from checksums.

## 10. Error handling & security

- **Transient errors:** SqlClient retry logic with exponential backoff for connection/transient failures.
- **Row-level errors:** a failed bulk batch is **bisected** to isolate bad rows; bad rows go to `error_row` with the reason; the task stops (stop-on-error) or continues (skip-and-log) per option.
- **Agent errors:** invalid patches are rejected with precise messages; the orchestrator retries the subagent once with the error, then surfaces the problem in the UI.
- **Secrets:** connection strings encrypted at rest; redacted in logs, events, packets, exports and UI (masked after save). Work packets contain no credentials or row data beyond profile samples (sample values can be disabled per project).
- **Server:** binds `127.0.0.1` only; random per-project token required on all API calls; CSRF-safe (token in header for mutations).
- **Destructive actions** (truncate target, execute) require typed confirmation.

## 11. Testing

- **Unit (xUnit):** normalisation + vectoriser + matcher scoring, type compatibility, topological sort and cycle handling, chunk planner, SQL templating, patch validation, state-machine transitions (incl. stale cascade and pause), crypto round-trip, redaction.
- **Integration:** SQL Server via Docker (`mcr.microsoft.com/mssql/server`) or LocalDB on Windows, seeded with the **LegacyShop → ShopV2** fixture pair (renames, splits, merges, type changes, orphan rows, a heap, an FK cycle). Covers discovery, auto-mapping quality baseline, SQL validate, full transfer, pause/resume mid-table, crash recovery, bad-row bisection.
- **UI:** Playwright smoke test of the main flow (optional in CI).
- **Demo:** `dbm demo` builds the same fixture for teammates to rehearse the full loop.

## 12. Delivery milestones

| # | Milestone | Done when |
|---|---|---|
| M0 | Skeleton | Solution builds; `dbm doctor` works; plugin installs locally (wake-up and detach spikes already verified) |
| M1 | Core platform | State store, crypto, connections, server + SSE + UI shell, pause/resume plumbing, `dbm next/await/apply` |
| M2 | Discovery + Analysis loop | Catalog + profile + rules + vector index; analysis screen; full review loop with schema-analyst |
| M3 | Mapping loop | Auto-mapper; mapping screen with direct edit; mapping-architect loop; blocking validation |
| M4 | SQL loop | Templating, validate, SQL screen, sql-engineer loop, script pack export |
| M5 | Transfer + report | Engine with chunking/checkpoints/bisection, execute screen, final report |
| M6 | Packaging + demo + docs | Marketplace install from git, `dbm demo`, README, cross-platform check |

## 13. Risks

| Risk | Mitigation |
|---|---|
| Background Bash may not wake the session on some surfaces (verified in CLI) | Re-check Desktop in M6; foreground timeout-loop fallback |
| ASP.NET Core runtime missing on teammate machines | `dbm doctor` detects it and prints the one-line install command per OS |
| Name-based matching weak for semantically different names | Synonym dictionary + profile signals + agent resolves low-confidence band; `IVectorizer` seam for embeddings later |
| Very large LOB columns | `SequentialAccess` + `EnableStreaming`; per-task chunk size override |
| Target triggers / constraints interfering with load | Detected in analysis; explicit pre/post scripts in SQL artifact reviewed by the human |
| Tables without keys cannot resume mid-table | Flagged in analysis; truncate-and-reload on resume |
