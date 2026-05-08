#!/usr/bin/env bash
# init-wizard.tape post-tape assertion.
#
# Validates that the wizard produced a usable, schema-valid state:
#   1) ${NETCLAW_HOME}/config/netclaw.json exists and parses as JSON
#   2) `netclaw doctor` (which runs ConfigSchemaDoctorCheck) exits 0
#   3) Top-level fields the tape was supposed to set look right:
#        - Providers["ollama"].Type        == "ollama"
#        - Providers["ollama"].Endpoint    == "http://ollama:11434"
#        - Models.Main.Provider            == "ollama"
#        - Models.Main.ModelId             == "qwen2:0.5b"
#        - Security.DeploymentPosture      == "Personal"
#   4) Identity/SOUL.md contains the user name we typed (SmokeTester).
#
# Invoked by scripts/smoke/run-tape.sh with these env vars exported:
#   PROJECT_NAME      docker compose project
#   COMPOSE_FILE      path to docker-compose.smoke.yml
#   TAPE_NAME         short tape name (init-wizard)
#   NETCLAW_HOME_IN   per-tape NETCLAW_HOME inside the container

set -euo pipefail

: "${PROJECT_NAME:?PROJECT_NAME must be set by run-tape.sh}"
: "${COMPOSE_FILE:?COMPOSE_FILE must be set by run-tape.sh}"
: "${NETCLAW_HOME_IN:?NETCLAW_HOME_IN must be set by run-tape.sh}"

compose() {
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" "$@"
}

in_sandbox() {
  compose exec -T \
    -e "NETCLAW_HOME=${NETCLAW_HOME_IN}" \
    netclaw-sandbox "$@"
}

config_path="${NETCLAW_HOME_IN}/config/netclaw.json"
soul_path="${NETCLAW_HOME_IN}/identity/SOUL.md"

echo "init-wizard: checking config exists at ${config_path}..."
if ! in_sandbox test -f "$config_path"; then
  echo "FAIL: ${config_path} does not exist after wizard run." >&2
  in_sandbox sh -lc "ls -la '$NETCLAW_HOME_IN' 2>&1; ls -la '$NETCLAW_HOME_IN/config' 2>&1" >&2 || true
  exit 1
fi

echo "init-wizard: validating config JSON parses..."
if ! in_sandbox sh -lc "jq empty < '$config_path'"; then
  echo "FAIL: ${config_path} is not valid JSON." >&2
  in_sandbox cat "$config_path" >&2 || true
  exit 1
fi

echo "init-wizard: running 'netclaw doctor' against produced config..."
# DoctorRunner exit codes (src/Netclaw.Cli/Doctor/DoctorRunner.cs):
#   0 = all checks passed
#   1 = at least one check failed (treat as assertion failure)
#   2 = warnings only, no failures (acceptable in smoke — e.g. "Personal posture
#       with HostAllowed shell" is an expected WARN for the Personal flow)
doctor_status=0
in_sandbox netclaw doctor || doctor_status=$?
if [[ $doctor_status -eq 1 ]]; then
  echo "FAIL: netclaw doctor reported errors (exit code 1)." >&2
  exit 1
fi
if [[ $doctor_status -ne 0 && $doctor_status -ne 2 ]]; then
  echo "FAIL: netclaw doctor exited with unexpected status $doctor_status." >&2
  exit 1
fi

echo "init-wizard: checking expected fields in netclaw.json..."
fail=0

assert_field() {
  local jq_expr="$1"
  local expected="$2"
  local actual
  actual="$(in_sandbox sh -lc "jq -r '$jq_expr // empty' < '$config_path'" | tr -d '\r')"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL: expected '${jq_expr}' == '${expected}', got '${actual}'." >&2
    fail=1
  else
    echo "  ok  ${jq_expr} == '${expected}'"
  fi
}

assert_field '.Providers.ollama.Type'       'ollama'
assert_field '.Providers.ollama.Endpoint'   'http://ollama:11434'
assert_field '.Models.Main.Provider'        'ollama'
assert_field '.Models.Main.ModelId'         'qwen2:0.5b'
assert_field '.Security.DeploymentPosture'  'Personal'

echo "init-wizard: checking identity/SOUL.md for typed user name..."
if ! in_sandbox grep -q 'Name: SmokeTester' "$soul_path"; then
  echo "FAIL: identity/SOUL.md does not contain 'Name: SmokeTester'." >&2
  in_sandbox cat "$soul_path" >&2 | head -40 || true
  fail=1
else
  echo "  ok  identity/SOUL.md contains 'Name: SmokeTester'"
fi

if (( fail )); then
  echo "--- netclaw.json contents ---" >&2
  in_sandbox cat "$config_path" >&2 || true
  exit 1
fi

echo "init-wizard: assertions passed."
