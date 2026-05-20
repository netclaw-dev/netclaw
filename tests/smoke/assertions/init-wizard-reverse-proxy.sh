#!/usr/bin/env bash
# init-wizard-reverse-proxy.tape post-tape assertion.
#
# Validates that the wizard surfaced reverse-proxy as an exposure mode and
# produced a startable config:
#   1) config/netclaw.json exists and parses as JSON
#   2) `netclaw doctor` does not report errors (exit 0 = clean, exit 2 = WARN ok)
#   3) Daemon section contains ExposureMode=reverse-proxy, Host=0.0.0.0,
#      and TrustedProxies[0]=10.0.0.0/24 — what the tape typed.
#   4) Bootstrap device file exists (reverse-proxy is non-local so the
#      wizard must seed at least one paired device).

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "init-wizard-reverse-proxy: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist after wizard run." >&2
  ls -la "$NETCLAW_HOME" 2>&1 >&2 || true
  exit 1
fi

config_json="$(read_config_json)"
if ! printf '%s' "$config_json" | jq empty >/dev/null 2>&1; then
  echo "FAIL: ${CONFIG_PATH} is not valid JSON." >&2
  printf '%s\n' "$config_json" >&2
  exit 1
fi

echo "init-wizard-reverse-proxy: running 'netclaw doctor'..."
doctor_status=0
"$NETCLAW_SMOKE_CLI" doctor || doctor_status=$?
if [[ $doctor_status -eq 1 ]]; then
  echo "FAIL: netclaw doctor reported errors (exit 1)." >&2
  exit 1
fi
if [[ $doctor_status -ne 0 && $doctor_status -ne 2 ]]; then
  echo "FAIL: netclaw doctor exited with unexpected status $doctor_status." >&2
  exit 1
fi

echo "init-wizard-reverse-proxy: checking Daemon section..."
assert_field '.Daemon.ExposureMode'      'reverse-proxy' "$config_json" || :
assert_field '.Daemon.Host'              '0.0.0.0'       "$config_json" || :
assert_field '.Daemon.TrustedProxies[0]' '10.0.0.0/24'   "$config_json" || :

echo "init-wizard-reverse-proxy: confirming bootstrap device was seeded..."
devices_path="${NETCLAW_HOME}/config/devices.json"
if [[ ! -f "$devices_path" ]]; then
  echo "FAIL: ${devices_path} not written — bootstrap device missing." >&2
  assert_fail=1
else
  device_count="$(jq 'length' "$devices_path" 2>/dev/null || echo 0)"
  if [[ "$device_count" -lt 1 ]]; then
    echo "FAIL: ${devices_path} contains no paired devices." >&2
    assert_fail=1
  else
    echo "  ok  ${devices_path} has $device_count device(s)"
  fi
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "init-wizard-reverse-proxy: assertions passed."
