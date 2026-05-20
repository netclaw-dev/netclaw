#!/usr/bin/env bash
# provider-model-cli.sh — provider/model CLI subcommands; no daemon required.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

ALT_MODEL="${SMOKE_OLLAMA_ALT_MODEL:-all-minilm:latest}"

log "Testing provider add (local-ollama)..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"

log "Testing provider list..."
provider_list="$(nc provider list 2>/dev/null || true)"
echo "$provider_list"
if [[ "$provider_list" == *"local-ollama"* ]]; then
  pass "provider list: includes local-ollama"
else
  fail "provider list: expected local-ollama"
fi

log "Testing model set (main to $SMOKE_MODEL)..."
nc model set main local-ollama "$SMOKE_MODEL"

log "Testing model list..."
model_list="$(nc model list 2>/dev/null || true)"
echo "$model_list"
if [[ "$model_list" == *"$SMOKE_MODEL"* ]]; then
  pass "model list: includes $SMOKE_MODEL"
else
  fail "model list: expected $SMOKE_MODEL"
fi

log "Testing model discover..."
discover_output="$(nc model discover local-ollama 2>/dev/null || true)"
echo "$discover_output"
if [[ "$discover_output" == *"$SMOKE_MODEL"* ]]; then
  pass "model discover: includes $SMOKE_MODEL"
else
  fail "model discover: expected $SMOKE_MODEL"
fi

log "Testing model switch to alternate model ($ALT_MODEL)..."
nc model set main local-ollama "$ALT_MODEL"
switched_list="$(nc model list 2>/dev/null || true)"
echo "$switched_list"
if [[ "$switched_list" == *"$ALT_MODEL"* ]]; then
  pass "model switch: list shows $ALT_MODEL"
else
  fail "model switch: expected $ALT_MODEL after switch"
fi

log "Testing model switch back to original ($SMOKE_MODEL)..."
nc model set main local-ollama "$SMOKE_MODEL"
restored_list="$(nc model list 2>/dev/null || true)"
echo "$restored_list"
if [[ "$restored_list" == *"$SMOKE_MODEL"* ]]; then
  pass "model switch back: list shows $SMOKE_MODEL"
else
  fail "model switch back: expected $SMOKE_MODEL"
fi

log "Testing provider add (second provider)..."
nc provider add test-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
added_list="$(nc provider list 2>/dev/null || true)"
echo "$added_list"
if [[ "$added_list" == *"test-ollama"* ]]; then
  pass "provider add: list includes test-ollama"
else
  fail "provider add: expected test-ollama"
fi

log "Testing provider remove..."
nc provider remove test-ollama
removed_list="$(nc provider list 2>/dev/null || true)"
echo "$removed_list"
if [[ "$removed_list" == *"test-ollama"* ]]; then
  fail "provider remove: test-ollama still present"
else
  pass "provider remove: test-ollama removed"
fi

log "Testing model set fallback then clear..."
nc model set fallback local-ollama "$ALT_MODEL"
nc model clear fallback
cleared_list="$(nc model list 2>/dev/null || true)"
echo "$cleared_list"
if [[ "$cleared_list" == *"$ALT_MODEL"* ]]; then
  fail "model clear: $ALT_MODEL still present in fallback"
else
  pass "model clear: $ALT_MODEL cleared from fallback"
fi

summarize
exit $?
