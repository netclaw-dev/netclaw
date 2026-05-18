#!/usr/bin/env bash
# provider-connect.sh — goal: Netclaw connects to Ollama and a model responds.
#
# Verifies the full provider path end-to-end: a seeded Ollama provider is
# reachable, model discovery sees the tool model, the daemon starts healthy,
# and a headless prompt returns a non-empty assistant response. Structural
# only — never asserts on the model's prose, which is non-deterministic.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v jq >/dev/null 2>&1 || die "jq is required for provider-connect.sh"

trap stop_daemon EXIT

log "Seeding Ollama provider..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"

log "Discovering models on local-ollama (expecting $SMOKE_TOOL_MODEL)..."
discover_output="$(nc model discover local-ollama 2>/dev/null || true)"
echo "$discover_output"
if [[ "$discover_output" == *"$SMOKE_TOOL_MODEL"* ]]; then
  pass "model discover: lists $SMOKE_TOOL_MODEL"
else
  die "model discover: expected $SMOKE_TOOL_MODEL in discovery output"
fi

log "Setting main model to $SMOKE_TOOL_MODEL..."
nc model set main local-ollama "$SMOKE_TOOL_MODEL"

log "Starting daemon..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

log "Sending a headless prompt and parsing the --json envelope..."
json_output="$(nc_chat -p --json "Reply with the single word: ready" 2>/dev/null || true)"
echo "$json_output"

if ! echo "$json_output" | jq -e . >/dev/null 2>&1; then
  die "chat --json: output did not parse as JSON"
fi
pass "chat --json: output parsed as a JSON envelope"

response="$(echo "$json_output" | jq -r '.response // empty')"
if [[ -n "$response" ]]; then
  pass "chat --json: .response is non-empty (model produced a reply)"
else
  die "chat --json: expected a non-empty .response"
fi

summarize
exit $?
