@echo off
rem db-migrate launcher (Windows cmd / PowerShell).
rem Runs engine\dist\Dbm.dll with the installed .NET runtime; builds it when missing, when DBM_REBUILD=1, or when
rem engine\dist\VERSION no longer matches plugin.json (needs the .NET SDK).
rem Exit codes: 3 = runtime missing, 4 = engine not built / build failed and there is no previous build to run;
rem otherwise the engine's own exit code.
rem Messages that carry a path go through :say, so a plugin path with ( ) & ! cannot break them.
setlocal EnableExtensions DisableDelayedExpansion

set "DBM_ENGINE=%~dp0..\engine"
set "DBM_DLL=%DBM_ENGINE%\dist\Dbm.dll"
set "DBM_MANIFEST=%~dp0..\.claude-plugin\plugin.json"
rem One lock for every builder of engine\dist: both launchers and both engine\release.* scripts. It is a directory
rem holding an owner file; it counts as abandoned once the directory itself is older than DBM_LOCK_STALE_MINUTES.
set "DBM_LOCK=%DBM_ENGINE%\.build.lock"
set "DBM_LOCK_OWNER=cmd-%RANDOM%%RANDOM%%RANDOM%"
set "DBM_LOCK_STALE_MINUTES=15"
set "DBM_FAILURE="

where dotnet >nul 2>nul
if errorlevel 1 (
  >&2 echo dbm: 'dotnet' was not found on PATH.
  >&2 echo Install the ASP.NET Core Runtime 8 or later, then open a new terminal:
  >&2 echo   winget install Microsoft.DotNet.AspNetCore.8
  exit /b 3
)

set "DBM_ASPNET_MAJOR=0"
for /f "tokens=2" %%v in ('dotnet --list-runtimes 2^>nul ^| findstr /b /c:"Microsoft.AspNetCore.App "') do call :consider %%v
if %DBM_ASPNET_MAJOR% LSS 8 (
  >&2 echo dbm: the ASP.NET Core Runtime 8 or later is required - found major version %DBM_ASPNET_MAJOR%.
  >&2 echo Install it, then open a new terminal:
  >&2 echo   Windows: winget install Microsoft.DotNet.AspNetCore.8
  >&2 echo   macOS:   brew install --cask dotnet
  >&2 echo   Linux:   https://learn.microsoft.com/dotnet/core/install/linux
  exit /b 3
)

if /i "%~1"=="doctor" for %%a in (%*) do if /i "%%~a"=="--rebuild" set "DBM_REBUILD=1"

set "DBM_PLUGIN_VERSION="
if exist "%DBM_MANIFEST%" for /f "usebackq tokens=2 delims=:, " %%a in (`findstr /r /c:"^ *.version. *:" "%DBM_MANIFEST%"`) do if not defined DBM_PLUGIN_VERSION set "DBM_PLUGIN_VERSION=%%~a"
call :read_dist_version

set "DBM_REASON="
if not exist "%DBM_DLL%" set "DBM_REASON=engine\dist is missing"
if not defined DBM_REASON if "%DBM_REBUILD%"=="1" set "DBM_REASON=rebuild requested"
if not defined DBM_REASON if defined DBM_PLUGIN_VERSION if not "%DBM_DIST_VERSION%"=="%DBM_PLUGIN_VERSION%" set "DBM_REASON=engine\dist %DBM_DIST_VERSION% differs from plugin.json %DBM_PLUGIN_VERSION%"
if not defined DBM_REASON goto run

dotnet --list-sdks 2>nul | findstr /r "." >nul
if errorlevel 1 goto no_sdk

call :acquire_lock
call :build_engine
set "DBM_BUILD_EXIT=%ERRORLEVEL%"
call :release_lock
if "%DBM_BUILD_EXIT%"=="0" goto run
if not exist "%DBM_DLL%" goto build_nothing_to_run
rem A failed rebuild must not take away the build that still works; it is retried on the next command.
call :read_dist_version
if not defined DBM_DIST_VERSION set "DBM_DIST_VERSION=without VERSION"
set "DBM_SAY=dbm: warning: %DBM_FAILURE%; running the previous engine\dist %DBM_DIST_VERSION% instead."
call :say

