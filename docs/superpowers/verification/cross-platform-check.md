# db-migrate — cross-platform launcher check

> **engine/dist is a committed build output. Rebuild and commit it as the LAST step before any version bump and
> before any merge to `main`** (`pwsh plugins/db-migrate/engine/release.ps1`, or `sh plugins/db-migrate/engine/release.sh`,
> then commit `plugins/db-migrate/engine/dist`). The launchers rebuild dist only when `engine/dist/VERSION` differs from
> `plugin.json`, never when the source changed at the same version, and a user without the .NET SDK cannot rebuild at
> all. A merge of engine source changes without a fresh dist ships the OLD engine to every such user, silently.
> `dbm doctor`'s dist check says the same thing: "a source change at the same version is not detected".

Fill in the Result column (PASS / FAIL + note) and the header fields. Commands run from the repository root.

- Date: 2026-09-18
- Tester: Claude (Task 6.2 implementer), on Windows 11 Enterprise 10.0.26100, Git Bash + PowerShell 7
- Commit: engine/dist built from source tree `8d984ee` (feat/db-migrate with Tasks 5.7 and 6.1 merged into feat/m6-dist) with the release fix `3b2a290` (path-neutral clean compile, machine-path gate); launchers as of `3b2a290`
- Release gates on the committed dist: `pwsh plugins/db-migrate/engine/release.ps1 -Check plugins/db-migrate/engine/dist` (or `sh … release.sh --check=plugins/db-migrate/engine/dist`) must print "all release gates pass"
- Plugin version (plugin.json): 0.1.0
- engine/dist size (from the release summary): 86 files, 29.7 MB (`du -sh`: 30M); largest `runtimes\win-x64\native\e_sqlite3.dll` 1,932 KB
- Runtime on the test machine: .NET 8.0.28 (ASP.NET Core 8.0.27, 8.0.28, 10.0.8, 10.0.9 installed), SDK 10.0.301

## Windows (verified in M6)

