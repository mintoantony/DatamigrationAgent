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
.PARAMETER Check
  Runs only the release gates (contents, machine path, PDB entry) against an existing build folder, for example
  plugins/db-migrate/engine/dist, and exits: nothing is built or replaced.
.EXAMPLE
  pwsh plugins/db-migrate/engine/release.ps1
.EXAMPLE
  pwsh plugins/db-migrate/engine/release.ps1 -Check plugins/db-migrate/engine/dist
#>
[CmdletBinding()]
param([switch]$SkipTests, [switch]$KeepAllRuntimes, [string]$Check)

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

# True when the file's bytes contain $Needle (case-insensitive). Latin-1 maps every byte to one char, and dropping the
# NULs makes UTF-16LE text (.NET user strings) searchable too, so one search covers both encodings.
function Test-BinaryContains([string]$File, [string]$Needle) {
    $text = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($File)).Replace("`0", '')
    return $text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

# The release gates, asserted rather than eyeballed, run against a build BEFORE it replaces engine/dist:
#  1. no symbol file, native host or settings file that could carry a connection string;
#  2. no file holds this checkout's absolute path (in either separator style): a builder's path must never ship, and
#     its presence means the bytes depend on where the checkout sits (Ruling 180);
#  3. Dbm.dll holds no ".pdb" at all: a CodeView debug entry, even a path-mapped one, means the compile was reused from
#     an earlier Release build with symbols instead of the clean DebugType=none compile this script makes.
function Assert-ReleaseGates([string]$Dir) {
    $all = @(Get-ChildItem -LiteralPath $Dir -Recurse -File)
    $forbidden = @($all | Where-Object {
            $ForbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -like 'appsettings.*.json'
        } | ForEach-Object { [IO.Path]::GetRelativePath($Dir, $_.FullName) })
    if ($forbidden.Count -gt 0) {
        throw ("the build must not contain symbol files, native hosts or environment settings, found: " + ($forbidden -join ', '))
    }
    Write-Host '  no .pdb, .exe, .user, .suo, .mdb or appsettings.*.json'

    $needles = @($repoRoot, ($repoRoot -replace '\\', '/')) | Select-Object -Unique
    $leaks = @(foreach ($file in $all) {
            foreach ($needle in $needles) {
                if (Test-BinaryContains $file.FullName $needle) { "$([IO.Path]::GetRelativePath($Dir, $file.FullName)) contains $needle" }
            }
        })
    if ($leaks.Count -gt 0) { throw ("the build carries this checkout's path: " + ($leaks -join '; ')) }
    Write-Host "  no file contains the checkout path ($repoRoot)"

    $dll = Join-Path $Dir 'Dbm.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "the build has no Dbm.dll: $dll" }
    if (Test-BinaryContains $dll '.pdb') {
        throw 'Dbm.dll has a PDB (CodeView) debug entry: it was not compiled from scratch with DebugType=none'
    }
    Write-Host '  Dbm.dll has no PDB (CodeView) entry'
}

if ($Check) {
    Write-Host "== release gates on $Check"
    Assert-ReleaseGates (Resolve-Path -LiteralPath $Check).Path
    Write-Host '  all release gates pass'
    exit 0
}
$manifest = Join-Path $pluginRoot '.claude-plugin/plugin.json'
$dist = Join-Path $engine 'dist'
$tmp = Join-Path $engine "dist.tmp.$PID"
$old = $null
# The same lock as bin/dbm and bin/dbm.cmd use, so a release and a launcher rebuild never publish at the same time.
$lock = Join-Path $engine '.build.lock'
$lockOwner = "release-ps1-$PID"
$LockStaleMinutes = 15

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

# Staleness by the lock's own age, never by how long we waited; only a lock this process owns is released.
function Enter-BuildLock {
    $announced = $false
    while ($true) {
        try {
            New-Item -ItemType Directory -Path $lock -ErrorAction Stop | Out-Null
            break
        }
        catch {
            if ($_.CategoryInfo.Category -ne 'ResourceExists') { throw }
        }
        if (-not $announced) {
            Write-Host 'release: waiting for another engine build to finish (engine/.build.lock)...'
            $announced = $true
        }
        $item = Get-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
        if ($item -and ((Get-Date) - $item.LastWriteTime).TotalMinutes -ge $LockStaleMinutes) {
            Write-Warning "removing engine/.build.lock, older than $LockStaleMinutes minutes (a build that crashed)"
            Remove-Item -LiteralPath $lock -Recurse -Force -ErrorAction SilentlyContinue
            continue
        }
        Start-Sleep -Seconds 1
    }
    Set-Content -LiteralPath (Join-Path $lock 'owner') -Value $lockOwner -NoNewline
}

