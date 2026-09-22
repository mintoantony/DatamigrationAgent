# Transfer playbook

Read this when `dbm next` returns a transfer-related action. The transfer runs inside the dbm server; you never move data, never
see rows or connection strings, and never start a transfer on your own initiative.

## What `dbm next` means

| Action / reason | What is happening | What you do |
|---|---|---|
| `await` / `execute` | SQL approved. The human runs pre-flight, picks options, clicks **Execute…**, types the target database name and clicks **Start transfer**. | One line: "SQL approved — open the Execute screen (<Url>) to run pre-flight and start the transfer." Keep `dbm await` running in the background. |
| `await` / `transfer` | A run is in progress. | Nothing. If the human asks for progress, run `dbm transfer status` once and report one line. Keep `dbm await` in the background; do not poll. |
| `await` / `transfer_paused` | Paused by the human, or by a server restart (crash recovery turns interrupted runs into paused ones). All committed chunks are safe; the plan's pre-load SQL (e.g. a disabled foreign key) stays in force until the run completes or is cancelled. | One line: "The transfer is paused — resume it from the Execute screen." Resume only when the human asks: `dbm transfer resume`. |
| `stop` / `transfer_failed` | Stop-on-error rejected a row, a task/script failed, or (under skip-and-log too) a task failed with `bad_task` because every row of a chunk failed alike on a non-constraint error, or because its first 3 chunks (or its whole source, if smaller) loaded no row while rejecting rows. The plan's pre-load SQL is still in force (Resume expects it); the Execute screen names it. | Run `dbm transfer status`; report the failed task, its target and its error (already redacted) and the matching option below, then end your turn — no `dbm await` after a `stop` (it returns at once). After the human acts, they type `/db-migrate resume`. |
| `stop` / `transfer_cancelled` | The human cancelled. Rows already committed stay in the target. Cancel ran the plan's post-load SQL; the run's notes say, statement by statement, what it restored and what it could not (with the server's text). | Report the `summary`, and any note that says NOT restored. Then end your turn. The human's options: a new run from the Execute screen (it loads every table again, so it needs "Truncate target first" or a confirmation naming the tables that already hold rows — see *Facts*), or reopen Analysis, Mapping or SQL to change the plan first. |
| `stop` / `complete` | Finished. | Report the one-line summary. If it says "target tables were not empty before this run", the counts compare rows added, not the tables — say so. The final report is stored as a **Complete** artifact (one per completed run): the UI's **Report** step shows it (with a picker for earlier runs' reports), `dbm artifact complete` prints the latest, and `dbm export report` writes the latest as `.dbmigrate/exports/final-report.html`. If the summary names a task that "loaded 0 of N rows" (it then never says "validated"), say so: that is a mapping problem, not bad data (see *Facts*) — unless the summary also says "target tables were not empty before this run" and that task's errors are duplicate keys (PRIMARY KEY / UNIQUE, "Cannot insert duplicate key"): then those rows were already in the target. To run again with a changed plan, the human reopens Analysis, Mapping or SQL. |

## Commands (all JSON, all go through the local server)

| Command | Effect |
|---|---|
| `dbm transfer status` | `{"runId","status","done","total","errors","tasksDone","tasksTotal","tasks":[running/paused/failed tasks]}` |
| `dbm transfer pause` | Running tasks finish their current chunk, commit its checkpoint, then stop. |
| `dbm transfer resume` | Continues a paused **or failed** run from the last committed checkpoint of every task — no duplicates, no gaps. |
| `dbm transfer cancel` | Running: stop after the current chunk. Paused/failed: cancel now. Either way the plan's post-load SQL then runs (best effort) to undo its pre-load SQL, and the checkpoint table is dropped unless the run was started with "Keep the checkpoint table". |
| `dbm transfer start --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]` | Only when the human explicitly asks you to start from the CLI **and** gave you the target database name. Into target tables that already hold rows it is refused with `target_not_empty` unless `--truncate` is given; the confirmation by name exists only in the Execute screen's start dialog. |

Every one of them can come back as a refusal — `{"error":"<code>","message":"<sentence>"}` and exit 1. The message is written for the
human: report it as it stands rather than rewording it. The codes you will actually see are `busy` (another runner has this run),
`not_running`, `not_resumable`, `not_cancellable`, `paused_run` (a paused run must be resumed or cancelled first), `not_ready` (no
approved SQL plan, or it cannot be read), `preflight_failed`, `target_not_empty` (the details list each non-empty table and whether it
has a key), `confirm_required` / `confirm_mismatch`, `no_connection`, `no_plan`, and
`target_changed` / `target_unknown` (the saved target connection no longer points, or cannot be shown to point, at the database the
run loaded into — that one is for the human to fix in the UI, never by re-pointing it yourself).

## After a failure

- **A row was rejected (stop-on-error):** the failing row is in the task's error list (Execute screen → task → errors). The human can
  fix the source data and then `dbm transfer resume` (the failed chunk is retried), or cancel and start a new run with "Skip and log
  bad rows" and "Truncate target first" (the new run starts from the first row; see *Facts*).
