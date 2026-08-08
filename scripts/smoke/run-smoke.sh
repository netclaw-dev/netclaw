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
LIGHT_TAPES=(help init-wizard init-existing init-redo-identity provider-add provider-rename config-search config-exposure config-posture config-features config-audience config-channels config-mention-thread config-surfaces config-ops-surfaces config-workspaces-picker config-skill-picker config-back-nav tui-cleanup mcp-permissions approvals model-manager sessions-tui)
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
SHOT_TAPES=(help wizard-screens provider-manager mcp-permissions config-search)
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

# Which frames each capture tape emits. Used by the blank-frame retry
# (run_shot_tape_with_retry): after a tape runs, only its own captures are
# inspected for the transient blank described below. Keep in sync with the
# `Screenshot` directives in tests/smoke/tapes/screenshots/<tape>.tape.
#
# A function with a case is used instead of `declare -A` because macOS ships
# bash 3.2, which has no associative arrays — `declare -A` there parses the
# `[help]=...` entries as indexed-array assignments and aborts under set -u
# (`help: unbound variable`), breaking the non-screenshot smoke modes too.
shot_tape_frames() {
  case "$1" in
    help)             echo "help" ;;
    wizard-screens)   echo "wizard-provider-picker wizard-security-posture" ;;
    provider-manager) echo "provider-manager-empty" ;;
    mcp-permissions)  echo "mcp-permissions-server-list mcp-permissions-tool-grid" ;;
    config-search)    echo "config-search-selection config-search-brave-entry config-search-saved" ;;
    *)                echo "" ;;
  esac
}

# Max attempts per tape when a transient blank frame is detected. A TUI screen
# can render momentarily blank because Termina emits a full-screen clear
# ([2J) + repaint as one write on a startup resize event, and VHS can
# sample the PNG between the clear and the repaint half of that same write.
# The write is atomic from the app's side (real users never see it); only VHS's
# mid-write PTY sampling does. Re-running the tape re-captures a settled frame.
SHOT_BLANK_RETRIES="${SHOT_BLANK_RETRIES:-5}"

# Pixel tolerance (ImageMagick AE) for a frame to count as matching its
# baseline. Shared by compare_shot_frame (the pass/fail gate) and the retry
# trigger (a capture above this differs enough to re-run). ~2 character cells —
# clears a single shell-cursor cell (~493 px) while still failing on real
# content changes (thousands of px). See compare_shot_frame for the rationale.
SHOT_AE_TOLERANCE="${SHOT_AE_TOLERANCE:-1000}"

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
  local user_home="${RUN_ROOT}/home/user-shot-${tape}"
  rm -rf "$home"
  rm -rf "$user_home"
  mkdir -p "$user_home"
  if ! HOME="$user_home" \
       NETCLAW_HOME="$home" \
       NETCLAW_DAEMON_ENDPOINT="$SMOKE_DAEMON_BASE_URL" \
       NETCLAW_DAEMON__PORT="$SMOKE_DAEMON_PORT" \
       DAEMON_BASE_URL="$SMOKE_DAEMON_BASE_URL" \
       DAEMON_PORT="$SMOKE_DAEMON_PORT" \
       NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
       NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
       ARTIFACT_DIR="${SMOKE_LOG_DIR}/tapes/shot-${tape}" \
       TAPE_PREAMBLE="$SHOT_PREAMBLE" \
       TAPE_BODY_DIR="$SHOT_TAPES_DIR" \
       bash "${SMOKE_SCRIPTS}/run-native-tape.sh" "$tape"; then
    failed+=("shot-tape:${tape}")
  fi
}

# frame_is_blank <png> — true if the capture is a near-uniform frame, i.e. the
# transient Termina full-refresh blank (see SHOT_BLANK_RETRIES). Baseline-
# independent: it counts unique colors with ImageMagick `identify %k`. A blank
# frame is the solid theme background (~1 color); any populated TUI screen has
# hundreds. The threshold (16) sits far below the sparsest real frame and far
# above a blank, so it never misclassifies a real screen as blank.
frame_is_blank() {
  local png="$1"
  command -v identify >/dev/null 2>&1 || return 1   # can't tell → treat as not blank
  [[ -f "$png" ]] || return 1                        # missing capture is handled elsewhere
  local colors
  colors=$(identify -format '%k' "$png" 2>/dev/null || echo "")
  [[ "$colors" =~ ^[0-9]+$ ]] || return 1
  (( colors < 16 ))
}

