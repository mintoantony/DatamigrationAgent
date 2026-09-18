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
:acquire_lock
set "DBM_ANNOUNCED="
:acquire_lock_loop
mkdir "%DBM_LOCK%" 2>nul && goto acquire_lock_owned
if not defined DBM_ANNOUNCED >&2 echo dbm: waiting for another engine build to finish - engine\.build.lock...
set "DBM_ANNOUNCED=1"
call :lock_is_stale && goto acquire_lock_break
ping -n 2 127.0.0.1 >nul
goto acquire_lock_loop
:acquire_lock_break
>&2 echo dbm: removing engine\.build.lock, older than %DBM_LOCK_STALE_MINUTES% minutes - a build that crashed.
rd /s /q "%DBM_LOCK%" 2>nul
goto acquire_lock_loop
:acquire_lock_owned
>"%DBM_LOCK%\owner" <nul set /p "=%DBM_LOCK_OWNER%"
exit /b 0

rem Exit 0 when the lock directory is at least DBM_LOCK_STALE_MINUTES old; cmd has no minute-level file age of its own.
:lock_is_stale
powershell -NoProfile -NonInteractive -Command "try { $age = ((Get-Date) - (Get-Item -LiteralPath $env:DBM_LOCK -Force -ErrorAction Stop).LastWriteTime).TotalMinutes; if ($age -ge [double]$env:DBM_LOCK_STALE_MINUTES) { exit 0 } } catch { }; exit 1" >nul 2>nul
exit /b %ERRORLEVEL%

:release_lock
set "DBM_LOCK_HOLDER="
if exist "%DBM_LOCK%\owner" set /p DBM_LOCK_HOLDER=<"%DBM_LOCK%\owner"
if "%DBM_LOCK_HOLDER%"=="%DBM_LOCK_OWNER%" rd /s /q "%DBM_LOCK%" 2>nul
exit /b 0

rem Publishes into a per-process dist.tmp.N and swaps it in. Returns 0 with the new build in place, or sets
rem DBM_FAILURE and returns 1 with engine\dist as it was.
:build_engine
call :read_dist_version
if exist "%DBM_DLL%" if not "%DBM_REBUILD%"=="1" if defined DBM_PLUGIN_VERSION if "%DBM_DIST_VERSION%"=="%DBM_PLUGIN_VERSION%" exit /b 0
>&2 echo dbm: building the engine - %DBM_REASON% - about a minute...
if exist "%DBM_DLL%" dotnet "%DBM_DLL%" stop >nul 2>&1
set "DBM_TMP=%DBM_ENGINE%\dist.tmp.%RANDOM%%RANDOM%"
if exist "%DBM_TMP%" rd /s /q "%DBM_TMP%"
set "DBM_BUILD_VERSION=%DBM_PLUGIN_VERSION%"
if not defined DBM_BUILD_VERSION set "DBM_BUILD_VERSION=0.0.0"
dotnet publish "%DBM_ENGINE%\Dbm\Dbm.csproj" -c Release -o "%DBM_TMP%" --nologo -v q -p:Version=%DBM_BUILD_VERSION% -p:DebugType=none -p:UseAppHost=false 1>&2
if errorlevel 1 goto build_failed
call :prune_natives
<nul set /p "=%DBM_BUILD_VERSION%" >"%DBM_TMP%\VERSION"
set "DBM_OLD=%DBM_ENGINE%\dist.old.%RANDOM%%RANDOM%"
if exist "%DBM_ENGINE%\dist" move "%DBM_ENGINE%\dist" "%DBM_OLD%" >nul 2>&1 || goto build_in_use
move "%DBM_TMP%" "%DBM_ENGINE%\dist" >nul 2>&1 || goto build_no_swap
rd /s /q "%DBM_OLD%" 2>nul
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
