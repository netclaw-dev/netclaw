# PRD-006: MCP Tool Integration

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-08-07 (MCP prompts as dynamic Netclaw skills)
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
5. MCP prompt workflows are discoverable through the existing skill system.

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

### MCP-009 Daemon-Bound Server Ownership

Each configured MCP server SHALL have at most one published client generation
per Netclaw daemon. A replacement MAY initialize while the published generation
continues serving, but it SHALL remain unpublished until initialization
succeeds, and the replaced generation SHALL not be disposed while it has
in-flight calls. A local STDIO server process and its internal state are shared
by all sessions authorized to use that server; Netclaw session identity SHALL
not launch or select a separate MCP process.

### MCP-010 Secure OAuth Lifecycle

HTTP MCP OAuth SHALL delegate protocol operations to the MCP C# SDK while
Netclaw owns local browser-flow brokering, credential persistence, and client
lifecycle. Persisted tokens and dynamically registered client credentials SHALL
be bound to the configured MCP resource identity and SHALL NOT be supplied after
that identity changes or when a legacy record lacks a binding. Credential
persistence failures, invalid callback state, and authorization failures SHALL
fail visibly without deleting the last working credentials or connection.

Concurrent authorization and reconnect attempts SHALL coalesce per server.
Ambiguous transport failures SHALL NOT automatically replay a tool invocation,
because the remote operation may already have completed.

### MCP-011 Prompt Discovery and Skill Adaptation

Netclaw SHALL discover prompt descriptors from each enabled server that
declares prompt support. It SHALL publish tools and prompts in one immutable
server generation.

Each prompt SHALL enter the unified skill catalog as
`mcp__<server>__<prompt>`. The agent SHALL render a selected prompt through
`skill_load` and `prompts/get`.

The existing MCP server grant SHALL control prompt discovery and use. A prompt
SHALL NOT grant a tool or bypass a tool approval.

`skill_load` SHALL validate required and unknown prompt arguments before the
remote request. It SHALL preserve prompt roles and source attribution.

A failed prompt discovery or refresh SHALL keep the last good generation.
The existing catalog poll SHALL include prompt descriptors.

## Non-Goals (MVP)

- Dynamic marketplace discovery of MCP servers
- Unmanaged auto-install of remote tool bundles
- Multi-tenant tool permission partitioning
- Proactive MCP catalog subscriptions
- MCP resource discovery and read operations
- MCP prompt completion API support
- First-party client autocomplete for prompt skills

## Acceptance Criteria

1. `netclaw mcp validate` reports pass for a healthy server.
2. Denied MCP tool calls return policy deny reason.
3. Diagnostics show MCP server status and last error.
4. Memorizer store/search/get cycle works through Netclaw session.
5. MCP tools appear in session tool definitions when server is enabled and
   granted.
6. Unavailable MCP server does not crash the session.
7. Calls from different authorized sessions to one local STDIO profile use the
   same daemon-owned client and child process.
8. Repointing an MCP profile does not send credentials bound to its old resource
   identity to the new endpoint.
9. Legacy OAuth records without a resource binding fail closed and direct the
   operator to reauthorize.
10. OAuth token persistence failure is visible and does not advance caller-visible
   credential state.
11. A transport failure may reconnect the server for later calls but does not
    replay the failed tool invocation automatically.
12. A prompt-capable server contributes canonical MCP prompt skills to an
    authorized session.
13. `skill_load` renders an MCP prompt with validated arguments and source
    attribution.
14. A denied server contributes no prompt skills to that audience.

## Cross-References

- MVP scope: PRD-001
- Security policy: PRD-002 (SEC-003 grant format)
- CLI validation: PRD-004
- Provider tool calling: PRD-005 (MP-010)
- Local memory tier: PRD-007
- Dynamic context discovery research: `docs/research/dynamic-context-discovery.md`
