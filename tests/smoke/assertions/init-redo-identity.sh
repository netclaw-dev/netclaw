#!/usr/bin/env bash
# init-redo-identity.tape post-tape assertion.
#
# Two invariants of the "Redo identity setup" flow:
#   1. It finalizes past the timezone submit. WriteIdentityFiles only runs after
#      the final identity sub-step, so SOUL.md / TOOLING.md existing proves the
#      timezone loop did not recur (the tape would otherwise hang on the
#      "Identity updated" anchor and vhs would exit non-zero).
#   2. It rewrites ONLY the identity files and the Identity section of netclaw.json —
#      it must never call WriteConfig, so nothing else may appear in the seeded
#      netclaw.json (the empty object). configVersion is stamped on every config save.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0
TOOLING_PATH="${NETCLAW_HOME}/identity/TOOLING.md"

echo "init-redo-identity: checking identity files were written..."
if [[ ! -s "$SOUL_PATH" ]]; then
  echo "FAIL: ${SOUL_PATH} missing or empty — redo never finalized past the timezone step." >&2
  assert_fail=1
else
  echo "  ok  SOUL.md written"
fi
if [[ ! -s "$TOOLING_PATH" ]]; then
  echo "FAIL: ${TOOLING_PATH} missing or empty — redo did not write identity files." >&2
  assert_fail=1
else
  echo "  ok  TOOLING.md written"
fi

echo "init-redo-identity: checking config was not clobbered..."
rest="$(jq -cS 'del(.Identity) | del(.configVersion)' "$CONFIG_PATH" 2>/dev/null || true)"
if [[ "$rest" != "{}" ]]; then
  echo "FAIL: netclaw.json changed outside Identity — redo must not call WriteConfig. Got: ${rest}" >&2
  assert_fail=1
else
  echo "  ok  netclaw.json unchanged outside Identity"
fi
if ! jq -e '.Identity.AgentName | type == "string"' "$CONFIG_PATH" >/dev/null 2>&1; then
  echo "FAIL: netclaw.json has no Identity.AgentName — redo did not persist the identity." >&2
  assert_fail=1
else
  echo "  ok  Identity persisted to netclaw.json"
fi

if (( assert_fail )); then
  exit 1
fi

echo "init-redo-identity: assertions passed (identity written, rest of config preserved)."
