#!/usr/bin/env bash
# config-features.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-features: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Memory.Enabled' 'true' "$config_json" || :
assert_field '.Search.Enabled' 'true' "$config_json" || :
assert_field '.Search.Backend' 'duckduckgo' "$config_json" || :
assert_field '.SkillSync.Enabled' 'true' "$config_json" || :
assert_field '.Scheduling.Enabled' 'false' "$config_json" || :
assert_field '.SubAgents.Enabled' 'true' "$config_json" || :
assert_field '.Webhooks.Enabled' 'false' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-features: assertions passed."
