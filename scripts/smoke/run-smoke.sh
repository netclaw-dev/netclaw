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
#   <profile>   light | full | screenshots | <scenario-or-tape-short-name>
#   [filters]   when <profile> is light/full, restrict to the named
#               tapes/scenarios (optional)
#
# The `screenshots` profile provisions Ollama + the binary exactly like
# `light`, then runs the capture tapes under tests/smoke/tapes/screenshots/
# and compares each emitted PNG byte-for-byte against the approved baseline
# in tests/smoke/screenshots/<frame>.approved.png. Missing baselines and
# mismatches fail the run; the actual/diff PNGs are collected for review.
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
#   NETCLAW_SMOKE_MCP_SERVER pre-published Netclaw.SmokeMcpServer executable
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
SHOT_TAPES_DIR="${ROOT_DIR}/tests/smoke/tapes/screenshots"
SHOT_PREAMBLE="${TAPES_DIR}/screenshot-preamble.tape"
SHOT_BASELINE_DIR="${ROOT_DIR}/tests/smoke/screenshots"

SMOKE_RID="${SMOKE_RID:-linux-x64}"
SMOKE_LOG_DIR="${SMOKE_LOG_DIR:-${ROOT_DIR}/smoke-logs}"

# Cheapest harness checks first so a harness-level break fails fast
# before paying for the wizard + probe tapes.
LIGHT_TAPES=(help init-wizard provider-add provider-rename tui-cleanup)
FULL_TAPES=("${LIGHT_TAPES[@]}")

LIGHT_SCENARIOS=(
  doctor
  daemon-lifecycle
  provider-model-cli
  context-window
  sessions-and-chat
  stats
  reminders
  pairing
  mcp-setup
)
FULL_SCENARIOS=("${LIGHT_SCENARIOS[@]}")

# Screenshot capture tapes (under tests/smoke/tapes/screenshots/). Each tape
# may emit several `Screenshot "/tmp/shot-<frame>.png"` directives. SHOT_FRAMES
# is the full set of frame names the harness compares against baselines — it
# MUST stay in sync with the Screenshot paths in those tapes.
SHOT_TAPES=(help wizard-screens provider-manager)
SHOT_FRAMES=(
  help
  wizard-provider-picker
  wizard-security-posture
  provider-manager-empty
)

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
shots_mode=0

case "$mode" in
  light)
    tapes=("${LIGHT_TAPES[@]}")
    scenarios=("${LIGHT_SCENARIOS[@]}")
    ;;
  full)
    tapes=("${FULL_TAPES[@]}")
    scenarios=("${FULL_SCENARIOS[@]}")
    ;;
  screenshots)
    # Provision like `light` (the wizard frames need a reachable provider)
    # but run the capture tapes + PNG comparison instead of flow tapes.
    shots_mode=1
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
  # Kill any stray smoke daemon. Scoped to NETCLAW_SMOKE_DAEMON's full path
  # so a production netclawd on the same box is never targeted — an
  # unscoped `pkill -f netclawd` used to live here and silently SIGKILLed
  # the developer's daemon mid-flight (see netclaw-dev/netclaw#1116).
  if [[ -n "${NETCLAW_SMOKE_DAEMON:-}" ]]; then
    pkill -f "$NETCLAW_SMOKE_DAEMON" >/dev/null 2>&1 || true
  fi
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

# ── 1b) Resolve the deterministic test MCP server ────────────────────────────

# The mcp-setup scenario registers Netclaw.SmokeMcpServer as a stdio MCP
# server. `dotnet run` is unusable as the MCP command — its build chatter
# corrupts the stdio JSON-RPC channel — so a self-contained published
# executable is required. Honor a pre-published path from CI; otherwise
# publish one into the run root.
if [[ -n "${NETCLAW_SMOKE_MCP_SERVER:-}" ]]; then
  echo "==> Using pre-published smoke MCP server from environment."
else
  echo "==> Publishing smoke MCP server (rid=${SMOKE_RID})..."
  mcp_out="${RUN_ROOT}/mcp-server"
  dotnet publish "${ROOT_DIR}/tests/Netclaw.SmokeMcpServer" \
    -c Release -r "$SMOKE_RID" --self-contained /p:PublishSingleFile=true \
    -o "$mcp_out"
  NETCLAW_SMOKE_MCP_SERVER="${mcp_out}/Netclaw.SmokeMcpServer"
fi

NETCLAW_SMOKE_MCP_SERVER="$(cd "$(dirname "$NETCLAW_SMOKE_MCP_SERVER")" && pwd)/$(basename "$NETCLAW_SMOKE_MCP_SERVER")"
if [[ ! -x "$NETCLAW_SMOKE_MCP_SERVER" ]]; then
  echo "ERROR: smoke MCP server not found / not executable: $NETCLAW_SMOKE_MCP_SERVER" >&2
  exit 1
