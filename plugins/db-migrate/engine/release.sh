#!/bin/sh
# Builds the committed engine/dist for the version in plugin.json (the POSIX twin of release.ps1).
# Usage: sh plugins/db-migrate/engine/release.sh [--skip-tests] [--keep-all-runtimes]
#
# WHEN TO RUN THIS: engine/dist is a COMMITTED BUILD OUTPUT, and nothing rebuilds it automatically. The launcher only
# rebuilds when engine/dist/VERSION differs from plugin.json - NOT when the source changed at the same version. So a
# committed dist can silently lag the committed source. Rebuild and commit engine/dist as the LAST STEP before any
# version bump and before any merge to main: change the source, then run this script, then commit dist in that order.
# If you merge engine source changes to main without re-running this, every user without a .NET SDK runs the old code.
set -eu

engine=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
plugin_root=$(dirname -- "$engine")
repo_root=$(dirname -- "$(dirname -- "$plugin_root")")
manifest="$plugin_root/.claude-plugin/plugin.json"
marketplace="$repo_root/.claude-plugin/marketplace.json"
dist="$engine/dist"
tmp="$engine/dist.tmp.$$"
old="$engine/dist.old.$$"
# The same lock as bin/dbm and bin/dbm.cmd use, so a release and a launcher rebuild never publish at the same time.
lock="$engine/.build.lock"
lock_owner="release-sh-$$"
lock_stale_minutes=15
skip_tests=0
keep_all_runtimes=0
for arg in "$@"; do
  case "$arg" in
    --skip-tests) skip_tests=1 ;;
    --keep-all-runtimes) keep_all_runtimes=1 ;;
    *) echo "usage: release.sh [--skip-tests] [--keep-all-runtimes]" >&2; exit 1 ;;
  esac
done

die() {
  echo "release: $*" >&2
  exit 1
}

# The publish output carries ~75 MB of native files db-migrate never loads: MSAL's WAM broker (SqlClient's Entra modes
# use MSAL's managed/browser path, not the broker) and runtimes for platforms this plugin does not target. Removing them
# keeps the committed engine/dist near 30 MB. --keep-all-runtimes publishes everything. Keep bin/dbm(.cmd) in sync.
prune_natives() {
  target="$1/runtimes"
  [ -d "$target" ] || return 0
  for rid in browser browser-wasm maccatalyst-x64 maccatalyst-arm64 linux-armel linux-mips64 linux-ppc64le \
    linux-s390x linux-musl-s390x linux-riscv64 linux-musl-riscv64 linux-x86; do
    rm -rf "$target/$rid"
  done
  find "$target" -type f -name '*msalruntime*' -delete
}

# Staleness by the lock's own age, and only a lock this process owns is released (see bin/dbm).
acquire_lock() {
  announced=0
  while ! mkdir "$lock" 2>/dev/null; do
    if [ "$announced" = 0 ]; then
      echo "release: waiting for another engine build to finish (engine/.build.lock)..." >&2
      announced=1
    fi
    if [ -n "$(find "$lock" -maxdepth 0 -mmin +"$lock_stale_minutes" 2>/dev/null)" ]; then
      echo "release: removing engine/.build.lock, older than $lock_stale_minutes minutes (a build that crashed)." >&2
      rm -rf "$lock"
      continue
    fi
    sleep 1
  done
  printf '%s' "$lock_owner" > "$lock/owner"
}

release_lock() {
  if [ "$(cat "$lock/owner" 2>/dev/null || true)" = "$lock_owner" ]; then rm -rf "$lock"; fi
}

# The version inside the db-migrate entry of marketplace.json; empty when the entry has no version field.
marketplace_version() {
  if command -v node >/dev/null 2>&1; then
    node -e 'const m = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
const p = (m.plugins || []).find(x => x && x.name === "db-migrate");
process.stdout.write(p && typeof p.version === "string" ? p.version : "");' "$1"
  else
    # Without node: the first "version" after a "name": "db-migrate", reset at every closing brace, so the
    # marketplace's own top-level name (also db-migrate) never pairs with a later entry's version.
    tr -d '\r' < "$1" | awk '
      /"name"[[:space:]]*:[[:space:]]*"db-migrate"/ { inside = 1 }
      inside && /"version"[[:space:]]*:/ { sub(/.*"version"[[:space:]]*:[[:space:]]*"/, ""); sub(/".*/, ""); print; exit }
      inside && /}/ { inside = 0 }'
  fi
}

