#!/usr/bin/env bash
# config-ops-surfaces.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-ops-surfaces: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.ExternalSkills.Sources[0].Name' 'netclaw-smoke-config-ops-skills' "$config_json" || :
assert_field '.ExternalSkills.Sources[0].Path' '/tmp/netclaw-smoke-config-ops-skills' "$config_json" || :
assert_field '.SkillFeeds.Feeds == null' 'true' "$config_json" || :
assert_field '.Telemetry.Enabled' 'true' "$config_json" || :
assert_field '.Telemetry.Otlp.Endpoint' 'http://127.0.0.1:4318' "$config_json" || :
assert_field '.Notifications.Webhooks[0].Url' 'https://hooks.slack.com/services/T000/B000/SECRET' "$config_json" || :
assert_field '.Notifications.Webhooks[0].Format' 'Slack' "$config_json" || :
assert_field '.Notifications.DeduplicationWindowSeconds' '300' "$config_json" || :
assert_field '.Notifications.MaxRetries' '2' "$config_json" || :
assert_field '.Notifications.TimeoutSeconds' '10' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-ops-surfaces: assertions passed."
