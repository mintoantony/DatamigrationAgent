# db-migrate troubleshooting (orchestrator reference)

Read this when a `dbm` command exits non-zero, prints `{"error":"<code>","message":"<text>"}`, or the user reports a
problem. Apply the remedy and tell the user **one line** (plus the exact command they must run, if any). Never ask for
or repeat a connection string; connection problems are fixed by the user in the browser Setup screen.

## Launcher and engine (before the CLI even runs)

The launcher (`bin/dbm`, `bin/dbm.cmd`) writes plain text to stderr, not JSON:

- **exit 3, "'dotnet' was not found" or "the ASP.NET Core Runtime 8 or later is required"** — give the user the install
  command for their OS from the message (Windows `winget install Microsoft.DotNet.AspNetCore.8`; macOS
  `brew install --cask dotnet`; Linux package `aspnetcore-runtime-8.0`) and tell them to restart Claude Code afterwards.
  Nothing else works until then.
- **exit 4, "engine not built ... and no .NET SDK is installed"** — the plugin copy has no `engine/dist`. Tell the user
  to run `claude plugin update db-migrate@db-migrate` (releases ship the build) or install the .NET 8 SDK, then restart.

With the .NET SDK installed the launcher rebuilds `engine/dist` when it is missing, when its `VERSION` differs from
`plugin.json`, or when asked (`DBM_REBUILD=1`, `dbm doctor --rebuild`). It prints "building the engine (…, about a
minute)", and "waiting for another engine build to finish" while a second `dbm` builds. When the rebuild fails:

- **"dbm: warning: <reason>; running the previous engine/dist <version> instead."** — the command ran on the old build
  and its own output follows; nothing is lost and the rebuild is retried on the next command. The reason is one of
  "engine build failed (see the messages above)", "engine build could not be finished (…)", "engine/dist is in use by a
  running dbm server - run 'dbm stop' in that project folder to let the update through", or "engine/dist could not be
  replaced". Mention it in one line: for "in
  use", the user runs `dbm stop` in the other project folder whose server runs from this engine; for "build failed",
  report the first compiler error (`dotnet build plugins/db-migrate/engine/Dbm.sln` shows it in full).
- **exit 4, "dbm: <reason>."** — the same failures when there is no previous build to fall back to, so nothing ran.
  If the message says the previous build "is in <folder> - rename it to dist", tell the user exactly that.

`dbm doctor` prints JSON: `{"ok":…,"checks":[{"name":…,"ok":…,"detail":…}]}` with eight checks (`runtime`,
`aspnetcore`, `sqlclient`, `sqlite`, `home`, `protector`, `server`, `dist`). Report any check whose `ok` is false, with
its `detail`. `dbm doctor --quiet` prints nothing when healthy and otherwise one line per failing check,
`dbm doctor: <check>: <detail>`. The session-start hook runs it and shows its lines to the user and to you as
"db-migrate health check at session start"; act on them like the checks below.

- **`protector` false** — connection strings cannot be encrypted or read back. On Windows this needs an interactive user
  profile; elsewhere check `~/.dbmigrate/key` (32 bytes, mode 600). Saved connections may have to be entered again.
- **`dist` false** — the built engine does not match the plugin version (or, "cannot check: …", the versions could not
  be read). Relay the detail: update the plugin (`claude plugin update db-migrate@db-migrate`, then restart Claude Code);
  with the .NET 8 SDK installed the launcher rebuilds `engine/dist` by itself.

## CLI errors

- **`no_project`** — the command ran outside a project folder. Run `dbm init` in the folder the user means (ask which
  one if unclear), or `cd` to the folder that contains `.dbmigrate/`.
- **`usage`** — a wrong or missing argument; the message shows the correct form.
- **`unknown_command`** — the engine is older or newer than this skill. Run `dbm version` and `dbm doctor`, and tell the
  user to update the plugin (`claude plugin update db-migrate@db-migrate`, then restart Claude Code).
- **`internal`** — an unexpected engine error. Report the message; `dbm status` shows whether the project is still usable.
- **`server_error`** — the project's server gave an answer `dbm` could not use: from `dbm await`, "await failed with
  HTTP <status>"; from `dbm export`, a body that is not a JSON object (the message names the method, the URL, the HTTP
  status and the first 200 characters of the body). The server is misbehaving or another program answers on its port:
  run `dbm stop`, then `dbm ui`, then retry. If it recurs, read the last 40 lines of `.dbmigrate/server.log`.
- **`server_unreachable`** (`dbm await`) — "Lost the connection to the dbm server 3 times; see <server.log path>": the
  server kept dying or restarting under a waiting `dbm await`. Read the last 40 lines of that log, then run `dbm next`
  and continue; if the server will not stay up, follow *Server and browser* below.
- **`invalid_patch`** (`dbm apply`) — the patch file is not a valid patch at all (not JSON, empty, no `phase`, an
  operation without `op`/`path`); nothing was checked against the project. **`patch_rejected`** (`dbm apply`, exit 1,
  `{"ok":false,"error":"patch_rejected","message":…,"errors":[…],"warnings":[…]}`) — a well-formed patch that failed
  validation; `message` is the `errors` joined with "; ". **`not_found`** from `dbm apply` — the patch file does not
  exist (the subagent did not write it). For all three see *`dbm apply` rejected* below.

