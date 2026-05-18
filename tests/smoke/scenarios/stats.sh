#!/usr/bin/env bash
# stats.sh — `netclaw stats` text / json / --days / skills surfaces.
#
# Folded from scripts/smoke/check.sh (~lines 340-382). The stats command
# reads from the running daemon, which needs at least one completed
# session to report token counters — so this scenario seeds config,
# starts a daemon, runs a headless prompt, then exercises stats.
#
# Self-contained: seeds provider + model config, starts a fresh daemon,
# asserts, stops it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

SMOKE_MODEL="${SMOKE_OLLAMA_MODEL:-qwen2:0.5b}"
OLLAMA_ENDPOINT="${SMOKE_OLLAMA_ENDPOINT:-http://localhost:11434}"

nc() { run_timed "$STEP_TIMEOUT_SECONDS" "$NETCLAW_SMOKE_CLI" "$@"; }

cleanup() { stop_daemon; }
trap cleanup EXIT

log "Seeding provider + model config..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
nc model set main local-ollama "$SMOKE_MODEL"

log "Starting daemon for stats tests..."
if ! start_daemon; then
  fail "daemon did not start"
  summarize || exit 1
  exit 1
fi
wait_for_health || { fail "daemon health endpoint not ready"; summarize || exit 1; exit 1; }

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
