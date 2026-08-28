#!/usr/bin/env bash
# provider-rename.tape post-tape assertion.
#
# Validates the rename swapped the dictionary key in netclaw.json:
#   - 'seed-provider' is gone
#   - 'renamed-provider' exists with the original Type/Endpoint
#   - `netclaw provider list` reflects the rename
#
# See provider-add.sh for why doctor is not run here.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "provider-rename: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

assert_field '(.Providers | has("seed-provider"))'      'false'                    "$config_json" || :
assert_field '(.Providers | has("renamed-provider"))'   'true'                     "$config_json" || :
assert_field '.Providers["renamed-provider"].Type'      'openai-compatible'        "$config_json" || :
assert_field '.Providers["renamed-provider"].Endpoint'  "$SMOKE_LLM_ENDPOINT"     "$config_json" || :

echo "provider-rename: cross-checking 'netclaw provider list'..."
list_output="$("$NETCLAW_SMOKE_CLI" provider list 2>/dev/null | tr -d '\r')"
if echo "$list_output" | grep -qE '^seed-provider[[:space:]]'; then
  echo "FAIL: 'seed-provider' still shown in provider list." >&2
  assert_fail=1
else
  echo "  ok  'seed-provider' absent from provider list"
fi
if ! echo "$list_output" | grep -qE '^renamed-provider[[:space:]]+OpenAI-compatible'; then
  echo "FAIL: 'renamed-provider' missing from provider list." >&2
  printf -- '--- provider list ---\n%s\n' "$list_output" >&2
  assert_fail=1
else
  echo "  ok  'renamed-provider' present in provider list"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "provider-rename: assertions passed."
