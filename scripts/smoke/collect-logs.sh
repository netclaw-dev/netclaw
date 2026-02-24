#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.smoke.yml}"
PROJECT_NAME="${PROJECT_NAME:-netclaw-smoke}"
LOG_DIR="${1:-${SMOKE_LOG_DIR:-$ROOT_DIR/smoke-logs}}"
STEP_TIMEOUT_SECONDS="${STEP_TIMEOUT_SECONDS:-60}"

mkdir -p "$LOG_DIR"

run_timed() {
  local seconds="$1"
  shift

  if command -v timeout >/dev/null 2>&1; then
    timeout "${seconds}" "$@"
  else
    "$@"
  fi
}

run_timed "$STEP_TIMEOUT_SECONDS" docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" ps -a >"$LOG_DIR/compose-ps.txt" || true
run_timed "$STEP_TIMEOUT_SECONDS" docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" logs --no-color >"$LOG_DIR/compose-all.log" || true

for service in ollama ollama-init netclaw-sandbox; do
  run_timed "$STEP_TIMEOUT_SECONDS" docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" logs --no-color "$service" \
    >"$LOG_DIR/${service}.log" || true
done

run_timed "$STEP_TIMEOUT_SECONDS" docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" exec -T netclaw-sandbox sh -lc \
  'if [ -f /root/.netclaw/logs/daemon.log ]; then cat /root/.netclaw/logs/daemon.log; fi' \
  >"$LOG_DIR/daemon.log" || true

run_timed "$STEP_TIMEOUT_SECONDS" docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" exec -T netclaw-sandbox sh -lc \
  'if [ -f /root/.netclaw/netclaw.pid ]; then cat /root/.netclaw/netclaw.pid; fi' \
  >"$LOG_DIR/netclaw.pid" || true

echo "Smoke logs collected at: $LOG_DIR"
