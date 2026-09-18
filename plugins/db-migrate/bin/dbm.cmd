@echo off
rem db-migrate launcher (Windows cmd / PowerShell).
rem Runs engine\dist\Dbm.dll with the installed .NET runtime; builds it when missing, when DBM_REBUILD=1, or when
rem engine\dist\VERSION no longer matches plugin.json (needs the .NET SDK).
rem Exit codes: 3 = runtime missing, 4 = engine not built / build failed; otherwise the engine's own exit code.
setlocal EnableExtensions DisableDelayedExpansion

set "DBM_ENGINE=%~dp0..\engine"
set "DBM_DLL=%DBM_ENGINE%\dist\Dbm.dll"
set "DBM_MANIFEST=%~dp0..\.claude-plugin\plugin.json"
set "DBM_LOCK=%DBM_ENGINE%\.build.lock"

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
if errorlevel 1 (
  rem A stale dist without an SDK still runs; `dbm doctor` reports the mismatch.
  if exist "%DBM_DLL%" goto run
  >&2 echo dbm: engine not built - %DBM_DLL% is missing and no .NET SDK is installed to build it.
  >&2 echo      Install the .NET 8 SDK or use a release of the plugin that ships engine\dist.
  exit /b 4
)

call :acquire_lock
call :build_engine
set "DBM_BUILD_EXIT=%ERRORLEVEL%"
rd "%DBM_LOCK%" 2>nul
if not "%DBM_BUILD_EXIT%"=="0" exit /b 4

:run
dotnet "%DBM_DLL%" %*
exit /b %ERRORLEVEL%

:consider
for /f "tokens=1 delims=." %%m in ("%~1") do if %%m GTR %DBM_ASPNET_MAJOR% set "DBM_ASPNET_MAJOR=%%m"
exit /b 0

:read_dist_version
set "DBM_DIST_VERSION="
if exist "%DBM_ENGINE%\dist\VERSION" set /p DBM_DIST_VERSION=<"%DBM_ENGINE%\dist\VERSION"
exit /b 0

:acquire_lock
set /a DBM_WAITED=0
:acquire_lock_loop
mkdir "%DBM_LOCK%" 2>nul && exit /b 0
set /a DBM_WAITED+=1
if %DBM_WAITED% GEQ 180 rd /s /q "%DBM_LOCK%" 2>nul & set /a DBM_WAITED=0
ping -n 2 127.0.0.1 >nul
goto acquire_lock_loop

rem Publishes into dist.tmp and swaps it in, so a failed or locked build always leaves a working engine\dist behind.
:build_engine
call :read_dist_version
if exist "%DBM_DLL%" if not "%DBM_REBUILD%"=="1" if defined DBM_PLUGIN_VERSION if "%DBM_DIST_VERSION%"=="%DBM_PLUGIN_VERSION%" exit /b 0
>&2 echo dbm: building the engine - %DBM_REASON% - about a minute...
if exist "%DBM_DLL%" dotnet "%DBM_DLL%" stop >nul 2>&1
if exist "%DBM_ENGINE%\dist.tmp" rd /s /q "%DBM_ENGINE%\dist.tmp"
set "DBM_BUILD_VERSION=%DBM_PLUGIN_VERSION%"
if not defined DBM_BUILD_VERSION set "DBM_BUILD_VERSION=0.0.0"
dotnet publish "%DBM_ENGINE%\Dbm\Dbm.csproj" -c Release -o "%DBM_ENGINE%\dist.tmp" --nologo -v q -p:Version=%DBM_BUILD_VERSION% -p:DebugType=none -p:UseAppHost=false 1>&2
if errorlevel 1 (
  rd /s /q "%DBM_ENGINE%\dist.tmp" 2>nul
  >&2 echo dbm: engine build failed - see the messages above.
  exit /b 1
)
call :prune_natives
<nul set /p "=%DBM_BUILD_VERSION%" >"%DBM_ENGINE%\dist.tmp\VERSION"
set "DBM_OLD=%DBM_ENGINE%\dist.old.%RANDOM%%RANDOM%"
if exist "%DBM_ENGINE%\dist" move "%DBM_ENGINE%\dist" "%DBM_OLD%" >nul 2>&1 || goto build_in_use
move "%DBM_ENGINE%\dist.tmp" "%DBM_ENGINE%\dist" >nul 2>&1 || goto build_no_swap
rd /s /q "%DBM_OLD%" 2>nul
exit /b 0

rem The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker - SqlClient's Entra
rem modes use MSAL's managed/browser path, not the broker - and runtimes for platforms this plugin does not target.
rem Removing them keeps the committed engine\dist near 30 MB. Keep engine\release.* in sync with this list.
:prune_natives
if not exist "%DBM_ENGINE%\dist.tmp\runtimes" exit /b 0
for %%r in (browser browser-wasm maccatalyst-x64 maccatalyst-arm64 linux-armel linux-mips64 linux-ppc64le linux-s390x linux-musl-s390x linux-riscv64 linux-musl-riscv64 linux-x86) do if exist "%DBM_ENGINE%\dist.tmp\runtimes\%%r" rd /s /q "%DBM_ENGINE%\dist.tmp\runtimes\%%r"
for /r "%DBM_ENGINE%\dist.tmp\runtimes" %%f in (*msalruntime*) do del /f /q "%%f" >nul 2>&1
exit /b 0

:build_in_use
rd /s /q "%DBM_ENGINE%\dist.tmp" 2>nul
>&2 echo dbm: engine\dist is in use by a running dbm server - run "dbm stop" in that project folder and retry.
exit /b 1

:build_no_swap
if exist "%DBM_OLD%" move "%DBM_OLD%" "%DBM_ENGINE%\dist" >nul 2>&1
rd /s /q "%DBM_ENGINE%\dist.tmp" 2>nul
>&2 echo dbm: engine\dist could not be replaced.
exit /b 1
