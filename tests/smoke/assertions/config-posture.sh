#!/usr/bin/env bash
# config-posture.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-posture: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Security.DeploymentPosture' 'Team' "$config_json" || :
assert_field '.Security.ShellExecutionMode' 'Off' "$config_json" || :
assert_field '.Security.StrictDefaults' 'true' "$config_json" || :
assert_field '.Tools.ShellMode' 'Off' "$config_json" || :
assert_field '.Tools.AudienceProfiles.Team.AllowedTools | index("web_search") != null' 'true' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-posture: assertions passed."
