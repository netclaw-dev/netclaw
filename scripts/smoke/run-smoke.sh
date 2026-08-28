#!/usr/bin/env bash
# Unified native smoke harness entrypoint (no Docker for the binary).
#
# Runs the real netclaw / netclawd binaries natively against a native
# OpenAI-compatible smoke LLM process. Drives both the interactive VHS tapes and the
# non-interactive scenario scripts.
#
# Usage:
#   scripts/smoke/run-smoke.sh <profile> [filters...]
#
#   <profile>   light | full | screenshots | <scenario-or-tape-short-name>
#   [filters]   when <profile> is light/full, restrict to the named
#               tapes/scenarios (optional)
#
# The `screenshots` profile provisions the smoke LLM + the binary exactly like
# `light`, then runs the capture tapes under tests/smoke/tapes/screenshots/
# and compares each final lossless PNG frame against the approved baseline
# in tests/smoke/screenshots/<frame>.approved.png. Missing baselines and
# mismatches fail the run; the actual/diff PNGs are collected for review.
#
# What it does, in order:
#   1) Resolve the binaries: use NETCLAW_SMOKE_CLI / NETCLAW_SMOKE_DAEMON
#      if exported, else publish via scripts/build/publish-binaries.sh.
#   2) Start the loopback smoke LLM server.
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
#   NETCLAW_SMOKE_LLM_SERVER pre-published Netclaw.SmokeLlmServer executable
#   SMOKE_LLM_MODEL          primary model          (default: netclaw-smoke-tool-model)
#   SMOKE_LOG_DIR            artifact dir           (default: ./smoke-logs)
#   SMOKE_DAEMON_PORT        isolated daemon port   (default: 56199)
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
LIGHT_TAPES=(help init-wizard init-existing init-redo-identity provider-add provider-rename config-search config-exposure config-posture config-features config-audience config-channels config-mention-thread config-surfaces config-ops-surfaces config-workspaces-picker config-skill-picker config-back-nav tui-cleanup mcp-permissions mcp-permissions-save approvals model-manager sessions-tui)
FULL_TAPES=("${LIGHT_TAPES[@]}")

LIGHT_SCENARIOS=(
  doctor
  daemon-lifecycle
  provider-model-cli
  sessions-and-chat
  stats
  reminders
  pairing
  mcp-setup
  webhook-routes
)
FULL_SCENARIOS=("${LIGHT_SCENARIOS[@]}")

# Screenshot capture tapes are under tests/smoke/tapes/screenshots/. The shared
# preamble records lossless PNG frames. SHOT_FRAMES is the full set of frame
# names that the harness compares against baselines.
SHOT_TAPES=(
  help
  wizard-provider-picker
  wizard-security-posture
  provider-manager-empty
  mcp-permissions-server-list
  mcp-permissions-tool-grid
  config-search-selection
  config-search-brave-entry
  config-search-saved
)
SHOT_FRAMES=(
  help
  wizard-provider-picker
  wizard-security-posture
  provider-manager-empty
  mcp-permissions-server-list
  mcp-permissions-tool-grid
  config-search-selection
  config-search-brave-entry
  config-search-saved
)

# Pixel tolerance for a frame to match its baseline.
# Two character cells cover a shell cursor artifact.
# A real content change differs by thousands of pixels.
SHOT_AE_TOLERANCE="${SHOT_AE_TOLERANCE:-1000}"
SHOT_CAPTURE_ATTEMPTS=3

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

SMOKE_DAEMON_PORT="${SMOKE_DAEMON_PORT:-56199}"
SMOKE_DAEMON_BASE_URL="http://127.0.0.1:${SMOKE_DAEMON_PORT}"

