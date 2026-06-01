#!/usr/bin/env bash
# config-surfaces.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-surfaces: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Workspaces.Directory' '/tmp/netclaw-smoke-config-surfaces-workspaces' "$config_json" || :
assert_field '.Webhooks.Enabled' 'false' "$config_json" || :
assert_field '.Webhooks.ExecutionTimeoutSeconds' '45' "$config_json" || :
assert_field '(.McpServers // {} | has("browser_playwright"))' 'false' "$config_json" || :
assert_field '(.McpServers // {} | has("browser_chrome_devtools"))' 'false' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-surfaces: assertions passed."