# frame_needs_retry <frame> — true if the capture looks like a transient Termina
# full-refresh artifact that re-running the tape can clear. Two shapes:
#   * fully blank (frame_is_blank) — VHS sampled the [2J-cleared frame.
#   * partial/garbled — VHS sampled mid-repaint, so only the top rows landed
#     (e.g. the MCP tool grid captured before its lower rows painted). Such a
#     frame has plenty of colors (so frame_is_blank misses it) but differs from
#     baseline by far more than the cursor tolerance.
# A genuine regression also trips the second branch, but it reproduces every
# attempt and so still fails at compare time — only the latency differs.
frame_needs_retry() {
  local frame="$1"
  local capture="/tmp/shot-${frame}.png"
  local baseline="${SHOT_BASELINE_DIR}/${frame}.approved.png"
  [[ -f "$capture" ]] || return 1
  frame_is_blank "$capture" && return 0
  command -v compare >/dev/null 2>&1 || return 1
  [[ -f "$baseline" ]] || return 1
  local ae ae_int
  ae=$(compare -metric AE "$baseline" "$capture" /dev/null 2>&1 || true)
  ae_int="${ae%%.*}"; ae_int="${ae_int// /}"
  [[ "$ae_int" =~ ^[0-9]+$ ]] || return 1
  (( ae_int > SHOT_AE_TOLERANCE ))
}

# run_shot_tape_with_retry <tape> — run a capture tape, then inspect the frames
# it emits. If any is a transient blank or missing (SHOT_BLANK_RETRIES), re-run
# the whole tape so the next attempt captures a settled frame. Bounded so a
# genuinely broken tape still fails at compare time instead of looping forever.
#
# Two transient shapes are retried:
#   * blank / partial frame — VHS sampled between a Termina [2J clear and
#     repaint (frame_needs_retry).
#   * missing capture — the tape timed out (e.g. a Wait+Screen anchor fired
#     too early) before reaching the Screenshot command. frame_needs_retry
#     returns false for missing files, so we handle this case explicitly.
#
# In both cases the tape-level failure added by run_shot_tape to failed[] is
# rolled back before the retry so that a clean retry does not count as a run
# failure. If all SHOT_BLANK_RETRIES attempts produce a bad frame, the last
# tape-level failure is left in place for compare_shot_frame to report on.
run_shot_tape_with_retry() {
  local tape="$1"
  local frames
  frames="$(shot_tape_frames "$tape")"
  local attempt=1
  while :; do
    local failed_before=${#failed[@]}
    run_shot_tape "$tape"
    [[ -z "$frames" ]] && return          # no frame map → accept the single run

    local bad="" f
    for f in $frames; do
      # A missing capture means the tape timed out before reaching Screenshot.
      # Treat it the same as a blank/partial frame: retry if budget remains.
      if [[ ! -f "/tmp/shot-${f}.png" ]]; then
        bad="${f} (missing — tape timed out)"
        break
      fi
      if frame_needs_retry "$f"; then
        bad="$f"
        break
      fi
    done

    if [[ -z "$bad" ]]; then
      # All captures present and settled. Remove any tape-level failure that
      # run_shot_tape added for this attempt — a clean retry is not a failure.
      if (( ${#failed[@]} > failed_before )); then
        failed=("${failed[@]:0:$failed_before}")
      fi
      return
    fi

    if (( attempt >= SHOT_BLANK_RETRIES )); then
      echo "  WARN: ${tape} produced a transient frame (${bad}) on all ${attempt} attempts;" >&2
      echo "        leaving it for compare_shot_frame to fail on." >&2
      return
    fi
    echo "  RETRY: ${tape} attempt ${attempt} produced a transient frame (${bad}) —" >&2
    echo "         re-running tape (blank or partial Termina full-refresh capture)." >&2
    # Roll back the tape-level failure before the next attempt.
    if (( ${#failed[@]} > failed_before )); then
      failed=("${failed[@]:0:$failed_before}")
    fi
    attempt=$((attempt + 1))
    for f in $frames; do rm -f "/tmp/shot-${f}.png"; done   # clear stale captures
  done
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

  # Use ImageMagick pixel comparison rather than cmp -s (byte-for-byte).
  # Two sources of false failures are tolerated:
  #   1. VHS PNG zlib encoder jitter — same pixels, different byte streams
  #      across process invocations (AE = 0, always passes).
  #   2. Terminal cursor block — Set CursorBlink false freezes the cursor but
  #      not its on/off state; the shell-prompt cursor cell can appear or not
  #      between runs. The block is one character cell (measured AE≈493 at this
  #      geometry). AE_CURSOR_TOLERANCE is set to ~2 cells so a single cursor
  #      cell passes with margin, while real regressions still fail — a changed
  #      word/line differs by thousands of px, a blank screen by ~68,000.
  # Fall back to cmp -s only if ImageMagick is absent. The tolerance
  # (SHOT_AE_TOLERANCE) is shared with the retry trigger (frame_needs_retry).
  if command -v compare >/dev/null 2>&1; then
    local ae
    ae=$(compare -metric AE "$baseline" "$capture" /dev/null 2>&1 || true)
    local ae_int="${ae%%.*}"
    ae_int="${ae_int// /}"
    if [[ "${ae_int:-0}" -le "$SHOT_AE_TOLERANCE" ]]; then
      echo "  PASS: ${frame} — pixel-close to baseline (AE=${ae_int:-0})."
      return
    fi
  else
    if cmp -s "$baseline" "$capture"; then
      echo "  PASS: ${frame} — pixel-identical to baseline."
      return
    fi
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
    run_shot_tape_with_retry "$tape"
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
