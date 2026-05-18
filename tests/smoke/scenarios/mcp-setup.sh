#!/usr/bin/env bash
# mcp-setup.sh — goal: register the deterministic test MCP server and verify
# the agent actually invokes one of its tools.
#
# The MCP server (Netclaw.SmokeMcpServer) exposes add(a,b) and echo(text).
# add(2,2) is always 4, so the tool RESULT is deterministic even though the
# LLM prose is not. This scenario REQUIRES the tool-calling model
# ($SMOKE_TOOL_MODEL); qwen2:0.5b cannot emit tool calls.
#
# MCP servers are loaded by the daemon at startup, so `mcp add` MUST run
# before the daemon starts.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v jq >/dev/null 2>&1 || die "jq is required for mcp-setup.sh"

# The MCP server binary is published + exported by run-smoke.sh.
MCP_SERVER="${NETCLAW_SMOKE_MCP_SERVER:-}"
if [[ -z "$MCP_SERVER" ]]; then
  die "NETCLAW_SMOKE_MCP_SERVER is not set — run via scripts/smoke/run-smoke.sh"
fi
if [[ ! -x "$MCP_SERVER" ]]; then
  die "MCP server binary not found / not executable: $MCP_SERVER"
fi
log "Using MCP server binary: $MCP_SERVER"

MCP_SERVER_NAME="smoke-math"
NETCLAW_JSON="${NETCLAW_HOME}/config/netclaw.json"

trap stop_daemon EXIT

log "Seeding provider + tool model ($SMOKE_TOOL_MODEL)..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
nc model set main local-ollama "$SMOKE_TOOL_MODEL"

# ── Register the MCP server (before the daemon starts) ──
log "Registering MCP server '$MCP_SERVER_NAME' (stdio, --grant-all)..."
nc mcp add --transport stdio --grant-all "$MCP_SERVER_NAME" -- "$MCP_SERVER"

log "Verifying netclaw.json recorded the McpServers entry..."
if [[ ! -f "$NETCLAW_JSON" ]]; then
  die "expected config file at $NETCLAW_JSON"
fi
if jq -e --arg n "$MCP_SERVER_NAME" '.McpServers[$n] != null' "$NETCLAW_JSON" >/dev/null 2>&1; then
  pass "netclaw.json: McpServers[\"$MCP_SERVER_NAME\"] is present"
else
  die "netclaw.json: McpServers[\"$MCP_SERVER_NAME\"] missing"
fi

mcp_command="$(jq -r --arg n "$MCP_SERVER_NAME" '.McpServers[$n].Command // empty' "$NETCLAW_JSON")"
if [[ "$mcp_command" == "$MCP_SERVER" ]]; then
  pass "netclaw.json: McpServers[\"$MCP_SERVER_NAME\"].Command points at the server binary"
else
  die "netclaw.json: expected Command '$MCP_SERVER', got '$mcp_command'"
fi

log "Verifying 'mcp list' shows the server..."
mcp_list="$(nc mcp list 2>/dev/null || true)"
echo "$mcp_list"
if [[ "$mcp_list" == *"$MCP_SERVER_NAME"* ]]; then
  pass "mcp list: includes $MCP_SERVER_NAME"
else
  die "mcp list: expected $MCP_SERVER_NAME"
fi

# ── Start the daemon so it loads the MCP server ──
log "Starting daemon (loads the MCP server)..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

# ── Drive the agent to use the add tool ──
log "Prompting the agent to use the add tool (2 + 2)..."
json_output="$(nc_chat -p --json \
  "Use the add tool to add 2 and 2. Reply with only the number." 2>/dev/null || true)"
echo "$json_output"

if ! echo "$json_output" | jq -e . >/dev/null 2>&1; then
  die "chat --json: output did not parse as JSON"
fi

# HARD assert: the agent emitted a tool call whose name resolves to `add`.
# MCP tool names are namespaced as {server}/{tool}, so match on a /add or
# bare add tail.
add_call="$(echo "$json_output" \
  | jq -r '[.toolCalls[]? | select((.toolName // "") | test("(^|[/:])add$"))] | length')"
if [[ "$add_call" =~ ^[0-9]+$ ]] && (( add_call > 0 )); then
  pass "tool call: agent invoked the MCP 'add' tool ($add_call call(s))"
else
  tool_names="$(echo "$json_output" | jq -rc '[.toolCalls[]?.toolName]')"
  die "tool call: agent did not invoke the 'add' tool (toolCalls=$tool_names)"
fi

# SOFT assert: the prose contains the deterministic result (4). Small models
# sometimes drop the number from the final message — that is a model-quality
# issue, not a harness bug, so warn rather than fail.
response="$(echo "$json_output" | jq -r '.response // empty')"
if [[ "$response" == *"4"* ]]; then
  pass "response: includes the deterministic result '4'"
else
  warn "response: did not include '4' (model-quality, not a harness bug)"
fi

# Cross-check the deterministic tool RESULT in the per-session log.
session_id="$(echo "$json_output" | jq -r '.sessionId // empty')"
if [[ -n "$session_id" ]]; then
  sanitized="${session_id//\//-}"
  log_file="${NETCLAW_HOME}/logs/${sanitized}.log"
  if [[ -f "$log_file" ]] && grep -qE 'TOOL_RESULT:.*\b4\b' "$log_file"; then
    pass "session log: TOOL_RESULT recorded the deterministic value 4"
  else
    warn "session log: no 'TOOL_RESULT ... 4' line found (model-quality / timing)"
  fi
fi

summarize
exit $?
