# SPEC-009: MCP Integration Contract

Source PRDs: `PRD-006`, `PRD-004`, `PRD-002`

## Purpose

Define configuration, validation, policy enforcement, and diagnostics behavior
for MCP server integration.

## Server Configuration

- MCP servers are configured as named profiles
- each profile declares transport details and auth material source
- profiles are disabled by default until explicitly enabled

## Validation Contract

- `netclaw mcp validate` checks connectivity, protocol handshake, and tool
  discovery
- validation returns structured pass/fail with remediation guidance

## Runtime Contract

- tool discovery from enabled MCP profiles occurs during startup
- discovered tools are exposed through Netclaw tool registry
- tool invocation is gated by ACL/policy grants

## Failure Handling

- unreachable MCP server does not crash session actors
- failed servers are marked degraded and excluded from invocation
- retries are bounded and observable

## Diagnostics

- report server state: healthy | degraded | unavailable
- include discovered tool count per server
- include last error and timestamp
- distinguish auth-required, auth-failed, and unreachable states on the daemon runtime path
- never claim OAuth auth failure from an offline-only probe when daemon truth is unavailable