fi
export NETCLAW_SMOKE_MCP_SERVER
echo "    NETCLAW_SMOKE_MCP_SERVER=${NETCLAW_SMOKE_MCP_SERVER}"

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

# ── Screenshot regression ────────────────────────────────────────────────────

# Artifact dir for screenshot review PNGs (actual / diff / candidate).
SHOT_ARTIFACT_DIR="${SMOKE_LOG_DIR}/screenshots"

# run_shot_tape <tape> — run one capture tape through run-native-tape.sh,
# pointed at the screenshot preamble + tapes/screenshots/ body dir. The
# capture tapes have no assertion; run-native-tape.sh handles that already.
run_shot_tape() {
  local tape="$1"
  echo
  echo "════════════════════════════════════════════════════════"
  echo "Screenshot tape: ${tape}"
  echo "════════════════════════════════════════════════════════"
  local home="${RUN_ROOT}/home/shot-${tape}"
  rm -rf "$home"
  if ! NETCLAW_HOME="$home" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       ARTIFACT_DIR="${SMOKE_LOG_DIR}/tapes/shot-${tape}" \
       TAPE_PREAMBLE="$SHOT_PREAMBLE" \
       TAPE_BODY_DIR="$SHOT_TAPES_DIR" \
       bash "${SMOKE_SCRIPTS}/run-native-tape.sh" "$tape"; then
    failed+=("shot-tape:${tape}")
  fi
}

# compare_shot_frame <frame> — compare /tmp/shot-<frame>.png against the
# committed baseline. Records a failure (and writes review PNGs) on a
# missing capture, missing baseline, or pixel mismatch.
compare_shot_frame() {
  local frame="$1"
  local capture="/tmp/shot-${frame}.png"
  local baseline="${SHOT_BASELINE_DIR}/${frame}.approved.png"
  mkdir -p "$SHOT_ARTIFACT_DIR"

  if [[ ! -f "$capture" ]]; then
    echo "  FAIL: ${frame} — no capture at ${capture} (tape did not emit it)" >&2
    failed+=("shot:${frame}")
    return
  fi

  if [[ ! -f "$baseline" ]]; then
    cp "$capture" "${SHOT_ARTIFACT_DIR}/${frame}.actual.png"
    echo "  FAIL: ${frame} — no approved baseline." >&2
    echo "        Review the uploaded PNG and commit it as" >&2
    echo "        tests/smoke/screenshots/${frame}.approved.png" >&2
    echo "        (actual saved to ${SHOT_ARTIFACT_DIR}/${frame}.actual.png)" >&2
    failed+=("shot:${frame}")
    return
  fi

  if cmp -s "$baseline" "$capture"; then
    echo "  PASS: ${frame} — pixel-identical to baseline."
    return
  fi

  # Mismatch — keep the actual, and a visual diff if ImageMagick is around.
  cp "$capture" "${SHOT_ARTIFACT_DIR}/${frame}.actual.png"
  echo "  FAIL: ${frame} — differs from baseline." >&2
  echo "        actual saved to ${SHOT_ARTIFACT_DIR}/${frame}.actual.png" >&2
  if command -v compare >/dev/null 2>&1; then
    # `compare` exits non-zero on any difference; that is expected here.
    compare "$baseline" "$capture" "${SHOT_ARTIFACT_DIR}/${frame}.diff.png" \
      >/dev/null 2>&1 || true
    if [[ -f "${SHOT_ARTIFACT_DIR}/${frame}.diff.png" ]]; then
      echo "        diff saved to ${SHOT_ARTIFACT_DIR}/${frame}.diff.png" >&2
    fi
  else
    echo "        (ImageMagick 'compare' not found — no diff PNG generated)" >&2
  fi
  failed+=("shot:${frame}")
}

if [[ "$shots_mode" -eq 1 ]]; then
  # Fresh /tmp so a stale capture from an earlier run cannot be compared.
  rm -f /tmp/shot-*.png
  for tape in "${SHOT_TAPES[@]}"; do
    run_shot_tape "$tape"
  done

  echo
  echo "════════════════════════════════════════════════════════"
  echo "Screenshot comparison"
  echo "════════════════════════════════════════════════════════"
  for frame in "${SHOT_FRAMES[@]}"; do
    compare_shot_frame "$frame"
  done
else
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
fi

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
if [[ "$shots_mode" -eq 1 ]]; then
  echo "All screenshot frames matched their baselines (${#SHOT_FRAMES[@]} frame(s))."
else
  echo "All smoke checks passed (${#tapes[@]} tape(s), ${#scenarios[@]} scenario(s))."
fi
