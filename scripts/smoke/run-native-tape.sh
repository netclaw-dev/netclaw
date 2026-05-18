#!/usr/bin/env bash
# Run a single VHS tape against the native netclaw binary.
#
# Usage:
#   scripts/smoke/run-native-tape.sh <tape-short-name>
#
# Steps:
#   1) Substitute placeholders in preamble.tape and prepend it to the
#      tape body, producing a combined temp tape.
#   2) Run `vhs` against the combined tape with a hard timeout.
#   3) If a per-tape assertion exists at
#      tests/smoke/assertions/<short-name>.sh, run it.
#   4) On failure, collect artifacts (tape GIF, NETCLAW_HOME logs +
#      config) into ${ARTIFACT_DIR} from the host filesystem.
#
# Required environment (set by run-smoke.sh):
#   NETCLAW_SMOKE_CLI     absolute path to the `netclaw` binary
#   NETCLAW_SMOKE_DAEMON  absolute path to the `netclawd` binary
#
# Environment knobs:
#   NETCLAW_HOME      per-tape home dir; default <tmp>/tape-home-<name>
#   TAPE_TIMEOUT_S    hard timeout per tape (default: 600)
#   ARTIFACT_DIR      failure artifact dir (default: smoke-logs/tapes/<name>)
#   KEEP_TEMP         set to 1 to retain the combined tape for inspection
#   TAPE_PREAMBLE     preamble file to prepend  (default: <TAPES_DIR>/preamble.tape)
#   TAPE_BODY_DIR     directory holding <name>.tape (default: TAPES_DIR)
#
# TAPE_PREAMBLE / TAPE_BODY_DIR let the `screenshots` mode of run-smoke.sh
# point this runner at screenshot-preamble.tape and tests/smoke/tapes/
# screenshots/ without forking the runner. When both are unset the flow-tape
# behavior is byte-identical to before they existed.

set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <tape-short-name>" >&2
  exit 2
fi

TAPE_NAME="$1"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAPES_DIR="${ROOT_DIR}/tests/smoke/tapes"
ASSERT_DIR="${ROOT_DIR}/tests/smoke/assertions"

TAPE_TIMEOUT_S="${TAPE_TIMEOUT_S:-600}"
ARTIFACT_DIR="${ARTIFACT_DIR:-${ROOT_DIR}/smoke-logs/tapes/${TAPE_NAME}}"

: "${NETCLAW_SMOKE_CLI:?NETCLAW_SMOKE_CLI must be set (run via run-smoke.sh)}"
: "${NETCLAW_SMOKE_DAEMON:?NETCLAW_SMOKE_DAEMON must be set (run via run-smoke.sh)}"

# The directory containing both binaries is put on PATH inside the tape.
NETCLAW_BIN_DIR="$(cd "$(dirname "$NETCLAW_SMOKE_CLI")" && pwd)"

# Per-tape NETCLAW_HOME on the host filesystem.
NETCLAW_HOME="${NETCLAW_HOME:-$(mktemp -d)/tape-home-${TAPE_NAME}}"

# Preamble + body dir are overridable so the screenshots mode can swap in
# screenshot-preamble.tape + tests/smoke/tapes/screenshots/. Defaults keep
# the flow-tape behavior unchanged.
preamble="${TAPE_PREAMBLE:-${TAPES_DIR}/preamble.tape}"
body="${TAPE_BODY_DIR:-${TAPES_DIR}}/${TAPE_NAME}.tape"
assertion="${ASSERT_DIR}/${TAPE_NAME}.sh"

if [[ ! -f "$preamble" ]]; then
  echo "ERROR: preamble not found at $preamble" >&2
  exit 1
fi

if [[ ! -f "$body" ]]; then
  echo "ERROR: tape body not found at $body" >&2
  exit 1
fi

if ! command -v vhs >/dev/null 2>&1; then
  echo "ERROR: vhs not on PATH. Run scripts/smoke/install-vhs.sh first." >&2
  exit 1
fi

tmp_dir="$(mktemp -d)"
combined="${tmp_dir}/${TAPE_NAME}.tape"

cleanup() {
  if [[ "${KEEP_TEMP:-0}" == "1" ]]; then
    echo "KEEP_TEMP=1 — combined tape retained at: $combined"
  else
    rm -rf "$tmp_dir"
  fi
}
trap cleanup EXIT