- **`bad_task`:** a permission error → the human grants it and resumes; bad source data → fix it and resume.
- **`bad_task` "Every row of the first … chunks of <table> was rejected":** the reason names the most common error and its
  number. Usually a mapping defect (an FK column bound to the wrong expression, a CHECK no row satisfies) → reopen, below. (A
  re-run into a table that already held rows is not stopped when every reject is a duplicate key — those rows were already
  there — so this failure in a re-run means some other error, such as a foreign key or CHECK.) The rejected rows are recorded; if the human decides they really are bad rows, Resume carries on from the next chunk and does not
  judge the task again (a keyless task is rolled back instead, so for it only the reopen helps).
- **A mapping or SQL defect** (any failure, or a completed run with a whole table rejected): the human reopens Mapping or SQL on its
  review screen. Reopening cancels a failed run first (running its post-load SQL); after the phase is approved again the Execute
  screen starts a **new** run. Reopen is refused while a run is running or paused — pause and cancel it first.
- **Pre-flight failed:** report the failing checks. Schema drift → the human re-runs discovery in the UI (allowed before the first run
  and after a completed, cancelled or failed run). `chunk_keys` → a task's source query repeats its chunk key (a join that multiplies
  rows), which would lose rows at chunk boundaries: the SQL phase must make the key unique or drop the join.
- **Connection/permission errors:** report them; the human fixes access and resumes.
- Never suggest editing the target tables by hand, disabling constraints, or re-running with truncation without the human deciding.

## Facts worth knowing

- Chunks are keyset pages of the task's source key; each chunk and its checkpoint commit in one target transaction. The first 3
  chunks of every task hold at most 1,000 rows, then the chunk size applies. A row error that ends the transaction (a target
  trigger's ROLLBACK, with "Fire target triggers") makes the runner read that chunk's rows again in 1,000-row chunks to isolate the
  row, then go on at the chunk size: slower than a clean chunk, not a stop.
- Tasks without a usable key load in a single transaction: a pause waits for them to finish, a failure restarts them from scratch.
  So a pause can take as long as the biggest keyless table does. Until it takes effect the Execute screen shows **pausing…** and
  `dbm transfer status` still reports `running` — that is the pause working, not a pause being ignored.
- A run that completed can still carry notes (a checkpoint table that was not ours, rejected rows that were counted but never
  written down). They are in the final report under `notes`; a completed run with notes is not the same as a clean one.
- The final report is the **Complete** artifact. The **Report** step shows it with an **Export HTML** button; from the CLI,
  `dbm export report` writes the same self-contained file (it fails with `export_unavailable` before a run has completed).
- Rejected rows (skip-and-log) are counted per task; the final report lists up to 5 of them per task (key, error and SQL Server
  error number as `errorNumber` - absent for rows recorded by an older version of the plugin - never row data) and validates row
  counts and column checksums.
- Under skip-and-log, constraint violations (FOREIGN KEY, CHECK, PRIMARY KEY, UNIQUE) are always per-row rejects, even when every
  row of a chunk fails alike. A wrong FK or CHECK mapping therefore shows as a task whose first chunks reject everything: once its
  first 3 chunks (at most 1,000 rows each, whatever the chunk size; or its whole source, if smaller) have loaded no row, the task fails with `bad_task` instead of rejecting the
  whole table one row at a time. The exception is a re-run into a table that already held rows whose rejects are all duplicate
  keys (PRIMARY KEY / UNIQUE, by error number 2627 / 2601; a recorded reject without a number never counts): those rows were
  already there, so the task runs on. A task that still ends with 0 rows loaded of a
  non-empty source (after Resume, or in such a re-run) is named in the report's one-line summary ("app.Orders loaded 0 of 3,005
  rows"), which then never says "validated". If the summary says "target tables were not empty before this run" and the task's
  errors are duplicate keys, its rows were already in the target — say that. Otherwise treat it as a mapping problem, not bad
  data: the human reopens Mapping (or SQL), fixes it, approves again and starts a new run with "Truncate target first". Never
  suggest editing the target by hand.
- Checkpoints belong to one run, so a new run starts from the first row. Pre-flight's *Target row counts* line names the target
  tables that already hold rows and which of them have no primary key or unique index ("rows will be loaded again — duplicates").
  A new run into them is refused unless "Truncate target first" is ticked or the start dialog's confirmation names them; keyed
  tables then reject the repeated rows and keyless ones get them twice. The report of such a run says "target tables were not empty
  before this run; counts compare rows added" instead of "validated".
- The plan's pre-load SQL (for an FK cycle, `NOCHECK CONSTRAINT` on the cut foreign key) is in force while a run is running, paused
  or failed. A completed run runs the post-load SQL (`WITH CHECK CHECK CONSTRAINT`); so does Cancel, best effort, recording each
  statement's outcome in the run's notes. A constraint the rows already loaded violate is re-enabled without the check and reported
  as not trusted. Both scripts must be safe to run twice.
- "Truncate target first" deletes **every** row in each target table of the plan, including rows that were never this migration's.
  Suggest it only if those tables hold nothing the human needs to keep; otherwise the target must be restored (for example from a
  backup). It is always the human's decision.
