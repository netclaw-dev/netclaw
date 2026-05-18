#!/usr/bin/env bash
# context-window.sh — context-window auto-detection.
#
# Folded from scripts/smoke/check.sh (~lines 240-277). Verifies that
# `netclaw doctor` and the daemon status API report the provider-detected
# context window (not the 32k hardcoded default) when no explicit
# ContextWindow is configured.
#
# Self-contained: seeds provider + model config, starts a fresh daemon,
# asserts, stops it. (In check.sh this config was built up by an earlier
# section running against the same daemon.)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

SMOKE_MODEL="${SMOKE_OLLAMA_MODEL:-qwen2:0.5b}"
OLLAMA_ENDPOINT="${SMOKE_OLLAMA_ENDPOINT:-http://localhost:11434}"

nc() { run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" "$@"; }

cleanup() { stop_daemon; }
trap cleanup EXIT

log "Seeding provider + model config..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
nc model set main local-ollama "$SMOKE_MODEL"

log "Starting daemon for context window test..."
if ! start_daemon; then
  fail "daemon did not start"
  summarize || exit 1
  exit 1
fi
wait_for_health || { fail "daemon health endpoint not ready"; summarize || exit 1; exit 1; }

log "Verifying context window auto-detection via status API..."
ctx_json="$(run_timed "$STEP_TIMEOUT_SECONDS" curl -fsS "${DAEMON_BASE_URL}/api/health/status" 2>/dev/null || true)"
ctx_window="$(echo "$ctx_json" | python3 -c "import sys,json; print(json.load(sys.stdin).get('model',{}).get('contextWindow',0))" 2>/dev/null || echo 0)"
log "Daemon reports context window: $ctx_window tokens"
if [[ "${ctx_window:-0}" -le 32768 ]]; then
  # qwen2:0.5b has a context window > 32k that Ollama reports via /api/show.
  # If we get exactly 32768 or less, auto-detection failed and we hit the
  # hardcoded default — which is the bug this test guards against. Treated
  # as a WARN, not a fail (some models have a native 32k context).
  warn "Context window is $ctx_window (<= 32768). Auto-detection may not have worked."
else
  pass "context window: auto-detected $ctx_window tokens (> 32768)"
fi

log "Verifying netclaw doctor reports auto-detected context window..."
doctor_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor 2>&1 || true)"
echo "$doctor_output"
if [[ "$doctor_output" == *"Using default 32,768 tokens"* ]]; then
  fail "doctor: still reports the hardcoded 32k default"
elif [[ "$doctor_output" == *"Auto-detected"* ]]; then
  pass "doctor: shows auto-detected context window"
elif [[ "$doctor_output" == *"Context window explicitly set"* ]]; then
  pass "doctor: context window explicitly configured (no auto-detection needed)"
else
  fail "doctor: unexpected output for context window check"
fi

summarize
exit $?
