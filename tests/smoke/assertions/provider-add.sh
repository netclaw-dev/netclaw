#!/usr/bin/env bash
# provider-add.tape post-tape assertion.
#
# Validates that the TUI add flow wrote 'smoke-add-provider' with the
# expected Type/Endpoint to netclaw.json, and that `netclaw provider
# list` shows the new row.
#
# Doctor is NOT run here — this tape produces a partial config (no
# Tools/Security/Models, which come from `netclaw init`). doctor would
# correctly [FAIL] on the missing sections, but those failures are
# orthogonal to the surface this tape tests.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "provider-add: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  ls -la "$NETCLAW_HOME" "$NETCLAW_HOME/config" 2>&1 >&2 || true
  exit 1
fi

config_json="$(read_config_json)"
if ! printf '%s' "$config_json" | jq empty >/dev/null 2>&1; then
  echo "FAIL: ${CONFIG_PATH} is not valid JSON." >&2
  exit 1
fi

echo "provider-add: checking 'smoke-add-provider' in config..."
assert_field '.Providers["smoke-add-provider"].Type'     'openai-compatible'     "$config_json" || :
assert_field '.Providers["smoke-add-provider"].Endpoint' "$SMOKE_LLM_ENDPOINT"  "$config_json" || :

echo "provider-add: cross-checking 'netclaw provider list'..."
list_output="$("$NETCLAW_SMOKE_CLI" provider list 2>/dev/null | tr -d '\r')"
if ! echo "$list_output" | grep -qE '^smoke-add-provider[[:space:]]+OpenAI-compatible'; then
  echo "FAIL: 'smoke-add-provider' row missing or malformed in 'provider list' output." >&2
  printf -- '--- provider list ---\n%s\n' "$list_output" >&2
  assert_fail=1
else
  echo "  ok  'smoke-add-provider' present in provider list"
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "provider-add: assertions passed."
