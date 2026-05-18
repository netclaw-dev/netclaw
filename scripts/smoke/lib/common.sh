#!/usr/bin/env bash
# Shared helpers for the native (non-Docker) smoke harness.
#
# Source this file; do not execute it. De-Dockerized versions of the
# helpers that used to live in scripts/smoke/check.sh — the daemon now
# runs as a native host process bound to loopback 127.0.0.1:5199.
#
# Callers must export before sourcing or before calling the helpers:
#   NETCLAW_SMOKE_CLI     absolute path to the `netclaw` binary
#   NETCLAW_SMOKE_DAEMON  absolute path to the `netclawd` binary
#   NETCLAW_DAEMON_PATH   = NETCLAW_SMOKE_DAEMON (CLI daemon resolver)
#   NETCLAW_HOME          per-scenario config/state directory
#
# Environment knobs:
#   START_TIMEOUT_SECONDS  daemon start/health timeout (default: 180)
#   STOP_TIMEOUT_SECONDS   daemon stop timeout         (default: 90)
#   STEP_TIMEOUT_SECONDS   per-command timeout         (default: 120)
#   DAEMON_HEALTH_URL      health endpoint base        (default loopback:5199)

START_TIMEOUT_SECONDS="${START_TIMEOUT_SECONDS:-180}"
STOP_TIMEOUT_SECONDS="${STOP_TIMEOUT_SECONDS:-90}"
STEP_TIMEOUT_SECONDS="${STEP_TIMEOUT_SECONDS:-120}"
DAEMON_BASE_URL="${DAEMON_BASE_URL:-http://127.0.0.1:5199}"

# ── Output / counters ────────────────────────────────────────────────────────

PASS=0
FAIL=0

log()  { echo "[smoke] $*"; }
pass() { echo "  PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "  FAIL: $1" >&2; FAIL=$((FAIL + 1)); }
warn() { echo "  [WARN] $1" >&2; }

# Print the running totals and return non-zero if anything failed. Scenario
# scripts call this at the end and `exit` on its status.
summarize() {
  echo
  echo "[smoke] results: ${PASS} passed, ${FAIL} failed"
  if (( FAIL > 0 )); then
    return 1
  fi
  return 0
}

# ── Portable timeout ─────────────────────────────────────────────────────────

# run_timed <seconds> <cmd...>
# GNU `timeout` (Linux), `gtimeout` (macOS + coreutils), or a perl alarm
# fallback — stock macOS ships no `timeout`. Kept portable so the future
# macOS leg needs no change here.
run_timed() {
  local secs="$1"; shift
  if command -v timeout >/dev/null 2>&1; then
    timeout "${secs}s" "$@"
  elif command -v gtimeout >/dev/null 2>&1; then
    gtimeout "${secs}s" "$@"
  elif command -v perl >/dev/null 2>&1; then
    perl -e 'alarm shift; exec @ARGV' "$secs" "$@"
  else
    "$@"
  fi
}

# ── Daemon lifecycle ─────────────────────────────────────────────────────────

# start_daemon — launch the native daemon detached and poll until it reports
# running. Returns non-zero on timeout.
start_daemon() {
  : "${NETCLAW_SMOKE_CLI:?NETCLAW_SMOKE_CLI must be set}"
  log "Starting daemon (detached) ..."
  # Detach so the CLI's `daemon start` does not hold our stdio.
  run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" daemon start >/dev/null 2>&1 || true

  local deadline=$((SECONDS + START_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    local status_output
    status_output="$(run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" daemon status 2>/dev/null || true)"
    if [[ "$status_output" == *"Daemon running"* ]]; then
      log "Daemon running."
      return 0
    fi
    sleep 2
  done

  log "ERROR: daemon did not report running within ${START_TIMEOUT_SECONDS}s."
  return 1
}

# wait_for_health — poll the daemon's readiness endpoint until healthy.
wait_for_health() {
  local deadline=$((SECONDS + START_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    local health_output
    health_output="$(run_timed "$STEP_TIMEOUT_SECONDS" curl -fsS "${DAEMON_BASE_URL}/api/health/ready" 2>/dev/null || true)"
    if [[ "$health_output" == "healthy" || "$health_output" == '"healthy"' ]]; then
      log "Health endpoint ready."
      return 0
    fi
    sleep 2
  done

  log "ERROR: health endpoint not ready within ${START_TIMEOUT_SECONDS}s."
  return 1
}

# stop_daemon — best-effort daemon stop. Never fails the caller.
stop_daemon() {
  : "${NETCLAW_SMOKE_CLI:?NETCLAW_SMOKE_CLI must be set}"
  run_timed "$STOP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" daemon stop >/dev/null 2>&1 || true
}
