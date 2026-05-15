#!/usr/bin/env bash
# init-wizard.tape post-tape assertion.
#
# Validates that the wizard produced a usable, schema-valid state:
#   1) config/netclaw.json exists and parses as JSON
#   2) `netclaw doctor` (which runs ConfigSchemaDoctorCheck) does not
#      report errors (exit 0 = clean; exit 2 = WARNs only — acceptable
#      for Personal posture + HostAllowed shell which trips a warn)
#   3) Provider/model/posture fields in netclaw.json match what the
#      tape typed
#   4) Identity/SOUL.md contains the typed user name

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "init-wizard: reading produced config..."
if ! in_sandbox test -f "$CONFIG_PATH"; then
  echo "FAIL: ${CONFIG_PATH} does not exist after wizard run." >&2
  in_sandbox sh -lc "ls -la '$NETCLAW_HOME_IN' 2>&1" >&2 || true
  exit 1
fi

config_json="$(read_config_json)"
if ! printf '%s' "$config_json" | jq empty >/dev/null 2>&1; then
  echo "FAIL: ${CONFIG_PATH} is not valid JSON." >&2
  printf '%s\n' "$config_json" >&2
  exit 1
fi

echo "init-wizard: running 'netclaw doctor'..."
# DoctorRunner exit codes (src/Netclaw.Cli/Doctor/DoctorRunner.cs):
#   0 = all PASS, 1 = errors (fail), 2 = WARNs only (acceptable)
doctor_status=0
in_sandbox netclaw doctor || doctor_status=$?
if [[ $doctor_status -eq 1 ]]; then
  echo "FAIL: netclaw doctor reported errors (exit 1)." >&2
  exit 1
fi
if [[ $doctor_status -ne 0 && $doctor_status -ne 2 ]]; then
  echo "FAIL: netclaw doctor exited with unexpected status $doctor_status." >&2
  exit 1
fi

echo "init-wizard: checking expected fields in netclaw.json..."
assert_field '.Providers.ollama.Type'       'ollama'                "$config_json" || :
assert_field '.Providers.ollama.Endpoint'   'http://ollama:11434'   "$config_json" || :
assert_field '.Models.Main.Provider'        'ollama'                "$config_json" || :
assert_field '.Models.Main.ModelId'         'qwen2:0.5b'            "$config_json" || :
assert_field '.Security.DeploymentPosture'  'Personal'              "$config_json" || :

echo "init-wizard: checking identity/SOUL.md for typed user name..."
if ! in_sandbox grep -q 'Name: SmokeTester' "$SOUL_PATH"; then
  echo "FAIL: identity/SOUL.md does not contain 'Name: SmokeTester'." >&2
  in_sandbox cat "$SOUL_PATH" >&2 | head -40 || true
  assert_fail=1
else
  echo "  ok  identity/SOUL.md contains 'Name: SmokeTester'"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "init-wizard: assertions passed."
