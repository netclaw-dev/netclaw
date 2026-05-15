#!/usr/bin/env bash
# provider-rename.tape post-tape assertion.
#
# Validates the rename swapped the dictionary key in netclaw.json:
#   - 'seed-ollama' is gone
#   - 'renamed-ollama' exists with the original Type/Endpoint
#   - `netclaw provider list` reflects the rename
#
# See provider-add.sh for why doctor is not run here.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "provider-rename: reading produced config..."
if ! in_sandbox test -f "$CONFIG_PATH"; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

# One jq pass extracts everything we care about. Saves three additional
# `docker compose exec` round trips compared to per-field exec.
assert_field '(.Providers | has("seed-ollama"))'      'false'                "$config_json" || :
assert_field '(.Providers | has("renamed-ollama"))'   'true'                 "$config_json" || :
assert_field '.Providers["renamed-ollama"].Type'      'ollama'               "$config_json" || :
assert_field '.Providers["renamed-ollama"].Endpoint'  'http://ollama:11434'  "$config_json" || :

echo "provider-rename: cross-checking 'netclaw provider list'..."
list_output="$(in_sandbox netclaw provider list 2>/dev/null | tr -d '\r')"
if echo "$list_output" | grep -qE '^seed-ollama[[:space:]]'; then
  echo "FAIL: 'seed-ollama' still shown in provider list." >&2
  assert_fail=1
else
  echo "  ok  'seed-ollama' absent from provider list"
fi
if ! echo "$list_output" | grep -qE '^renamed-ollama[[:space:]]+Ollama'; then
  echo "FAIL: 'renamed-ollama' missing from provider list." >&2
  printf -- '--- provider list ---\n%s\n' "$list_output" >&2
  assert_fail=1
else
  echo "  ok  'renamed-ollama' present in provider list"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "provider-rename: assertions passed."
