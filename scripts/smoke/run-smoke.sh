#!/usr/bin/env bash
# Unified native smoke harness entrypoint (no Docker for the binary).
#
# Runs the real netclaw / netclawd binaries natively against a native
# Ollama host process. Drives both the interactive VHS tapes and the
# non-interactive scenario scripts.
#
# Usage:
#   scripts/smoke/run-smoke.sh <profile> [filters...]
#
#   <profile>   light | full | <scenario-or-tape-short-name>
#   [filters]   when <profile> is light/full, restrict to the named
#               tapes/scenarios (optional)
#
# What it does, in order:
#   1) Resolve the binaries: use NETCLAW_SMOKE_CLI / NETCLAW_SMOKE_DAEMON
#      if exported, else publish via scripts/build/publish-binaries.sh.
#   2) Provision native Ollama: install, `ollama serve`, pull models.
#   3) Ensure vhs is installed.
#   4) Run interactive tapes (run-native-tape.sh) + non-interactive
#      scenarios (tests/smoke/scenarios/*.sh). Each gets a fresh
#      NETCLAW_HOME under a run-scoped temp root.
#   5) Collect artifacts on failure; tear down.
#   6) Exit non-zero if anything failed.
#
# Environment knobs:
#   NETCLAW_SMOKE_CLI / NETCLAW_SMOKE_DAEMON  pre-built binary paths
#   SMOKE_RID                publish RID            (default: linux-x64)
#   SMOKE_OLLAMA_MODEL       primary model          (default: qwen2:0.5b)
#   SMOKE_OLLAMA_ALT_MODEL   alternate model        (default: all-minilm:latest)
#   SMOKE_LOG_DIR            artifact dir           (default: ./smoke-logs)
#   KEEP_RUN_ROOT            set 1 to keep the temp run root

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SMOKE_SCRIPTS="${ROOT_DIR}/scripts/smoke"
TAPES_DIR="${ROOT_DIR}/tests/smoke/tapes"
SCENARIOS_DIR="${ROOT_DIR}/tests/smoke/scenarios"

SMOKE_RID="${SMOKE_RID:-linux-x64}"
SMOKE_LOG_DIR="${SMOKE_LOG_DIR:-${ROOT_DIR}/smoke-logs}"

# Cheapest harness checks first so a harness-level break fails fast
# before paying for the wizard + probe tapes.
LIGHT_TAPES=(help init-wizard provider-add provider-rename tui-cleanup)
FULL_TAPES=("${LIGHT_TAPES[@]}")

LIGHT_SCENARIOS=(
  daemon-lifecycle
  provider-model-cli
  context-window
  sessions-and-chat
  stats
  reminders
  pairing
)
FULL_SCENARIOS=("${LIGHT_SCENARIOS[@]}")

usage() {
  sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

if [[ $# -lt 1 ]]; then
  usage
fi

mode="$1"; shift || true
filters=("$@")

# ── Resolve which tapes / scenarios to run ───────────────────────────────────

tapes=()
scenarios=()

case "$mode" in
  light)
    tapes=("${LIGHT_TAPES[@]}")
    scenarios=("${LIGHT_SCENARIOS[@]}")
    ;;
  full)
    tapes=("${FULL_TAPES[@]}")
    scenarios=("${FULL_SCENARIOS[@]}")
    ;;
  *)
    if [[ -f "${TAPES_DIR}/${mode}.tape" ]]; then
      tapes=("$mode")
    elif [[ -f "${SCENARIOS_DIR}/${mode}.sh" ]]; then
      scenarios=("$mode")
    else
      echo "ERROR: '${mode}' is not 'light', 'full', or a known tape/scenario." >&2
      echo "       Tapes:     ${TAPES_DIR}/<name>.tape" >&2
      echo "       Scenarios: ${SCENARIOS_DIR}/<name>.sh" >&2
      exit 2
    fi
    ;;
esac

