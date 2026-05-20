#!/usr/bin/env bash
# reminders.sh — reminder create / list / history / delete lifecycle.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

REMINDER_WAIT_TIMEOUT="${REMINDER_WAIT_TIMEOUT:-150}"

seed_and_start_daemon

REMINDER_ID="smoke-lifecycle-$$"

log "Testing reminder create (one-shot in 1m, id=$REMINDER_ID)..."
nc reminder create "$REMINDER_ID" once 1m "Say OK in one word"

log "Verifying reminder appears in list..."
reminder_list="$(nc reminder list 2>/dev/null || true)"
echo "$reminder_list"
if [[ "$reminder_list" == *"$REMINDER_ID"* ]]; then
  pass "reminder list: includes $REMINDER_ID"
else
  fail "reminder list: expected $REMINDER_ID"
fi

log "Waiting up to ${REMINDER_WAIT_TIMEOUT}s for reminder to execute and record history..."
history_found=false
deadline=$((SECONDS + REMINDER_WAIT_TIMEOUT))
while (( SECONDS < deadline )); do
  history_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" reminder history "$REMINDER_ID" --last 5 2>/dev/null || true)"
  if [[ "$history_output" == *"fired_at"* ]]; then
    history_found=true
    log "Reminder executed. History:"
    echo "$history_output"
    break
  fi
  sleep 5
done

if [[ "$history_found" == "true" ]]; then
  pass "reminder history: $REMINDER_ID executed and recorded history"
else
  fail "reminder history: timed out waiting for $REMINDER_ID to execute"
fi

log "Deleting reminder $REMINDER_ID..."
nc reminder delete "$REMINDER_ID"

log "Verifying reminder is absent from list after delete..."
after_delete_list="$(nc reminder list 2>/dev/null || true)"
echo "$after_delete_list"
if [[ "$after_delete_list" == *"$REMINDER_ID"* ]]; then
  fail "reminder delete: $REMINDER_ID still in list after delete"
else
  pass "reminder delete: $REMINDER_ID absent from list"
fi

log "Verifying history returns not-found for deleted reminder..."
history_exit=0
run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" reminder history "$REMINDER_ID" >/dev/null 2>&1 || history_exit=$?
if [[ "$history_exit" -eq 0 ]]; then
  fail "reminder history: expected non-zero exit for deleted reminder $REMINDER_ID"
else
  pass "reminder history: returned exit $history_exit for deleted reminder"
fi

summarize
exit $?
