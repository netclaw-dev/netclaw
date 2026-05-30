#!/usr/bin/env bash
# config-channels.tape post-tape assertion.
#
# Validates the read-only Channels page did not mutate seeded channel config.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-channels: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Slack.Enabled' 'true' "$config_json" || :
assert_field '(.Slack.AllowedChannelIds | length)' '2' "$config_json" || :
assert_field '(.Slack.AllowedUserIds | length)' '1' "$config_json" || :
assert_field '.Mattermost.DefaultChannelId' 'town-square' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-channels: assertions passed."
