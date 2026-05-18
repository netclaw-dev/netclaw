#!/usr/bin/env bash
# Shared helpers for native tape post-tape assertion scripts. Source this
# from tests/smoke/assertions/<tape-name>.sh.
#
# Sources require these env vars (set by scripts/smoke/run-native-tape.sh):
#   NETCLAW_HOME        per-tape NETCLAW_HOME on the host filesystem
#   NETCLAW_SMOKE_CLI   absolute path to the `netclaw` binary
#
# Unlike the Docker assertions (tests/smoke-interactive/assertions/_lib.sh),
# config and SOUL.md are read directly from the host filesystem — there is
# no container to exec into.

: "${NETCLAW_HOME:?NETCLAW_HOME must be set by run-native-tape.sh}"
: "${NETCLAW_SMOKE_CLI:?NETCLAW_SMOKE_CLI must be set by run-native-tape.sh}"

CONFIG_PATH="${NETCLAW_HOME}/config/netclaw.json"
SOUL_PATH="${NETCLAW_HOME}/identity/SOUL.md"

# Cat the produced netclaw.json from the host filesystem to stdout.
read_config_json() {
  cat "$CONFIG_PATH" 2>/dev/null
}

# Assert a jq expression against a JSON blob passed by value.
#   Usage: assert_field <jq_expr> <expected> "$json_blob"
# `tostring` so booleans and missing paths produce comparable strings
# ("true"/"false"/"null") — `// empty` would collapse `false` to "".
# Sets `assert_fail` on mismatch (caller initialises and inspects).
assert_field() {
  local jq_expr="$1"
  local expected="$2"
  local json="$3"
  local actual
  actual="$(printf '%s' "$json" | jq -r "${jq_expr} | tostring" 2>/dev/null | tr -d '\r')"
  if [[ "$actual" != "$expected" ]]; then
    printf 'FAIL: expected %s == %s, got %s\n' "$jq_expr" "$expected" "$actual" >&2
    assert_fail=1
    return 1
  fi
  printf '  ok  %s == %s\n' "$jq_expr" "$expected"
}
