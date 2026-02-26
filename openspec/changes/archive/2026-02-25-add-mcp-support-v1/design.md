# Design: MCP Support v1

## Context

Netclaw currently plans for local tools and provider integration. MCP needs to
be integrated without weakening ACL controls.

## Goals / Non-Goals

Goals:

- define secure MCP configuration and validation contracts
- ensure MCP invocation passes through existing policy gates
- expose MCP health through CLI and UI diagnostics

Non-goals:

- implementing marketplace discovery or automatic remote installs

## Decisions

### Decision 1: MCP is opt-in

MCP profiles are configured and enabled explicitly; default runtime has no MCP
servers enabled.

### Decision 2: Shared policy gate

MCP tools use the same ACL and grant checks as local tools.

### Decision 3: Degraded server isolation

MCP server outages are isolated and observable; they do not crash session actors.
