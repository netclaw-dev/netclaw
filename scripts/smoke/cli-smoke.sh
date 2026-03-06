#!/usr/bin/env bash
# cli-smoke.sh — offline + daemon-optional CLI smoke tests
# Runs without a live daemon for offline commands; daemon-requiring commands
# are attempted but a "daemon unavailable" exit code is accepted.
#
# Usage:
#   bash scripts/smoke/cli-smoke.sh
#
# Environment:
#   NETCLAW_CLI   Path to netclaw binary. If unset, uses dotnet run.
#   BUILD_CONFIG  dotnet build configuration (default: Release).

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_CONFIG="${BUILD_CONFIG:-Release}"
CLI_PROJECT="$ROOT_DIR/src/Netclaw.Cli/Netclaw.Cli.csproj"

if [[ -n "${NETCLAW_CLI:-}" ]]; then
  run_netclaw() { "$NETCLAW_CLI" "$@"; }
else
  run_netclaw() { dotnet run --project "$CLI_PROJECT" --no-build -c "$BUILD_CONFIG" -- "$@"; }
fi

PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1"; FAIL=$((FAIL + 1)); }

# ── Offline commands (no daemon required) ────────────────────────────────────

echo "=== netclaw version ==="
version_output="$(run_netclaw version)"
echo "$version_output"
if [[ "$version_output" == *"netclaw"* ]]; then
  pass "version: output contains 'netclaw'"
else
  fail "version: expected output to contain 'netclaw', got: $version_output"
fi

version_exit=0
run_netclaw version >/dev/null || version_exit=$?
if [[ $version_exit -eq 0 ]]; then
  pass "version: exits 0"
else
  fail "version: expected exit 0, got $version_exit"
fi

echo ""
echo "=== netclaw --help ==="
help_output="$(run_netclaw --help)"
echo "$help_output"
if [[ "$help_output" == *"Usage:"* ]]; then
  pass "--help: output contains 'Usage:'"
else
  fail "--help: expected 'Usage:' in output"
fi
if [[ "$help_output" == *"sessions"* ]]; then
  pass "--help: output mentions 'sessions'"
else
  fail "--help: expected 'sessions' in help output"
fi

echo ""
echo "=== netclaw doctor ==="
# doctor exits 0 (pass), 1 (fail), or 2 (warn) — all valid; anything > 2 is a crash
set +e
doctor_exit=0
doctor_output="$(run_netclaw doctor 2>&1)"
doctor_exit=$?
set -e
echo "$doctor_output"
if [[ $doctor_exit -le 2 ]]; then
  pass "doctor: exits with valid code $doctor_exit"
else
  fail "doctor: unexpected exit code $doctor_exit (expected 0, 1, or 2)"
fi

echo ""
echo "=== netclaw sessions --help ==="
sessions_help_exit=0
sessions_help_output="$(run_netclaw sessions --help)" || sessions_help_exit=$?
echo "$sessions_help_output"
if [[ $sessions_help_exit -eq 0 && "$sessions_help_output" == *"--once"* ]]; then
  pass "sessions --help: exits 0 and mentions --once"
else
  fail "sessions --help: exit=$sessions_help_exit, missing --once in output"
fi

# ── Daemon-requiring commands (non-zero on daemon unavailable is accepted) ───

echo ""
echo "=== netclaw status (daemon-optional) ==="
set +e
status_exit=0
status_output="$(run_netclaw status 2>&1)"
status_exit=$?
set -e
echo "$status_output"
# 0=healthy, 2=degraded, 1=error/unreachable — all valid
if [[ $status_exit -le 2 ]]; then
  pass "status: exits with valid code $status_exit"
else
  fail "status: unexpected exit code $status_exit (expected 0, 1, or 2)"
fi

echo ""
echo "=== netclaw sessions --once (daemon-optional) ==="
set +e
sessions_exit=0
sessions_output="$(run_netclaw sessions --once 2>&1)"
sessions_exit=$?
set -e
echo "$sessions_output"
# 0=sessions listed, 1=daemon unreachable — both valid
if [[ $sessions_exit -le 1 ]]; then
  pass "sessions --once: exits with valid code $sessions_exit"
else
  fail "sessions --once: unexpected exit code $sessions_exit (expected 0 or 1)"
fi

echo ""
echo "=== netclaw sessions --json (daemon-optional) ==="
set +e
sessions_json_exit=0
sessions_json_output="$(run_netclaw sessions --json 2>&1)"
sessions_json_exit=$?
set -e
echo "$sessions_json_output"
# 0=sessions listed, 1=daemon unreachable — both valid; must not launch TUI (exits)
if [[ $sessions_json_exit -le 1 ]]; then
  pass "sessions --json: exits with valid code $sessions_json_exit (not TUI)"
else
  fail "sessions --json: unexpected exit code $sessions_json_exit (expected 0 or 1)"
fi

# ── No-args and unknown command behavior ─────────────────────────────────────

echo ""
echo "=== netclaw (no args) ==="
set +e
noargs_exit=0
noargs_output="$(run_netclaw 2>&1)"
noargs_exit=$?
set -e
echo "$noargs_output"
if [[ $noargs_exit -eq 2 && "$noargs_output" == *"Usage:"* ]]; then
  pass "no-args: exits 2 and prints help"
else
  fail "no-args: expected exit 2 with 'Usage:' in output, got exit=$noargs_exit"
fi

echo ""
echo "=== netclaw unknown-command ==="
set +e
unknown_exit=0
unknown_output="$(run_netclaw unknown-command 2>&1)"
unknown_exit=$?
set -e
echo "$unknown_output"
if [[ $unknown_exit -eq 2 && "$unknown_output" == *"unknown-command"* ]]; then
  pass "unknown-command: exits 2 and mentions the bad command"
else
  fail "unknown-command: expected exit 2 with command name in output, got exit=$unknown_exit"
fi

# ── Summary ──────────────────────────────────────────────────────────────────

echo ""
echo "Results: $PASS passed, $FAIL failed"
if [[ $FAIL -gt 0 ]]; then
  echo "CLI smoke: FAILED"
  exit 1
fi
echo "CLI smoke: PASSED"