| # | Check | Command | Expected | Result |
|---|---|---|---|---|
| W1 | sh launcher syntax | `sh -n plugins/db-migrate/bin/dbm` | no output, exit 0 | PASS: no output, exit 0 |
| W2 | sh launcher under Git Bash | `bash plugins/db-migrate/bin/dbm version` | `{"version":"<v>","runtime":"..."}`, no build line | PASS: `{"version":"0.1.0","runtime":".NET 8.0.28"}`, exit 0, no build line |
| W3 | cmd launcher | `plugins\db-migrate\bin\dbm.cmd version` (PowerShell) | the same JSON line | PASS: same JSON, exit 0. `dbm.cmd doctor --quiet`: no output, exit 0 |
| W4 | exec bit + line endings | `git ls-files -s plugins/db-migrate/bin/dbm` ; `git ls-files --eol plugins/db-migrate/bin/*` | `100755`; `dbm` `i/lf`, `dbm.cmd` `attr/text eol=crlf` | PASS: `100755`; `dbm` `i/lf w/lf attr/text eol=lf`; `dbm.cmd` `i/lf w/crlf attr/text eol=crlf` |
| W5 | doctor | `plugins\db-migrate\bin\dbm.cmd doctor` | `ok:true`; 8 checks; `protector` detail starts `dpapi`; `dist` says it matches plugin.json | PASS: `ok:true`, 8 checks (runtime, aspnetcore, sqlclient, sqlite, home, protector, server, dist); `protector` = `dpapi round-trip`; `dist` = `engine/dist VERSION 0.1.0 matches plugin.json (a source change at the same version is not detected)` |
| W6 | stale dist rebuilds | write `0.0.0-stale` into `engine/dist/VERSION`, run the launcher again | stderr `dbm: building the engine (engine/dist 0.0.0-stale differs from plugin.json <v>, about a minute)...`, then the version JSON; `VERSION` back to `<v>`; no `dist.tmp*`, `dist.old.*` or `.build.lock` left behind | PASS: exactly that build line, then the JSON; `VERSION` 0.1.0; 30M; nothing left behind; a second run prints only the JSON. The rebuilt dist is byte-identical (md5 of all 86 files) to the release script's output, and it stays so even when `obj/` already holds a Release compile with symbols (re-checked after `3b2a290` with both `bin/dbm` and `dbm.cmd`: the launchers now compile from scratch like the release scripts). Since `3b2a290` the release output is also independent of the checkout directory: releases from `D:\…\m6-dist` and from a copy at `C:\…\scratchpad\n4 other\repo` (both `release.ps1` and `release.sh`) gave 86 hash-identical files, and `Dbm.dll` holds no absolute path and no PDB/CodeView entry. Byte-identical holds for the same line-ending settings: `engine/dist` embeds and copies text files (the sample `.sql` resources, `wwwroot`) as they are in the working tree |
| W7 | forced rebuild | `DBM_REBUILD=1 bash plugins/db-migrate/bin/dbm version` | build line `rebuild requested`, then the JSON | PASS: `dbm: building the engine (rebuild requested, about a minute)...`, then the JSON, exit 0, nothing left behind |
| W8 | locked dist | start a server (`dbm init` in a temp folder), then force a rebuild from the repo | either `engine\dist is in use by a running dbm server ...` and the old build keeps working, or the rebuild succeeds and a `dist.old.*` folder remains until that server stops — record which | PASS, **the "in use" branch**: `dbm init w8 --no-browser` in a temp folder started a server (pid 63296) from engine\dist; `DBM_REBUILD=1 dbm.cmd version` from the repo printed `dbm: warning: engine\dist is in use by a running dbm server - run 'dbm stop' in that project folder to let the update through; running the previous engine\dist 0.1.0 instead.`, then the JSON, exit 0. Nothing left behind; the server kept running; `dbm stop` in the temp folder stopped it |
| W9 | runtime missing | `env -u DOTNET_ROOT PATH=/usr/bin:/bin HOME=/tmp bash plugins/db-migrate/bin/dbm version` | `dbm: 'dotnet' was not found on PATH.` plus the winget hint, exit 3 | PASS: `dbm: 'dotnet' was not found on PATH.`, `Install the ASP.NET Core Runtime 8 (or later)...`, `  Windows: winget install Microsoft.DotNet.AspNetCore.8`, exit 3 |
| W10 | rebuild keeps a git checkout clean (open item 42) | in a clean checkout of a released commit: `DBM_REBUILD=1 sh plugins/db-migrate/bin/dbm version`, the same under `dash`, and `DBM_REBUILD=1` + `plugins\db-migrate\bin\dbm.cmd version`; then `git status --porcelain plugins/db-migrate/engine/dist` | nothing: the launchers pin the wwwroot file times like `engine/release.*` (Ruling 189) | PASS (sweep 2026-09-18, engine source at `6202719`, `wwwroot/index.html` touched first): no output after each of sh, dash and cmd. The launchers before the fix left `Dbm.staticwebassets.endpoints.json` modified (36 `Last-Modified` values moved from 2000-01-01 to the checkout's times) |
| W11 | crashed builds are swept (open item 39) | leave `dist.tmp.aged1` and `dist.old.aged1` (mtime 2020) and `dist.tmp.fresh1`, `dist.old.fresh1` (now) in `engine/`, then force a build with each builder | `dbm: removing engine/<name>, left behind by a build that crashed.` for the two aged ones only; the fresh ones stay; with `engine/dist` missing, `dist.old.aged1` stays too | PASS (sweep 2026-09-18) for `bin/dbm` under sh and dash, `dbm.cmd`, `release.sh` under sh and dash, `release.ps1`: the aged pair removed, the fresh pair kept; with `engine/dist` missing (`bin/dbm` sh, `dbm.cmd`, `release.sh` dash) only `dist.tmp.aged1` removed. The builders before the fix kept all four |

Also verified on Windows during Task 6.2 (probe output in the Task 6.2 report): a failed rebuild or a refused swap runs the
previous build with one warning line (exit 4 only when there is no build at all); the `engine/.build.lock` is waited on
while fresh, broken once 15 minutes old, broken after 15 minutes of waiting when its age cannot be read (no PowerShell),
and released only by its owner; the release gates fail a build whose `Dbm.dll` carries the checkout path (the
`96de621` build did: `D:\DatamigrationAgent\.worktrees\m6-dist\…\obj\Release\net8.0\Dbm.pdb`) or any PDB/CodeView entry;
a plugin path containing `( ) &` keeps the cmd messages intact; the SessionStart hook
prints valid hook JSON (`systemMessage` + `additionalContext`) for a failing check and nothing when healthy.

## macOS / Linux (expected behaviour — run on a teammate's machine and record)

The same `bin/dbm` runs everywhere. Only two things differ from Windows:

1. **Detached server spawn.** `ServerControl` starts `dotnet Dbm.dll serve --workspace <root> --detached` through
   `/bin/sh -c 'nohup … </dev/null >/dev/null 2>&1 &'`, so the server survives the tool call, the terminal and Claude
   itself. (On Windows the same effect comes from `UseShellExecute=true` with a hidden window.)
2. **Key storage.** Connection strings are encrypted with AES-GCM using a 32-byte key at `~/.dbmigrate/key` (or
   `$DBM_HOME/key`), created with mode `0600`; encrypted values start with `aesgcm:`. Windows uses DPAPI (`dpapi:`) and
   has no key file.

| # | Check | Command | Expected | Result |
|---|---|---|---|---|
| U1 | launcher | `plugins/db-migrate/bin/dbm version` | the version JSON; no apphost needed (dist has no `Dbm` executable) | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
| U2 | runtime hint | on a machine without ASP.NET Core 8+: `plugins/db-migrate/bin/dbm version` | exit 3 with the apt/dnf (Linux) or brew (macOS) hint | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
| U3 | detached server | `mkdir /tmp/dbmx && cd /tmp/dbmx && <repo>/plugins/db-migrate/bin/dbm init x --no-browser`, close the terminal, then in a new one: `curl -s "http://127.0.0.1:<port>/api/health" -H "X-Dbm-Token: <token from .dbmigrate/server.json>"` | `{"ok":true,"pid":<pid>}` after the first terminal is gone | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
| U4 | own session | `ps -o pid,ppid,pgid,command -p <pid>` | PPID is 1 (nohup'd), command is `dotnet …/Dbm.dll serve --workspace /tmp/dbmx --detached` | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
| U5 | key file mode | Linux `stat -c '%a %s' ~/.dbmigrate/key` · macOS `stat -f '%Lp %z' ~/.dbmigrate/key` | `600 32` | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
| U6 | doctor | `plugins/db-migrate/bin/dbm doctor` | `ok:true`; `protector` detail starts `aesgcm` | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
| U7 | cleanup | `cd /tmp/dbmx && <repo>/plugins/db-migrate/bin/dbm stop` | `{"ok":true,...}`; the pid is gone | NOT RUN: no macOS/Linux machine in the sweep of 2026-09-18 (open item 39); still to be run on a teammate's machine |
