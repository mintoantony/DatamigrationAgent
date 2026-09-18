#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
  Builds the committed engine/dist for the version in plugin.json.
.DESCRIPTION
  Checks that plugin.json, marketplace.json and Directory.Build.props agree on the version, runs the unit tests (and the
  node UI tests when node is installed), publishes Release into dist.tmp, swaps it into engine/dist with a VERSION file,
  smoke-tests it, validates the plugin with the claude CLI when available, and prints a size summary.

  WHEN TO RUN THIS: engine/dist is a COMMITTED BUILD OUTPUT, and nothing rebuilds it automatically. The launcher only
  rebuilds when engine/dist/VERSION differs from plugin.json - NOT when the source changed at the same version. So a
  committed dist can silently lag the committed source. Rebuild and commit engine/dist as the LAST STEP before any
  version bump and before any merge to main: change the source, then run this script, then commit dist in that order.
  If you merge engine source changes to main without re-running this, every user without a .NET SDK runs the old code.
.EXAMPLE
  pwsh plugins/db-migrate/engine/release.ps1
#>
[CmdletBinding()]
param([switch]$SkipTests, [switch]$KeepAllRuntimes)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker (SqlClient's Entra modes
# use MSAL's managed/browser path, not the broker) and runtimes for platforms this plugin does not target. Removing them
# keeps the committed engine/dist near 30 MB. -KeepAllRuntimes publishes everything. Keep bin/dbm(.cmd) in sync.
$PrunedRuntimes = @(
    'browser', 'browser-wasm', 'maccatalyst-x64', 'maccatalyst-arm64', 'linux-armel', 'linux-mips64',
    'linux-ppc64le', 'linux-s390x', 'linux-musl-s390x', 'linux-riscv64', 'linux-musl-riscv64', 'linux-x86')

# Nothing in a committed dist may be a symbol file, a native host or a settings file that could carry a connection
# string: dist is pushed to a public-facing repository and shipped to every user of the plugin.
$ForbiddenExtensions = @('.pdb', '.exe', '.user', '.suo', '.mdb')

function Remove-UnusedNatives([string]$Root) {
    $runtimes = Join-Path $Root 'runtimes'
    if (-not (Test-Path $runtimes)) { return }
    foreach ($rid in $PrunedRuntimes) {
        $dir = Join-Path $runtimes $rid
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }
    Get-ChildItem $runtimes -Recurse -File | Where-Object Name -like '*msalruntime*' | Remove-Item -Force
}

$engine = $PSScriptRoot
$pluginRoot = Split-Path -Parent $engine
$repoRoot = Split-Path -Parent (Split-Path -Parent $pluginRoot)
$manifest = Join-Path $pluginRoot '.claude-plugin/plugin.json'
$dist = Join-Path $engine 'dist'
$tmp = Join-Path $engine 'dist.tmp'
$old = $null

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

$version = (Get-Content -Raw $manifest | ConvertFrom-Json).version
if ([string]::IsNullOrWhiteSpace($version)) { throw "No version in $manifest" }
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "plugin.json version '$version' is not SemVer (x.y.z[-pre])" }
Write-Host "db-migrate release $version" -ForegroundColor Cyan