## Server and browser

**"The dbm server did not start within 20 s"** (an `internal` error from `dbm next`, `dbm init`, `dbm ui`, `dbm await`,
`dbm discover`, `dbm export` or `dbm transfer …`):

1. Run `dbm stop`, then `dbm ui` again.
2. If it still fails, read the last 40 lines of `.dbmigrate/server.log` with the Read tool and act on it: "address
   already in use" → `dbm stop` and retry; "database is locked" → another `dbm` process holds the project, wait 10 s;
   access denied → the folder is read-only or on a synced/network drive, so suggest a local folder.
3. Run `dbm doctor` and report any failing check.

**"This page needs its access link." in the browser** (`unauthorized`, 401) — the server restarted and issued a new
token, so the old tab is dead. Run `dbm ui` and give the user the new URL.

**`forbidden_host` (403)** — the page was opened through another host name or a proxy. Tell the user to use the exact
`http://127.0.0.1:<port>/?t=<token>` URL that `dbm ui` prints.

**"Agent offline" banner** — only means no Claude session has run `dbm next` or held a `dbm await` in the last two
minutes while Claude has work to do. If you are running, run `dbm next` and continue the loop (you probably ended it).

## Workflow

**`stop` with reason `job_failed`** — a server job (discover, analyze, automap, sqlgen) failed; `summary` holds the
error. Common causes: connection or login failure (see *Connections*), missing `VIEW DEFINITION` / read permission on
the source, or a timeout on a very large database. Tell the user in one line the cause, that **Retry** in the error banner
re-runs the job, and that `/db-migrate resume` continues afterwards. Then end your turn. Do **not** start `dbm await`
after any `stop`: it returns at once while the next action is not `await`, so it would wake you with the same `stop`
again and again.

**`dbm apply` rejected** (`patch_rejected`, `invalid_patch` or `not_found`, exit 1)

- `patch_rejected` whose message says "baseVersion … does not match the current version", or that the phase is no
  longer drafting or reworking ("agent patches are accepted only while drafting or reworking"): the human edited or
  approved meanwhile. Not a failure — run `dbm next` and continue.
- Any other `patch_rejected` (unknown table or column, bad JSON pointer, a feedback item without a response, SQL
  validation failed), `invalid_patch`, or `not_found` (no patch file): dispatch the same subagent once more with the
  packet plus `The previous patch was rejected: <message>`, then `dbm apply` again.
- Rejected twice: stop retrying. One line to the user: "The <phase> patch was rejected twice: <short message>. Type
  /db-migrate resume to let Claude try again, or press Take over on the <phase> screen to edit it yourself." Then end
  your turn, with no `dbm await` (`dbm next` would hand back the same agent work at once). While the phase is drafting or
  reworking the UI refuses direct edits and Request changes; **Take over** is the way out: it discards the pending agent
  work and puts the phase back to awaiting review on its current version (open comments stay open), after which
  `dbm next` answers `await`/`review`. The UI refuses it while Claude counts as connected (an open `dbm await`, or a
  `dbm next`/`dbm await` in the last 2 minutes), so it takes effect only once this turn has ended. A patch applied after
  a take-over is refused ("agent patches are accepted only while drafting or reworking"): run `dbm next` and continue.

**Approval blocked (409 `blocked` in the UI)** — mapping approval needs every source column mapped or dropped and every
non-nullable target column without a default filled in. The UI lists the blockers; the user resolves them there.

**Drift detected** (`drift_detected` event, or approval refused with "Schema changed since discovery") — tell the user to
press **Re-run discovery** on the Analysis screen. Re-discovery marks the later phases stale and the loop regenerates them
for review.

**Phase shows `stale`** — an upstream phase was reopened; nothing to fix. After the upstream approval the engine re-runs
the stale phase and `dbm next` returns work again.

**`await` with reason `paused`** — the user paused the project. Say "Paused — press Resume in the UI to continue." and
keep a background `dbm await`. A patch you already hold can still be applied.

## Transfer

Details and options are in `reference/transfer.md`. The short version:

- **`await` / `transfer_paused`** — the run is paused; **Resume** on the Execute screen continues from the last
  committed chunk. Say "The transfer is paused — resume it from the Execute screen." and keep a background `dbm await`.
- **`stop` / `transfer_failed`** — the error is on the Execute screen (`dbm transfer status` shows the run). Report the
  remedy in one line and end your turn (no `dbm await`); after the user presses **Resume** they type `/db-migrate
  resume`. **Resume** continues a failed run from each task's last committed checkpoint. Typical causes:
  - row errors with *stop on error*: fix the source data and Resume; or cancel and start a new run with *skip and log*.
    A new run starts from the first row again, so it needs *Truncate target first* (see the truncate caution below) or
    the start dialog's confirmation naming the tables that already hold rows — keyed tables then reject every row the
    cancelled run already loaded and keyless tables are loaded twice;
  - a full transaction log on the target: grow it or switch to SIMPLE recovery, then Resume;
  - a dropped connection: Resume;
  - missing `ALTER`/`INSERT` permission on a target table: grant it, then Resume.
