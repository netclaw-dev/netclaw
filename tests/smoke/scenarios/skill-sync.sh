#!/usr/bin/env bash
# skill-sync.sh — prove an operator sync updates the live daemon inventory.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v python3 >/dev/null 2>&1 || die "python3 is required for skill-sync.sh"

FEED_PHASE_FILE="${NETCLAW_HOME}/skill-feed-phase"
FEED_LOG_FILE="${NETCLAW_HOME}/skill-feed.log"
NETCLAW_JSON="${NETCLAW_HOME}/config/netclaw.json"
EXPECTED_CONFIG="${NETCLAW_HOME}/skill-sync.expected.json"
RESOURCE_FILE="${NETCLAW_HOME}/skills/.server-feeds/smoke-feed/smoke-feed-skill/references/proof.txt"
FEED_PID=""

cleanup() {
  stop_daemon
  if [[ -n "$FEED_PID" ]] && kill -0 "$FEED_PID" 2>/dev/null; then
    kill "$FEED_PID" 2>/dev/null || true
    wait "$FEED_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

printf 'A\n' >"$FEED_PHASE_FILE"
python3 "${SCRIPT_DIR}/skill-sync-feed.py" "$FEED_PHASE_FILE" \
  >"$FEED_LOG_FILE" 2>&1 &
FEED_PID=$!
for _ in $(seq 1 100); do
  FEED_URL="$(sed -n 's/^\[skill-feed:listening\] //p' "$FEED_LOG_FILE" | head -1)"
  [[ -n "$FEED_URL" ]] && break
  if ! kill -0 "$FEED_PID" 2>/dev/null; then
    die "the local skill feed exited before it published its URL"
  fi
  sleep 0.1
done
[[ -n "${FEED_URL:-}" ]] || die "the local skill feed did not publish its URL"
run_timed 15 curl --retry 100 --retry-delay 0 --retry-max-time 10 \
  -fsS "${FEED_URL}/health" >/dev/null || die "the local skill feed did not become ready"

mkdir -p "$(dirname "$NETCLAW_JSON")"
printf '{\n  "Daemon": {\n    "Port": %s\n  }\n}\n' "$DAEMON_PORT" >"$NETCLAW_JSON"
cp "$NETCLAW_JSON" "$EXPECTED_CONFIG"

export NETCLAW_Memory__Enabled=false
export NETCLAW_SkillFeeds__SyncIntervalMinutes=0
export NETCLAW_SkillFeeds__Feeds__0__Name=smoke-feed
export NETCLAW_SkillFeeds__Feeds__0__Url="$FEED_URL"
export NETCLAW_SkillFeeds__Feeds__0__Enabled=true
export NETCLAW_SkillFeeds__Feeds__0__TimeoutSeconds=30

log "Start the published daemon with the local RFC skill feed..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

log "Run the first operator skill sync for phase A..."
first_status=0
first_output="$(nc skill sync 2>&1)" || first_status=$?
echo "$first_output"
if [[ "$first_status" -eq 0 && "$first_output" == *"smoke-feed: ok "* ]]; then
  pass "first skill sync: phase A succeeded after the startup pass"
else
  die "first skill sync: expected a successful smoke-feed result"
fi

expected_a="$(printf 'native skill sync resource phase A\n' | python3 -c 'import hashlib,sys; print(hashlib.sha256(sys.stdin.buffer.read()).hexdigest())')"
[[ -f "$RESOURCE_FILE" ]] || die "first skill sync: the phase A resource is absent"
actual_a="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest())' "$RESOURCE_FILE")"
if [[ "$actual_a" == "$expected_a" ]]; then
  pass "first skill sync: the installed resource has the exact phase A SHA-256"
else
  die "first skill sync: the installed resource digest does not match phase A"
fi

printf 'B\n' >"$FEED_PHASE_FILE"
log "Run the second operator skill sync for phase B..."
second_status=0
second_output="$(nc skill sync 2>&1)" || second_status=$?
echo "$second_output"
if [[ "$second_status" -eq 0 \
      && "$second_output" == *"smoke-feed: ok changed=1 unchanged=0 rejected=0 failed=0 sidecar=absent"* ]]; then
  pass "second skill sync: phase B reports one changed RFC skill"
else
  die "second skill sync: expected one changed RFC skill and no failures"
fi

pass_id="$(printf '%s\n' "$second_output" | sed -n 's/^Skill sync pass \([0-9a-f]\{32\}\)$/\1/p')"
if [[ -n "$pass_id" ]]; then
  pass "second skill sync: the CLI reports a valid daemon pass ID"
else
  fail "second skill sync: the CLI did not report a valid daemon pass ID"
fi

if [[ -n "$pass_id" ]] \
    && grep -R -F "External skill sync pass completed. ${pass_id}" \
      "${NETCLAW_HOME}/logs" >/dev/null 2>&1; then
  pass "second skill sync: the daemon completion log has the CLI pass ID"
else
  fail "second skill sync: the daemon completion log lacks the CLI pass ID"
fi

expected_b="$(printf 'native skill sync resource phase B\n' | python3 -c 'import hashlib,sys; print(hashlib.sha256(sys.stdin.buffer.read()).hexdigest())')"
[[ -f "$RESOURCE_FILE" ]] || die "second skill sync: the phase B resource is absent"
actual_b="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest())' "$RESOURCE_FILE")"
if [[ "$actual_b" == "$expected_b" ]]; then
  pass "second skill sync: the installed resource has the exact phase B SHA-256"
else
  fail "second skill sync: the installed resource digest does not match phase B"
fi

inventory_file="${NETCLAW_HOME}/skill-inventory.json"
if run_timed "$STEP_TIMEOUT_SECONDS" curl -fsS "${DAEMON_BASE_URL}/api/skills" >"$inventory_file" \
    && python3 - "$inventory_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    inventory = json.load(stream)
rows = [row for row in inventory["skills"] if row["name"] == "smoke-feed-skill"]
raise SystemExit(0 if len(rows) == 1 and rows[0]["version"] == "2.0.0" else 1)
PY
then
  pass "live inventory: /api/skills reports version 2.0.0 from the daemon registry"
else
  fail "live inventory: /api/skills does not report version 2.0.0"
fi

if cmp -s "$NETCLAW_JSON" "$EXPECTED_CONFIG"; then
  pass "skill sync: the command did not change netclaw.json"
else
  fail "skill sync: the command changed netclaw.json"
fi

summarize
exit $?
