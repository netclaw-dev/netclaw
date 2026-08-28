#!/usr/bin/env bash
# reminders.sh — reminder create / list / history / delete lifecycle.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

REMINDER_WAIT_TIMEOUT="${REMINDER_WAIT_TIMEOUT:-150}"

seed_and_start_daemon

ONE_SHOT_ID="smoke-execution-$$"
DELETE_ID="smoke-delete-$$"
request_count_before=0
if [[ -f "$SMOKE_LLM_REQUEST_RECORD" ]]; then
  request_count_before="$(wc -l <"$SMOKE_LLM_REQUEST_RECORD")"
fi

log "Testing one-shot reminder create (id=$ONE_SHOT_ID)..."
nc reminder create "$ONE_SHOT_ID" once 1m "Say OK in one word"

log "Verifying reminder appears in list..."
reminder_list="$(nc reminder list 2>/dev/null || true)"
echo "$reminder_list"
if [[ "$reminder_list" == *"$ONE_SHOT_ID"* ]]; then
  pass "reminder list: includes $ONE_SHOT_ID"
else
  fail "reminder list: expected $ONE_SHOT_ID"
fi

log "Waiting up to ${REMINDER_WAIT_TIMEOUT}s for the one-shot reminder to call the smoke LLM..."
execution_found=false
deadline=$((SECONDS + REMINDER_WAIT_TIMEOUT))
while (( SECONDS < deadline )); do
  request_count_after=0
  if [[ -f "$SMOKE_LLM_REQUEST_RECORD" ]]; then
    request_count_after="$(wc -l <"$SMOKE_LLM_REQUEST_RECORD")"
  fi
  if (( request_count_after > request_count_before )); then
    execution_found=true
    break
  fi
  sleep 5
done

if [[ "$execution_found" == "true" ]]; then
  pass "reminder execution: $ONE_SHOT_ID called the smoke LLM"
else
  fail "reminder execution: timed out waiting for $ONE_SHOT_ID"
fi

log "Verifying the completed one-shot reminder is absent from the list..."
completed_list="$(nc reminder list 2>/dev/null || true)"
echo "$completed_list"
if [[ "$completed_list" == *"$ONE_SHOT_ID"* ]]; then
  fail "one-shot reminder: $ONE_SHOT_ID remained after execution"
else
  pass "one-shot reminder: $ONE_SHOT_ID was removed after execution"
fi

log "Testing reminder delete (id=$DELETE_ID)..."
nc reminder create "$DELETE_ID" interval 1m "Say OK in one word"
nc reminder delete "$DELETE_ID"

log "Verifying the deleted reminder is absent from the list..."
after_delete_list="$(nc reminder list 2>/dev/null || true)"
echo "$after_delete_list"
if [[ "$after_delete_list" == *"$DELETE_ID"* ]]; then
  fail "reminder delete: $DELETE_ID remained in the list"
else
  pass "reminder delete: $DELETE_ID is absent from the list"
fi

log "Verifying history returns not-found for deleted reminder..."
history_exit=0
run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" reminder history "$DELETE_ID" >/dev/null 2>&1 || history_exit=$?
if [[ "$history_exit" -eq 0 ]]; then
  fail "reminder history: expected non-zero exit for deleted reminder $DELETE_ID"
else
  pass "reminder history: returned exit $history_exit for deleted reminder"
fi

summarize
exit $?
