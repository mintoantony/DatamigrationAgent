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
- **exit 4, "engine build failed"** — the launcher tried to build the engine and the compile failed; the previous
  `engine/dist`, if there was one, is left in place, but this command did not run. Report the first compiler error from
  the output; suggest `dotnet build plugins/db-migrate/engine/Dbm.sln` to see it in full.
- **exit 4, "engine/dist is in use by a running dbm server"** — a rebuild could not replace the engine because a `dbm`
  server of another project folder is running from it. The previous build is untouched, but this command did not run.
  Tell the user to run `dbm stop` in that project folder, then run the command again.
- **exit 4, "engine/dist could not be replaced"** — the new build could not be moved into place; the previous build was
  put back. Run the command again; if it repeats, suggest closing whatever holds files in `engine/` and retrying.

`dbm doctor` prints JSON: `{"ok":…,"checks":[{"name":…,"ok":…,"detail":…}]}` with eight checks (`runtime`,
`aspnetcore`, `sqlclient`, `sqlite`, `home`, `protector`, `server`, `dist`). Report any check whose `ok` is false, with
its `detail`. `dbm doctor --quiet` (the session-start hook) prints nothing when healthy and otherwise one line,
`dbm doctor: <check>: <detail>; …`.

- **`protector` false** — connection strings cannot be encrypted or read back. On Windows this needs an interactive user
  profile; elsewhere check `~/.dbmigrate/key` (32 bytes, mode 600). Saved connections may have to be entered again.
- **`dist` false** — the built engine does not match the plugin version. In a checkout with the .NET SDK the launcher
  rebuilds by itself on the next command; otherwise tell the user to update the plugin, or (in a development checkout)
  run `plugins/db-migrate/engine/release.ps1`.

## CLI errors

- **`no_project`** — the command ran outside a project folder. Run `dbm init` in the folder the user means (ask which
  one if unclear), or `cd` to the folder that contains `.dbmigrate/`.
- **`usage`** — a wrong or missing argument; the message shows the correct form.
- **`unknown_command`** — the engine is older or newer than this skill. Run `dbm version` and `dbm doctor`, and tell the
  user to update the plugin (`claude plugin update db-migrate@db-migrate`, then restart Claude Code).
- **`internal`** — an unexpected engine error. Report the message; `dbm status` shows whether the project is still usable.
- **`server_error`** — the project's server gave an answer `dbm` could not use (from `dbm await`, or from `dbm export`
  when the answer was not JSON; the message names the request and the HTTP status). The server is misbehaving or another
  program answers on its port: run `dbm stop`, then `dbm ui`, then retry. If it recurs, read the last 40 lines of
  `.dbmigrate/server.log`.
- **`server_unreachable`** (`dbm await`) — the connection to the server was lost repeatedly. Run `dbm next` and continue;
  if it recurs, follow *Server and browser* below.
- **`patch_rejected`, `invalid_patch`** (`dbm apply`) — see *`dbm apply` rejected* below.

## Server and browser

**"The dbm server did not start within 20 s"** (an `internal` error from `dbm init`, `dbm ui`, `dbm await`,
`dbm export` or `dbm transfer …`):

1. Run `dbm stop`, then `dbm ui` again.
2. If it still fails, read the last 40 lines of `.dbmigrate/server.log` with the Read tool and act on it: "address
   already in use" → `dbm stop` and retry; "database is locked" → another `dbm` process holds the project, wait 10 s;
   access denied → the folder is read-only or on a synced/network drive, so suggest a local folder.
3. Run `dbm doctor` and report any failing check.

**`unauthorized` (401) in the browser** — the server restarted and issued a new token, so the old tab is dead. Run
`dbm ui` and give the user the new URL.

**`forbidden_host` (403)** — the page was opened through another host name or a proxy. Tell the user to use the exact
`http://127.0.0.1:<port>/?t=<token>` URL that `dbm ui` prints.

**"Agent offline" banner** — only means no Claude session has run `dbm next` or held a `dbm await` in the last two
minutes while Claude has work to do. If you are running, run `dbm next` and continue the loop (you probably ended it).

## Workflow

**`stop` with reason `job_failed`** — a server job (discover, analyze, automap, sqlgen) failed; `summary` holds the
error. Common causes: connection or login failure (see *Connections*), missing `VIEW DEFINITION` / read permission on
the source, or a timeout on a very large database. Tell the user the cause and that **Retry** in the error banner re-runs
the job, then start a background `dbm await` (it returns when Claude has work again).

**`dbm apply` rejected** (`patch_rejected`, exit 1)

- The message says the base version does not match, or the phase is no longer `drafting`/`reworking`: the human edited
  or approved meanwhile. Not a failure — run `dbm next` and continue.
- Validation errors (unknown table or column, bad JSON pointer, a feedback item without a response, SQL validation
  failed) or `invalid_patch` (the file is not a valid patch): dispatch the same subagent once more with the packet plus
  `The previous patch was rejected: <message>`, then `dbm apply` again.
- Rejected twice: stop retrying. One line to the user: "The <phase> patch was rejected twice: <short message>. Add
  clarifying feedback or edit directly in the UI, then press Request changes." Keep a background `dbm await`.

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

- **`await` / `transfer_paused`** — the user paused the run; **Resume** on the Execute screen continues from the last
  committed chunk. Keep a background `dbm await`.
- **`stop` / `transfer_failed`** — the error is on the Execute screen (`dbm transfer status` shows the run). Typical: row
  errors with *stop on error* (fix the data or the mapping and Resume, or cancel and start a new run with *skip and
  log*), a full transaction log on
  the target (grow it or switch to SIMPLE recovery, then Resume), a dropped connection (Resume continues from the
  checkpoint), or missing `ALTER`/`INSERT` permission on a target table. Report the remedy in one line, then keep a
  background `dbm await`.
- **`bad_task`** in the error — every row of a chunk failed with the same plan-shaped error (invalid column or object,
  conversion, NULL into NOT NULL, truncation, overflow, permission). That is a mapping or SQL defect, not bad data, even
  under *skip and log*: fix the mapping or SQL task it names, then Resume.
- **`stop` / `transfer_cancelled`** — the user cancelled; rows already committed stay in the target. Report and end.

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

After the user saves a corrected connection the engine re-runs discovery by itself; keep a background `dbm await`.

## Demo and exports

- **`demo_exists`** — the demo databases exist already. Re-run with `--force` (drops and recreates exactly those two
  databases) or choose another `--prefix`.
- **`locked`** (`dbm demo --attach`) — this project already started a transfer; run the demo in a new, empty folder.
- **`sql_error`** (`dbm demo`) — usually the login on `--server` cannot connect or lacks `CREATE DATABASE` (`dbcreator`).
  If the first database was created and the second failed, the message names both databases and what happened to each,
  and says to re-run with `--force` to start over.
- **`not_found`** (`dbm export`) — the server has no export of that kind; the kinds are `analysis`, `sql`, `sqlpack`,
  `report`.
- **`export_unavailable`** (`dbm export`) — that phase has nothing to export yet (e.g. `report` before a completed run).
- **`internal`** from `dbm export … --out <path>` — the copy to `--out` failed (the message names the path); the export
  itself is saved under `.dbmigrate/exports/`.
