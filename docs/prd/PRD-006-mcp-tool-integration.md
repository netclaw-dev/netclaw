# PRD-006: MCP Tool Integration

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (Memorizer as external memory tier, tool loading)
- Depends on: `PRD-001`, `PRD-002`, `PRD-004`

## Goal

Support MCP servers in MVP so Netclaw can use externally hosted tools through a
controlled and auditable integration path. Memorizer serves as the primary
external memory tier for research findings, knowledge base, and cross-session
learning.

## Product Outcomes

1. Operators can register and validate MCP servers during onboarding or CLI.
2. MCP tools are available to Netclaw sessions only when policy allows.
3. MCP connectivity and failures are visible in diagnostics.
4. Memorizer provides durable cross-session knowledge that outlives compaction.

## Two-Tier Memory Architecture

Netclaw uses a two-tier memory model:

| Tier | Storage | Content | Scope |
|------|---------|---------|-------|
| Local (PRD-007) | Files on disk | Personality, projects, environment, schedules | Personal, operational |
| External (this PRD) | MCP/Memorizer | Research, knowledge base, cross-session learning | Knowledge, durable |

Local memory is small, personal, and loaded into context. External memory is
queried on demand via MCP tool calls.

## Requirements

### MCP-001 Server Configuration

Operators SHALL configure MCP servers via config/CLI with named profiles.
Each profile includes: name, transport (stdio/SSE), command/URL, environment
variables, and enable/disable flag.

### MCP-002 Connection Validation

CLI SHALL validate MCP server reachability and protocol compatibility.
`netclaw mcp test {server}` SHALL attempt a tool listing and report results.

### MCP-003 Policy Gating

MCP tool invocation SHALL be subject to the same ACL/data grant checks as local
tools. Grants use the format `mcp:{server_name}` per SEC-003 in PRD-002.

### MCP-004 Safe Defaults

MCP integration is disabled until explicitly configured and enabled. Each MCP
server must be individually enabled.

### MCP-005 Diagnostics

Runtime diagnostics SHALL show server health, tool discovery state, available
tool count, and recent MCP invocation failures.

### MCP-006 Tool Discovery and Registration

On startup (and on demand), Netclaw SHALL discover available tools from each
enabled MCP server and register them as available tool definitions for the MEAI
tool calling pipeline. Tool definitions are refreshed on session start.

### MCP-007 Memorizer as External Memory

Memorizer SHALL be the recommended first MCP server configured during
onboarding. It provides:

- `store` — persist research findings and knowledge
- `search` — semantic search across stored memories
- `get` / `get_many` — retrieve specific memories
- `delete` — remove outdated memories
- `create_relationship` — link related memories

The agent uses Memorizer for:
- Saving research findings that should outlive the current session
- Retrieving previously learned knowledge on related topics
- Building a knowledge base across conversations and sessions
- Pre-compaction memory flush (saving durable context before compaction)

### MCP-008 Graceful Degradation

Runtime SHALL degrade gracefully when MCP server is unavailable:

- Tool calls to unavailable servers return a clear error message
- The agent continues operating with remaining tools
- Reconnection is attempted on next tool call
- Diagnostics flag the outage

## Non-Goals (MVP)

- Dynamic marketplace discovery of MCP servers
- Unmanaged auto-install of remote tool bundles
- Multi-tenant tool permission partitioning
- Hot-reload of MCP tool definitions (requires session reboot)

## Acceptance Criteria

1. `netclaw mcp validate` reports pass for a healthy server.
2. Denied MCP tool calls return policy deny reason.
3. Diagnostics show MCP server status and last error.
4. Memorizer store/search/get cycle works through Netclaw session.
5. MCP tools appear in session tool definitions when server is enabled and
   granted.
6. Unavailable MCP server does not crash the session.

## Cross-References

- MVP scope: PRD-001
- Security policy: PRD-002 (SEC-003 grant format)
- CLI validation: PRD-004
- Provider tool calling: PRD-005 (MP-010)
- Local memory tier: PRD-007
