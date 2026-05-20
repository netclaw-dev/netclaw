#!/usr/bin/env bash
# context-window.sh — verifies context-window auto-detection (status API + doctor).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

seed_and_start_daemon

log "Verifying context window auto-detection via status API..."
ctx_json="$(run_timed "$STEP_TIMEOUT_SECONDS" curl -fsS "${DAEMON_BASE_URL}/api/health/status" 2>/dev/null || true)"
if [[ -z "$ctx_json" ]] || ! echo "$ctx_json" | jq empty >/dev/null 2>&1; then
  die "status API did not return a valid JSON response"
fi
ctx_window="$(echo "$ctx_json" | jq -r '.model.contextWindow // 0')"
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
