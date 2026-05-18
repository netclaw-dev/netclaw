#!/usr/bin/env bash
# Shared helpers for the native smoke harness — source, do not execute.
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
    echo "ERROR: no timeout mechanism (timeout/gtimeout/perl) found; cannot bound '$*'." >&2
    return 1
  fi
}

# ── Daemon lifecycle ─────────────────────────────────────────────────────────

# ensure_daemon_port_free — block until 127.0.0.1:5199 has no LISTEN socket.
# Every tape and scenario daemon binds the same fixed port; a daemon orphaned
# by an earlier NETCLAW_HOME is invisible to `netclaw daemon stop` (which only
# signals the PID in the current home's PID file) and will squat the port,
# making every later daemon crash on bind. Hard-kill any such straggler.
# Returns non-zero if the port is still held after the timeout.
ensure_daemon_port_free() {
  local port=5199
  local deadline=$((SECONDS + 30))
  while (( SECONDS < deadline )); do
    local holders
    holders="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
    [[ -z "$holders" ]] && return 0
    # Allow a graceful exit a moment before resorting to a hard kill.
    sleep 1
    holders="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
    [[ -z "$holders" ]] && return 0
    log "port ${port} still held by PID(s): ${holders} — hard-killing."
    # shellcheck disable=SC2086 # word-split is intended: holders may be multi-PID
    kill -9 $holders 2>/dev/null || pkill -9 -f netclawd 2>/dev/null || true
    sleep 1
  done
  log "ERROR: port ${port} never freed."
  return 1
}

# start_daemon — launch the native daemon detached and poll until it reports
# running. Returns non-zero on timeout.
start_daemon() {
  : "${NETCLAW_SMOKE_CLI:?NETCLAW_SMOKE_CLI must be set}"
  # Defense in depth: a daemon orphaned by an earlier tape/scenario would still
  # hold :5199 and make this start crash on bind. Clear it before launching.
  if ! ensure_daemon_port_free; then
    log "ERROR: daemon port not free; refusing to start."
    return 1
  fi
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
  # `daemon stop` only signals the PID in this NETCLAW_HOME's PID file; make
  # sure the listening socket is actually released before the next daemon
  # tries to bind it.
  ensure_daemon_port_free || true
}

# ── Scenario helpers ─────────────────────────────────────────────────────────

# Smoke model + Ollama endpoint defaults — shared by every scenario so
# they cannot drift apart.
SMOKE_MODEL="${SMOKE_OLLAMA_MODEL:-qwen2:0.5b}"
OLLAMA_ENDPOINT="${SMOKE_OLLAMA_ENDPOINT:-http://localhost:11434}"

# nc — run the netclaw CLI under the per-step timeout.
nc() { run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" "$@"; }

# die <msg> — record a failure, print the summary, exit non-zero.
die() { fail "$1"; summarize || true; exit 1; }

# seed_provider_model — write a minimal provider + main-model config so a
# fresh NETCLAW_HOME has a usable provider before the daemon starts.
seed_provider_model() {
  nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
  nc model set main local-ollama "$SMOKE_MODEL"
}

# seed_and_start_daemon — the common scenario preamble: install the daemon
# stop trap, seed config, start the daemon, wait for health. die()s on
# failure.
seed_and_start_daemon() {
  trap stop_daemon EXIT
  log "Seeding provider + model config..."
  seed_provider_model
  log "Starting daemon..."
  start_daemon || die "daemon did not start"
  wait_for_health || die "daemon health endpoint not ready"
}
