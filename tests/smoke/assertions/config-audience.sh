#!/usr/bin/env bash
# config-audience.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-audience: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Tools.AudienceProfiles.Team.ToolsMode' 'Allowlist' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.AllowedTools | index("file_read") != null' 'true' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.AllowedTools | index("web_search") != null' 'true' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.AllowedTools | index("web_fetch") != null' 'true' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.McpServersMode' 'Allowlist' "$config_json" || :
assert_field '(.Tools.AudienceProfiles.Team.AllowedMcpServers | length)' '0' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.McpServerToolGrants' 'null' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.ApprovalPolicy' 'null' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-audience: assertions passed."
