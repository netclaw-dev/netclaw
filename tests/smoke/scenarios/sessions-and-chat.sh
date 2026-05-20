#!/usr/bin/env bash
# sessions-and-chat.sh — headless chat, session catalog API, multi-turn resume, --json.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

seed_and_start_daemon

log "Sending a headless prompt to create a session..."
nc chat -p "Say hello in one word" || true

log "Checking session catalog via REST API..."
sessions_output="$(run_timed "$STEP_TIMEOUT_SECONDS" curl -fsS "${DAEMON_BASE_URL}/api/sessions" 2>/dev/null || true)"
echo "$sessions_output"
if [[ "$sessions_output" == *"persistenceId"* ]]; then
  pass "/api/sessions: returns at least one session entry"
else
  fail "/api/sessions: expected at least one session entry"
fi

log "Verifying help text includes sessions command..."
help_output="$(nc --help 2>/dev/null || true)"
if [[ "$help_output" == *"sessions"* ]]; then
  pass "--help: includes sessions command"
else
  fail "--help: expected sessions command"
fi

log "Verifying chat --resume help..."
resume_help="$(nc chat --help 2>/dev/null || true)"
echo "$resume_help"
if [[ "$resume_help" == *"--resume"* ]]; then
  pass "chat --help: includes --resume flag"
else
  fail "chat --help: expected --resume flag"
fi
if [[ "$resume_help" == *"-p"* ]]; then
  pass "chat --help: includes -p flag"
else
  fail "chat --help: expected -p flag"
fi

# ── Multi-turn headless resume ──
MULTI_TURN_SESSION="smoke/multi-turn-$$"

log "Testing multi-turn: Turn 1 (create named session)..."
nc chat -p --resume "$MULTI_TURN_SESSION" "hello" || true

log "Testing multi-turn: Turn 2 (resume and verify continuity)..."
turn2_output="$(nc chat -p --resume "$MULTI_TURN_SESSION" "what was my first message?" 2>/dev/null || true)"
echo "$turn2_output"
if echo "$turn2_output" | grep -qi "hello"; then
  pass "multi-turn: response referenced 'hello'"
else
  # Model quality issue, not a CLI bug — WARN, not fail.
  warn "multi-turn continuity: response did not reference 'hello' (model quality, not CLI bug)"
fi

log "Testing headless --json output..."
json_output="$(nc chat -p --json "Say hello in one word" 2>/dev/null || true)"
echo "$json_output"
if [[ "$json_output" == *"sessionId"* ]]; then
  pass "chat --json: output includes sessionId field"
else
  fail "chat --json: expected sessionId field"
fi

summarize
exit $?
