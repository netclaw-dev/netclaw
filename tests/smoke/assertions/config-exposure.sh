#!/usr/bin/env bash
# config-exposure.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-exposure: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Daemon.ExposureMode' 'reverse-proxy' "$config_json" || :
assert_field '.Daemon.Host' '0.0.0.0' "$config_json" || :
assert_field '.Daemon.Port' '5299' "$config_json" || :
assert_field '.Daemon.DisableSelfUpdate' 'true' "$config_json" || :
assert_field '.Daemon.TrustedProxies[0]' '10.0.0.0/24' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-exposure: assertions passed."