teardown_done=0
teardown() {
  [[ $teardown_done -eq 1 ]] && return 0
  teardown_done=1
  echo "==> Tearing down native smoke harness..."
  if [[ -n "${SMOKE_LLM_PID:-}" ]] && kill -0 "$SMOKE_LLM_PID" 2>/dev/null; then
    kill "$SMOKE_LLM_PID" 2>/dev/null || true
    wait "$SMOKE_LLM_PID" 2>/dev/null || true
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

# ── 2) Start the deterministic loopback smoke LLM ───────────────────────────

if [[ -n "${NETCLAW_SMOKE_LLM_SERVER:-}" ]]; then
  echo "==> Using pre-published smoke LLM server from environment."
else
  echo "==> Publishing smoke LLM server (rid=${SMOKE_RID})..."
  llm_out="${RUN_ROOT}/llm-server"
  dotnet publish "${ROOT_DIR}/tests/Netclaw.SmokeLlmServer" \
    -c Release -r "$SMOKE_RID" --self-contained /p:PublishSingleFile=true \
    -o "$llm_out"
  NETCLAW_SMOKE_LLM_SERVER="${llm_out}/Netclaw.SmokeLlmServer"
fi

NETCLAW_SMOKE_LLM_SERVER="$(cd "$(dirname "$NETCLAW_SMOKE_LLM_SERVER")" && pwd)/$(basename "$NETCLAW_SMOKE_LLM_SERVER")"
if [[ ! -x "$NETCLAW_SMOKE_LLM_SERVER" ]]; then
  echo "ERROR: smoke LLM server not found / not executable: $NETCLAW_SMOKE_LLM_SERVER" >&2
  exit 1
fi

SMOKE_LLM_MODEL="${SMOKE_LLM_MODEL:-netclaw-smoke-tool-model}"
SMOKE_LLM_LOG="${RUN_ROOT}/smoke-llm.log"
SMOKE_LLM_REQUEST_RECORD="${RUN_ROOT}/smoke-llm-requests.jsonl"
"$NETCLAW_SMOKE_LLM_SERVER" --port 0 --request-record "$SMOKE_LLM_REQUEST_RECORD" >"$SMOKE_LLM_LOG" 2>&1 &
SMOKE_LLM_PID=$!
for _ in $(seq 1 100); do
  SMOKE_LLM_ENDPOINT="$(sed -n 's/^\[smoke-llm:listening\] //p' "$SMOKE_LLM_LOG" | head -1)"
  if [[ -n "$SMOKE_LLM_ENDPOINT" ]] && curl -fsS "${SMOKE_LLM_ENDPOINT}/health" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$SMOKE_LLM_PID" 2>/dev/null; then
    echo "ERROR: smoke LLM server exited before it became healthy. Log: $SMOKE_LLM_LOG" >&2
    cat "$SMOKE_LLM_LOG" >&2 || true
    exit 1
  fi
  sleep 0.1
done
if [[ -z "${SMOKE_LLM_ENDPOINT:-}" ]] || ! curl -fsS "${SMOKE_LLM_ENDPOINT}/health" >/dev/null 2>&1; then
  echo "ERROR: smoke LLM server did not become healthy." >&2
  cat "$SMOKE_LLM_LOG" >&2 || true
  exit 1
fi
export NETCLAW_SMOKE_LLM_SERVER SMOKE_LLM_MODEL SMOKE_LLM_ENDPOINT SMOKE_LLM_LOG SMOKE_LLM_REQUEST_RECORD
echo "    SMOKE_LLM_ENDPOINT=${SMOKE_LLM_ENDPOINT}"

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
  local user_home="${RUN_ROOT}/home/user-tape-${tape}"
  rm -rf "$home"
  rm -rf "$user_home"
  mkdir -p "$user_home"
  if ! HOME="$user_home" \
       NETCLAW_HOME="$home" \
       TAPE_USER_HOME="$user_home" \
       NETCLAW_DAEMON_ENDPOINT="$SMOKE_DAEMON_BASE_URL" \
       NETCLAW_DAEMON__PORT="$SMOKE_DAEMON_PORT" \
       DAEMON_BASE_URL="$SMOKE_DAEMON_BASE_URL" \
       DAEMON_PORT="$SMOKE_DAEMON_PORT" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       SMOKE_LLM_ENDPOINT="$SMOKE_LLM_ENDPOINT" \
       SMOKE_LLM_MODEL="$SMOKE_LLM_MODEL" \
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
  local user_home="${RUN_ROOT}/home/user-scenario-${scenario}"
  rm -rf "$home"
  rm -rf "$user_home"
  mkdir -p "$home"
  mkdir -p "$user_home"
  if ! HOME="$user_home" \
       NETCLAW_HOME="$home" \
       TAPE_USER_HOME="$user_home" \
       NETCLAW_DAEMON_ENDPOINT="$SMOKE_DAEMON_BASE_URL" \
       NETCLAW_DAEMON__PORT="$SMOKE_DAEMON_PORT" \
       DAEMON_BASE_URL="$SMOKE_DAEMON_BASE_URL" \
       DAEMON_PORT="$SMOKE_DAEMON_PORT" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       SMOKE_LLM_ENDPOINT="$SMOKE_LLM_ENDPOINT" \
       SMOKE_LLM_MODEL="$SMOKE_LLM_MODEL" \
       NETCLAW_DAEMON_PATH="$NETCLAW_SMOKE_DAEMON" \
       bash "${SCENARIOS_DIR}/${scenario}.sh"; then
    failed+=("scenario:${scenario}")
  fi
}

