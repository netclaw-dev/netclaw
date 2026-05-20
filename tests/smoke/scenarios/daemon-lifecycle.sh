#!/usr/bin/env bash
# daemon-lifecycle.sh — daemon start / status / health / stop on empty config.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

trap stop_daemon EXIT

log "daemon-lifecycle: starting daemon..."
if start_daemon; then
  pass "daemon start: daemon reports running"
else
  die "daemon start: daemon did not report running"
fi

log "Checking daemon status..."
status_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" daemon status 2>/dev/null || true)"
echo "$status_output"
if [[ "$status_output" == *"Daemon running"* ]]; then
  pass "daemon status: reports running"
else
  fail "daemon status: expected 'Daemon running'"
fi

log "Waiting for daemon health endpoint..."
if wait_for_health; then
  pass "daemon health: /api/health/ready reports healthy"
else
  fail "daemon health: endpoint did not become ready"
fi

log "Stopping daemon..."
stop_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" daemon stop 2>/dev/null || true)"
echo "$stop_output"

log "Verifying daemon stopped..."
stopped_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" daemon status 2>/dev/null || true)"
echo "$stopped_output"
if [[ "$stopped_output" == *"Daemon running"* ]]; then
  fail "daemon stop: daemon still running after stop"
else
  pass "daemon stop: daemon is stopped"
fi

summarize
exit $?
