#!/usr/bin/env bash
# config-workspaces-picker.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-workspaces-picker: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

# The picker writes an absolute path under the per-tape HOME (a random temp dir not
# exported to this assertion), so check the suffix — the operator chose the "picked"
# subdir of the seeded $HOME/ws workspaces tree.
assert_field '(.Workspaces.Directory | endswith("/ws/picked"))' 'true' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-workspaces-picker: assertions passed."
