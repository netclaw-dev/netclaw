## Why

MCP catalog polling can delay remote tool and prompt changes for five minutes.
GitHub issue #1808 requires immediate change signals while the poll remains the repair path.

## What Changes

- Listen for tool and prompt list changes on each daemon-owned MCP client generation.
- Use `subscriptions/listen` with an acknowledgement for MCP revision 2026-07-28.
- Use direct list-change notifications for older servers that declare this support.
- Coalesce repeated signals into one active refresh and one queued follow-up refresh.
- Publish tools and prompts as one immutable generation after a successful refresh.
- Keep the last good generation after a failed refresh.
- Recreate the notification lease after a reconnect and remove stale leases.
- Keep the existing poll for repair and for servers without notification support.
- Report compatibility and failure states through structured logs.
- Update PRD-006 and the `netclaw-operations` system skill.

This change does not add MCP resources, resource subscriptions, configuration, status API fields, CLI fields, or TUI behavior.
GitHub issue #1807 owns modern and legacy resource subscription behavior.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-mcp`: Add immediate MCP tool and prompt catalog refresh with modern and legacy protocol support.

## Impact

The change affects `McpClientManager`, its MCP SDK adapter, connection snapshots, lifecycle tests, PRD-006, and operations guidance.

The change adds no public API, configuration, actor message, persistence, security policy, or grant change.
The existing MCP server grant remains authoritative for all published tools and prompts.

The daemon logs unsupported or failed notification setup and continues the existing poll.
The poll is a normal compatibility and repair path, not a silent replacement for a failed catalog publication.
