#!/usr/bin/env bash
# chat-completion.sh — goal: a headless chat turn produces a valid envelope,
# persists the session, and a --resume turn keeps the same session id.
#
# Structural assertions only — the envelope shape, usage counters, session
# persistence, and resume continuity are deterministic; the prose is not.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v jq >/dev/null 2>&1 || die "jq is required for chat-completion.sh"

trap stop_daemon EXIT

log "Seeding provider + tool model ($SMOKE_TOOL_MODEL)..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
nc model set main local-ollama "$SMOKE_TOOL_MODEL"

log "Starting daemon..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

# ── Turn 1: a fresh headless turn yields a complete envelope ──
RESUME_SESSION="smoke/chat-completion-$$"

log "Turn 1: headless --json --resume $RESUME_SESSION..."
turn1="$(nc_chat -p --json --resume "$RESUME_SESSION" "Reply with the single word: ready" 2>/dev/null || true)"
echo "$turn1"

if ! echo "$turn1" | jq -e . >/dev/null 2>&1; then
  die "turn 1: --json output did not parse as JSON"
fi
pass "turn 1: --json output parsed"

turn1_session="$(echo "$turn1" | jq -r '.sessionId // empty')"
if [[ -n "$turn1_session" ]]; then
  pass "turn 1: .sessionId is non-empty ($turn1_session)"
else
  die "turn 1: expected a non-empty .sessionId"
fi

turn1_response="$(echo "$turn1" | jq -r '.response // empty')"
if [[ -n "$turn1_response" ]]; then
  pass "turn 1: .response is non-empty"
else
  die "turn 1: expected a non-empty .response"
fi

# .usage is present with a positive output-token count.
turn1_out_tokens="$(echo "$turn1" | jq -r '.usage.outputTokens // 0')"
if [[ "$turn1_out_tokens" =~ ^[0-9]+$ ]] && (( turn1_out_tokens > 0 )); then
  pass "turn 1: .usage present with outputTokens > 0 ($turn1_out_tokens)"
else
  die "turn 1: expected .usage.outputTokens > 0, got '$turn1_out_tokens'"
fi

# ── Session persistence ──
log "Verifying the session was persisted..."
sessions_output="$(run_timed "$STEP_TIMEOUT_SECONDS" curl -fsS "${DAEMON_BASE_URL}/api/sessions" 2>/dev/null || true)"
echo "$sessions_output"
if echo "$sessions_output" | jq -e --arg id "$turn1_session" \
     'any(.[]?; (.persistenceId // .sessionId // "") == $id)' >/dev/null 2>&1; then
  pass "/api/sessions: lists the persisted session $turn1_session"
else
  # The headless per-session log file is the secondary persistence signal.
  sanitized="${turn1_session//\//-}"
  if [[ -f "${NETCLAW_HOME}/logs/${sanitized}.log" ]]; then
    pass "session persisted: per-session log file ${sanitized}.log exists"
  else
    die "session not persisted: not in /api/sessions and no per-session log file"
  fi
fi

# ── Turn 2: --resume keeps the same session id ──
log "Turn 2: --resume $RESUME_SESSION (must keep the same session id)..."
turn2="$(nc_chat -p --json --resume "$RESUME_SESSION" "Reply with the single word: again" 2>/dev/null || true)"
echo "$turn2"

if ! echo "$turn2" | jq -e . >/dev/null 2>&1; then
  die "turn 2: --json output did not parse as JSON"
fi

turn2_session="$(echo "$turn2" | jq -r '.sessionId // empty')"
if [[ -n "$turn2_session" && "$turn2_session" == "$turn1_session" ]]; then
  pass "resume: turn 2 kept the same session id ($turn2_session)"
else
  die "resume: expected session id '$turn1_session', got '$turn2_session'"
fi

summarize
exit $?
