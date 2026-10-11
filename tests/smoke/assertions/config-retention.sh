#!/usr/bin/env bash
# Check the value from the native retention editor with the CLI consumer.
set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0
config_json="$(read_config_json)"
assert_field '.configVersion' '1' "$config_json" || :
assert_field '.Retention.Logs.Days' '45' "$config_json" || :
(( assert_fail == 0 )) || exit 1

actual="$("$NETCLAW_SMOKE_CLI" config retention)"
expected='Daemon and crash logs: keep 45 days'
if [[ "$actual" != "$expected" ]]; then
  printf 'FAIL: expected %s, got %s\n' "$expected" "$actual" >&2
  exit 1
fi
echo 'config-retention: assertions passed.'
