#!/usr/bin/env bash
# reminders.sh — reminder create / list / history / delete lifecycle.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

REMINDER_WAIT_TIMEOUT="${REMINDER_WAIT_TIMEOUT:-60}"

seed_and_start_daemon

ONE_SHOT_ID="smoke-execution-$$"
DELETE_ID="smoke-delete-$$"
request_count_before=0
if [[ -f "$SMOKE_LLM_REQUEST_RECORD" ]]; then
  request_count_before="$(wc -l <"$SMOKE_LLM_REQUEST_RECORD")"
fi

log "Testing one-shot reminder create (id=$ONE_SHOT_ID)..."
nc reminder create "$ONE_SHOT_ID" once 10s "Say OK in one word"

log "Verifying reminder appears in list..."
reminder_list="$(nc reminder list --json 2>/dev/null || true)"
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
  sleep 1
done

if [[ "$execution_found" == "true" ]]; then
  pass "reminder execution: $ONE_SHOT_ID called the smoke LLM"
else
  fail "reminder execution: timed out waiting for $ONE_SHOT_ID"
fi

log "Verifying the one-shot reminder ends as completed, leaves the list, and keeps its history..."
# A one-shot deletes itself after the run. Settlement runs just after the model call returns,
# so poll briefly until the id leaves the list. A failed list command never counts as gone.
remaining_list=""
list_ok=false
settle_deadline=$((SECONDS + 15))
while (( SECONDS < settle_deadline )); do
  list_exit=0
  remaining_list="$(nc reminder list --json 2>/dev/null)" || list_exit=$?
  if [[ "$list_exit" -eq 0 && "$remaining_list" != *"$ONE_SHOT_ID"* ]]; then
    list_ok=true
    break
  fi
  sleep 1
done
echo "$remaining_list"
if [[ "$list_ok" == "true" ]]; then
  pass "one-shot reminder: $ONE_SHOT_ID left the list after execution"
else
  fail "one-shot reminder: $ONE_SHOT_ID is still listed after execution"
fi
ended_status_exit=0
ended_status="$(nc reminder status "$ONE_SHOT_ID" 2>/dev/null)" || ended_status_exit=$?
echo "$ended_status"
if [[ "$ended_status_exit" -eq 0 && "$ended_status" == *"Ended:               completed at"* ]]; then
  pass "one-shot reminder: $ONE_SHOT_ID ended as completed"
else
  fail "one-shot reminder: expected exit 0 and 'Ended: completed at' for $ONE_SHOT_ID (exit $ended_status_exit)"
fi
completed_history_exit=0
completed_history="$(nc reminder history "$ONE_SHOT_ID" 2>/dev/null)" || completed_history_exit=$?
echo "$completed_history"
if [[ "$completed_history_exit" -eq 0 && "$completed_history" == *"ok"* ]]; then
  pass "reminder history: $ONE_SHOT_ID history is readable after execution"
else
  fail "reminder history: expected exit 0 with a recorded run for completed $ONE_SHOT_ID (exit $completed_history_exit)"
fi

log "Testing reminder delete (id=$DELETE_ID)..."
nc reminder create "$DELETE_ID" interval 1m "Say OK in one word"
nc reminder delete "$DELETE_ID"

log "Verifying the deleted reminder is absent from the list..."
after_delete_list="$(nc reminder list --json 2>/dev/null || true)"
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
