#!/usr/bin/env bash
# mcp-setup.sh — goal: register an MCP server and verify the daemon
# connects to it and indexes its tools.
#
# The deterministic test server (Netclaw.SmokeMcpServer) exposes
# add/echo/record-tasks/process-info over stdio. This scenario hard-verifies netclaw's
# MCP integration:
# `mcp add` records the server in config, and on daemon startup the daemon
# spawns the stdio server, completes the MCP handshake, and registers its
# tools — confirmed from the daemon log.
#
# It deliberately does NOT drive the agent to invoke a tool. A reliable
# tool-calling turn needs a model far too large to run on a CPU CI runner
# in reasonable time (a small model just rambles); that end-to-end check
# belongs on a cloud inference provider, not in this harness.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../../scripts/smoke/lib/common.sh
. "${SCRIPT_DIR}/../../../scripts/smoke/lib/common.sh"

command -v jq >/dev/null 2>&1 || die "jq is required for mcp-setup.sh"

# The MCP server binary is published + exported by run-smoke.sh.
MCP_SERVER="${NETCLAW_SMOKE_MCP_SERVER:-}"
[[ -n "$MCP_SERVER" ]] || die "NETCLAW_SMOKE_MCP_SERVER is not set — run via scripts/smoke/run-smoke.sh"
[[ -x "$MCP_SERVER" ]] || die "MCP server binary not found / not executable: $MCP_SERVER"
log "Using MCP server binary: $MCP_SERVER"

MCP_SERVER_NAME="smoke-math"
NETCLAW_JSON="${NETCLAW_HOME}/config/netclaw.json"

trap stop_daemon EXIT

log "Seeding provider + model ($SMOKE_MODEL)..."
nc provider add local-ollama ollama --endpoint "$OLLAMA_ENDPOINT"
nc model set main local-ollama "$SMOKE_MODEL"

# ── Register the MCP server (before the daemon starts) ──
log "Registering MCP server '$MCP_SERVER_NAME' (stdio, --grant-all)..."
nc mcp add --transport stdio --grant-all "$MCP_SERVER_NAME" -- "$MCP_SERVER"

log "Verifying netclaw.json recorded the McpServers entry..."
[[ -f "$NETCLAW_JSON" ]] || die "expected config file at $NETCLAW_JSON"
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
[[ "$mcp_list" == *"$MCP_SERVER_NAME"* ]] || die "mcp list: expected $MCP_SERVER_NAME"
pass "mcp list: includes $MCP_SERVER_NAME"

# ── Start the daemon — it spawns + handshakes the MCP server at startup ──
log "Starting daemon (loads the MCP server)..."
start_daemon || die "daemon did not start"
wait_for_health || die "daemon health endpoint not ready"

# ── Hard assert: the daemon connected to the MCP server and indexed its
#    tools. This is the real MCP-integration signal — the stdio handshake
#    plus tool registration — deterministic and fast. The connection is
#    logged during daemon startup; poll briefly in case the log write
#    trails the health endpoint.
log "Verifying the daemon connected to the MCP server..."
connect_line=""
for _ in $(seq 1 15); do
  connect_line="$(grep -hE "MCP server '${MCP_SERVER_NAME}' connected" \
    "${NETCLAW_HOME}"/logs/daemon-*.log 2>/dev/null | head -1 || true)"
  [[ -n "$connect_line" ]] && break
  sleep 1
done
if [[ -n "$connect_line" ]]; then
  echo "  ${connect_line}"
  pass "daemon log: MCP server '$MCP_SERVER_NAME' connected"
else
  die "daemon log: no 'MCP server ${MCP_SERVER_NAME} connected' line — stdio handshake failed"
fi

# The test server exposes exactly four tools (add, echo, record-tasks, process-info) —
# confirm the daemon registered all of them.
if [[ "$connect_line" == *"(4 tools)"* ]]; then
  pass "daemon log: MCP server registered 4 tools (add, echo, record-tasks, process-info)"
else
  die "daemon log: expected '(4 tools)' in the connection line, got: $connect_line"
fi

summarize
exit $?
