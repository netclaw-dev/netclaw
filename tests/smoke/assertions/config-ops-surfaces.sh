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

assert_field '.Telemetry.Enabled' 'true' "$config_json" || :
assert_field '.Telemetry.Otlp.Endpoint' 'http://127.0.0.1:4318' "$config_json" || :
# The TUI names an unnamed Slack webhook "slack-webhook". It keeps the entry (Format) in netclaw.json
# and the URL in secrets.json, encrypted.
assert_field '.Notifications.Webhooks["slack-webhook"].Format' 'Slack' "$config_json" || :
assert_field '.Notifications.Webhooks["slack-webhook"] | has("Url")' 'false' "$config_json" || :
if grep -Fq 'T000/B000/SECRET' <<<"$config_json"; then
  echo "FAIL: the webhook URL is in netclaw.json." >&2
  assert_fail=1
fi

secrets_path="${NETCLAW_HOME}/config/secrets.json"
if [[ ! -f "$secrets_path" ]]; then
  echo "FAIL: ${secrets_path} does not exist." >&2
  exit 1
fi
secrets_json="$(cat "$secrets_path")"
assert_field '(.Notifications.Webhooks["slack-webhook"].Url | startswith("ENC:"))' 'true' "$secrets_json" || :
# The editor writes only what the operator chose. The delivery defaults come from code, not from the file.
assert_field '.Notifications.DeduplicationWindowSeconds' 'null' "$config_json" || :
assert_field '.Notifications.MaxRetries' 'null' "$config_json" || :
assert_field '.Notifications.TimeoutSeconds' 'null' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  printf -- '--- secrets.json contents ---\n%s\n' "${secrets_json:-}" >&2
  exit 1
fi

echo "config-ops-surfaces: assertions passed."
