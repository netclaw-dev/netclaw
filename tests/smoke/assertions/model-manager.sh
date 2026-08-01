#!/usr/bin/env bash
# model-manager.tape post-tape assertion.
#
# The tape assigns an image proxy through the TUI. This script checks the
# canonical named-model shape and the required modality metadata.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "model-manager: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"
proxy_name="$(printf '%s' "$config_json" | jq -r '.Models.Proxies.Image')"

assert_field '.Models.Proxies.Image' 'smoke-ollama-smoke-vision' "$config_json" || :
assert_field ".Models.Definitions[\"${proxy_name}\"].Provider" 'smoke-ollama' "$config_json" || :
assert_field ".Models.Definitions[\"${proxy_name}\"].ModelId" 'smoke-vision' "$config_json" || :
assert_field ".Models.Definitions[\"${proxy_name}\"].InputModalities" 'Text, Image' "$config_json" || :
assert_field ".Models.Definitions[\"${proxy_name}\"].OutputModalities" 'Text' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "model-manager: assertions passed."