function Exit-BuildLock {
    $ownerFile = Join-Path $lock 'owner'
    if ((Test-Path -LiteralPath $ownerFile) -and (Get-Content -Raw -LiteralPath $ownerFile) -eq $lockOwner) {
        Remove-Item -LiteralPath $lock -Recurse -Force -ErrorAction SilentlyContinue
    }
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
# XPath, not .Project.PropertyGroup.Version: the props file has more than one PropertyGroup (the Release one carries no
# Version), and StrictMode rejects member access on a group that lacks it.
$propsVersion = ([xml](Get-Content -Raw (Join-Path $engine 'Directory.Build.props'))).SelectSingleNode('/Project/PropertyGroup/Version').InnerText
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

Enter-BuildLock
try {
    if (Test-Path (Join-Path $dist 'Dbm.dll')) {
        & dotnet (Join-Path $dist 'Dbm.dll') stop *> $null   # best effort: frees the DLLs if a server runs from this dist
    }

    Write-Host '== publish'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    # Compile from scratch: publish would otherwise reuse a Release compile already in obj/ (this script's own
    # `dotnet test -c Release` makes one) together with its PDB, whatever DebugType is passed here.
    foreach ($stale in @('Dbm/obj/Release', 'Dbm/bin/Release')) {
        $path = Join-Path $engine $stale
        if (Test-Path -LiteralPath $path) { Remove-Item -Recurse -Force -LiteralPath $path }
    }
    Invoke-Checked 'dotnet publish' {
        dotnet publish (Join-Path $engine 'Dbm/Dbm.csproj') -c Release -o $tmp --nologo `
            "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false
    }
    if (-not $KeepAllRuntimes) { Remove-UnusedNatives $tmp }
    Set-Content -Path (Join-Path $tmp 'VERSION') -Value $version -NoNewline

    # The gates and the smoke test run against the new build BEFORE it replaces engine/dist, so a failed release leaves
    # dist as it was.
    Write-Host '== release gates'
    Assert-ReleaseGates $tmp

    Write-Host '== smoke test'
    $versionOut = (& dotnet (Join-Path $tmp 'Dbm.dll') version) -join ' '
    if ($LASTEXITCODE -ne 0 -or $versionOut -notmatch [regex]::Escape($version)) {
        throw "Smoke test failed: 'dbm version' printed: $versionOut"
    }
    Write-Host "  $versionOut"

    if (Test-Path $dist) {
        $old = Join-Path $engine ('dist.old.' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        try {
            Move-Item -LiteralPath $dist -Destination $old
        }
        catch {
            throw "engine/dist is in use by a running dbm server. Run 'dbm stop' in each project folder that uses this checkout, then retry."
        }
    }
    try {
        Move-Item -LiteralPath $tmp -Destination $dist
    }
    catch {
        # The old build is already out of the way, so put it back; claim it is back only when that worked.
        $why = $_.Exception.Message
        if ($old -and (Test-Path $old)) {
            try {
                Move-Item -LiteralPath $old -Destination $dist
            }
            catch {
                throw "engine/dist could not be replaced ($why), and the previous build could not be put back: it is in $old - rename it to dist."
            }
        }
        throw "engine/dist could not be replaced; the previous build is still in place. $why"
    }
    if ($old) { Remove-Item -Recurse -Force $old -ErrorAction SilentlyContinue }
}
finally {
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    Exit-BuildLock
}

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

Write-Host ''
Write-Host "Next: git add -A plugins/db-migrate/engine/dist && git commit -m 'build: release engine $version'" -ForegroundColor Cyan
Write-Host "      claude plugin tag plugins/db-migrate --push   (creates db-migrate--v$version)" -ForegroundColor Cyan
