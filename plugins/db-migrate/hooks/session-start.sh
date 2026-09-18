#!/bin/sh
# SessionStart hook: runs `dbm doctor --quiet` through the launcher. Silent when everything is healthy. Otherwise it
# prints one JSON object that shows the problems to the user (systemMessage) and gives them to Claude
# (additionalContext). Both the launcher's own messages (runtime missing, engine not built, a rebuild that fell back
# to the previous build) and doctor's failing checks are included. It always exits 0: a failing check is something
# to report, and a non-zero exit would shrink it to a bare "hook error" notice that Claude never sees.

out=$(sh "$(dirname -- "$0")/../bin/dbm" doctor --quiet 2>&1) || true
[ -n "$out" ] || exit 0

# JSON string escaping in portable sh: drop CR and other control characters, turn tabs into spaces, escape
# backslashes (Windows paths) and double quotes, then join the lines with a literal \n.
text=$(printf '%s\n' "$out" | tr '\t' ' ' | tr -d '\000-\010\013-\037' |
  sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | awk 'NR > 1 { printf "%s", "\\n" } { printf "%s", $0 }')

printf '{"systemMessage":"db-migrate: %s","hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"db-migrate health check at session start:\\n%s"}}\n' "$text" "$text"
exit 0
