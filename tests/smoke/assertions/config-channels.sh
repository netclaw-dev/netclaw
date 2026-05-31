#!/usr/bin/env bash
# config-channels.tape post-tape assertion.
#
# Validates the Channels editor saved Slack management changes while preserving secrets.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0
SECRETS_PATH="${NETCLAW_HOME}/config/secrets.json"

echo "config-channels: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

if [[ ! -f "$SECRETS_PATH" ]]; then
  echo "FAIL: ${SECRETS_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"
secrets_json="$(cat "$SECRETS_PATH")"

assert_field '.Slack.Enabled' 'true' "$config_json" || :
assert_field '(.Slack.AllowedChannelIds | length)' '3' "$config_json" || :
assert_field '.Slack.AllowedChannelIds[0]' 'C01' "$config_json" || :
assert_field '.Slack.AllowedChannelIds[1]' 'C02' "$config_json" || :
assert_field '.Slack.AllowedChannelIds[2]' 'C09' "$config_json" || :
assert_field '.Slack.DefaultChannelId' 'C01' "$config_json" || :
assert_field '(.Slack.AllowedUserIds | length)' '1' "$config_json" || :
assert_field '.Slack.AllowedUserIds[0]' 'U09' "$config_json" || :
assert_field '.Slack.AllowDirectMessages' 'false' "$config_json" || :
assert_field '.Slack.ChannelAudiences.C01' 'public' "$config_json" || :
assert_field '.Slack.ChannelAudiences.C02' 'team' "$config_json" || :
assert_field '.Slack.ChannelAudiences.C09' 'team' "$config_json" || :
assert_field '(.Slack.BotToken | startswith("ENC:"))' 'true' "$secrets_json" || :
assert_field '(.Slack.AppToken | startswith("ENC:"))' 'true' "$secrets_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  printf -- '--- secrets.json contents ---\n%s\n' "$secrets_json" >&2
  exit 1
fi

echo "config-channels: assertions passed."
