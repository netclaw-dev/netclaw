#!/usr/bin/env bash
# stats.sh — `netclaw stats` text / json / --days / skills surfaces.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

seed_and_start_daemon

log "Creating a completed session so stats has data..."
nc chat -p "Say hello in one word" || true

log "Testing netclaw stats (text)..."
stats_output="$(nc stats 2>/dev/null || true)"
echo "$stats_output"
if [[ "$stats_output" == *"tokens:"* ]]; then
  pass "stats: output includes token counters"
else
  fail "stats: expected token counters"
fi

log "Testing netclaw stats --json..."
stats_json="$(nc stats --json 2>/dev/null || true)"
echo "$stats_json"
if [[ "$stats_json" == *"inputTokensTotal"* ]]; then
  pass "stats --json: includes inputTokensTotal field"
else
  fail "stats --json: expected inputTokensTotal field"
fi

log "Testing netclaw stats --days 7..."
stats_days="$(nc stats --days 7 2>/dev/null || true)"
echo "$stats_days"
if [[ "$stats_days" == *"date"* ]]; then
  pass "stats --days 7: includes daily breakdown with date column"
else
  fail "stats --days 7: expected daily breakdown with date column"
fi

log "Testing netclaw stats skills..."
skill_stats_output="$(nc stats skills 2>/dev/null || true)"
echo "$skill_stats_output"
if [[ "$skill_stats_output" == *"by method:"* || "$skill_stats_output" == *"No skill loads recorded."* ]]; then
  pass "stats skills: method breakdown or empty-state message present"
else
  fail "stats skills: expected method breakdown or empty-state message"
fi

log "Testing netclaw stats skills --json..."
skill_stats_json="$(nc stats skills --json 2>/dev/null || true)"
echo "$skill_stats_json"
if [[ "$skill_stats_json" == *"\"daily\""* ]]; then
  pass "stats skills --json: includes daily field"
else
  fail "stats skills --json: expected daily field"
fi

summarize
exit $?