- **`bad_task`** in the error — every row of a chunk failed with the same non-constraint error. Split by what the error
  says:
  - a permission error (e.g. `INSERT` or `ALTER` denied): nothing in the plan is wrong — the user grants the permission
    and presses Resume;
  - the source data (a value that does not convert or fit): fix the data and Resume;
  - a mapping or SQL defect (invalid column or object, a conversion or truncation the plan itself causes, NULL into
    NOT NULL): the user reopens Mapping or SQL on its review screen. The reopen cancels the failed run first (running
    the plan's post-load SQL); after re-approval the Execute screen starts a new run, which needs *Truncate target
    first* or the confirmation by name, because the target still holds the rows this run committed.
- **Truncate caution** — *Truncate target first* deletes **every** row in each target table of the plan, not only rows
  this migration loaded. Recommend it only if those tables hold nothing the user needs to keep; otherwise the target
  must be restored (for example from a backup), never edited by hand. It is the user's decision.
- **`stop` / `transfer_cancelled`** — the user cancelled; rows already committed stay in the target. Cancel ran the
  plan's post-load SQL: report any run note that says NOT restored (it carries the server's text; the constraint was
  re-enabled without the check, or is still disabled). Report the `summary` and end.
- **`target_not_empty`** (from `dbm transfer start` or the Execute button) — a new run would load into target tables
  that already hold rows; the details say which have no key (their rows would be duplicated). The user ticks
  *Truncate target first* (truncate caution) or confirms the tables by name in the start dialog.
- **Reopen refused** ("… cannot be reopened: transfer run N is paused/running") — the run must be cancelled first
  (Pause, then Cancel); a completed, cancelled or failed run allows it.

## Connections (the user fixes these in the Setup screen)

Never ask for the connection string. Match the error text the user or the job reports:

| Error text contains | Remedy to tell the user |
|---|---|
| `certificate chain was issued by an authority that is not trusted`, `SSL Provider` | SqlClient encrypts by default: add `TrustServerCertificate=True` for test servers, or install the server certificate's CA, or set `HostNameInCertificate=<name on the certificate>`. |
| `A network-related or instance-specific error`, `error: 26`, `error: 40` | Wrong host, instance or port, or a firewall. Use `Server=tcp:<host>,<port>`; named instances need the SQL Browser (UDP 1434) reachable. |
| `Login failed for user`, `18456` | Wrong credentials, a disabled login, or no access to the database in `Initial Catalog`. With Windows auth the login is the user running Claude Code. |
| `Cannot open database`, `4060` | `Database`/`Initial Catalog` names a database that does not exist or is not accessible to this login. |
| `Cannot find an authentication provider for 'ActiveDirectory<Mode>'` | The engine is missing the Entra ID package: update the plugin (`claude plugin update db-migrate@db-migrate`). |
| `AADSTS...`, `DefaultAzureCredential failed` | Entra sign-in failed. `Active Directory Default` uses an existing `az login` / IDE sign-in; service principals need `Authentication=Active Directory Service Principal;User Id=<app id>;Password=<secret>`; `Managed Identity` works only on an Azure host. |
| `(localdb)`, `error: 50` | LocalDB is Windows-only and per user: `sqllocaldb start MSSQLLocalDB` in a terminal. |

After the user saves a corrected connection the engine re-runs discovery by itself. If you are waiting on an `await`
action, your background `dbm await` wakes when there is work; after a `stop` (e.g. `job_failed`) you have ended your turn
and the user types `/db-migrate resume`.

## Demo and exports

- **`demo_exists`** — the demo databases exist already. Re-run with `--force` (drops and recreates exactly those two
  databases — ask the user first: anything in them is lost, also for any other project folder that uses them) or
  choose another `--prefix`.
- **`locked`** (`dbm demo --attach`) — this project already started a transfer; run the demo in a new, empty folder.
- **`sql_error`** (`dbm demo`) — usually the login on `--server` cannot connect or lacks `CREATE DATABASE` (`dbcreator`).
  The message lists every step already taken on the server, then the step that failed and the server's text:
  "Failed <step>: <server text>. No database was created or dropped." when nothing had changed yet, otherwise
  "<Step>, <step>, …, then failed <step>: <server text>. Re-run with --force to start over." (for example "Dropped …,
  created …, then failed creating …"). With `--attach`, a failure after both databases were seeded says "Created and
  seeded <source> and <target>, then failed saving them as this project's connections: … Re-run with --force --attach
  to start over." Relay the message, then the remedy it names.
- **`not_found`** (`dbm export`) — the server has no export of that kind; the kinds are `analysis`, `sql`, `sqlpack`,
  `report`.
- **`export_unavailable`** (`dbm export`) — that phase has nothing to export yet (e.g. `report` before a completed run).
- **`internal`** from `dbm export … --out <path>` — the copy to `--out` failed (the message names the path); the export
  itself is saved under `.dbmigrate/exports/`.
