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
  diff between any two versions. **Approve** signs off the version on your screen and nothing else: if a newer version
  arrived meanwhile (Claude's, or an edit from another tab), approval is refused with *A newer version arrived — review
  it first* and the page reloads to show it.

## Setup

Paste the **source** and **target** ADO.NET connection strings into the two masked fields and press **Test**. The
server, database, version, edition, collation, compatibility level and sign-in method appear. Press **Save** for each
side; when both are saved, discovery starts automatically. **Change** replaces a saved connection until a transfer has
started; after that the connections are locked.

- Connection strings are encrypted on your machine and masked after saving. Don't paste them into the Claude chat.
- To try the tool without your own databases, use the demo (see the README). With Windows authentication (a
  connection string without a password) you can ask Claude to run `dbm demo --server "<your server>" --attach`. If the
  server needs a SQL login, run that command yourself in a terminal outside Claude Code, in the project folder (which
  `--attach` needs) — not with `!` in Claude Code,
  whose command and output land in the conversation — so the password never reaches the chat (ask Claude for the
  launcher's full path first: `dbm` is on the PATH only inside Claude Code). Without `--attach` the
  command only prints the two connection strings with any password replaced by `***`, and you would have to put the
  password back in here yourself, so `--attach` is the easier way.
- A certificate error on Test: add `TrustServerCertificate=True` for test servers, or install the server's CA certificate.
- Entra ID: for example `Authentication=Active Directory Default` (uses your `az login` or IDE sign-in) or
  `Active Directory Interactive`, which opens a sign-in window on this machine.
- **Privacy — Send sample values to Claude** (on by default). On, the column profiles Claude reads include up to 3 real
  values per text column and its min/max. Off, the values already collected are removed from the project and its
  work packets (the state database is overwritten as far as SQLite allows; raw pages freed before the switch can still
  hold old values until reused) and discovery collects none, so nothing Claude reads carries a sample value; it
  still sees null shares, distinct counts, lengths and value patterns, and its analysis and mapping suggestions may be
  less precise. Exports you wrote earlier to `.dbmigrate/exports` keep the values they were written with. Turning it back on takes effect at the
  next discovery (**Re-run discovery** on Analysis). `dbm config sample-values off` switches them off from the command
  line; switching them back on is your decision and is done only here.

## Discovery

Runs by itself: extracts both catalogs, profiles the columns (null share, distinct values, lengths, value patterns,
sample values unless you switched them off in Setup), builds the search index and records a schema fingerprint per database. Nothing to do; large databases
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
- **Direct editing**: edit a task's SQL yourself; **Save as new version** creates a version authored by you.
- **Download script pack** gives the plan as `.sql` files plus a README for a DBA review.

**What to do:** read every task with warnings or custom SQL, comment where needed, approve.

## Reopen, stale phases and drift

- **Reopen** an approved phase to change it: every later phase up to Execute becomes *Stale*, is regenerated after your
  re-approval (carrying over what still applies) and comes back for review.
- **After a run.** Analysis, Mapping and SQL can be reopened - and discovery re-run, and the connections changed - before
  the first run and after a run that completed, was cancelled or failed. Reopening after a failed run cancels it first
  (see *Cancel* below). While a run is running or paused the Reopen button is disabled with the reason: pause and cancel
  it first. After you approve SQL again, **Execute** starts a new run; the earlier runs' final reports stay on the Report
  screen (pick one from the list at the top).
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
   | Truncate target first | off | deletes **every** row in each target table of the plan before loading — also rows that were never this migration's |
   | Validate column checksums | on | per-column checksum comparison for tables that were empty before the run |
   | Table lock | off | bulk-load with `TABLOCK`: faster, but blocks other users of the target |
   | Fire target triggers | off | let target triggers run during the bulk insert |
   | Keep the checkpoint table | off | keep `dbo.__dbm_checkpoint` after completion, for auditing |

3. Press **Execute…**, type the **target database name** and press **Start transfer**.

**Loading into a target that already has rows.** Pre-flight's *Target row counts* line names the target tables that
already hold rows and says which of them have no primary key or unique index: those would get their rows a second time
(duplicates), while tables with a key reject the repeated rows as row errors. The same applies to a new run after a
cancelled or failed one: checkpoints belong to one run, so a new run starts from the first row again. So a new run into
non-empty tables needs a deliberate choice: tick **Truncate target first** — but only if the target tables hold nothing
you need to keep, because it deletes every row in them, not only this migration's — or tick the confirmation in the
start dialog, which names the non-empty tables and the keyless ones. Without either the server refuses the start
(`target_not_empty`). The final report of such a run says "target tables were not empty before this run; counts compare
rows added" instead of "validated". If neither choice is right, the target has to be restored (for example from a
backup); never edit it by hand.

**Chunk keys.** When a task's source query joins other tables, pre-flight checks that its chunk key is still unique in
that query. A join that repeats rows (one order per order line, say) would make the key repeat and lose rows at chunk
boundaries, so it blocks the run with the task and one repeated key named; fix the key or the join in the SQL phase.

**Rejected rows under *Skip and log bad rows*.** Constraint violations (foreign key, CHECK, primary key or unique) are
always skipped and logged row by row, even when every row of a chunk fails alike. But when every row of a chunk (of more
than one row) fails with the same other error, the task fails (`bad_task`) and the run stops. What to do depends on the
error:

- **A permission error** (e.g. `INSERT` or `ALTER` denied): nothing in the plan is wrong. Grant the permission and press
  **Resume**; the run continues from its checkpoints.
- **Bad source data** (a value that does not convert or fit): fix the data and press **Resume**.
- **A mapping or SQL defect** (an invalid column or object, a conversion or truncation the plan itself causes, NULL
  into NOT NULL): reopen Mapping or SQL, fix it and approve again (the reopen cancels the failed run first). The target
  still holds what this run committed, so the new run needs **Truncate target first** (with the caution above) or the
  confirmation by name.

**A task that loads nothing is a mapping problem.** Because constraint violations are always per-row rejects, a wrong
foreign-key or CHECK mapping under *Skip and log bad rows* shows as a task whose rows are all rejected. Once a task's
first 3 chunks (or its whole source, if it is smaller) have loaded no row at all, the task fails (`bad_task`) with a
reason that says every row was rejected and names the most common error and its number, and the run stops - rather than
rejecting the whole table one row at a time. The rejected rows are logged. Usually the fix is to reopen Mapping, fix it
and run again with **Truncate target first**, as above. If the rows really are bad, **Resume** carries on from the next
chunk and does not stop the task for this again (a task without a key is rolled back instead, so for it only the fix
helps). A run that still ends with a task that loaded 0 of its source rows names it in the report's headline ("app.Orders
loaded 0 of 3,005 rows") instead of saying the row counts were validated.

The exception is a new run into tables that already held rows (see above): when every rejected row of such a table is a
duplicate key (primary key or unique, told by SQL Server's error number 2627 or 2601), those rows were already there, so
the task is not stopped and the headline's
"loaded 0 of N" is expected - it is not a mapping problem. A foreign-key or CHECK error in such a run still stops the task.

**Live view**: overall and per-task progress bars, rows per second, ETA, a throughput sparkline, the rejected-row count
and a log tail. **Pause** lets every task commit its current chunk and stops; **Resume** continues from the checkpoints,
also after a failure, a crash or a reboot; **Cancel run** stops for good, and rows already committed stay in the target.

**What Cancel restores.** The plan's pre-load SQL can switch things off in the target for the load - for a foreign-key
cycle it disables the cut constraint (`NOCHECK`). While a run is running, paused or failed that stays in force (Resume
expects it), and the Execute screen lists the statements. **Cancel** then runs the plan's post-load SQL, best effort,
and the run's notes say per statement what was restored and what was not, with the server's own message. A constraint
that the rows already loaded violate is re-enabled without the check - it guards new writes but is *not trusted* - and
the note tells you to fix or remove those rows and run the statement again. A completed run runs the post-load SQL as
part of finishing.

## Final report

Per task: source rows, rows loaded, rejected rows, duration, rows per second and validation results (row counts and
column checksums), plus the run's options and any notes the run recorded. Up to 5 rejected rows per task are listed by
key with their error and SQL Server's error number (rows recorded by an older version of the plugin have none). In the demo with *Skip and log bad rows* you see 8 rejected rows: 2 orphan orders and their 2
lines (foreign key), 3 comments longer than the target column (truncation) and 1 zero quantity (CHECK constraint).

Read the report before you call a run clean: a *Completed* run can still carry notes, and a task with 0 rows loaded and
its rows rejected points at the mapping, not at the data (see *Execute*); the headline names such a task instead of
saying "validated". The one exception is a run into tables that were not empty (the headline says so) whose rejected
rows are duplicate keys: those rows were already in the target.

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
- When Claude reports a failure (a job failed, the transfer failed or was cancelled, a draft was rejected twice) it
  ends its turn instead of waiting. After you have fixed the cause and pressed **Retry** or **Resume**, type
  `/db-migrate resume`.
- **Take over.** While Claude is drafting or reworking a phase you cannot edit it or request changes. If Claude is stuck
  there (its patch was rejected twice), press **Take over** on that phase: it discards Claude's pending work and puts
  the phase back to *Awaiting review* on its current version. On Mapping and SQL you can then edit it yourself, approve
  it or request changes again. Analysis cannot be edited by hand, and an Analysis taken over while Claude was drafting it
  has no narrative yet, so it cannot be approved: add comments with your guidance and press **Request changes**. Your
  open comments stay open, and a patch Claude delivers afterwards is refused. The button is disabled while Claude is
  connected (it may be applying a patch at that moment): wait until Claude has stopped and the page shows *Agent
  offline* (2 minutes after its last command).
