#!/usr/bin/env bash
# config-mention-thread.tape post-tape assertion.
#
# Validates that toggling the per-channel mention rule On for C01 with Space on
# the Channels & Permissions list (autosave, no detail leaf) persists
# Slack.MentionRequiredInThreadByChannel.C01 = true.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-mention-thread: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Slack.Enabled' 'true' "$config_json" || :
assert_field '.Slack.AllowedChannelIds[0]' 'C01' "$config_json" || :
# The tape toggled the per-channel mention rule On for C01 and applied it.
assert_field '.Slack.MentionRequiredInThreadByChannel.C01' 'true' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-mention-thread: assertions passed."
