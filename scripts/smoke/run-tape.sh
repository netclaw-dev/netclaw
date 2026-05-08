#!/usr/bin/env bash
# Run a single VHS tape against the smoke compose stack.
#
# Usage:
#   scripts/smoke/run-tape.sh <tape-short-name>
#
# Where <tape-short-name> matches a file at:
#   tests/smoke-interactive/tapes/<tape-short-name>.tape
#
# Steps:
#   1) Substitute placeholders in preamble.tape and prepend it to the
#      tape body, producing a combined temp tape.
#   2) Run `vhs` against the combined tape with a hard timeout.
#   3) If a per-tape assertion exists at
#      tests/smoke-interactive/assertions/<short-name>.sh, run it.
#   4) On failure, collect artifacts (last-frame PNG, daemon log,
#      NETCLAW_HOME tarball, compose logs) into ${ARTIFACT_DIR}.
#
# Environment knobs:
#   PROJECT_NAME       compose project (default: netclaw-smoke)
#   COMPOSE_FILE       compose file path (default: docker-compose.smoke.yml)
#   TAPE_TIMEOUT_S     hard timeout per tape (default: 600)
#   ARTIFACT_DIR       failure artifact dir (default: smoke-logs/tapes/<name>)
#   KEEP_TEMP          set to 1 to retain the combined tape for inspection

set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <tape-short-name>" >&2
  exit 2
fi

TAPE_NAME="$1"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAPES_DIR="${ROOT_DIR}/tests/smoke-interactive/tapes"
ASSERT_DIR="${ROOT_DIR}/tests/smoke-interactive/assertions"

PROJECT_NAME="${PROJECT_NAME:-netclaw-smoke}"
COMPOSE_FILE="${COMPOSE_FILE:-${ROOT_DIR}/docker-compose.smoke.yml}"
TAPE_TIMEOUT_S="${TAPE_TIMEOUT_S:-600}"
ARTIFACT_DIR="${ARTIFACT_DIR:-${ROOT_DIR}/smoke-logs/tapes/${TAPE_NAME}}"

preamble="${TAPES_DIR}/preamble.tape"
body="${TAPES_DIR}/${TAPE_NAME}.tape"
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

# Per-tape NETCLAW_HOME inside the container, written into preamble + exposed
# to the assertion script for post-tape inspection.
NETCLAW_HOME_IN="/tmp/tape-${TAPE_NAME}"

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
  cp -v "/tmp/tape-${TAPE_NAME}.gif" "${ARTIFACT_DIR}/" 2>/dev/null
  cp -v "/tmp/tape-${TAPE_NAME}.png" "${ARTIFACT_DIR}/" 2>/dev/null
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" logs --no-color \
    > "${ARTIFACT_DIR}/compose.log" 2>&1
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" exec -T netclaw-sandbox \
    sh -c "ls -la '$NETCLAW_HOME_IN' 2>/dev/null && cat '$NETCLAW_HOME_IN/config.json' 2>/dev/null" \
    > "${ARTIFACT_DIR}/netclaw_home.txt" 2>&1
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" exec -T netclaw-sandbox \
    tar -C "$NETCLAW_HOME_IN" -czf - . 2>/dev/null \
    > "${ARTIFACT_DIR}/netclaw_home.tar.gz" \
    || true
  cp -v "$combined" "${ARTIFACT_DIR}/${TAPE_NAME}.combined.tape" 2>/dev/null
  set -e
  echo "    artifacts: $(ls "${ARTIFACT_DIR}" 2>/dev/null | tr '\n' ' ')"
}

# Substitute placeholders in preamble. Sed delimiter is '|' since paths
# contain '/'. PROJECT_NAME and TAPE_NAME are restricted to safe chars
# by convention (set by us), so escaping is minimal.
sed \
  -e "s|__PROJECT__|${PROJECT_NAME}|g" \
  -e "s|__COMPOSE_FILE__|${COMPOSE_FILE}|g" \
  -e "s|__TAPE_NAME__|${TAPE_NAME}|g" \
  "$preamble" > "$combined"

# Append body (skip its `Output ...` line if present, since the preamble
# does not declare one and vhs expects exactly one Output directive).
# Easier: keep both, last-write-wins on Output is fine in vhs.
cat "$body" >> "$combined"

mkdir -p "$ARTIFACT_DIR"

echo "==> Running tape: ${TAPE_NAME} (timeout=${TAPE_TIMEOUT_S}s)"
echo "    PROJECT_NAME=${PROJECT_NAME}"
echo "    COMPOSE_FILE=${COMPOSE_FILE}"
echo "    NETCLAW_HOME_IN=${NETCLAW_HOME_IN}"

vhs_status=0
if command -v timeout >/dev/null 2>&1; then
  timeout --foreground "${TAPE_TIMEOUT_S}" vhs "$combined" || vhs_status=$?
else
  vhs "$combined" || vhs_status=$?
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
  PROJECT_NAME="$PROJECT_NAME" \
  COMPOSE_FILE="$COMPOSE_FILE" \
  TAPE_NAME="$TAPE_NAME" \
  NETCLAW_HOME_IN="$NETCLAW_HOME_IN" \
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