:run
dotnet "%DBM_DLL%" %*
exit /b %ERRORLEVEL%

:build_nothing_to_run
set "DBM_SAY=dbm: %DBM_FAILURE%."
call :say
exit /b 4

rem A stale dist without an SDK still runs; `dbm doctor` reports the mismatch.
:no_sdk
if exist "%DBM_DLL%" goto run
set "DBM_SAY=dbm: engine not built - %DBM_DLL% is missing and no .NET SDK is installed to build it."
call :say
>&2 echo      Install the .NET 8 SDK or use a release of the plugin that ships engine\dist.
exit /b 4

:say
setlocal EnableDelayedExpansion
>&2 echo(!DBM_SAY!
endlocal & exit /b 0

:consider
for /f "tokens=1 delims=." %%m in ("%~1") do if %%m GTR %DBM_ASPNET_MAJOR% set "DBM_ASPNET_MAJOR=%%m"
exit /b 0

:read_dist_version
set "DBM_DIST_VERSION="
if exist "%DBM_ENGINE%\dist\VERSION" set /p DBM_DIST_VERSION=<"%DBM_ENGINE%\dist\VERSION"
exit /b 0

rem Staleness is judged by the lock's own age, never by how long this process has waited: a build that runs longer
rem than the wait must keep its lock. Only a lock whose owner file names this process is ever released by it.
rem When the lock's age cannot be read (powershell missing, or blocked by AppLocker/WDAC), the waiter falls back to its
rem own count, so an abandoned lock is still broken eventually: after DBM_LOCK_STALE_MINUTES x 60 one-second waits.
:acquire_lock
set "DBM_ANNOUNCED="
set /a DBM_WAITED=0
set /a DBM_WAIT_LIMIT=DBM_LOCK_STALE_MINUTES*60
:acquire_lock_loop
mkdir "%DBM_LOCK%" 2>nul && goto acquire_lock_owned
if not defined DBM_ANNOUNCED >&2 echo dbm: waiting for another engine build to finish - engine\.build.lock...
set "DBM_ANNOUNCED=1"
call :lock_is_stale
if errorlevel 2 goto acquire_lock_age_unknown
if not errorlevel 1 goto acquire_lock_break
goto acquire_lock_sleep
:acquire_lock_age_unknown
if %DBM_WAITED% GEQ %DBM_WAIT_LIMIT% goto acquire_lock_break_by_wait
:acquire_lock_sleep
set /a DBM_WAITED+=1
set /a DBM_WAIT_MOD=DBM_WAITED %% 60
if %DBM_WAIT_MOD% EQU 0 >&2 echo dbm: still waiting for engine\.build.lock - at least %DBM_WAITED% s so far...
ping -n 2 127.0.0.1 >nul
goto acquire_lock_loop
:acquire_lock_break
>&2 echo dbm: removing engine\.build.lock, older than %DBM_LOCK_STALE_MINUTES% minutes - a build that crashed.
rd /s /q "%DBM_LOCK%" 2>nul
goto acquire_lock_loop
:acquire_lock_break_by_wait
>&2 echo dbm: removing engine\.build.lock after waiting %DBM_LOCK_STALE_MINUTES% minutes - its age cannot be read because powershell did not run.
rd /s /q "%DBM_LOCK%" 2>nul
set /a DBM_WAITED=0
goto acquire_lock_loop
:acquire_lock_owned
>"%DBM_LOCK%\owner" <nul set /p "=%DBM_LOCK_OWNER%"
exit /b 0

rem Exit 0 = the lock directory is at least DBM_LOCK_STALE_MINUTES old, 1 = it is younger (or already gone),
rem 2 = cannot tell. cmd has no minute-level file age of its own, so this asks powershell, whose "younger" answer is a
rem distinctive 3: a powershell that cannot start - 9009 when missing, 1 or another code when blocked - is never
rem mistaken for "younger".
:lock_is_stale
powershell -NoProfile -NonInteractive -Command "try { $age = ((Get-Date) - (Get-Item -LiteralPath $env:DBM_LOCK -Force -ErrorAction Stop).LastWriteTime).TotalMinutes; if ($age -ge [double]$env:DBM_LOCK_STALE_MINUTES) { exit 0 } } catch { }; exit 3" >nul 2>nul
set "DBM_PS_EXIT=%ERRORLEVEL%"
if "%DBM_PS_EXIT%"=="0" exit /b 0
if "%DBM_PS_EXIT%"=="3" exit /b 1
exit /b 2

:release_lock
set "DBM_LOCK_HOLDER="
if exist "%DBM_LOCK%\owner" set /p DBM_LOCK_HOLDER=<"%DBM_LOCK%\owner"
if "%DBM_LOCK_HOLDER%"=="%DBM_LOCK_OWNER%" rd /s /q "%DBM_LOCK%" 2>nul
exit /b 0

rem -o reaches MSBuild as a property value, where "," and ";" separate values (ruling 189, open item 41): DBM_TMP_ARG is
rem DBM_TMP with them escaped as %%2C and %%3B. Delayed expansion only inside, and only when there is something to escape,
rem so a path with ! is not touched.
:msbuild_path
set "DBM_TMP_ARG=%DBM_TMP%"
set "DBM_TMP_PLAIN=%DBM_TMP:,=%"
set "DBM_TMP_PLAIN=%DBM_TMP_PLAIN:;=%"
if "%DBM_TMP_PLAIN%"=="%DBM_TMP%" exit /b 0
setlocal EnableDelayedExpansion
set "DBM_X=!DBM_TMP:,=%%2C!"
set "DBM_X=!DBM_X:;=%%3B!"
for /f "delims=" %%p in ("!DBM_X!") do endlocal & set "DBM_TMP_ARG=%%p"
exit /b 0

rem Publishes into a per-process dist.tmp.N and swaps it in. Returns 0 with the new build in place, or sets
rem DBM_FAILURE and returns 1 with engine\dist as it was.
:build_engine
call :read_dist_version
if exist "%DBM_DLL%" if not "%DBM_REBUILD%"=="1" if defined DBM_PLUGIN_VERSION if "%DBM_DIST_VERSION%"=="%DBM_PLUGIN_VERSION%" exit /b 0
>&2 echo dbm: building the engine - %DBM_REASON% - about a minute...
if exist "%DBM_DLL%" dotnet "%DBM_DLL%" stop >nul 2>&1
call :sweep_abandoned
set "DBM_TMP=%DBM_ENGINE%\dist.tmp.%RANDOM%%RANDOM%"
if exist "%DBM_TMP%" rd /s /q "%DBM_TMP%"
rem Compile from scratch, as engine\release.* do: publish would otherwise reuse an earlier Release compile in obj\
rem together with its PDB, and the build would differ from the committed one.
if exist "%DBM_ENGINE%\Dbm\obj\Release" rd /s /q "%DBM_ENGINE%\Dbm\obj\Release"
if exist "%DBM_ENGINE%\Dbm\bin\Release" rd /s /q "%DBM_ENGINE%\Dbm\bin\Release"
rem Static web assets record each wwwroot file's last-write time as Last-Modified in Dbm.staticwebassets.endpoints.json,
rem so the time a checkout happened would reach engine\dist and git would show it modified (ruling 189, open item 42).
rem Pin it exactly as engine\release.* do. cmd cannot set a file time, so powershell does; where it cannot run, the
rem build still works and only that one file differs from the committed dist.
set "DBM_WWWROOT=%DBM_ENGINE%\Dbm\wwwroot"
powershell -NoProfile -NonInteractive -Command "$t = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc); Get-ChildItem -LiteralPath $env:DBM_WWWROOT -Recurse -File | ForEach-Object { $_.LastWriteTimeUtc = $t }" >nul 2>nul
set "DBM_BUILD_VERSION=%DBM_PLUGIN_VERSION%"
if not defined DBM_BUILD_VERSION set "DBM_BUILD_VERSION=0.0.0"
call :msbuild_path
dotnet publish "%DBM_ENGINE%\Dbm\Dbm.csproj" -c Release -o "%DBM_TMP_ARG%" --nologo -v q -p:Version=%DBM_BUILD_VERSION% -p:DebugType=none -p:UseAppHost=false 1>&2
if errorlevel 1 goto build_failed
call :prune_natives
<nul set /p "=%DBM_BUILD_VERSION%" >"%DBM_TMP%\VERSION"
set "DBM_OLD=%DBM_ENGINE%\dist.old.%RANDOM%%RANDOM%"
if exist "%DBM_ENGINE%\dist" move "%DBM_ENGINE%\dist" "%DBM_OLD%" >nul 2>&1 || goto build_in_use
move "%DBM_TMP%" "%DBM_ENGINE%\dist" >nul 2>&1 || goto build_no_swap
rd /s /q "%DBM_OLD%" 2>nul
exit /b 0

rem A publish that crashed (killed, power cut) leaves its dist.tmp.N (~30 MB) behind, and a swap that crashed its
rem dist.old.N; nothing else ever removes them. Runs under the build lock, so no other build is using one - except a build
rem whose lock was broken as stale, which is why only entries older than DBM_LOCK_STALE_MINUTES go. The ids are not all
rem process ids (here %%RANDOM%%, a GUID in release.ps1), so age is the one test every builder can apply. A dist.old.N is
rem kept while engine\dist is missing: then it may be the previous build a failed swap-back names. cmd has no file age,
rem so powershell does it; where powershell cannot run nothing is swept. Keep bin/dbm and engine\release.* in sync.
:sweep_abandoned
powershell -NoProfile -NonInteractive -Command "$dll = Join-Path $env:DBM_ENGINE 'dist\Dbm.dll'; Get-ChildItem -LiteralPath $env:DBM_ENGINE -Directory -Force | Where-Object { ($_.Name -like 'dist.tmp.*' -or ($_.Name -like 'dist.old.*' -and (Test-Path -LiteralPath $dll))) -and ((Get-Date) - $_.LastWriteTime).TotalMinutes -ge [double]$env:DBM_LOCK_STALE_MINUTES } | ForEach-Object { 'dbm: removing engine\' + $_.Name + ', left behind by a build that crashed.'; Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }" 1>&2 2>nul
exit /b 0

rem The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker - SqlClient's Entra
rem modes use MSAL's managed/browser path, not the broker - and runtimes for platforms this plugin does not target.
rem Removing them keeps the committed engine\dist near 30 MB. Keep engine\release.* in sync with this list.
:prune_natives
if not exist "%DBM_TMP%\runtimes" exit /b 0
for %%r in (browser browser-wasm maccatalyst-x64 maccatalyst-arm64 linux-armel linux-mips64 linux-ppc64le linux-s390x linux-musl-s390x linux-riscv64 linux-musl-riscv64 linux-x86) do if exist "%DBM_TMP%\runtimes\%%r" rd /s /q "%DBM_TMP%\runtimes\%%r"
for /r "%DBM_TMP%\runtimes" %%f in (*msalruntime*) do del /f /q "%%f" >nul 2>&1
exit /b 0

:build_failed
rd /s /q "%DBM_TMP%" 2>nul
set "DBM_FAILURE=engine build failed - see the messages above"
exit /b 1

:build_in_use
rd /s /q "%DBM_TMP%" 2>nul
set "DBM_FAILURE=engine\dist is in use by a running dbm server - run 'dbm stop' in that project folder to let the update through"
exit /b 1

rem The old build is already out of the way here, so put it back; claim it is back only when the move-back worked.
:build_no_swap
rd /s /q "%DBM_TMP%" 2>nul
set "DBM_FAILURE=engine\dist could not be replaced"
if not exist "%DBM_OLD%" exit /b 1
move "%DBM_OLD%" "%DBM_ENGINE%\dist" >nul 2>&1 && exit /b 1
set "DBM_FAILURE=engine\dist could not be replaced, and the previous build could not be put back: it is in %DBM_OLD% - rename it to dist"
exit /b 1
