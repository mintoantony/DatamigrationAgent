# db-migrate — installed-plugin end-to-end results

- Date: 2026-09-18
- Tester: Claude (controller session), acting as the human through the web UI's own HTTP API (`X-Dbm-Token`, the calls `wwwroot/js` makes)
- Commit (`git rev-parse --short HEAD`): 6c9f379 (feat/db-migrate after the final-review fixes); fixes from section G on top
- Plugin version (plugin.json): 0.1.0
- Claude Code CLI version (`claude --version`): 2.1.276
- Claude Desktop version: not run (section E is for the user)
- OS: Windows 11 Enterprise 10.0.26100, Git Bash as the Bash tool
- SQL Server used: `(localdb)\MSSQLLocalDB` (SQL Server 2025 Express, 17.0.4025.3)

**How it was run.** Sessions B–D ran headless (`claude -p`, `--output-format stream-json`, resumed with `-r <session>`),
one Claude process per turn, with `--allowedTools "Bash Agent Read Write Glob Grep"`. Every agent step was a real
subagent of the installed plugin on the default model. Headless mode cannot show whether an interactive session wakes by
itself when a background `dbm await` finishes, so each turn after a browser action was started with `/db-migrate resume`
(SKILL.md's documented re-entry). The checks that need an interactive session or Claude Desktop are marked **USER**.

Before starting, neither `DbmDemo_LegacyShop` nor `DbmDemo_ShopV2` existed; both were dropped by exact name afterwards.

## A. Packaging and install

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| P1 | `claude plugin validate plugins/db-migrate` | exit 0, no errors | exit 0 | PASS |
| P2 | `claude plugin validate .` (marketplace) | exit 0, no errors | exit 0 | PASS |
| P3 | `claude plugin validate plugins/db-migrate --strict` | exit 0; record any warnings | exit 0, no warnings | PASS |
| P4 | `claude plugin marketplace add <repo>`, then `marketplace list` | marketplace `db-migrate` listed with the local path | added from `D:\DatamigrationAgent\.worktrees\feat-db-migrate` (the repo root checkout is on `main`); listed as `Source: Directory (…)` | PASS |
| P5 | `claude plugin install db-migrate@db-migrate`, then `plugin list` | installed and enabled, version = plugin.json | `Version: 0.1.0`, `Scope: user`, enabled | PASS |
| P6 | `claude plugin details db-migrate` | 1 skill, 3 agents, a SessionStart hook; projected token cost | 1 skill, 3 agents, SessionStart hook (harness-only); always-on ~496 tokens; on-invoke skill ~1.6k, mapping-architect ~7.4k, schema-analyst ~2.2k, sql-engineer ~3.2k | PASS |
| P7 | installed copy has `engine/dist/VERSION` = the version | no build on first use | cache copy `VERSION` = `0.1.0`; `dbm version` from it prints `{"version":"0.1.0","runtime":".NET 8.0.28"}` with no build message. Note G-5: a directory marketplace runs the plugin from the source folder | PASS |

## B. First CLI session

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| S1 | new session in an empty folder | no hook error; SessionStart hook listed | the init event lists the plugin; no hook error in any of 10 sessions. `/hooks` menu not viewable headless | PASS (menu: USER) |
| S2 | the skill name(s) | record the exact name(s) | registered as `db-migrate:db-migrate`; typing the bare `/db-migrate status` resolves to it | PASS |
| S3 | agents | three agents listed | `db-migrate:mapping-architect`, `db-migrate:schema-analyst`, `db-migrate:sql-engineer` | PASS |
| S4 | `dbm version` | one JSON line, no build message | as P7 | PASS |
| S5 | `/db-migrate` | `dbm init`, one line with the URL, background `dbm await` | `init` (`created:true`) → `next` (`await/setup`) → `dbm await` with `run_in_background:true`; one line to the user | PASS |
| S6 | Claude runs the demo command with `--attach` | `ok:true, attached:true`; discovery starts | `ok:true`, both connections attached, discovery and the analysis script ran, `next` = schema-analyst draft | PASS |
| S7 | first agent dispatch | `subagent_type: "db-migrate:schema-analyst"` succeeds | exactly that; the subagent ran `dbm apply … --dry-run` only (as its playbook says) and the orchestrator applied → v1 | PASS |

## C. Review loops

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| A1 | Analysis v1 | narrative and findings; heap finding for `dbo.AUDIT_LOG` | v1 by `agent`: "Narrative and commentary for 2 high findings"; F001 = no key on `dbo.AUDIT_LOG` (5,000 rows) | PASS |
| A2 | 2 feedback items → Request changes (2) | rework, v2 answers both items | anchored item on F001 and a general item; schema-analyst rework → v2, both `addressed` with specific responses (summary ~115 words). Wake-by-itself: USER | PASS (wake: USER) |
| A3 | Approve Analysis | auto-mapper, mapping-architect drafts, Mapping v1 awaits review | as expected; v1 with 23 ops and 9 type-risk warnings, which the orchestrator reported in its one line | PASS — see G-2 |
| M1 | edit `app.Customers.Email` | new version authored `human` | `POST /api/edit/mapping` → v2 `human` | PASS |
| M2 | anchored feedback on `Phone`, left as a draft | listed as a draft | `colmap:app.Customers.Phone`, `draft` | PASS |
| R1 | Pause | "Paused — press Resume…", background await | Pause → the next session answered "The migration is paused. Press Resume in the UI to continue." and started a background await | PASS |
| R2 | UI after the session exits; agent dot offline within ~2 min | | `/api/state` still said `agentOnline:true` about a minute after the last headless session; not conclusive headless | USER |
| R3 | new session, `/db-migrate resume` | one line reporting Paused | a fresh session (not resumed) did exactly that | PASS |
| R4 | Resume | Claude continues | after Resume + Request changes, `/db-migrate resume` went straight to the mapping rework | PASS (wake: USER) |
| M3 | Request changes (1) | Phone trimmed, item `addressed`, Email edit preserved | v3 by `agent`: Phone `LTRIM(RTRIM(s.[PHONE_NO]))`, Email still `LOWER(LTRIM(RTRIM(s.[EMAIL_ADDR])))`, item `addressed` | PASS |
| M4 | Approve Mapping | no blockers; SQL generated; v1 awaits review | approve `ok`; SQL v0 generated by script, `next` = review (no agent draft needed) | PASS |
| Q1 | line comment on the Orders task → Request changes (1) | sql-engineer reworks, comment added, `addressed` | anchor `sql:T05:5`; v1 by `agent` adds `-- ORD_STATUS lookup: …` above `st.[STATUS_CD]`; `addressed` | PASS |
| Q2 | Download script pack | zip with `.sql` files and a README | 9 entries: `00_pre.sql`, six task files, `99_post.sql`, `README.md` | PASS |
| Q3 | Approve SQL | Execute screen active | approve `ok` | PASS |
| Z1 | idle 10 minutes at a review | `/usage` unchanged | not measurable headless (each turn is its own process; nothing runs between turns) | USER |

## D. Execute and report

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| X1 | pre-flight | every check passes; target row counts 0 | 13/13 ok, including `target_rows` (all empty) and the new `chunk_keys` probe on T05's join | PASS |
| X2 | Execute, skip and log, typed confirmation `DbmDemo_ShopV2` | run starts, live progress | `{"ok":true,"runId":1}`; completed in about 1 s at scale 1 | PASS |
| X3 | final report | 8 rejected: Orders 5, OrderLines 3; Customers 1000, Addresses 1500, Products 200, AuditEvents 5000 | AuditEvents 5000/0, Customers 1000/0, Products 200/0, Addresses 1500/0, Orders 3000/**5**, OrderLines 8999/**3**; every count validated; checksums 23/23 | PASS |
| X4 | Claude after completion | reports the `complete` summary, ends the loop | "The migration is complete: 19,699 of 19,707 rows went into 6 tables in 1 second, and 8 rows were rejected. …" (3 lines, no await) | PASS |
| X5 | `dbm export report` | `{ok:true,file:"final-report.html",…}`, opens offline | `ok:true`, 169,388 bytes; no `http(s)://` script or stylesheet reference; no `Password`/`pwd=` | PASS |

## E. Claude Desktop (Code tab, local session)

| # | Check | Expected | Actual / notes | Pass/Fail |
|---|---|---|---|---|
| D1 | new local session in `%TEMP%\dbm-e2e-desktop` | the skill is in the `/` menu | | USER |
| D2 | `/db-migrate`, demo with `--prefix DbmDesk_ --attach` | Analysis reaches review; background `dbm await` | | USER |
| D3 | Approve Analysis in the browser, type nothing | the session resumes by itself within ~10 s | | USER |
| D4 | only if D3 failed: type "continue" | foreground `dbm await --timeout 540` | | USER |

Cleanup done: `dbm stop`; `DROP DATABASE DbmDemo_LegacyShop; DROP DATABASE DbmDemo_ShopV2`; no `dotnet … Dbm.dll`
process left.

## F. Token usage

Measured per headless turn from the `result` event (`total_cost_usd`; input = uncached + cache-write + cache-read).
About 100k input tokens of every turn are this machine's base context (many other plugins installed), almost all cache
reads; the db-migrate part is the skill (~1.6k) plus the loop.

| Turn | What happened | Cost | Input tokens | Output | Subagent tokens |
|---|---|---|---|---|---|
| 1 | `/db-migrate`: init, await setup | $0.36 | 201k | 503 | – |
| 2 | demo attach, schema-analyst draft, await review | $0.33 | 257k | 830 | 12.7k |
| 3 | analysis rework (2 items) | $0.31 | 235k | 709 | 12.4k |
| 4 | mapping-architect draft | $0.61 | 256k | 809 | 35.9k |
| 5 | new session while paused | $0.29 | 121k | 216 | – |
| 6 | mapping rework (1 item) | $0.25 | 220k | 686 | 16.0k |
| 7 | SQL rework (1 item) | $0.31 | 240k | 660 | 15.0k |
| 8 | complete summary | $0.08 | 102k | 180 | – |
| 9 | `dbm export report` | $0.06 | 103k | 151 | – |

Total for the migration: about **$2.60** (a separate $0.49 diagnostic turn, caused by the test shell's own
`MSYS_NO_PATHCONV`, is excluded; it found G-1). Per phase: Analysis ≈ $1.00, Mapping ≈ $1.15, SQL ≈ $0.31,
Transfer + report ≈ $0.14. No phase is near 200k *new* tokens: the largest subagent (mapping draft) used 35.9k.
Waiting costs nothing: nothing runs between turns.

## G. Findings and fixes

| # | Check | Finding | Fix applied (commit) |
|---|---|---|---|
| G-1 | S2 (diagnostic turn) | `bin/dbm` fails when `MSYS_NO_PATHCONV=1` (or `MSYS2_ARG_CONV_EXCL`) is set in Git Bash: `dotnet` gets `/d/…/Dbm.dll` unconverted and cannot find it | `bin/dbm` hands `dotnet` a Windows-form root (`cygpath -m`) when `cygpath` exists; verified with `MSYS_NO_PATHCONV=1` under bash and dash |
| G-2 | A3 | mapping-architect's `dbm artifact mapping --path /tables/app.AuditEvents` became `C:/Program Files/Git/tables/…` (Git Bash path conversion); the agent had to find `MSYS2_ARG_CONV_EXCL` itself (2 wasted tool calls) | `dbm artifact --path` accepts a pointer without the leading `/` (harm test `Artifact_path_without_the_leading_slash_is_the_same_pointer`, red: `path 'items/b' must start with '/'`); both playbooks now say `--path tables/…` / `--path tasks/<id>` |
| G-3 | found while fixing G-1 | `bin/dbm` and `engine/release.sh` (`#!/bin/sh`) used bash-only `${var//x/y}` (added by Ruling 189): under dash (`/bin/sh` on Debian/Ubuntu) the rebuild dies with `Bad substitution` | replaced with a POSIX `sed`; `DBM_REBUILD=1 dash bin/dbm version` rebuilds and runs; `dash -n engine/release.sh` passes |
| G-4 | found while fixing G-1 | a launcher rebuild (unlike `release.*`) does not pin the wwwroot file times, so in a git checkout it leaves `engine/dist/Dbm.staticwebassets.endpoints.json` modified | not fixed: open item 42 (cosmetic; the rebuilt engine works) |
| G-5 | P4/P7 | a marketplace added from a local **directory** runs the plugin from that directory, not from the cache copy — editing the checkout changes the live plugin | none needed; noted for testers. Teammates installing from git get the cached copy |
