# Transfer playbook

Read this when `dbm next` returns a transfer-related action. The transfer runs inside the dbm server; you never move data, never
see rows or connection strings, and never start a transfer on your own initiative.

## What `dbm next` means

| Action / reason | What is happening | What you do |
|---|---|---|
| `await` / `execute` | SQL approved. The human runs pre-flight, picks options, clicks **Execute…**, types the target database name and clicks **Start transfer**. | One line: "SQL approved — open the Execute screen (<Url>) to run pre-flight and start the transfer." Keep `dbm await` running in the background. |
| `await` / `transfer` | A run is in progress. | Nothing. If the human asks for progress, run `dbm transfer status` once and report one line. Keep `dbm await` in the background; do not poll. |
| `await` / `transfer_paused` | Paused by the human, or by a server restart (crash recovery turns interrupted runs into paused ones). All committed chunks are safe. | Say it is paused. Resume only when the human asks: `dbm transfer resume`. |
| `stop` / `transfer_failed` | Stop-on-error rejected a row, a task/script failed, or (under skip-and-log too) a task failed with `bad_task` because every row of a chunk failed alike on a non-constraint error. | Run `dbm transfer status`; report the failed task, its target and its error (already redacted) and the matching option below, then end your turn — no `dbm await` after a `stop` (it returns at once). After the human acts, they type `/db-migrate resume`. |
| `stop` / `transfer_cancelled` | The human cancelled. Rows already committed stay in the target. | Report it and end your turn. A new run can be started from the Execute screen; it starts from the first row again, so it needs "Truncate target first" (see *Facts* for what that deletes) or keyed tables reject the loaded rows and keyless ones double. |
| `stop` / `complete` | Finished and validated. | Report the one-line summary. The final report is stored as the **Complete** artifact: the UI's **Report** step shows it, `dbm artifact complete` prints it, and `dbm export report` writes it as `.dbmigrate/exports/final-report.html`. If any task loaded 0 rows while rejecting rows, say so: that is a mapping problem, not bad data (see *Facts*). |

## Commands (all JSON, all go through the local server)

| Command | Effect |
|---|---|
| `dbm transfer status` | `{"runId","status","done","total","errors","tasksDone","tasksTotal","tasks":[running/paused/failed tasks]}` |
| `dbm transfer pause` | Running tasks finish their current chunk, commit its checkpoint, then stop. |
| `dbm transfer resume` | Continues a paused **or failed** run from the last committed checkpoint of every task — no duplicates, no gaps. |
| `dbm transfer cancel` | Running: stop after the current chunk. Paused/failed: cancel now. Drops the checkpoint table unless the run was started with "Keep the checkpoint table". |
| `dbm transfer start --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]` | Only when the human explicitly asks you to start from the CLI **and** gave you the target database name. |

Every one of them can come back as a refusal — `{"error":"<code>","message":"<sentence>"}` and exit 1. The message is written for the
human: report it as it stands rather than rewording it. The codes you will actually see are `busy` (another runner has this run),
`not_running`, `not_resumable`, `not_cancellable`, `paused_run` (a paused run must be resumed or cancelled first), `not_ready` (no
approved SQL plan, or it cannot be read), `preflight_failed`, `confirm_required` / `confirm_mismatch`, `no_connection`, `no_plan`, and
`target_changed` / `target_unknown` (the saved target connection no longer points, or cannot be shown to point, at the database the
run loaded into — that one is for the human to fix in the UI, never by re-pointing it yourself).

## After a failure

- **A row was rejected (stop-on-error):** the failing row is in the task's error list (Execute screen → task → errors). The human can
  fix the source data and then `dbm transfer resume` (the failed chunk is retried), or cancel and start a new run with "Skip and log
  bad rows" and "Truncate target first" (the new run starts from the first row; see *Facts*). A mapping or SQL defect cannot be
  fixed in this project once a transfer has started: it means a new project folder.
- **`bad_task`:** a permission error → the human grants it and resumes; bad source data → fix it and resume; a mapping or SQL
  defect → a new project folder, as above.
- **Pre-flight failed:** report the failing checks; schema drift means discovery must be re-run (the human does that in the UI).
  Re-running discovery is refused once a transfer has started.
- **Connection/permission errors:** report them; the human fixes access and resumes.
- Never suggest editing the target tables by hand, disabling constraints, or re-running with truncation without the human deciding.

## Facts worth knowing

- Chunks are keyset pages of the task's source key; each chunk and its checkpoint commit in one target transaction.
- Tasks without a usable key load in a single transaction: a pause waits for them to finish, a failure restarts them from scratch.
  So a pause can take as long as the biggest keyless table does. Until it takes effect the Execute screen shows **pausing…** and
  `dbm transfer status` still reports `running` — that is the pause working, not a pause being ignored.
- A run that completed can still carry notes (a checkpoint table that was not ours, rejected rows that were counted but never
  written down). They are in the final report under `notes`; a completed run with notes is not the same as a clean one.
- The final report is the **Complete** artifact. The **Report** step shows it with an **Export HTML** button; from the CLI,
  `dbm export report` writes the same self-contained file (it fails with `export_unavailable` before a run has completed).
- Rejected rows (skip-and-log) are counted per task; the final report lists up to 5 of them per task (key and error, never row
  data) and validates row counts and column checksums.
- Under skip-and-log, constraint violations (FOREIGN KEY, CHECK, PRIMARY KEY, UNIQUE) are always per-row rejects, even when every
  row of a chunk fails alike. So a wrong FK or CHECK mapping can end **completed** with a whole table rejected. Treat a task with
  0 rows loaded and many rejected as a mapping problem, not bad data. Once a transfer has started, no phase of this project can be
  reopened and a completed run cannot be followed by a new one, so fixing the mapping means a new project folder (and cleaning the
  target, e.g. with "Truncate target first" there). Tell the human that plainly; never suggest editing the target by hand.
- Pre-flight only warns about non-empty target tables. Checkpoints belong to one run, so a new run starts from the first row.
  Loading again without "Truncate target first" rejects the duplicates of keyed tables and loads keyless tables a second time
  (their rows double), and the row-count validation still passes because it counts the rows added by this run.
- "Truncate target first" deletes **every** row in each target table of the plan, including rows that were never this migration's.
  Suggest it only if those tables hold nothing the human needs to keep; otherwise the target must be restored (for example from a
  backup). It is always the human's decision.
