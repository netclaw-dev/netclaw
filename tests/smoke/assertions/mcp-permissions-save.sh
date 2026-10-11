#!/usr/bin/env bash
# Check the config that the native permission page writes.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"
assert_field '.Tools.AudienceProfiles.Personal.McpServersMode' 'Allowlist' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Personal.AllowedMcpServers' '[]' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.McpServersMode' 'Allowlist' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.AllowedMcpServers' '["smoke-math"]' "$config_json" || :
assert_field '(.Tools.AudienceProfiles.Team.McpServerToolGrants["smoke-math"] | sort)' \
  '["add","echo","image-with-metadata","image-with-notes","process-info","record-tasks"]' "$config_json" || :

if (( assert_fail )); then
  exit 1
fi

echo "mcp-permissions-save: assertions passed."
