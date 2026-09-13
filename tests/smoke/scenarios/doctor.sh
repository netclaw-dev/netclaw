#!/usr/bin/env bash
# doctor.sh — goal: `netclaw doctor` runs to completion without crashing.
#
# `netclaw doctor` is an offline diagnostic (no daemon). A crash in it was
# reported on macOS, so this scenario runs it on a fresh config and asserts
# it exits with a documented code (0 clean / 1 errors / 2 warnings) — never
# a crash signal (>= 128) or a hang (the run_timed 124).
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

log "Running 'netclaw doctor' on a fresh config..."
doctor_status=0
run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor || doctor_status=$?
echo "netclaw doctor exit code: ${doctor_status}"

case "$doctor_status" in
  0) pass "doctor: exited 0 (all checks passed)" ;;
  1) pass "doctor: exited 1 (errors reported — expected on a fresh/empty config)" ;;
  2) pass "doctor: exited 2 (warnings only)" ;;
  124) die "doctor: timed out (hung) under run_timed" ;;
  *)
    if (( doctor_status >= 128 )); then
      die "doctor: crashed — killed by signal $(( doctor_status - 128 )) (exit ${doctor_status})"
    fi
    die "doctor: unexpected exit code ${doctor_status}"
    ;;
esac

log "Testing 'doctor --fix' legacy migration guard..."
config_path="${NETCLAW_HOME}/config/netclaw.json"
cat >"$config_path" <<'JSON'
{
  "configVersion": 1,
  "Models": {
    "Main": {
      "Provider": "local",
      "ModelId": "qwen3:30b"
    }
  }
}
JSON
expected_config="${NETCLAW_HOME}/doctor-legacy.expected.json"
cp "$config_path" "$expected_config"
crash_count_before="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
fix_status=0
fix_output="$(
  NETCLAW_Models__Main__ContextWindow=65536 run_timed \
    "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor --fix --yes 2>&1
)" || fix_status=$?
echo "$fix_output"
crash_count_after="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
if [[ "$fix_status" -eq 1 \
      && "$fix_output" == Error:*"Cannot migrate Models"* \
      && "$fix_output" != *"Unhandled exception"* \
      && "$fix_output" != *"Fatal error"* \
      && "$crash_count_after" == "$crash_count_before" ]] \
    && cmp -s "$config_path" "$expected_config"; then
  pass "doctor --fix: migration guard exits 1 without crash artefacts or config changes"
else
  fail "doctor --fix: migration guard was not a clean validation failure"
fi

json_status=0
json_output="$(
  NETCLAW_Models__Main__ContextWindow=65536 run_timed \
    "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor --fix --yes --format json 2>&1
)" || json_status=$?
echo "$json_output"
if [[ "$json_status" -eq 1 \
      && "$json_output" == *'"exitCode": 1'* \
      && "$json_output" == *'"name": "model-configuration"'* \
      && "$json_output" == *"Cannot migrate Models"* \
      && "$json_output" != *"Unhandled exception"* \
      && "$json_output" != *"Fatal error"* ]]; then
  pass "doctor --fix --format json: migration guard preserves the JSON error envelope"
else
  fail "doctor --fix --format json: migration guard did not return structured validation output"
fi

log "Testing unresolved named model references fail cleanly..."
cat >"$config_path" <<'JSON'
{
  "configVersion": 1,
  "Providers": {
    "local": {
      "Type": "openai-compatible",
      "Endpoint": "${SMOKE_LLM_ENDPOINT}"
    }
  },
  "Models": {
    "Definitions": {
      "known": {
        "Provider": "local",
        "ModelId": "qwen3:30b"
      }
    },
    "Roles": {
      "Main": "missing"
    }
  }
}
JSON
expected_config="${NETCLAW_HOME}/doctor-unresolved.expected.json"
cp "$config_path" "$expected_config"
crash_count_before="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"

model_status=0
model_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" model list 2>&1)" || model_status=$?
echo "$model_output"
if [[ "$model_status" -eq 1
      && "$model_output" == *"Models:Roles:Main references unknown definition 'missing'."*
      && "$model_output" != *"could not be parsed"*
      && "$model_output" != *"doctor --fix"*
      && "$model_output" != *"Unhandled exception"*
      && "$model_output" != *"Fatal error"* ]]; then
  pass "model list: unresolved role reports the exact reference without autofix guidance"
else
  fail "model list: unresolved role was not reported cleanly"
fi

doctor_status=0
doctor_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor 2>&1)" || doctor_status=$?
echo "$doctor_output"
if [[ "$doctor_status" -eq 1
      && "$doctor_output" == *"Models:Roles:Main references unknown definition 'missing'."*
      && "$doctor_output" != *"Unhandled exception"*
      && "$doctor_output" != *"Fatal error"* ]]; then
  pass "doctor: unresolved role exits 1 without crashing"
else
  fail "doctor: unresolved role did not return a clean validation failure"
fi

json_status=0
json_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor --format json 2>&1)" || json_status=$?
echo "$json_output"
if [[ "$json_status" -eq 1
      && "$json_output" == *'"exitCode": 1'*
      && "$json_output" == *"Models:Roles:Main references unknown definition"*
      && "$json_output" == *"missing"*
      && "$json_output" != *"Unhandled exception"*
      && "$json_output" != *"Fatal error"* ]]; then
  pass "doctor --format json: unresolved role preserves the JSON error envelope"
else
  fail "doctor --format json: unresolved role was not structured cleanly"
fi

fix_status=0
fix_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor --fix --yes 2>&1)" || fix_status=$?
echo "$fix_output"
crash_count_after="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
if [[ "$fix_status" -eq 1
      && "$fix_output" == *"Models:Roles:Main references unknown definition 'missing'."*
      && "$fix_output" != *"Unhandled exception"*
      && "$fix_output" != *"Fatal error"*
      && "$crash_count_after" == "$crash_count_before" ]] && cmp -s "$config_path" "$expected_config"; then
  pass "doctor --fix: unresolved role exits 1 without guessing, crashing, or changing config"
else
  fail "doctor --fix: unresolved role handling was unsafe or crashed"
fi
summarize
exit $?
