#!/usr/bin/env bash
# Native Ollama provisioning for the native smoke harness.
#
# Source this file; do not execute it. It expects RUN_ROOT to be set by
# the caller (run-smoke.sh) so the serve PID + log land under the run root.
#
# Functions:
#   ollama_ensure_installed   install ollama if it is not on PATH (Linux only)
#   ollama_serve_start        start `ollama serve` in the background, wait ready
#   ollama_serve_stop         kill the recorded serve PID if still alive
#   ollama_pull <model>       pull a model with retries (top flake source)
#
# Environment knobs:
#   SMOKE_OLLAMA_MODEL       primary model       (default: qwen2:0.5b)
#   SMOKE_OLLAMA_ALT_MODEL   alternate model     (default: all-minilm:latest)
#   OLLAMA_HOST              host:port to bind   (default: 127.0.0.1:11434)
#   RUN_ROOT                 run-scoped temp dir (set by run-smoke.sh)

SMOKE_OLLAMA_MODEL="${SMOKE_OLLAMA_MODEL:-qwen2:0.5b}"
SMOKE_OLLAMA_ALT_MODEL="${SMOKE_OLLAMA_ALT_MODEL:-all-minilm:latest}"
export OLLAMA_HOST="${OLLAMA_HOST:-127.0.0.1:11434}"

# Endpoint derived from OLLAMA_HOST; the smoke API probes always use 127.0.0.1.
OLLAMA_API_BASE="${OLLAMA_API_BASE:-http://${OLLAMA_HOST}}"

OLLAMA_PID_FILE="${OLLAMA_PID_FILE:-${RUN_ROOT:-/tmp}/ollama.pid}"
OLLAMA_SERVE_LOG="${OLLAMA_SERVE_LOG:-${RUN_ROOT:-/tmp}/ollama-serve.log}"

ollama_ensure_installed() {
  if command -v ollama >/dev/null 2>&1; then
    echo "ollama already installed at $(command -v ollama)"
    return 0
  fi

  local uname_s
  uname_s="$(uname -s)"
  case "$uname_s" in
    Linux)
      echo "Installing ollama via official install script..."
      curl -fsSL https://ollama.com/install.sh | sh
      ;;
    Darwin)
      if ! command -v brew >/dev/null 2>&1; then
        echo "ERROR: ollama is not installed and Homebrew ('brew') is not on PATH." >&2
        echo "       Install Homebrew from https://brew.sh, then: brew install ollama" >&2
        return 1
      fi
      echo "Installing ollama via Homebrew..."
      brew install ollama
      ;;
    *)
      echo "ERROR: ollama is not installed and automatic install is not supported on ${uname_s}." >&2
      echo "       See https://ollama.com/download for supported platforms." >&2
      return 1
      ;;
  esac

  if ! command -v ollama >/dev/null 2>&1; then
    echo "ERROR: ollama install completed but 'ollama' is still not on PATH." >&2
    return 1
  fi
  echo "ollama installed at $(command -v ollama)"
}

ollama_serve_start() {
  local ready_timeout="${OLLAMA_READY_TIMEOUT:-120}"

  if curl -fsS "${OLLAMA_API_BASE}/api/tags" >/dev/null 2>&1; then
    echo "ollama already serving at ${OLLAMA_API_BASE}; not starting a new instance."
    return 0
  fi

  echo "Starting 'ollama serve' (OLLAMA_HOST=${OLLAMA_HOST})..."
  mkdir -p "$(dirname "$OLLAMA_SERVE_LOG")"
  ollama serve >"$OLLAMA_SERVE_LOG" 2>&1 &
  local pid=$!
  echo "$pid" >"$OLLAMA_PID_FILE"
  echo "ollama serve started (pid=${pid}); log: ${OLLAMA_SERVE_LOG}"

  local deadline=$((SECONDS + ready_timeout))
  while (( SECONDS < deadline )); do
    if curl -fsS "${OLLAMA_API_BASE}/api/tags" >/dev/null 2>&1; then
      echo "ollama API ready at ${OLLAMA_API_BASE}."
      return 0
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "ERROR: ollama serve process exited before becoming ready." >&2
      echo "--- ollama serve log ---" >&2
      cat "$OLLAMA_SERVE_LOG" >&2 || true
      return 1
    fi
    sleep 2
  done

  echo "ERROR: ollama API did not become ready within ${ready_timeout}s." >&2
  echo "--- ollama serve log ---" >&2
  cat "$OLLAMA_SERVE_LOG" >&2 || true
  return 1
}

ollama_serve_stop() {
  if [[ ! -f "$OLLAMA_PID_FILE" ]]; then
    return 0
  fi
  local pid
  pid="$(cat "$OLLAMA_PID_FILE" 2>/dev/null || true)"
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    echo "Stopping ollama serve (pid=${pid})..."
    kill "$pid" 2>/dev/null || true
    # Give it a moment, then hard-kill if still alive.
    local deadline=$((SECONDS + 10))
    while (( SECONDS < deadline )); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 1
    done
    kill -9 "$pid" 2>/dev/null || true
  fi
  rm -f "$OLLAMA_PID_FILE"
}

ollama_pull() {
  local model="$1"
  local attempts="${OLLAMA_PULL_ATTEMPTS:-3}"
  local attempt=1

  while (( attempt <= attempts )); do
    echo "Pulling model '${model}' (attempt ${attempt}/${attempts})..."
    if ollama pull "$model"; then
      echo "Model '${model}' pulled."
      return 0
    fi
    echo "WARNING: pull of '${model}' failed (attempt ${attempt}/${attempts})." >&2
    attempt=$((attempt + 1))
    if (( attempt <= attempts )); then
      sleep 5
    fi
  done

  echo "ERROR: failed to pull model '${model}' after ${attempts} attempts." >&2
  return 1
}
