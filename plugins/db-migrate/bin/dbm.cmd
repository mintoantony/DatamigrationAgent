@echo off
rem db-migrate launcher (Windows cmd / PowerShell).
rem Runs engine\dist\Dbm.dll with the installed .NET runtime; builds it once when missing (needs the .NET SDK).
rem Exit codes: 3 = runtime missing, 4 = engine not built / build failed; otherwise the engine's own exit code.
setlocal EnableExtensions DisableDelayedExpansion

set "DBM_ENGINE=%~dp0..\engine"
set "DBM_DLL=%DBM_ENGINE%\dist\Dbm.dll"

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

set "DBM_NEED_BUILD="
if not exist "%DBM_DLL%" set "DBM_NEED_BUILD=1"
if "%DBM_REBUILD%"=="1" set "DBM_NEED_BUILD=1"
if not defined DBM_NEED_BUILD goto run

dotnet --list-sdks 2>nul | findstr /r "." >nul
if errorlevel 1 (
  >&2 echo dbm: engine not built - %DBM_DLL% is missing and no .NET SDK is installed to build it.
  >&2 echo      Install the .NET 8 SDK or use a release of the plugin that ships engine\dist.
  exit /b 4
)
>&2 echo dbm: building the engine - first run, about a minute...
dotnet publish "%DBM_ENGINE%\Dbm\Dbm.csproj" -c Release -o "%DBM_ENGINE%\dist" --nologo -v q 1>&2
if errorlevel 1 (
  >&2 echo dbm: engine build failed - see the messages above.
  exit /b 4
)

:run
dotnet "%DBM_DLL%" %*
exit /b %ERRORLEVEL%

:consider
for /f "tokens=1 delims=." %%m in ("%~1") do if %%m GTR %DBM_ASPNET_MAJOR% set "DBM_ASPNET_MAJOR=%%m"
exit /b 0
