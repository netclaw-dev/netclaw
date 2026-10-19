#!/usr/bin/env bash
# config-exposure.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-exposure: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"
editor_state_path="${NETCLAW_HOME}/config/editor-state.json"

assert_field '.Daemon.ExposureMode' 'local' "$config_json" || :
assert_field '.Daemon.Host' 'null' "$config_json" || :
assert_field '.Daemon.Port' '5299' "$config_json" || :
assert_field '.Daemon.DisableSelfUpdate' 'true' "$config_json" || :
assert_field '.Daemon.TrustedProxies' 'null' "$config_json" || :
assert_field '.Daemon.UpdateChannel' 'beta' "$config_json" || :
# The save must keep the posture. The old save reset a Team install to Personal / HostAllowed.
assert_field '.Security.DeploymentPosture' 'Team' "$config_json" || :
assert_field '.Security.ShellExecutionMode' 'SandboxOnly' "$config_json" || :
assert_field '.Security.StrictDefaults' 'true' "$config_json" || :
assert_field '.Tools.ShellMode' 'SandboxOnly' "$config_json" || :

if [[ ! -f "$editor_state_path" ]]; then
  echo "FAIL: ${editor_state_path} does not exist." >&2
  assert_fail=1
else
  editor_state_json="$(cat "$editor_state_path")"
  assert_field '.Sections["exposure-mode"]["ReverseProxy.TrustedProxies"][0]' '10.0.0.0/24' "$editor_state_json" || :
fi

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-exposure: assertions passed."
