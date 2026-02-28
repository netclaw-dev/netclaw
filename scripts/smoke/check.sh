#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.smoke.yml}"
PROJECT_NAME="${PROJECT_NAME:-netclaw-smoke}"
INIT_TIMEOUT_SECONDS="${INIT_TIMEOUT_SECONDS:-1200}"
STEP_TIMEOUT_SECONDS="${STEP_TIMEOUT_SECONDS:-120}"
START_TIMEOUT_SECONDS="${START_TIMEOUT_SECONDS:-180}"
STOP_TIMEOUT_SECONDS="${STOP_TIMEOUT_SECONDS:-90}"

compose() {
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" "$@"
}

run_sandbox() {
  compose exec -T netclaw-sandbox "$@"
}

run_sandbox_timed() {
  local seconds="$1"
  shift

  if command -v timeout >/dev/null 2>&1; then
    timeout "${seconds}" docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" exec -T netclaw-sandbox "$@"
  else
    run_sandbox "$@"
  fi
}

wait_for_ollama_init() {
  local init_id
  init_id="$(compose ps -a -q ollama-init)"
  if [[ -z "$init_id" ]]; then
    echo "ollama-init container not found. Run scripts/smoke/up.sh first."
    return 1
  fi

  local deadline=$((SECONDS + INIT_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    local status
    local exit_code
    status="$(docker inspect -f '{{.State.Status}}' "$init_id")"
    exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$init_id")"

    if [[ "$status" == "exited" ]]; then
      if [[ "$exit_code" == "0" ]]; then
        echo "ollama-init completed successfully."
        return 0
      fi

      echo "ollama-init failed with exit code $exit_code."
      compose logs ollama-init
      return 1
    fi

    sleep 5
  done

  echo "Timed out waiting for ollama-init to complete after ${INIT_TIMEOUT_SECONDS}s."
  compose logs ollama-init
  return 1
}

ensure_sandbox_running() {
  local sandbox_id
  sandbox_id="$(compose ps -q netclaw-sandbox)"
  if [[ -z "$sandbox_id" ]]; then
    echo "netclaw-sandbox container not found."
    return 1
  fi

  local status
  status="$(docker inspect -f '{{.State.Status}}' "$sandbox_id")"
  if [[ "$status" != "running" ]]; then
    echo "netclaw-sandbox is not running (status=$status)."
    compose logs netclaw-sandbox
    return 1
  fi

  return 0
}

start_daemon_with_timeout() {
  echo "Starting daemon (detached exec to avoid stdio hang)..."
  compose exec -T -d netclaw-sandbox netclaw daemon start >/dev/null

  local deadline=$((SECONDS + START_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    local status_output
    status_output="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw daemon status || true)"
    if [[ "$status_output" == *"Daemon running"* ]]; then
      echo "$status_output"
      return 0
    fi

    sleep 2
  done

  echo "Timed out waiting for daemon to report running after ${START_TIMEOUT_SECONDS}s."
  return 1
}

cleanup() {
  run_sandbox_timed "$STOP_TIMEOUT_SECONDS" netclaw daemon stop >/dev/null 2>&1 || true
}

trap cleanup EXIT

wait_for_ollama_init
ensure_sandbox_running

start_daemon_with_timeout

echo "Checking daemon status..."
status_output="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw daemon status)"
echo "$status_output"
if [[ "$status_output" != *"Daemon running"* ]]; then
  echo "Expected daemon to be running."
  exit 1
fi

echo "Checking daemon health endpoint..."
health_output="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" curl -fsS http://127.0.0.1:5199/api/health/ready)"
if [[ "$health_output" != "healthy" && "$health_output" != '"healthy"' ]]; then
  echo "Expected /api/health/ready to return healthy, got: $health_output"
  exit 1
fi

echo "Stopping daemon..."
stop_output="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw daemon stop || true)"
echo "$stop_output"

echo "Verifying daemon stopped..."
stopped_output="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw daemon status)"
echo "$stopped_output"
if [[ "$stopped_output" == *"Daemon running"* ]]; then
  echo "Expected daemon to be stopped."
  exit 1
fi

# ── Model & Provider CLI smoke tests ──
# These tests exercise the provider/model CLI subcommands against a live
# Ollama instance. We start fresh — the sandbox has env-var config for the
# daemon, but the CLI config files are empty. We use the CLI commands
# themselves to build up config, then verify switching works.

SMOKE_MODEL="${SMOKE_OLLAMA_MODEL:-qwen2:0.5b}"
ALT_MODEL="${SMOKE_OLLAMA_ALT_MODEL:-all-minilm:latest}"

echo "Testing provider add (local-ollama)..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw provider add local-ollama ollama --endpoint http://ollama:11434

echo "Testing provider list..."
provider_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw provider list)"
echo "$provider_list"
if [[ "$provider_list" != *"local-ollama"* ]]; then
  echo "Expected provider list to include local-ollama."
  exit 1
fi

echo "Testing model set (main to $SMOKE_MODEL)..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model set main local-ollama "$SMOKE_MODEL"

echo "Testing model list..."
model_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model list)"
echo "$model_list"
if [[ "$model_list" != *"$SMOKE_MODEL"* ]]; then
  echo "Expected model list to include $SMOKE_MODEL."
  exit 1
fi

echo "Testing model discover..."
discover_output="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model discover local-ollama)"
echo "$discover_output"
if [[ "$discover_output" != *"$SMOKE_MODEL"* ]]; then
  echo "Expected discovered models to include $SMOKE_MODEL."
  exit 1
fi

echo "Testing model switch to alternate model ($ALT_MODEL)..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model set main local-ollama "$ALT_MODEL"
switched_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model list)"
echo "$switched_list"
if [[ "$switched_list" != *"$ALT_MODEL"* ]]; then
  echo "Expected model list to show $ALT_MODEL after switch."
  exit 1
fi

echo "Testing model switch back to original ($SMOKE_MODEL)..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model set main local-ollama "$SMOKE_MODEL"
restored_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model list)"
echo "$restored_list"
if [[ "$restored_list" != *"$SMOKE_MODEL"* ]]; then
  echo "Expected model list to show $SMOKE_MODEL after switch back."
  exit 1
fi

echo "Testing provider add (second provider)..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw provider add test-ollama ollama --endpoint http://ollama:11434
added_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw provider list)"
echo "$added_list"
if [[ "$added_list" != *"test-ollama"* ]]; then
  echo "Expected provider list to include test-ollama after add."
  exit 1
fi

echo "Testing provider remove..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw provider remove test-ollama
removed_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw provider list)"
echo "$removed_list"
if [[ "$removed_list" == *"test-ollama"* ]]; then
  echo "Expected test-ollama to be removed from provider list."
  exit 1
fi

echo "Testing model set fallback then clear..."
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model set fallback local-ollama "$ALT_MODEL"
run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model clear fallback
cleared_list="$(run_sandbox_timed "$STEP_TIMEOUT_SECONDS" netclaw model list)"
echo "$cleared_list"
if [[ "$cleared_list" == *"$ALT_MODEL"* ]]; then
  echo "Expected $ALT_MODEL to be cleared from fallback."
  exit 1
fi

echo "Smoke sandbox checks passed."
