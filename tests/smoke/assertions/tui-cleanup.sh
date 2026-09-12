#!/usr/bin/env bash
# tui-cleanup.tape post-tape assertion.
#
# The tape's own Wait+Screen anchors are the primary regression
# detector — if the alt screen corrupts during arrow navigation, the
# row anchors stop matching and the tape times out. This script
# confirms the seeded providers survived the TUI round-trip.
#
# See provider-add.sh for why doctor is not run here.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "tui-cleanup: checking seeded providers persisted across TUI exit..."
list_output="$("$NETCLAW_SMOKE_CLI" provider list 2>/dev/null | tr -d '\r')"

for name in seed-a seed-b; do
  if ! echo "$list_output" | grep -qE "^${name}[[:space:]]+OpenAI-compatible"; then
    echo "FAIL: provider '$name' missing from list after TUI exit." >&2
    assert_fail=1
  else
    echo "  ok  '$name' still present"
  fi
done

if (( assert_fail )); then
  printf -- '--- provider list ---\n%s\n' "$list_output" >&2
  exit 1
fi

echo "tui-cleanup: assertions passed."
