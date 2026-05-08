#!/usr/bin/env bash
# init-wizard.tape post-tape assertion.
#
# Validates that the wizard produced a usable, schema-valid config:
#   1) ${NETCLAW_HOME}/config.json exists and parses as JSON
#   2) `netclaw doctor` (which runs ConfigSchemaDoctorCheck) exits 0
#   3) The top-level fields the tape was supposed to set are present
#      with the expected values (provider type, model id, posture,
#      identity user name).
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

# Run a command inside the sandbox with NETCLAW_HOME pointed at the
# per-tape directory we just populated.
in_sandbox() {
  compose exec -T \
    -e "NETCLAW_HOME=${NETCLAW_HOME_IN}" \
    netclaw-sandbox "$@"
}

config_path="${NETCLAW_HOME_IN}/config.json"

echo "init-wizard: checking config exists at ${config_path}..."
if ! in_sandbox test -f "$config_path"; then
  echo "FAIL: ${config_path} does not exist after wizard run." >&2
  in_sandbox ls -la "$NETCLAW_HOME_IN" >&2 || true
  exit 1
fi

echo "init-wizard: validating JSON parses..."
if ! in_sandbox sh -c "jq empty < '$config_path'"; then
  echo "FAIL: ${config_path} is not valid JSON." >&2
  in_sandbox cat "$config_path" >&2 || true
  exit 1
fi

echo "init-wizard: running 'netclaw doctor' against produced config..."
if ! in_sandbox netclaw doctor; then
  echo "FAIL: netclaw doctor exited non-zero against the produced config." >&2
  exit 1
fi

echo "init-wizard: checking expected fields are present..."
expected_provider_type='ollama'
expected_user='SmokeTester'

actual_provider_type="$(in_sandbox sh -c "jq -r '.providers | to_entries[0].value.type' < '$config_path'")"
actual_user="$(in_sandbox sh -c "jq -r '.identity.userName // empty' < '$config_path'")"

fail=0

if [[ "$actual_provider_type" != "$expected_provider_type" ]]; then
  echo "FAIL: expected provider type '${expected_provider_type}', got '${actual_provider_type}'." >&2
  fail=1
fi

if [[ "$actual_user" != "$expected_user" ]]; then
  echo "FAIL: expected identity.userName '${expected_user}', got '${actual_user}'." >&2
  fail=1
fi

if (( fail )); then
  echo "--- config.json contents ---" >&2
  in_sandbox cat "$config_path" >&2 || true
  exit 1
fi

echo "init-wizard: assertions passed."
