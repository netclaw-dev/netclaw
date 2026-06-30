#!/usr/bin/env bash
# init-redo-identity.tape post-tape assertion.
#
# Two invariants of the "Redo identity setup" flow:
#   1. It finalizes past the timezone submit. WriteIdentityFiles only runs after
#      the final identity sub-step, so SOUL.md / TOOLING.md existing proves the
#      timezone loop did not recur (the tape would otherwise hang on the
#      "Identity updated" anchor and vhs would exit non-zero).
#   2. It rewrites ONLY identity files — it must never call WriteConfig, so the
#      seeded netclaw.json (the empty object) must survive untouched.

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
config_json="$(read_config_json | tr -d '[:space:]')"
if [[ "$config_json" != "{}" ]]; then
  echo "FAIL: netclaw.json changed — redo must not call WriteConfig. Got: ${config_json}" >&2
  assert_fail=1
else
  echo "  ok  netclaw.json untouched"
fi

if (( assert_fail )); then
  exit 1
fi

echo "init-redo-identity: assertions passed (identity written, config preserved)."
