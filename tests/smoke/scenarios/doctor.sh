#!/usr/bin/env bash
# doctor.sh — goal: `netclaw doctor` runs to completion without crashing.
#
# `netclaw doctor` is an offline diagnostic (no daemon). A crash in it was
# reported on macOS, so this scenario runs it on a fresh config and asserts
# it exits with a documented code (0 clean / 1 errors / 2 warnings) — never
# a crash signal (>= 128) or a hang (the run_timed 124).
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

log "Running 'netclaw doctor' on a fresh config..."
doctor_status=0
run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" doctor || doctor_status=$?
echo "netclaw doctor exit code: ${doctor_status}"

case "$doctor_status" in
  0) pass "doctor: exited 0 (all checks passed)" ;;
  1) pass "doctor: exited 1 (errors reported — expected on a fresh/empty config)" ;;
  2) pass "doctor: exited 2 (warnings only)" ;;
  124) die "doctor: timed out (hung) under run_timed" ;;
  *)
    if (( doctor_status >= 128 )); then
      die "doctor: crashed — killed by signal $(( doctor_status - 128 )) (exit ${doctor_status})"
    fi
    die "doctor: unexpected exit code ${doctor_status}"
    ;;
esac

summarize
exit $?