# ── Screenshot regression ────────────────────────────────────────────────────

# Artifact dir for screenshot review PNGs (actual / diff / candidate).
SHOT_ARTIFACT_DIR="${SMOKE_LOG_DIR}/screenshots"
SHOT_COMPARATOR="${SMOKE_SCRIPTS}/count-png-differences.py"

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
  local user_home="${RUN_ROOT}/home/user-shot-${tape}"
  local frame_dir="/tmp/shot-frames-${tape}"
  rm -rf "$home"
  rm -rf "$user_home"
  rm -rf "$frame_dir"
  mkdir -p "$user_home"
  if ! HOME="$user_home" \
       NETCLAW_HOME="$home" \
       NETCLAW_DAEMON_ENDPOINT="$SMOKE_DAEMON_BASE_URL" \
       NETCLAW_DAEMON__PORT="$SMOKE_DAEMON_PORT" \
       DAEMON_BASE_URL="$SMOKE_DAEMON_BASE_URL" \
       DAEMON_PORT="$SMOKE_DAEMON_PORT" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       SMOKE_LLM_ENDPOINT="$SMOKE_LLM_ENDPOINT" \
       SMOKE_LLM_MODEL="$SMOKE_LLM_MODEL" \
       ARTIFACT_DIR="${SMOKE_LOG_DIR}/tapes/shot-${tape}" \
       TAPE_PREAMBLE="$SHOT_PREAMBLE" \
       TAPE_BODY_DIR="$SHOT_TAPES_DIR" \
       bash "${SMOKE_SCRIPTS}/run-native-tape.sh" "$tape"; then
    failed+=("shot-tape:${tape}")
  fi
}

# copy_final_shot_frame <tape> <frame> — copy the final lossless recorder
# frame after VHS exits. VHS Screenshot can capture a stale browser frame even
# after Wait+Screen observes the settled terminal state.
copy_final_shot_frame() {
  local tape="$1"
  local frame="$2"
  local frame_dir="/tmp/shot-frames-${tape}"
  local final_frame

  final_frame=$(find "$frame_dir" -maxdepth 1 -type f -name '*.png' -print 2>/dev/null \
    | sort | tail -n 1)
  if [[ -z "$final_frame" ]]; then
    echo "  WARN: ${frame} did not produce any lossless recorder frames." >&2
    return 1
  fi

  cp "$final_frame" "/tmp/shot-${frame}.png"
}

