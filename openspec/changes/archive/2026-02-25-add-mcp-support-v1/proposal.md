# Proposal: Add MCP Support v1

## Source PRDs

- `PRD-006-mcp-tool-integration.md`
- `PRD-001-netclaw-mvp.md`

## Why

MCP support is now an MVP requirement and must be planned with the same
security and diagnostics standards as local tools.

## What Changes

1. Define MCP server profile and validation requirements.
2. Define ACL/policy-gated MCP invocation requirements.
3. Define CLI and diagnostics requirements for MCP health.

## Scope

In scope:

- planning artifacts and capability deltas

Out of scope:

- implementing protocol adapter code in this change

## Impact

- aligns MVP scope with operator needs
- reduces implementation ambiguity around MCP security
