#!/usr/bin/env bash
# model-manager.tape post-tape assertion.
#
# The tape seeds Main + Fallback through the CLI, changes the provider selection
# in the picker without confirming, then clears Fallback with the C keybinding.
#
# C moves through the highlighted row only: an arrow key changes the highlight
# without selecting, so a keybinding that reads SelectedItems sees an empty list
# and clears nothing. The on-disk result proves the highlighted row reached the
# writer, and that clearing a role leaves its definition in place.
#
# The tape then sets the Compaction output limit to 32000 and its thinking level
# to off in the P properties panel. The definition must hold both values in the
# canonical form that the daemon reads.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "model-manager: reading produced config..."
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

# The protected role survives the C keybinding.
assert_field '.Models.Roles.Main' 'alpha-models-deepseek-v4-flash' "$config_json" \
  || true

# The optional role the tape cleared is gone from Roles.
assert_field '.Models.Roles | has("Fallback")' 'false' "$config_json" \
  || true

# Clearing a role unmaps it; it does not delete the model definition.
assert_field '.Models.Definitions | has("alpha-models-fallback-model")' 'true' "$config_json" \
  || true

# The P properties panel persisted both settings on the Compaction definition.
assert_field '.Models.Roles.Compaction' 'charlie-ds-deepseek-v4-flash' "$config_json" \
  || true
assert_field '.Models.Definitions["charlie-ds-deepseek-v4-flash"].MaxOutputTokens' '32000' "$config_json" \
  || true
assert_field '.Models.Definitions["charlie-ds-deepseek-v4-flash"].Thinking' 'Off' "$config_json" \
  || true

if [[ $assert_fail -ne 0 ]]; then
  echo "model-manager: FAILED (C clear-role, definition retention, or P properties)" >&2
  exit 1
fi

echo "model-manager: OK (Main kept, Fallback unmapped by C, definition retained, P properties saved)"
