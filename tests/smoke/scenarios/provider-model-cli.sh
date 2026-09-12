#!/usr/bin/env bash
# provider-model-cli.sh — provider/model CLI subcommands; no daemon required.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

log "Testing provider add (local-smoke)..."
nc provider add local-smoke openai-compatible --endpoint "$SMOKE_LLM_ENDPOINT"

log "Testing provider list..."
provider_list="$(nc provider list 2>/dev/null || true)"
echo "$provider_list"
if [[ "$provider_list" == *"local-smoke"* ]]; then
  pass "provider list: includes local-smoke"
else
  fail "provider list: expected local-smoke"
fi

log "Testing model set (main to $SMOKE_MODEL)..."
nc model set main local-smoke "$SMOKE_MODEL"

log "Testing model list..."
model_list="$(nc model list 2>/dev/null || true)"
echo "$model_list"
if [[ "$model_list" == *"$SMOKE_MODEL"* ]]; then
  pass "model list: includes $SMOKE_MODEL"
else
  fail "model list: expected $SMOKE_MODEL"
fi

log "Testing model discover..."
discover_output="$(nc model discover local-smoke 2>/dev/null || true)"
echo "$discover_output"
if [[ "$discover_output" == *"$SMOKE_MODEL"* ]]; then
  pass "model discover: includes $SMOKE_MODEL"
else
  fail "model discover: expected $SMOKE_MODEL"
fi

log "Testing provider add (second provider)..."
nc provider add test-smoke openai-compatible --endpoint "$SMOKE_LLM_ENDPOINT"
added_list="$(nc provider list 2>/dev/null || true)"
echo "$added_list"
if [[ "$added_list" == *"test-smoke"* ]]; then
  pass "provider add: list includes test-smoke"
else
  fail "provider add: expected test-smoke"
fi

log "Testing provider remove..."
nc provider remove test-smoke
removed_list="$(nc provider list 2>/dev/null || true)"
echo "$removed_list"
if [[ "$removed_list" == *"test-smoke"* ]]; then
  fail "provider remove: test-smoke still present"
else
  pass "provider remove: test-smoke removed"
fi

log "Testing model set fallback then clear..."
nc model set fallback local-smoke "$SMOKE_MODEL"
nc model clear fallback
cleared_list="$(nc model list 2>/dev/null || true)"
echo "$cleared_list"
fallback_line="$(printf '%s\n' "$cleared_list" | awk '$1 == "Fallback" { print; exit }')"
if [[ "$fallback_line" == *"(not set)"* ]]; then
  pass "model clear: fallback is not set"
else
  fail "model clear: fallback remains configured"
fi

log "Testing legacy migration failures are clean model errors..."
config_path="${NETCLAW_HOME}/config/netclaw.json"
cat >"$config_path" <<JSON
{
  "configVersion": 1,
  "Providers": {
    "local-smoke": {
      "Type": "openai-compatible",
      "Endpoint": "${SMOKE_LLM_ENDPOINT}"
    }
  },
  "Models": {
    "Main": {
      "Provider": "local-smoke",
      "ModelId": "${SMOKE_MODEL}"
    }
  }
}
JSON
expected_config="${NETCLAW_HOME}/legacy-model.expected.json"
cp "$config_path" "$expected_config"
crash_count_before="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
migration_status=0
migration_output="$(
  NETCLAW_Models__Main__ContextWindow=65536 run_timed \
    "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" model set main local-smoke "$SMOKE_MODEL" 2>&1
)" || migration_status=$?
echo "$migration_output"
crash_count_after="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
new_crash_log="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' -newer "$expected_config" -print -quit 2>/dev/null)"
if [[ "$migration_status" -eq 1 \
      && "$migration_output" == Error:*"Cannot migrate Models"* \
      && "$migration_output" != *"Unhandled exception"* \
      && "$migration_output" != *"Fatal error"* \
      && -z "$new_crash_log" \
      && "$crash_count_after" == "$crash_count_before" \
      && ! -e "${config_path}.legacy-models.bak" ]] \
    && cmp -s "$config_path" "$expected_config"; then
  pass "model set: legacy environment guard exits 1 without crash artefacts or config changes"
else
  fail "model set: legacy environment guard was not a clean validation failure"
fi

cat >"$config_path" <<JSON
{
  "configVersion": 1,
  "Providers": {
    "local-smoke": {
      "Type": "openai-compatible",
      "Endpoint": "${SMOKE_LLM_ENDPOINT}"
    }
  },
  "Models": {
    "Main": {
      "Provider": "local-smoke",
      "ModelId": "${SMOKE_MODEL}",
      "ContextWindow": 32768
    },
    "Fallback": {
      "Provider": "local-smoke",
      "ModelId": "${SMOKE_MODEL}",
      "ContextWindow": 65536
    }
  }
}
JSON
cp "$config_path" "$expected_config"
conflict_crash_count_before="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
conflict_status=0
conflict_output="$(
  run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" \
    model set compaction local-smoke "$SMOKE_MODEL" 2>&1
)" || conflict_status=$?
echo "$conflict_output"
conflict_crash_count_after="$(find "${NETCLAW_HOME}/logs" -maxdepth 1 -name 'crash-*.log' 2>/dev/null | wc -l | tr -d ' ')"
if [[ "$conflict_status" -eq 1 \
      && "$conflict_output" == Error:*"Legacy model roles conflict"* \
      && "$conflict_output" != *"Unhandled exception"* \
      && "$conflict_output" != *"Fatal error"* \
      && "$conflict_crash_count_after" == "$conflict_crash_count_before" ]] \
    && cmp -s "$config_path" "$expected_config"; then
  pass "model set: conflicting legacy roles exit 1 without changing config"
else
  fail "model set: conflicting legacy roles were not a clean validation failure"
fi
summarize
exit $?
