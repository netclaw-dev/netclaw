#!/usr/bin/env bash
# config-search.tape post-tape assertion.
#
# Validates the redesigned Search flow persisted the expected DuckDuckGo
# backend back into netclaw.json and that no Brave API key leaked into the
# main config file.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-search: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '.Search.Backend' 'duckduckgo' "$config_json" || :
assert_field '(.Search | has("BraveApiKey"))' 'false' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-search: assertions passed."
