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

# Enabling Inbound Webhooks before any routes exist is the intended setup order:
# the toggle persists Enabled=true and the advisory just points at `netclaw webhooks
# set`. The gateway fails closed (404) per route until routes are authored.
assert_field '.Webhooks.Enabled' 'true' "$config_json" || :
assert_field '.Webhooks.ExecutionTimeoutSeconds' '45' "$config_json" || :
assert_field '(.McpServers // {} | has("browser_playwright"))' 'false' "$config_json" || :
assert_field '(.McpServers // {} | has("browser_chrome_devtools"))' 'false' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-surfaces: assertions passed."
