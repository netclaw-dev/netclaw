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
#   5) Identity/AGENTS.md contains the deployment playbook scaffold only

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "init-wizard: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist after wizard run." >&2
  ls -la "$NETCLAW_HOME" 2>&1 >&2 || true
  exit 1
fi

agents_path="${NETCLAW_HOME}/identity/AGENTS.md"
echo "init-wizard: checking deployment mission scaffold..."
if ! grep -q 'Deployment Mission and Operating Playbook' "$agents_path" 2>/dev/null; then
  echo "FAIL: ${agents_path} does not contain the deployment mission scaffold." >&2
  assert_fail=1
elif grep -q 'Search Decision Rules' "$agents_path" 2>/dev/null; then
  echo "FAIL: ${agents_path} duplicated embedded Netclaw operating rules." >&2
  assert_fail=1
else
  echo "  ok  identity/AGENTS.md contains only the deployment scaffold"
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
"$NETCLAW_SMOKE_CLI" doctor || doctor_status=$?
if [[ $doctor_status -eq 1 ]]; then
  echo "FAIL: netclaw doctor reported errors (exit 1)." >&2
  exit 1
fi
if [[ $doctor_status -ne 0 && $doctor_status -ne 2 ]]; then
  echo "FAIL: netclaw doctor exited with unexpected status $doctor_status." >&2
  exit 1
fi

echo "init-wizard: checking expected fields in netclaw.json..."
assert_field '.Providers.ollama.Type'       'ollama'                   "$config_json" || :
assert_field '.Providers.ollama.Endpoint'   'http://localhost:11434'   "$config_json" || :
assert_field '.Models.Roles.Main'                                  'ollama-qwen2-0-5b' "$config_json" || :
assert_field '.Models.Definitions[.Models.Roles.Main].Provider'     'ollama'             "$config_json" || :
assert_field '.Models.Definitions[.Models.Roles.Main].ModelId'      'qwen2:0.5b'         "$config_json" || :
assert_field '(.Models.Definitions[.Models.Roles.Main] | has("ContextWindow"))'     'false' "$config_json" || :
assert_field '(.Models.Definitions[.Models.Roles.Main] | has("InputModalities"))'   'false' "$config_json" || :
assert_field '(.Models.Definitions[.Models.Roles.Main] | has("OutputModalities"))'  'false' "$config_json" || :
assert_field '.Security.DeploymentPosture'  'Personal'                 "$config_json" || :

echo "init-wizard: checking identity/SOUL.md for typed user name..."
if ! grep -q 'Name: SmokeTester' "$SOUL_PATH" 2>/dev/null; then
  echo "FAIL: identity/SOUL.md does not contain 'Name: SmokeTester'." >&2
  head -40 "$SOUL_PATH" >&2 2>/dev/null || true
  assert_fail=1
else
  echo "  ok  identity/SOUL.md contains 'Name: SmokeTester'"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "init-wizard: assertions passed."
