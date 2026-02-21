# PRD-006: MCP Tool Integration

## Status

- State: Draft for execution
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-002`, `PRD-004`

## Goal

Support MCP servers in MVP so Netclaw can use externally hosted tools through a
controlled and auditable integration path.

## Product Outcomes

1. Operators can register and validate MCP servers during onboarding or CLI.
2. MCP tools are available to Netclaw sessions only when policy allows.
3. MCP connectivity and failures are visible in diagnostics.

## Requirements

### MCP-001 Server Configuration

Operators SHALL configure MCP servers via config/CLI with named profiles.

### MCP-002 Connection Validation

CLI SHALL validate MCP server reachability and protocol compatibility.

### MCP-003 Policy Gating

MCP tool invocation SHALL be subject to the same ACL/data grant checks as local
tools.

### MCP-004 Safe Defaults

MCP integration is disabled until explicitly configured and enabled.

### MCP-005 Diagnostics

Runtime diagnostics SHALL show server health, tool discovery state, and recent
MCP invocation failures.

## Non-Goals (MVP)

- dynamic marketplace discovery of MCP servers
- unmanaged auto-install of remote tool bundles
- multi-tenant tool permission partitioning

## Acceptance Criteria

1. `netclaw mcp validate` reports pass for a healthy server.
2. Denied MCP tool calls return policy deny reason.
3. Diagnostics show MCP server status and last error.
