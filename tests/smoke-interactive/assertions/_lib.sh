#!/usr/bin/env bash
# Shared helpers for tape post-tape assertion scripts. Source this from
# tests/smoke-interactive/assertions/<tape-name>.sh.
#
# Sources require these env vars (set by scripts/smoke/run-tape.sh):
#   PROJECT_NAME      docker compose project
#   COMPOSE_FILE      path to docker-compose.smoke.yml
#   NETCLAW_HOME_IN   per-tape NETCLAW_HOME inside the sandbox

: "${PROJECT_NAME:?PROJECT_NAME must be set by run-tape.sh}"
: "${COMPOSE_FILE:?COMPOSE_FILE must be set by run-tape.sh}"
: "${NETCLAW_HOME_IN:?NETCLAW_HOME_IN must be set by run-tape.sh}"

CONFIG_PATH="${NETCLAW_HOME_IN}/config/netclaw.json"
SOUL_PATH="${NETCLAW_HOME_IN}/identity/SOUL.md"

compose() {
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" "$@"
}

in_sandbox() {
  compose exec -T \
    -e "NETCLAW_HOME=${NETCLAW_HOME_IN}" \
    netclaw-sandbox "$@"
}

# Cat the produced netclaw.json from the sandbox to stdout. Single docker
# exec — callers should capture once and re-use, not re-call per assertion.
read_config_json() {
  in_sandbox cat "$CONFIG_PATH" 2>/dev/null
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
