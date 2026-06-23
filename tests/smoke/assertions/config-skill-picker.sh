#!/usr/bin/env bash
# config-skill-picker.tape post-tape assertion.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "config-skill-picker: reading produced config..."
if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "FAIL: ${CONFIG_PATH} does not exist." >&2
  exit 1
fi

config_json="$(read_config_json)"

# The picker derives the source Name from the folder basename and writes an absolute
# path under the per-tape HOME (a random temp dir not exported here), so check the
# basename Name exactly and the Path by suffix.
assert_field '.ExternalSkills.Sources[0].Name' 'netclaw-smoke-skill-picker' "$config_json" || :
assert_field '(.ExternalSkills.Sources[0].Path | endswith("/netclaw-smoke-skill-picker"))' 'true' "$config_json" || :

if (( assert_fail )); then
  printf -- '--- netclaw.json contents ---\n%s\n' "$config_json" >&2
  exit 1
fi

echo "config-skill-picker: assertions passed."