collect_failure_artifacts() {
  echo "==> Collecting failure artifacts to ${ARTIFACT_DIR}"
  set +e
  mkdir -p "${ARTIFACT_DIR}"
  cp -v "/tmp/tape-${TAPE_NAME}.gif" "${ARTIFACT_DIR}/" 2>/dev/null
  cp -v "/tmp/tape-${TAPE_NAME}.png" "${ARTIFACT_DIR}/" 2>/dev/null
  if [[ -d "${NETCLAW_HOME}/logs" ]]; then
    cp -r "${NETCLAW_HOME}/logs" "${ARTIFACT_DIR}/netclaw-home-logs" 2>/dev/null
  fi
  if [[ -f "${NETCLAW_HOME}/config/netclaw.json" ]]; then
    cp -v "${NETCLAW_HOME}/config/netclaw.json" "${ARTIFACT_DIR}/netclaw.json" 2>/dev/null
  fi
  ls -laR "${NETCLAW_HOME}" > "${ARTIFACT_DIR}/netclaw_home.txt" 2>&1
  cp -v "$combined" "${ARTIFACT_DIR}/${TAPE_NAME}.combined.tape" 2>/dev/null
  set -e
  echo "    artifacts: $(ls "${ARTIFACT_DIR}" 2>/dev/null | tr '\n' ' ')"
}

# Substitute placeholders in preamble. Sed delimiter is '|' since paths
# contain '/'. The substituted values are paths set by us, so escaping
# is minimal.
sed \
  -e "s|__NETCLAW_HOME__|${NETCLAW_HOME}|g" \
  -e "s|__NETCLAW_BIN_DIR__|${NETCLAW_BIN_DIR}|g" \
  -e "s|__NETCLAW_DAEMON__|${NETCLAW_SMOKE_DAEMON}|g" \
  -e "s|__TAPE_NAME__|${TAPE_NAME}|g" \
  "$preamble" > "$combined"

# Append body. The body declares its own `Output ...`; last-write-wins
# on Output is fine in vhs.
cat "$body" >> "$combined"

echo "==> Running native tape: ${TAPE_NAME} (timeout=${TAPE_TIMEOUT_S}s)"
echo "    NETCLAW_BIN_DIR=${NETCLAW_BIN_DIR}"
echo "    NETCLAW_HOME=${NETCLAW_HOME}"

vhs_status=0
if command -v timeout >/dev/null 2>&1; then
  timeout --foreground "${TAPE_TIMEOUT_S}" vhs "$combined" || vhs_status=$?
elif command -v gtimeout >/dev/null 2>&1; then
  gtimeout --foreground "${TAPE_TIMEOUT_S}" vhs "$combined" || vhs_status=$?
else
  echo "ERROR: no timeout tool (timeout/gtimeout) found; refusing to run vhs unbounded." >&2
  exit 1
fi

if [[ $vhs_status -ne 0 ]]; then
  echo "FAIL: vhs exited with status ${vhs_status} for tape ${TAPE_NAME}" >&2
  collect_failure_artifacts
  exit "$vhs_status"
fi

# Run per-tape assertion if present.
if [[ -x "$assertion" ]]; then
  echo "==> Running post-tape assertion: ${assertion}"
  assert_status=0
  NETCLAW_HOME="$NETCLAW_HOME" \
  NETCLAW_SMOKE_CLI="$NETCLAW_SMOKE_CLI" \
  NETCLAW_SMOKE_DAEMON="$NETCLAW_SMOKE_DAEMON" \
  NETCLAW_DAEMON_PATH="$NETCLAW_SMOKE_DAEMON" \
  TAPE_NAME="$TAPE_NAME" \
    "$assertion" || assert_status=$?
  if [[ $assert_status -ne 0 ]]; then
    echo "FAIL: assertion failed for tape ${TAPE_NAME} (status=${assert_status})" >&2
    collect_failure_artifacts
    exit "$assert_status"
  fi
elif [[ -f "$assertion" ]]; then
  echo "WARNING: $assertion exists but is not executable; skipping." >&2
fi

echo "==> ${TAPE_NAME}: OK"
