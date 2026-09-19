#!/usr/bin/env bash
# config-teams.tape post-tape assertion.
#
# Validates the normal Teams configuration and the encrypted secret boundary
# without ever printing the client secret or the secrets document.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0
SECRETS_PATH="${NETCLAW_HOME}/config/secrets.json"

echo "config-teams: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

if [[ ! -f "$SECRETS_PATH" ]]; then
  echo "FAIL: ${SECRETS_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"
secrets_json="$(<"$SECRETS_PATH")"

assert_field '.Teams.Enabled' 'true' "$config_json" || :
assert_field '.Teams.TenantId' 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' "$config_json" || :
assert_field '.Teams.ClientId' 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' "$config_json" || :
assert_field '.Teams.BotId' 'cccccccc-cccc-cccc-cccc-cccccccccccc' "$config_json" || :
assert_field '(.Teams.AllowedTeamIds | index("44444444-4444-4444-4444-444444444444") != null)' 'true' "$config_json" || :
assert_field '(.Teams.AllowedChannelIds | index("55555555-5555-5555-5555-555555555555") != null)' 'true' "$config_json" || :
assert_field '(.Teams.AllowedUserIds | index("66666666-6666-6666-6666-666666666666") != null)' 'true' "$config_json" || :
assert_field '(.Teams.AllowedUserIds | index("77777777-7777-7777-7777-777777777777") != null)' 'true' "$config_json" || :
assert_field '(.Teams.AllowedGroupIds | index("88888888-8888-8888-8888-888888888888") != null)' 'true' "$config_json" || :
assert_field '(.Teams.AllowedGroupIds | index("99999999-9999-9999-9999-999999999999") != null)' 'true' "$config_json" || :
assert_field '.Teams.AllowGroupChats' 'true' "$config_json" || :
assert_field '(.Teams.AllowedGroupChatIds | length)' '1' "$config_json" || :
assert_field '(.Teams.AllowedGroupChatIds | index("19:smoke-group-chat@thread.v2") != null)' 'true' "$config_json" || :
assert_field '.Teams.AllowAttachments' 'true' "$config_json" || :
assert_field '(.Teams | has("ClientSecret"))' 'false' "$config_json" || :

if printf '%s' "$config_json" | grep -Fq -e 'teams-smoke-client-secret' -e 'teams-smoke-replacement-secret'; then
  echo "FAIL: normal config contains the Teams client secret." >&2
  assert_fail=1
fi

if ! printf '%s' "$secrets_json" | jq -e '.Teams.ClientSecret | (type == "string" and startswith("ENC:"))' >/dev/null 2>&1; then
  echo "FAIL: Teams client secret is missing or not encrypted in secrets.json." >&2
  assert_fail=1
else
  echo "  ok  Teams client secret is encrypted in secrets.json"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  echo '--- secrets.json contents withheld ---' >&2
  exit 1
fi

echo "config-teams: assertions passed."
