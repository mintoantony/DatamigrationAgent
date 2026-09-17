# Transfer playbook

Read this when `dbm next` returns a transfer-related action. The transfer runs inside the dbm server; you never move data, never
see rows or connection strings, and never start a transfer on your own initiative.

## What `dbm next` means

| Action / reason | What is happening | What you do |
|---|---|---|
| `await` / `execute` | SQL approved. The human runs pre-flight, picks options and clicks **Execute** (typed confirmation of the target database). | One line: "SQL approved — open the Execute screen (<Url>) to run pre-flight and start the transfer." Keep `dbm await` running in the background. |
| `await` / `transfer` | A run is in progress. | Nothing. If the human asks for progress, run `dbm transfer status` once and report one line. Keep `dbm await` in the background; do not poll. |
| `await` / `transfer_paused` | Paused by the human, or by a server restart (crash recovery turns interrupted runs into paused ones). All committed chunks are safe. | Say it is paused. Resume only when the human asks: `dbm transfer resume`. |
| `stop` / `transfer_failed` | Stop-on-error rejected a row, or a task/script failed. | Run `dbm transfer status`; report the failed task, its target and its error (already redacted). Offer the options below. |
| `stop` / `transfer_cancelled` | The human cancelled. Rows already committed stay in the target. | Report it. A new run can be started from the Execute screen (usually with "Truncate target first"). |
| `stop` / `complete` | Finished and validated. | Report the one-line summary. The final report is under **Complete** in the UI; `dbm export report` writes it as HTML. |

## Commands (all JSON, all go through the local server)

| Command | Effect |
|---|---|
| `dbm transfer status` | `{"runId","status","done","total","errors","tasksDone","tasksTotal","tasks":[running/paused/failed tasks]}` |
| `dbm transfer pause` | Running tasks finish their current chunk, commit its checkpoint, then stop. |
| `dbm transfer resume` | Continues a paused **or failed** run from the last committed checkpoint of every task — no duplicates, no gaps. |
| `dbm transfer cancel` | Running: stop after the current chunk. Paused/failed: cancel now. Drops the checkpoint table. |
| `dbm transfer start --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]` | Only when the human explicitly asks you to start from the CLI **and** gave you the target database name. |

Every one of them can come back as a refusal — `{"error":"<code>","message":"<sentence>"}` and exit 1. The message is written for the
human: report it as it stands rather than rewording it. The codes you will actually see are `busy` (another runner has this run),
`not_running`, `not_resumable`, `not_cancellable`, `paused_run` (a paused run must be resumed or cancelled first), `preflight_failed`,
`confirm_required` / `confirm_mismatch`, and `target_changed` (the saved target connection no longer points at the database the run
loaded into — that one is for the human to fix in the UI, never by re-pointing it yourself).

## After a failure

- **A row was rejected (stop-on-error):** the failing row is in the task's error list (Execute screen → task → errors). The human can
  fix the source data or the mapping/SQL and then `dbm transfer resume` (the failed chunk is retried), or cancel and start a new run
  with "Skip and log bad rows".
- **Pre-flight failed:** report the failing checks; schema drift means discovery must be re-run (the human does that in the UI).
- **Connection/permission errors:** report them; the human fixes access and resumes.
- Never suggest editing the target tables by hand, disabling constraints, or re-running with truncation without the human deciding.

## Facts worth knowing

- Chunks are keyset pages of the task's source key; each chunk and its checkpoint commit in one target transaction.
- Tasks without a usable key load in a single transaction: a pause waits for them to finish, a failure restarts them from scratch.
  So a pause can take as long as the biggest keyless table does. Until it takes effect the Execute screen shows **pausing…** and
  `dbm transfer status` still reports `running` — that is the pause working, not a pause being ignored.
- A run that completed can still carry notes (a checkpoint table that was not ours, rejected rows that were counted but never
  written down). They are in the final report under `notes`; a completed run with notes is not the same as a clean one.
- Rejected rows (skip-and-log) are counted per task; the final report shows up to 5 samples per task and validates row counts and
  column checksums.