version=$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
[ -n "$version" ] || die "no version in $manifest"
echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' || die "plugin.json version '$version' is not SemVer (x.y.z[-pre])"
echo "db-migrate release $version"

if [ -f "$marketplace" ]; then
  mp_version=$(marketplace_version "$marketplace") || die "cannot read $marketplace"
  [ -z "$mp_version" ] || [ "$mp_version" = "$version" ] || die "marketplace.json lists db-migrate $mp_version but plugin.json says $version"
fi
props_version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$engine/Directory.Build.props" | head -n 1)
[ "$props_version" = "$version" ] || die "Directory.Build.props has <Version>$props_version</Version> but plugin.json says $version"

if [ "$skip_tests" = 0 ]; then
  echo "== unit tests"
  dotnet test "$engine/Dbm.Tests" -c Release --nologo --filter "Category!=Integration" || die "unit tests failed"
  if command -v node >/dev/null 2>&1 && ls "$engine"/Dbm.Tests/js/*.test.cjs >/dev/null 2>&1; then
    echo "== ui tests"
    node --test "$engine"/Dbm.Tests/js/*.test.cjs || die "ui tests failed"
  fi
fi

acquire_lock
trap 'rm -rf "$tmp"; release_lock' EXIT
trap 'exit 130' INT TERM

if [ -f "$dist/Dbm.dll" ]; then dotnet "$dist/Dbm.dll" stop >/dev/null 2>&1 || true; fi

echo "== publish"
rm -rf "$tmp"
dotnet publish "$engine/Dbm/Dbm.csproj" -c Release -o "$tmp" --nologo \
  "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false || die "dotnet publish failed"
[ "$keep_all_runtimes" = 1 ] || prune_natives "$tmp"
printf '%s' "$version" > "$tmp/VERSION"

# Both gates run against the new build BEFORE it replaces engine/dist, so a failed release leaves dist as it was.
# Asserted, not eyeballed: nothing in a committed dist may be a symbol file, a native host or a settings file that
# could carry a connection string.
echo "== contents check"
forbidden=$(find "$tmp" -type f \( -name '*.pdb' -o -name '*.exe' -o -name '*.user' -o -name '*.suo' \
  -o -name '*.mdb' -o -name 'appsettings.*.json' \) | sed "s|^$tmp/||" | sort | tr '\n' ' ')
[ -z "$forbidden" ] || die "the build must not contain symbol files, native hosts or environment settings, found: $forbidden"
echo "  no .pdb, .exe, .user, .suo, .mdb or appsettings.*.json"

echo "== smoke test"
version_out=$(dotnet "$tmp/Dbm.dll" version) || die "dbm version failed"
case "$version_out" in
  *"$version"*) echo "  $version_out" ;;
  *) die "smoke test failed: dbm version printed: $version_out" ;;
esac

if [ -d "$dist" ]; then
  mv "$dist" "$old" 2>/dev/null || die "engine/dist is in use by a running dbm server; run 'dbm stop' in each project folder, then retry"
fi
# The old build is already out of the way here, so a failing swap must put it back rather than leave no engine at all.
if ! mv "$tmp" "$dist" 2>/dev/null; then
  if [ -d "$old" ] && ! mv "$old" "$dist" 2>/dev/null; then
    die "engine/dist could not be replaced, and the previous build could not be put back: it is in $old - rename it to dist"
  fi
  die "engine/dist could not be replaced; the previous build is still in place"
fi
rm -rf "$old" 2>/dev/null || true
release_lock

if command -v claude >/dev/null 2>&1; then
  echo "== plugin validation"
  claude plugin validate "$plugin_root" || die "plugin validate failed"
  claude plugin validate "$repo_root" || die "marketplace validate failed"
else
  echo "warning: 'claude' is not on PATH; skipped 'claude plugin validate'" >&2
fi

echo "== size summary"
files=$(find "$dist" -type f | wc -l | tr -d ' ')
kb=$(du -sk "$dist" | cut -f1)
echo "  $files files, $((kb / 1024)) MB in $dist"
find "$dist" -type f -exec du -k {} + | sort -n -r | head -n 8 | while IFS="$(printf '\t')" read -r size path; do
  printf '  %9s KB  %s\n' "$size" "${path#"$dist"/}"
done
[ "$kb" -le 61440 ] || echo "warning: engine/dist is larger than 60 MB; check for unexpected dependencies before committing" >&2

echo
echo "Next: git add -A plugins/db-migrate/engine/dist && git commit -m 'build: release engine $version'"
echo "      claude plugin tag plugins/db-migrate --push   (creates db-migrate--v$version)"
