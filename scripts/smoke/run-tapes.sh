#!/usr/bin/env bash
# Driver for the interactive tape suite. CI and local devs share this
# same entrypoint so the two paths can't drift.
#
# Usage:
#   scripts/smoke/run-tapes.sh light                 # PR-gating subset
#   scripts/smoke/run-tapes.sh full                  # nightly full suite
#   scripts/smoke/run-tapes.sh <tape-short-name>     # one-off iteration
#
# Flags:
#   --keep-stack    don't bring the smoke compose stack down at end
#                   (useful for inner-loop tape iteration; prints the exact
#                   teardown command when finished)
#   --no-up         assume the smoke stack is already running; skip up.sh
#
# By default this script:
#   1) ensures vhs is installed (scripts/smoke/install-vhs.sh)
#   2) brings up the smoke stack (scripts/smoke/up.sh)
#   3) waits for ollama-init + daemon health (re-using check.sh helpers)
#   4) runs each requested tape via run-tape.sh
#   5) tears the stack down on exit unless --keep-stack
#
# Exit code is non-zero if any tape fails. All tapes are attempted
# regardless of earlier failures so CI gets a complete artifact set.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SMOKE_SCRIPTS="${ROOT_DIR}/scripts/smoke"
TAPES_DIR="${ROOT_DIR}/tests/smoke-interactive/tapes"

# Cheapest harness checks first so CI fails fast on harness-level
# breakage before paying for the wizard + probe tapes.
LIGHT_TAPES=(
  help
  init-wizard
  provider-add
  provider-rename
  tui-cleanup
)

FULL_TAPES=(
  help
  init-wizard
  provider-add
  provider-rename
  tui-cleanup
)

usage() {
  sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

if [[ $# -lt 1 ]]; then
  usage
fi

mode="$1"; shift || true
keep_stack=0
do_up=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --keep-stack) keep_stack=1; shift ;;
    --no-up)      do_up=0; shift ;;
    -h|--help)    usage ;;
    *)            echo "Unknown flag: $1" >&2; usage ;;
  esac
done

case "$mode" in
  light) tapes=("${LIGHT_TAPES[@]}") ;;
  full)  tapes=("${FULL_TAPES[@]}") ;;
  *)
    candidate="${TAPES_DIR}/${mode}.tape"
    if [[ -f "$candidate" ]]; then
      tapes=("$mode")
    else
      echo "ERROR: '${mode}' is not 'light', 'full', or a valid tape name." >&2
      echo "       Looked for ${candidate}" >&2
      exit 2
    fi
    ;;
esac

# 1) vhs install (idempotent).
"${SMOKE_SCRIPTS}/install-vhs.sh"

# 2) bring stack up (unless --no-up).
if [[ $do_up -eq 1 ]]; then
  echo "==> Bringing smoke stack up..."
  bash "${SMOKE_SCRIPTS}/up.sh"
fi

# 3) wait for ollama-init to complete. Reuse check.sh's helper by
#    sourcing the relevant fragment. To keep coupling loose we just
#    poll docker inspect inline.
PROJECT_NAME="${PROJECT_NAME:-netclaw-smoke}"
COMPOSE_FILE="${COMPOSE_FILE:-${ROOT_DIR}/docker-compose.smoke.yml}"
INIT_TIMEOUT_SECONDS="${INIT_TIMEOUT_SECONDS:-1200}"

wait_for_ollama_init() {
  local id status code deadline=$((SECONDS + INIT_TIMEOUT_SECONDS))
  id="$(docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" ps -a -q ollama-init)"
  if [[ -z "$id" ]]; then
    echo "ERROR: ollama-init container not found." >&2
    return 1
  fi
  while (( SECONDS < deadline )); do
    status="$(docker inspect -f '{{.State.Status}}' "$id")"
    code="$(docker inspect -f '{{.State.ExitCode}}' "$id")"
    if [[ "$status" == "exited" ]]; then
      if [[ "$code" == "0" ]]; then
        echo "    ollama-init: ready."
        return 0
      fi
      echo "ERROR: ollama-init exited with code $code." >&2
      docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" logs ollama-init >&2
      return 1
    fi
    sleep 5
  done
  echo "ERROR: ollama-init did not complete within ${INIT_TIMEOUT_SECONDS}s." >&2
  return 1
}

if [[ $do_up -eq 1 ]]; then
  echo "==> Waiting for ollama-init..."
  wait_for_ollama_init
fi

# 4) run tapes. Track failures but keep going.
failed=()
for tape in "${tapes[@]}"; do
  echo
  echo "════════════════════════════════════════════════════════"
  echo "Running tape: ${tape}"
  echo "════════════════════════════════════════════════════════"
  if ! PROJECT_NAME="$PROJECT_NAME" COMPOSE_FILE="$COMPOSE_FILE" \
       bash "${SMOKE_SCRIPTS}/run-tape.sh" "$tape"; then
    failed+=("$tape")
  fi
done

# 5) teardown unless --keep-stack.
if [[ $keep_stack -eq 1 ]]; then
  echo "==> --keep-stack: leaving smoke compose stack running."
  echo "==> Stop it when you're done with:"
  echo "    docker compose -p \"${PROJECT_NAME}\" -f \"${COMPOSE_FILE}\" down"
else
  echo "==> Tearing down smoke compose stack..."
  SMOKE_REMOVE_VOLUMES="${SMOKE_REMOVE_VOLUMES:-1}" \
    bash "${SMOKE_SCRIPTS}/down.sh" || true
fi

if (( ${#failed[@]} > 0 )); then
  echo
  echo "FAILURE: ${#failed[@]} tape(s) failed: ${failed[*]}" >&2
  exit 1
fi

echo
echo "All ${#tapes[@]} tape(s) passed."