# Optional filtering when light/full was requested.
if (( ${#filters[@]} > 0 )) && [[ "$mode" == "light" || "$mode" == "full" ]]; then
  filtered_tapes=()
  filtered_scenarios=()
  for f in "${filters[@]}"; do
    for t in "${tapes[@]}"; do
      [[ "$t" == "$f" ]] && filtered_tapes+=("$t")
    done
    for s in "${scenarios[@]}"; do
      [[ "$s" == "$f" ]] && filtered_scenarios+=("$s")
    done
  done
  tapes=("${filtered_tapes[@]:-}")
  scenarios=("${filtered_scenarios[@]:-}")
  # Strip the empty placeholder a `[@]:-` expansion can leave behind.
  [[ "${#tapes[@]}" -eq 1 && -z "${tapes[0]}" ]] && tapes=()
  [[ "${#scenarios[@]}" -eq 1 && -z "${scenarios[0]}" ]] && scenarios=()
fi

# ── Run-scoped temp root ─────────────────────────────────────────────────────

RUN_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/netclaw-smoke.XXXXXX")"
export RUN_ROOT
mkdir -p "${RUN_ROOT}/home"

teardown_done=0
teardown() {
  [[ $teardown_done -eq 1 ]] && return 0
  teardown_done=1
  echo "==> Tearing down native smoke harness..."
  # Stop Ollama if we started it.
  if declare -f ollama_serve_stop >/dev/null 2>&1; then
    ollama_serve_stop || true
  fi
  # Kill any stray daemon process.
  pkill -f netclawd >/dev/null 2>&1 || true
  if [[ "${KEEP_RUN_ROOT:-0}" == "1" ]]; then
    echo "    KEEP_RUN_ROOT=1 — run root retained at: $RUN_ROOT"
  else
    rm -rf "$RUN_ROOT" || true
  fi
}
trap teardown EXIT

# ── 1) Resolve binaries ──────────────────────────────────────────────────────

if [[ -n "${NETCLAW_SMOKE_CLI:-}" && -n "${NETCLAW_SMOKE_DAEMON:-}" ]]; then
  echo "==> Using pre-built binaries from environment."
else
  echo "==> Publishing binaries via publish-binaries.sh (rid=${SMOKE_RID})..."
  publish_out="${RUN_ROOT}/publish"
  bash "${ROOT_DIR}/scripts/build/publish-binaries.sh" \
    --rid "$SMOKE_RID" --component all --output-dir "$publish_out"
  NETCLAW_SMOKE_CLI="${publish_out}/cli/netclaw"
  NETCLAW_SMOKE_DAEMON="${publish_out}/daemon/netclawd"
fi

# Canonicalise to absolute paths.
NETCLAW_SMOKE_CLI="$(cd "$(dirname "$NETCLAW_SMOKE_CLI")" && pwd)/$(basename "$NETCLAW_SMOKE_CLI")"
NETCLAW_SMOKE_DAEMON="$(cd "$(dirname "$NETCLAW_SMOKE_DAEMON")" && pwd)/$(basename "$NETCLAW_SMOKE_DAEMON")"

if [[ ! -x "$NETCLAW_SMOKE_CLI" ]]; then
  echo "ERROR: netclaw CLI binary not found / not executable: $NETCLAW_SMOKE_CLI" >&2
  exit 1
fi
if [[ ! -x "$NETCLAW_SMOKE_DAEMON" ]]; then
  echo "ERROR: netclawd daemon binary not found / not executable: $NETCLAW_SMOKE_DAEMON" >&2
  exit 1
fi

# NETCLAW_DAEMON_PATH makes the CLI's daemon resolver find the daemon binary.
export NETCLAW_SMOKE_CLI NETCLAW_SMOKE_DAEMON
export NETCLAW_DAEMON_PATH="$NETCLAW_SMOKE_DAEMON"

echo "    NETCLAW_SMOKE_CLI=${NETCLAW_SMOKE_CLI}"
echo "    NETCLAW_SMOKE_DAEMON=${NETCLAW_SMOKE_DAEMON}"

# ── 2) Provision native Ollama ───────────────────────────────────────────────

# shellcheck source=lib/ollama.sh
. "${SMOKE_SCRIPTS}/lib/ollama.sh"

echo "==> Ensuring Ollama is installed..."
ollama_ensure_installed

echo "==> Starting Ollama serve..."
ollama_serve_start

echo "==> Pulling smoke models..."
ollama_pull "$SMOKE_OLLAMA_MODEL"
ollama_pull "$SMOKE_OLLAMA_ALT_MODEL"

# ── 3) Ensure vhs ────────────────────────────────────────────────────────────

echo "==> Ensuring vhs is installed..."
bash "${SMOKE_SCRIPTS}/install-vhs.sh"

# ── 4) Run tapes + scenarios ─────────────────────────────────────────────────

failed=()

run_one_tape() {
  local tape="$1"
  echo
  echo "════════════════════════════════════════════════════════"
  echo "Tape: ${tape}"
  echo "════════════════════════════════════════════════════════"
  local home="${RUN_ROOT}/home/tape-${tape}"
  rm -rf "$home"
  if ! NETCLAW_HOME="$home" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       ARTIFACT_DIR="${SMOKE_LOG_DIR}/tapes/${tape}" \
       bash "${SMOKE_SCRIPTS}/run-native-tape.sh" "$tape"; then
    failed+=("tape:${tape}")
  fi
}

run_one_scenario() {
  local scenario="$1"
  echo
  echo "════════════════════════════════════════════════════════"
  echo "Scenario: ${scenario}"
  echo "════════════════════════════════════════════════════════"
  local home="${RUN_ROOT}/home/scenario-${scenario}"
  rm -rf "$home"
  mkdir -p "$home"
  if ! NETCLAW_HOME="$home" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       NETCLAW_DAEMON_PATH="$NETCLAW_SMOKE_DAEMON" \
       bash "${SCENARIOS_DIR}/${scenario}.sh"; then
    failed+=("scenario:${scenario}")
  fi
}

# Tapes first (harness-level checks fail fast), then scenarios. All are
# attempted regardless of earlier failures so CI gets a complete artifact set.
for tape in "${tapes[@]:-}"; do
  [[ -z "$tape" ]] && continue
  run_one_tape "$tape"
done

for scenario in "${scenarios[@]:-}"; do
  [[ -z "$scenario" ]] && continue
  run_one_scenario "$scenario"
done

# ── 5) Collect artifacts on failure ──────────────────────────────────────────

if (( ${#failed[@]} > 0 )); then
  echo
  echo "==> Failures detected — collecting artifacts..."
  bash "${SMOKE_SCRIPTS}/collect-artifacts.sh" "$SMOKE_LOG_DIR" "$RUN_ROOT" || true
fi

# ── 6) Result ────────────────────────────────────────────────────────────────

if (( ${#failed[@]} > 0 )); then
  echo
  echo "FAILURE: ${#failed[@]} item(s) failed: ${failed[*]}" >&2
  exit 1
fi

echo
echo "All smoke checks passed (${#tapes[@]} tape(s), ${#scenarios[@]} scenario(s))."
