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
run the transfer without Claude. Claude is only needed when an agent has to write something; while such a step is
waiting and no Claude session is connected, the page shows **Agent offline — run `/db-migrate resume` in Claude Code
to let Claude continue …**.

## The screen

- **Left: phase stepper** — Setup, Discovery, Analysis, Mapping, SQL, Execute, Transfer, Report, each with a status:
  *Pending* (not started), *Running* (a server job is working), *Drafting* (Claude is writing the first version),
  *Awaiting review* (your turn), *Reworking* (Claude is addressing your feedback), *Approved*, *Stale* (an earlier phase
  was reopened, so this one must be redone).
- **Top bar** — project name, the *Agent online* / *Agent offline* indicator, **Pause / Resume**, and the theme switch.
- **Review bar** (on Analysis, Mapping and SQL) — the version picker, **Feedback** and **History**, which open the right
  drawer: the feedback thread for the current phase (your items and Claude's responses) and the version history with a
  diff between any two versions.

## Setup

Paste the **source** and **target** ADO.NET connection strings into the two masked fields and press **Test**. The
server, database, version, edition, collation, compatibility level and sign-in method appear. Press **Save** for each
side; when both are saved, discovery starts automatically. **Change** replaces a saved connection until a transfer has
started; after that the connections are locked.

- Connection strings are encrypted on your machine and masked after saving. Don't paste them into the Claude chat.
- To try the tool without your own databases, ask Claude to run
  `dbm demo --server "<your server>" --attach` (see the README). Without `--attach` the command only prints the two
  connection strings with any password replaced by `***`; with SQL authentication you must put the password back in
  here yourself, so `--attach` is the easier way.
- A certificate error on Test: add `TrustServerCertificate=True` for test servers, or install the server's CA certificate.
- Entra ID: for example `Authentication=Active Directory Default` (uses your `az login` or IDE sign-in) or
  `Active Directory Interactive`, which opens a sign-in window on this machine.

## Discovery

Runs by itself: extracts both catalogs, profiles the columns (null share, distinct values, lengths, value patterns,
sample values), builds the search index and records a schema fingerprint per database. Nothing to do; large databases
take a few minutes. On failure the error is shown with a **Retry** button — fix the cause first (usually permissions).

## Analysis

- **KPI tiles**: tables and columns on both sides, source rows and size, findings (with the critical and high counts),
  the estimated transfer time and the target's triggers.
- **Side-by-side inventory** with per-table drill-down, a foreign-key graph and data-quality findings.
- **Findings by severity**, for example heaps (no key, so no mid-table resume), deprecated or LOB types, collation
  differences, orphaned FK data, triggers on the target and truncation risk (source longer than target).
- **Claude's summary**, risks and recommendations.
- **Re-run discovery** and **Export HTML** at the top.

**What to do:** read the summary and the high-severity findings. Hover over any finding, table, column or the narrative
and press its comment button to attach feedback to exactly that element ("this table is obsolete, skip it", "explain the
collation risk"). Then press **Approve**, or **Request changes (n)** to send the n draft items to Claude, which answers
each one as *addressed* (with a note) or *declined* (with a reason) in version n+1, with a diff.

## Giving good feedback

Feedback is either *general* or *anchored*. Anchors keep Claude's rework small and precise:

| Anchor | Where you click |
|---|---|
| finding | a finding in Analysis |
| table / column (source or target) | a table or column in Analysis |
| narrative | the summary, risks and recommendations text |
| table mapping | a row in the Mapping grid |
| column mapping | a column row inside an expanded table mapping |
| SQL task | a task header on the SQL screen |
| SQL line | a line number inside a task's SQL |

Draft items stay private until you press **Request changes**, and you can delete drafts. Be concrete ("split CUST_NM on
the first space into FirstName and LastName"). Claude may decline with a reason; answer by adding another item.

## Mapping

- **Table grid**: source → target with a confidence bar and a method badge — `exact`, `fuzzy`, `vector`, `agent`
  (Claude), `human` (you), `carried` (kept from an earlier approved version).
- **Expand a row** for its column mappings: expression, source columns, default, confidence, method, type risk (e.g.
  `varchar(500) → nvarchar(200)` truncation) and candidates.
- **Direct editing**: change an expression, pick another candidate, set a default, **Skip table…** for a target table,
  or **Drop…** a source column or table that is not migrated. **Save as new version** creates a version authored by you.
- **Blockers**: unmapped source columns (map or drop each) and non-nullable target columns without a default or an
  expression. **Approve stays disabled until the list is empty.**

**What to do:** check the low-confidence rows first, fix simple things yourself, and use anchored feedback for anything
that needs reasoning (splits, merges, lookups, T-SQL transforms).

## SQL

- **Tasks in execution order**, one per target table (parents before children), each with its source query, column
  bindings, mode (`direct` bulk copy or `staging + merge`), identity insert and pre/post scripts. Global pre/post
  scripts handle FK cycles and similar cases.
- The SQL is validated against the real databases (compiled without executing, result-set shape, target column
  compatibility); errors and warnings appear per task.
- Click a line number to comment on that line, and use the version diff to see exactly what Claude changed.
- **Download script pack** gives the plan as `.sql` files plus a README for a DBA review.

**What to do:** read every task with warnings or custom SQL, comment where needed, approve.

## Reopen, stale phases and drift

- **Reopen** an approved phase to change it: every later phase up to Execute becomes *Stale*, is regenerated after your
  re-approval (carrying over what still applies) and comes back for review. Reopening is not possible once a transfer
  has started.
- **Drift**: before approval and in the pre-flight before execution the engine re-reads both schemas and compares
  fingerprints. If a database changed, approval is refused with "Schema changed since discovery"; press **Re-run
  discovery** on the Analysis screen, which refreshes the catalogs and marks the later phases stale.

## Execute

1. **Pre-flight checklist**: press **Run pre-flight**. It checks the approved SQL plan, connectivity to both databases,
   unchanged schemas, that the target tables exist, permissions, the checkpoint table, current target row counts, the
   estimated volume and tasks without a key. Fix anything that blocks first.
2. **Options**:

   | Option | Default | Meaning |
   |---|---|---|
   | Chunk size (rows) | 100 000 | rows per committed chunk (tasks with LOB columns use 5 000) |
   | Parallel tasks | 4 | tasks loaded at the same time; a task starts once the tables it depends on are loaded |
   | When a row is rejected | Stop at the first bad row | **Stop at the first bad row** = the chunk is rolled back and its task stops; **Skip and log bad rows** = bad rows are isolated, recorded and skipped |
   | Truncate target first | off | empties each target table before loading |
   | Validate column checksums | on | per-column checksum comparison for tables that were empty before the run |
   | Table lock | off | bulk-load with `TABLOCK`: faster, but blocks other users of the target |
   | Fire target triggers | off | let target triggers run during the bulk insert |
   | Keep the checkpoint table | off | keep `dbo.__dbm_checkpoint` after completion, for auditing |

3. Press **Execute…**, type the **target database name** and press **Start transfer**.

**Loading into a target that already has rows.** Pre-flight only *warns* about non-empty target tables. Tables with a
key reject the duplicates as row errors, but a table without a key is loaded again and its rows double — and the row
count validation still passes, because it counts the rows added by this run. If you are re-running a migration, tick
**Truncate target first** unless you really mean to append.

**Rejected rows under *Skip and log bad rows*.** Constraint violations (foreign key, CHECK, primary key or unique) are
always skipped and logged row by row, even when every row of a chunk fails alike. But when every row of a chunk (of more
than one row) fails with the same error that points at the plan itself — an invalid column or object, a conversion,
NULL into NOT NULL, truncation, overflow or a permission — the task fails (`bad_task`) instead, because that is a
mapping or SQL defect rather than bad data. The mapping and SQL phases cannot be reopened once a transfer has started,
so fixing it means cancelling the run and repeating the migration in a new project folder (with **Truncate target
first**).

**A task that loaded 0 rows is a mapping problem.** Because constraint violations are always per-row rejects, a wrong
foreign-key or CHECK mapping under *Skip and log bad rows* does not fail the run: it can end *Completed* with every row
of a table rejected. If a task shows 0 rows loaded and many rejected, do not treat it as bad data — read the rejected
rows' error, fix the mapping (in a new project folder, as above) and load again.

**Live view**: overall and per-task progress bars, rows per second, ETA, a throughput sparkline, the rejected-row count
and a log tail. **Pause** lets every task commit its current chunk and stops; **Resume** continues from the checkpoints,
also after a failure, a crash or a reboot; **Cancel run** stops for good, and rows already committed stay in the target.

## Final report

Per task: source rows, rows loaded, rejected rows, duration, rows per second and validation results (row counts and
column checksums), plus the run's options and any notes the run recorded. Up to 5 rejected rows per task are listed by
key with their error. In the demo with *Skip and log bad rows* you see 8 rejected rows: 2 orphan orders and their 2
lines (foreign key), 3 comments longer than the target column (truncation) and 1 zero quantity (CHECK constraint).

Read the report before you call a run clean: a *Completed* run can still carry notes, and a task with 0 rows loaded and
its rows rejected points at the mapping, not at the data (see *Execute*).

## Exports

**Export HTML** on the Analysis and Final report screens writes a single self-contained HTML file — it opens anywhere,
offline and read-only. The SQL screen offers the **Download script pack** zip. The same files, and an HTML export of the
SQL screen, from Claude Code or a terminal:

```
dbm export analysis          # -> .dbmigrate/exports/analysis-v<n>.html
dbm export sql               # -> .dbmigrate/exports/sql-v<n>.html (the SQL screen as one HTML file)
dbm export sqlpack           # -> .dbmigrate/exports/<project>-sql-v<n>.zip (the DBA script pack)
dbm export report            # -> .dbmigrate/exports/final-report.html (after a completed run)
dbm export analysis --out ./review/     # also copy it into ./review/ (or give a file name)
```

Every export is saved under `.dbmigrate/exports/` as well, and none of them contains a connection string.

## Pause, resume and Claude

- **Pause** (top bar) stops agent work in every phase: Claude finishes and saves any patch it is writing, then waits at
  no token cost. It does not stop a running transfer — use **Pause** on the Execute screen for that.
- **Resume** continues immediately when the Claude session is still open. Otherwise type `/db-migrate resume` in any new
  session in the same folder; the engine knows where to continue.
- While Claude waits for you, its session holds a background `dbm await` task. Anything that gives Claude work again —
  approving, requesting changes, resuming, reopening — completes that command and Claude continues on its own. If your
  Claude surface does not wake up, tell Claude to continue once and it switches to a foreground wait for the rest of the
  session.
