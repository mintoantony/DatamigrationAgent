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
dist="$engine/dist"
tmp="$engine/dist.tmp"
old="$engine/dist.old.$$"
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

version=$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)
[ -n "$version" ] || die "no version in $manifest"
echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' || die "plugin.json version '$version' is not SemVer (x.y.z[-pre])"
echo "db-migrate release $version"

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

if [ -f "$dist/Dbm.dll" ]; then dotnet "$dist/Dbm.dll" stop >/dev/null 2>&1 || true; fi

echo "== publish"
rm -rf "$tmp"
dotnet publish "$engine/Dbm/Dbm.csproj" -c Release -o "$tmp" --nologo \
  "-p:Version=$version" -p:DebugType=none -p:UseAppHost=false || die "dotnet publish failed"
[ "$keep_all_runtimes" = 1 ] || prune_natives "$tmp"
printf '%s' "$version" > "$tmp/VERSION"

if [ -d "$dist" ]; then
  mv "$dist" "$old" 2>/dev/null || { rm -rf "$tmp"; die "engine/dist is in use by a running dbm server; run 'dbm stop' in each project folder, then retry"; }
fi
# The old build is already out of the way here, so a failing swap must put it back rather than leave no engine at all.
if ! mv "$tmp" "$dist" 2>/dev/null; then
  if [ -d "$old" ]; then mv "$old" "$dist" 2>/dev/null || true; fi
  rm -rf "$tmp"
  die "engine/dist could not be replaced; the previous build is still in place"
fi
rm -rf "$old" 2>/dev/null || true

echo "== smoke test"
version_out=$(dotnet "$dist/Dbm.dll" version) || die "dbm version failed"
case "$version_out" in
  *"$version"*) echo "  $version_out" ;;
  *) die "smoke test failed: dbm version printed: $version_out" ;;
esac

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

# Asserted, not eyeballed: this is the last gate before these bytes are committed and shipped. Nothing in a committed
# dist may be a symbol file, a native host or a settings file that could carry a connection string.
forbidden=$(find "$dist" -type f \( -name '*.pdb' -o -name '*.exe' -o -name '*.user' -o -name '*.suo' \
  -o -name '*.mdb' -o -name 'appsettings.*.json' \) | sed "s|^$dist/||" | sort | tr '\n' ' ')
[ -z "$forbidden" ] || die "engine/dist must not contain symbol files, native hosts or environment settings, found: $forbidden"
echo "  no .pdb, .exe, .user, .suo or appsettings.*.json"

echo
echo "Next: git add -A plugins/db-migrate/engine/dist && git commit -m 'build: release engine $version'"
echo "      claude plugin tag plugins/db-migrate --push   (creates db-migrate--v$version)"
