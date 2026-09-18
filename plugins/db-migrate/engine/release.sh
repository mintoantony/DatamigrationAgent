#!/bin/sh
# Builds the committed engine/dist for the version in plugin.json (the POSIX twin of release.ps1).
# Usage: sh plugins/db-migrate/engine/release.sh [--skip-tests] [--keep-all-runtimes]
#        sh plugins/db-migrate/engine/release.sh --check=<build folder>   (release gates only; nothing is built)
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
check=""
for arg in "$@"; do
  case "$arg" in
    --skip-tests) skip_tests=1 ;;
    --keep-all-runtimes) keep_all_runtimes=1 ;;
    --check=*) check=${arg#--check=} ;;
    *) echo "usage: release.sh [--skip-tests] [--keep-all-runtimes] | --check=<build folder>" >&2; exit 1 ;;
  esac
done

die() {
  echo "release: $*" >&2
  exit 1
}

# True when the file's bytes contain $2 (case-insensitive). Dropping the NUL bytes makes UTF-16LE text (.NET user
# strings) searchable as plain ASCII, so one search covers both encodings.
binary_contains() {
  tr -d '\000' < "$1" | LC_ALL=C grep -q -i -F -- "$2"
}

# This checkout's absolute path in every form a build on this machine could have recorded it.
checkout_paths() {
  printf '%s\n' "$repo_root"
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$repo_root"
    cygpath -m "$repo_root"
  fi
}

# The release gates, asserted rather than eyeballed, run against a build BEFORE it replaces engine/dist:
#  1. no symbol file, native host or settings file that could carry a connection string;
#  2. no file holds this checkout's absolute path: a builder's path must never ship, and its presence means the bytes
#     depend on where the checkout sits (Ruling 180);
#  3. Dbm.dll holds no ".pdb" at all: a CodeView debug entry, even a path-mapped one, means the compile was reused from
#     an earlier Release build with symbols instead of the clean DebugType=none compile this script makes.
check_build() {
  dir=$1
  forbidden=$(find "$dir" -type f \( -name '*.pdb' -o -name '*.exe' -o -name '*.user' -o -name '*.suo' \
    -o -name '*.mdb' -o -name 'appsettings.*.json' \) | sed "s|^$dir/||" | sort | tr '\n' ' ')
  [ -z "$forbidden" ] || die "the build must not contain symbol files, native hosts or environment settings, found: $forbidden"
  echo "  no .pdb, .exe, .user, .suo, .mdb or appsettings.*.json"

  needles=$(checkout_paths)
  leaks=$(find "$dir" -type f | while IFS= read -r file; do
    printf '%s\n' "$needles" | while IFS= read -r needle; do
      if [ -n "$needle" ] && binary_contains "$file" "$needle"; then printf '%s contains %s; ' "${file#"$dir"/}" "$needle"; fi
    done
  done; true)
  [ -z "$leaks" ] || die "the build carries this checkout's path: $leaks"
  echo "  no file contains the checkout path ($repo_root)"

  [ -f "$dir/Dbm.dll" ] || die "the build has no Dbm.dll: $dir/Dbm.dll"
  ! binary_contains "$dir/Dbm.dll" ".pdb" || die "Dbm.dll has a PDB (CodeView) debug entry: it was not compiled from scratch with DebugType=none"
  echo "  Dbm.dll has no PDB (CodeView) entry"
}

if [ -n "$check" ]; then
  [ -d "$check" ] || die "no such build folder: $check"
  echo "== release gates on $check"
  check_build "$(CDPATH= cd -- "$check" && pwd)"
  echo "  all release gates pass"
  exit 0
fi

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

# Removes the dist.tmp.<id> (~30 MB) of a publish that crashed and the dist.old.<id> of a swap that crashed. Under the
# build lock, and only entries older than the lock's stale age, for the reasons given in bin/dbm's sweep_abandoned; a
# dist.old.<id> is kept while engine/dist is missing. Keep bin/dbm, bin/dbm.cmd and release.ps1 in sync with this.
sweep_abandoned() {
  find "$engine" -maxdepth 1 -type d \( -name 'dist.tmp.*' -o -name 'dist.old.*' \) -mmin +"$lock_stale_minutes" 2>/dev/null |
    while IFS= read -r leftover; do
      case "$leftover" in
        */dist.old.*) [ -f "$dist/Dbm.dll" ] || continue ;;
      esac
      echo "release: removing engine/${leftover##*/}, left behind by a build that crashed." >&2
      rm -rf "$leftover" 2>/dev/null || true
    done
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
sweep_abandoned

if [ -f "$dist/Dbm.dll" ]; then dotnet "$dist/Dbm.dll" stop >/dev/null 2>&1 || true; fi

echo "== publish"
rm -rf "$tmp"
# Compile from scratch: publish would otherwise reuse a Release compile already in obj/ (this script's own
# `dotnet test -c Release` makes one) together with its PDB, whatever DebugType is passed here.
rm -rf "$engine/Dbm/obj/Release" "$engine/Dbm/bin/Release"
# Static web assets record each wwwroot file's last-write time as Last-Modified in Dbm.staticwebassets.endpoints.json,
# so the time a checkout happened would reach the committed dist (ruling 189). Pin it.
find "$engine/Dbm/wwwroot" -type f -exec env TZ=UTC touch -t 200001010000.00 {} +
# -o reaches MSBuild as a property value, where ',' and ';' separate values (ruling 189): escape them, and '%' first.
# sed, not ${var//x/y}: that is bash-only and dies with "Bad substitution" where /bin/sh is dash (Debian, Ubuntu).
tmp_arg=$(printf '%s' "$tmp" | sed 's/%/%25/g; s/,/%2C/g; s/;/%3B/g')
dotnet publish "$engine/Dbm/Dbm.csproj" -c Release -o "$tmp_arg" --nologo \
  "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false || die "dotnet publish failed"
[ "$keep_all_runtimes" = 1 ] || prune_natives "$tmp"
printf '%s' "$version" > "$tmp/VERSION"

# The gates and the smoke test run against the new build BEFORE it replaces engine/dist, so a failed release leaves
# dist as it was.
echo "== release gates"
check_build "$tmp"

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
