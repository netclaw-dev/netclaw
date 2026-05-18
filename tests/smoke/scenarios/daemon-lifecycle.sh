#!/usr/bin/env bash
# daemon-lifecycle.sh — native daemon start / status / health / stop.
#
# Folded from scripts/smoke/check.sh (~lines 85-153). De-Dockerized: the
# daemon runs as a native host process bound to loopback 127.0.0.1:5199.
#
# Self-contained: starts a fresh daemon, asserts, stops it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

cleanup() { stop_daemon; }
trap cleanup EXIT

log "daemon-lifecycle: starting daemon..."
if start_daemon; then
  pass "daemon start: daemon reports running"
else
  fail "daemon start: daemon did not report running"
  summarize || exit 1
  exit 1
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
