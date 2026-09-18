---
name: db-migrate
description: Migrate or copy data from one SQL Server database into another existing SQL Server database with a different schema — schema analysis, source-to-target mapping and migration SQL reviewed by a human in a local web UI, then a resumable transfer. Use when the user asks to migrate, move, copy or transfer data between SQL Server databases, map a legacy schema to a new one, or types /db-migrate.
argument-hint: "[resume|status|ui|stop]"
---

# db-migrate orchestrator

The `dbm` engine (on PATH via this plugin) owns all state and decides every step. You only relay between `dbm`, the
three db-migrate subagents and the user. Keep every message to the user to ONE line, except the final completion
report or an error.

## Hard rules

- NEVER ask for, accept, print or repeat a connection string. Connections are entered only in the browser UI — or, for
  the sample databases, `dbm demo --server "<string the user gave you>" --attach`, only if it holds no password
  (Windows authentication). With a password, the user runs it in a terminal outside Claude Code (not `!`); give them
  the path from `p=$(command -v dbm); cygpath -w "$p.cmd" 2>/dev/null || echo "$p"` (Windows form on Windows).
- Do not read `.dbmigrate/state.db`, `server.json` or work packets yourself — subagents read their packet.
- Do not edit artifacts yourself; changes come only from subagent patches applied with `dbm apply`.
- When a `dbm` command fails (non-zero exit, or `{"error":...}`), read `reference/troubleshooting.md` and follow its
  remedy first.

## Arguments

- none or `resume` → run the loop below.
- `status` → run `dbm status`, report phase and status in one line, stop.
- `ui` → run `dbm ui`; say the page was opened (give the `url` if asked), stop.
- `stop` → run `dbm stop`, say the UI server is stopped, stop.

## Loop

1. Run `dbm init` (safe to repeat). If it printed `"created":true`, tell the user:
   "Opened the db-migrate UI in your browser — paste the source and target connection strings there."
2. Run `dbm next` and act on the `action` field of its JSON:
   - `agent` → use the Agent tool with `subagent_type: "db-migrate:<agent>"` and this exact prompt:
     `Work packet: <packet>\nWrite your patch to: <patchPath>\nFollow your playbook. Reply with one line.`
     Then run `dbm apply <patchPath>`.
     - If `apply` exits non-zero, dispatch the same subagent once more with the prompt above plus
       `\nThe previous patch was rejected: <errors joined with "; ">`, then `dbm apply <patchPath>` again.
     - If it fails again, tell the user the <phase> patch was rejected twice (short reason): `/db-migrate resume`
       retries, or **Take over** in the UI to edit it by hand; end your turn.
     Otherwise go back to step 2.
   - `await` → run `dbm await` with the Bash tool's `run_in_background: true`, then end your turn with at most one short line:
     `review` → "Waiting for your review of <phase> in the browser."; `setup` → "Waiting for the connection strings in
     the browser."; `execute` → "Ready to run the transfer from the browser."; `paused` → "Paused — press Resume in the
     UI to continue."; `transfer_paused` → "The transfer is paused — resume it from the Execute screen."; `job` /
     `transfer` → "Working in the background — I'll continue when it's done." `dbm await` returns only when the action
     stops being `await`: approving SQL (`review` → `execute`) does not wake it, so never promise a next message. Handle its
     output like `dbm next`'s.
   - For any `transfer*` reason, also read `reference/transfer.md` next to this file.
   - `stop` → report and end your turn (no `dbm await`); the user acts in the UI, then types `/db-migrate resume`:
     - `complete` → up to 3 lines: the `summary`, and that the final report is in the UI.
     - `job_failed` → one line with the `summary`; suggest fixing the cause and pressing Retry in the UI.
     - `transfer_failed` / `transfer_cancelled` → the `summary` (it names the options).
     - `server_stopped` → one line: the UI server was stopped; `/db-migrate resume` continues.
3. If a background `dbm await` fails or times out, run `dbm next` and continue from step 2.
4. If the user says nothing happened after they acted in the browser, this surface does not wake on a finished
   background command: from then on run `dbm await --timeout 540` in the foreground (Bash `timeout: 600000`) while it
   returns `await`.
