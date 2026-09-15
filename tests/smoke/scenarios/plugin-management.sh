#!/usr/bin/env bash
# plugin-management.sh — prove daemon-owned plugin changes without GitHub access.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

NETCLAW_JSON="${NETCLAW_HOME}/config/netclaw.json"

cleanup() {
  stop_daemon
}
trap cleanup EXIT

mkdir -p "$(dirname "$NETCLAW_JSON")"
cat >"$NETCLAW_JSON" <<EOF
{
  "configVersion": 1,
  "Daemon": {
    "Port": ${DAEMON_PORT}
  },
  "SkillFeeds": {
    "SyncIntervalMinutes": 0,
    "Feeds": [
      {
        "Name": "preserved-feed",
        "Url": "https://skills.example.invalid/",
        "Enabled": false,
        "TimeoutSeconds": 30
      }
    ],
    "Plugins": [
      {
        "Id": "smoke-plugin",
        "Repository": "owner/repository",
        "Format": "codex",
        "ReferenceKind": "Commit",
        "Reference": "13e26d39ed01d97ea592235d041304d289f4ba07",
        "Enabled": false,
        "TimeoutSeconds": 60
      }
    ]
  }
}
EOF

export NETCLAW_Memory__Enabled=false

log "Start the daemon with one disabled plugin..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

list_status=0
list_output="$(nc plugin list 2>&1)" || list_status=$?
echo "$list_output"
if [[ "$list_status" -eq 0 && "$list_output" == *"smoke-plugin"* && "$list_output" == *"disabled"* ]]; then
  pass "plugin list: the daemon reports the disabled source"
else
  die "plugin list: the disabled source is absent"
fi

remove_status=0
remove_output="$(nc plugin remove smoke-plugin --yes 2>&1)" || remove_status=$?
echo "$remove_output"
if [[ "$remove_status" -eq 0 && "$remove_output" == *"Removed plugin 'smoke-plugin'."* ]]; then
  pass "plugin remove: the CLI waited for the daemon restart and sync"
else
  die "plugin remove: the daemon-owned change failed"
fi

if jq -e '
  .Daemon.Port == '"$DAEMON_PORT"' and
  .SkillFeeds.SyncIntervalMinutes == 0 and
  (.SkillFeeds.Feeds | length) == 1 and
  .SkillFeeds.Feeds[0].Name == "preserved-feed" and
  (.SkillFeeds.Plugins == null)
' "$NETCLAW_JSON" >/dev/null; then
  pass "plugin remove: unrelated daemon and skill feed configuration remains present"
else
  die "plugin remove: the configuration mutation changed unrelated data"
fi

retry_status=0
retry_output="$(nc skill sync --retry-rejected 2>&1)" || retry_status=$?
echo "$retry_output"
if [[ "$retry_status" -eq 0 && "$retry_output" == *"Inventory: ok"* ]]; then
  pass "skill sync retry: the explicit retry pass completed"
else
  die "skill sync retry: the explicit retry pass failed"
fi

summarize
exit $?