$marketplace = Join-Path $repoRoot '.claude-plugin/marketplace.json'
if (Test-Path $marketplace) {
    $entry = (Get-Content -Raw $marketplace | ConvertFrom-Json).plugins | Where-Object { $_.name -eq 'db-migrate' }
    if ($entry -and $entry.PSObject.Properties['version'] -and $entry.version -ne $version) {
        throw "marketplace.json lists db-migrate $($entry.version) but plugin.json says $version"
    }
}
$propsVersion = ([xml](Get-Content -Raw (Join-Path $engine 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($propsVersion -ne $version) { throw "Directory.Build.props has <Version>$propsVersion</Version> but plugin.json says $version" }

if (-not $SkipTests) {
    Write-Host '== unit tests'
    Invoke-Checked 'unit tests' { dotnet test (Join-Path $engine 'Dbm.Tests') -c Release --nologo --filter 'Category!=Integration' }
    $jsDir = Join-Path $engine 'Dbm.Tests/js'
    if ((Get-Command node -ErrorAction SilentlyContinue) -and (Test-Path $jsDir)) {
        $jsFiles = @(Get-ChildItem $jsDir -Filter '*.test.cjs' | ForEach-Object FullName)
        if ($jsFiles.Count -gt 0) {
            Write-Host '== ui tests'
            Invoke-Checked 'ui tests' { node --test @jsFiles }
        }
    }
}

if (Test-Path (Join-Path $dist 'Dbm.dll')) {
    & dotnet (Join-Path $dist 'Dbm.dll') stop *> $null   # best effort: frees the DLLs if a server runs from this dist
}

Write-Host '== publish'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Invoke-Checked 'dotnet publish' {
    dotnet publish (Join-Path $engine 'Dbm/Dbm.csproj') -c Release -o $tmp --nologo `
        "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false
}
if (-not $KeepAllRuntimes) { Remove-UnusedNatives $tmp }
Set-Content -Path (Join-Path $tmp 'VERSION') -Value $version -NoNewline

if (Test-Path $dist) {
    $old = Join-Path $engine ('dist.old.' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Move-Item -LiteralPath $dist -Destination $old
    }
    catch {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
        throw "engine/dist is in use by a running dbm server. Run 'dbm stop' in each project folder that uses this checkout, then retry."
    }
}
try {
    Move-Item -LiteralPath $tmp -Destination $dist
}
catch {
    # The old build is already out of the way, so put it back rather than leave the user with no engine at all.
    if ($old -and (Test-Path $old)) { Move-Item -LiteralPath $old -Destination $dist -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    throw "engine/dist could not be replaced; the previous build is still in place. $($_.Exception.Message)"
}
if ($old) { Remove-Item -Recurse -Force $old -ErrorAction SilentlyContinue }

Write-Host '== smoke test'
$versionOut = (& dotnet (Join-Path $dist 'Dbm.dll') version) -join ' '
if ($LASTEXITCODE -ne 0 -or $versionOut -notmatch [regex]::Escape($version)) {
    throw "Smoke test failed: 'dbm version' printed: $versionOut"
}
Write-Host "  $versionOut"

if (Get-Command claude -ErrorAction SilentlyContinue) {
    Write-Host '== plugin validation'
    Invoke-Checked 'plugin validate' { claude plugin validate $pluginRoot }
    Invoke-Checked 'marketplace validate' { claude plugin validate $repoRoot }
}
else {
    Write-Warning "'claude' is not on PATH; skipped 'claude plugin validate'."
}

Write-Host '== size summary'
$files = @(Get-ChildItem $dist -Recurse -File)
$total = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ('  {0} files, {1:N1} MB in {2}' -f $files.Count, ($total / 1MB), $dist)
$files | Sort-Object Length -Descending | Select-Object -First 8 | ForEach-Object {
    Write-Host ('  {0,9:N0} KB  {1}' -f ($_.Length / 1KB), [IO.Path]::GetRelativePath($dist, $_.FullName))
}
if ($total -gt 60MB) { Write-Warning 'engine/dist is larger than 60 MB; check for unexpected dependencies before committing.' }

# Asserted, not eyeballed: this is the last gate before these bytes are committed and shipped.
$forbidden = @($files | Where-Object {
        $ForbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -like 'appsettings.*.json'
    } | ForEach-Object { [IO.Path]::GetRelativePath($dist, $_.FullName) })
if ($forbidden.Count -gt 0) {
    throw ("engine/dist must not contain symbol files, native hosts or environment settings, found: " + ($forbidden -join ', '))
}
Write-Host '  no .pdb, .exe, .user, .suo or appsettings.*.json'

Write-Host ''
Write-Host "Next: git add -A plugins/db-migrate/engine/dist && git commit -m 'build: release engine $version'" -ForegroundColor Cyan
Write-Host "      claude plugin tag plugins/db-migrate --push   (creates db-migrate--v$version)" -ForegroundColor Cyan
