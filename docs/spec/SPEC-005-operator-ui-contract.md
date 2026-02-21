# SPEC-005: Operator UI Contract (Ops Console)

Source PRDs: `PRD-003`, `PRD-002`, `PRD-004`

## Purpose

Define the data and interaction contract for an operations-first management UI.

## Delivery Stack (MVP)

- host: ASP.NET Core in-process with gateway runtime
- UI: Blazor Server components
- real-time channel: SignalR
- style assets: static CSS served by ASP.NET Core

No separate Node runtime is required for MVP console delivery.

## Information Architecture

### Route: `/overview`

Displays:

- gateway health and uptime
- Slack connection status
- active session count
- recent policy denies and critical errors

### Route: `/sessions`

Displays:

- searchable session table keyed by `{channelId}/{threadTs}`
- last activity, turn count, compaction status, recovery status

### Route: `/sessions/:id`

Displays:

- timeline of user and assistant turns (redacted as configured)
- latest snapshot metadata
- compaction history
- policy decisions attached to recent inbound events

### Route: `/policy`

Displays:

- ACL editor with schema-aware validation
- effective policy simulator for channel/sender/tool combinations
- pending change preview before apply

### Route: `/security`

Displays:

- bind mode and exposure mode
- pairing and privileged approval state
- high-risk configuration warnings

### Route: `/diagnostics`

Displays:

- filtered logs
- persistence and actor warnings
- guided remediation actions

### Route: `/tools`

Displays:

- configured MCP servers and health state
- discovered tool count per server
- recent tool invocation failures

## Data Contracts (Abstract)

### Health Summary

- `status`: healthy | degraded | unhealthy
- `uptimeSeconds`
- `slackConnected`: boolean
- `persistenceConnected`: boolean

### Session Summary

- `sessionId`
- `channelId`
- `threadTs`
- `lastActivityUtc`
- `turnCount`
- `compacted`: boolean

### Policy Decision Record

- `timestampUtc`
- `action`: allow | deny
- `reasonCode`
- `channelId`
- `senderId`
- `toolName` (optional)

## Interaction Rules

- dangerous actions require explicit confirmation
- policy edits require validation pass before apply
- inline warnings must include rationale and next step guidance
- read-only mode available for mobile triage

## Accessibility and Responsiveness

- keyboard navigable controls for all operator actions
- contrast-safe status color system
- mobile layout prioritizes overview and diagnostics read paths
