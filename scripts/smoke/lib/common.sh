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
#   DAEMON_BASE_URL        health endpoint base        (default loopback:56199)
#   DAEMON_PORT            daemon listen port          (default: port from DAEMON_BASE_URL or 56199)

START_TIMEOUT_SECONDS="${START_TIMEOUT_SECONDS:-180}"
STOP_TIMEOUT_SECONDS="${STOP_TIMEOUT_SECONDS:-90}"
STEP_TIMEOUT_SECONDS="${STEP_TIMEOUT_SECONDS:-120}"
DAEMON_BASE_URL="${DAEMON_BASE_URL:-http://127.0.0.1:56199}"
DAEMON_PORT="${DAEMON_PORT:-${DAEMON_BASE_URL##*:}}"

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

# pid_exe_path <pid> — best-effort lookup of a process's executable path.
# Linux: resolves /proc/<pid>/exe. macOS / restricted-pid fallback: parses
# `lsof -p`. Prints the resolved path on stdout, or nothing if it cannot be
# determined positively (pid gone, permission denied, unparseable lsof
# output, etc.). Callers MUST treat an empty result as "unknown — do not
# act"; the consequence of misidentifying a foreign pid as ours is silently
# SIGKILLing a bystander process.
pid_exe_path() {
  local pid="$1"
  local exe=""

  if [[ -L "/proc/${pid}/exe" ]]; then
    exe="$(readlink -f "/proc/${pid}/exe" 2>/dev/null || true)"
  fi

  # Fall back to lsof when /proc was unavailable or restricted. Both code
  # paths can return junk on restricted pids (lsof has been observed to
  # emit "/proc/<pid>/exe (readlink: Permission denied)" verbatim), so the
  # validation below applies to both.
  if [[ -z "$exe" ]] && command -v lsof >/dev/null 2>&1; then
    exe="$(lsof -p "$pid" -a -d txt -Fn 2>/dev/null \
      | awk '/^n/{print substr($0,2); exit}')"
  fi

  # Only emit results that are unambiguous: an absolute path to a regular
  # file that exists. Anything else is treated as "unknown".
  if [[ -n "$exe" && "$exe" == /* && -f "$exe" ]]; then
    printf '%s\n' "$exe"
  fi
}

# pid_is_smoke_daemon <pid> — true iff the pid's executable resolves to the
# binary at NETCLAW_SMOKE_DAEMON. A pid we cannot positively identify is
# treated as foreign on purpose: the alternative is silently SIGKILLing a
# bystander netclawd (e.g. the developer's production daemon on the same
# box), which the constitution's "no silent fallbacks" rule bans outright.
pid_is_smoke_daemon() {
  local pid="$1"
  : "${NETCLAW_SMOKE_DAEMON:?NETCLAW_SMOKE_DAEMON must be set}"
  local exe
  exe="$(pid_exe_path "$pid")"
  [[ -n "$exe" && "$exe" == "$NETCLAW_SMOKE_DAEMON" ]]
}

# ensure_daemon_port_free — block until the configured smoke daemon port has no LISTEN socket.
# Every tape and scenario daemon binds the same fixed port; a daemon orphaned
# by an earlier NETCLAW_HOME is invisible to `netclaw daemon stop` (which only
# signals the PID in the current home's PID file) and will squat the port,
# making every later daemon crash on bind. Hard-kill any such straggler — but
# only if it is in fact one of *our* smoke daemons. Returns non-zero if the
# port is still held after the timeout OR if it is held by a non-smoke
# process we refuse to touch.
ensure_daemon_port_free() {
  local port="$DAEMON_PORT"
  local deadline=$((SECONDS + 30))
  while (( SECONDS < deadline )); do
    local holders
    holders="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
    [[ -z "$holders" ]] && return 0
    # Allow a graceful exit a moment before resorting to a hard kill.
    sleep 1
    holders="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
    [[ -z "$holders" ]] && return 0

    # Separate smoke-owned holders (safe to hard-kill) from anything else.
    # Touching a foreign holder — e.g. a developer's `netclaw daemon start`
    # running against ~/.netclaw on the same box — would silently destroy
    # unrelated work, so we refuse and exit with a clear diagnostic.
    local pid killed_any=0
    local foreign=()
    for pid in $holders; do
      if pid_is_smoke_daemon "$pid"; then
        log "port ${port} held by smoke daemon (pid=${pid}); hard-killing."
        kill -9 "$pid" 2>/dev/null || true
        killed_any=1
      else
        local exe
        exe="$(pid_exe_path "$pid")"
        foreign+=("pid=${pid} exe=${exe:-<unknown>}")
      fi
    done

    if (( ${#foreign[@]} > 0 )); then
      log "ERROR: port ${port} is held by non-smoke process(es): ${foreign[*]}"
      log "       Refusing to hard-kill — these do not belong to the smoke harness."
      log "       Stop the offending process manually (e.g. \`netclaw daemon stop\`"
      log "       against the right NETCLAW_HOME) before re-running smoke tests."
      return 1
    fi

    (( killed_any == 1 )) && sleep 1
  done
  log "ERROR: port ${port} never freed."
  return 1
}

# kill_smoke_daemon_processes — best-effort hard-kill of every still-running
# smoke daemon. Scoped to NETCLAW_SMOKE_DAEMON's full path so it cannot reach
# a netclawd installed elsewhere on the box (the run-smoke.sh teardown trap
# used to call an unscoped `pkill -f netclawd` here; that killed the
# developer's production daemon — see netclaw-dev/netclaw#1116).
kill_smoke_daemon_processes() {
  : "${NETCLAW_SMOKE_DAEMON:?NETCLAW_SMOKE_DAEMON must be set}"
  # -f matches against the full command line, and the daemon is always
  # launched by absolute path, so this only catches binaries we own.
  pkill -f "$NETCLAW_SMOKE_DAEMON" >/dev/null 2>&1 || true
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