# capture_stable_shot <tape> <frame> — require two matching captures before
# baseline comparison. This quorum does not use the baseline. A stable visual
# change reaches compare_shot_frame and fails there.
capture_stable_shot() {
  local tape="$1"
  local frame="$2"
  local candidates=""
  local attempt
  # A tape run inside the loop can add "shot-tape:${tape}" to failed[] even
  # when a later attempt still reaches a clean quorum. A clean retry is not
  # a failure, so the entry is rolled back once quorum is reached.
  local failed_before=${#failed[@]}

  for (( attempt = 1; attempt <= SHOT_CAPTURE_ATTEMPTS; attempt++ )); do
    rm -f "/tmp/shot-${frame}.png"
    run_shot_tape "$tape"

    local capture="/tmp/shot-${frame}.png"
    if ! copy_final_shot_frame "$tape" "$frame"; then
      echo "  WARN: ${frame} attempt ${attempt} did not produce a capture." >&2
      continue
    fi

    local candidate="/tmp/shot-${frame}.candidate-${attempt}.png"
    cp "$capture" "$candidate"

    local previous
    for previous in $candidates; do
      local ae
      if ae=$(python3 "$SHOT_COMPARATOR" "$previous" "$candidate") \
          && [[ "$ae" -le "$SHOT_AE_TOLERANCE" ]]; then
        cp "$candidate" "$capture"
        echo "  STABLE: ${frame} reached a two-capture quorum (AE=${ae})."
        if (( ${#failed[@]} > failed_before )); then
          failed=("${failed[@]:0:$failed_before}")
        fi
        return
      fi
    done

    candidates="${candidates} ${candidate}"
  done

  mkdir -p "$SHOT_ARTIFACT_DIR"
  local candidate
  for candidate in $candidates; do
    cp "$candidate" "$SHOT_ARTIFACT_DIR/$(basename "$candidate")"
  done
  echo "  FAIL: ${frame} did not reach a two-capture quorum." >&2
  failed+=("shot-unstable:${frame}")
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

  # Compare decoded RGBA pixels rather than compressed PNG bytes.
  # Two sources of false failures are tolerated:
  #   1. VHS PNG zlib encoder jitter — same pixels, different byte streams
  #      across process invocations (AE = 0, always passes).
  #   2. Terminal cursor block — Set CursorBlink false freezes the cursor but
  #      not its on/off state; the shell-prompt cursor cell can appear or not
  #      between runs. The block is one character cell (measured AE≈493 at this
  #      geometry). SHOT_AE_TOLERANCE covers about two cells, so a single cursor
  #      cell passes with margin, while real regressions still fail — a changed
  #      word/line differs by thousands of px, a blank screen by ~68,000.
  # SHOT_AE_TOLERANCE applies to all frames.
  local ae
  if ! ae=$(python3 "$SHOT_COMPARATOR" "$baseline" "$capture"); then
    echo "  FAIL: ${frame} — the PNG comparator failed." >&2
    failed+=("shot:${frame}")
    return
  fi
  if [[ "$ae" -le "$SHOT_AE_TOLERANCE" ]]; then
    echo "  PASS: ${frame} — pixel-close to baseline (AE=${ae})."
    return
  fi

  # Keep the actual frame and an FFmpeg difference image.
  cp "$capture" "${SHOT_ARTIFACT_DIR}/${frame}.actual.png"
  echo "  FAIL: ${frame} — differs from baseline." >&2
  echo "        actual saved to ${SHOT_ARTIFACT_DIR}/${frame}.actual.png" >&2
  ffmpeg -y -v error -i "$baseline" -i "$capture" \
    -filter_complex 'blend=all_mode=difference' -frames:v 1 \
    "${SHOT_ARTIFACT_DIR}/${frame}.diff.png" || true
  if [[ -f "${SHOT_ARTIFACT_DIR}/${frame}.diff.png" ]]; then
    echo "        diff saved to ${SHOT_ARTIFACT_DIR}/${frame}.diff.png" >&2
  fi
  failed+=("shot:${frame}")
}

if [[ "$shots_mode" -eq 1 ]]; then
  # Fresh /tmp so a stale capture from an earlier run cannot be compared.
  rm -f /tmp/shot-*.png
  for index in "${!SHOT_TAPES[@]}"; do
    capture_stable_shot "${SHOT_TAPES[$index]}" "${SHOT_FRAMES[$index]}"
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
