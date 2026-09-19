## Why

Netclaw currently supports Slack, Discord, and Mattermost but cannot operate in
Microsoft Teams. Teams is a core collaboration surface for self-hosted and
enterprise operators, and its HTTP activity model requires an adapter that
preserves Netclaw's actor ownership, default-deny security posture, and
restart-safe approval flows.

## What Changes

- Add a disabled-by-default Microsoft Teams channel adapter to the daemon.
- Accept authenticated Teams personal-chat and channel activities, translate
  them at the transport boundary, and route them through Netclaw session actors.
- Add tenant-aware session identity, Teams ACL/mention policy, bounded output,
  Adaptive Card approvals, supported attachment staging, and proactive reminder
  delivery.
- Add Teams configuration, diagnostics, app-package assets, and an operator
  deployment runbook.
- Keep group chat, meeting chat, Microsoft Graph file retrieval, delegated
  user actions, SSO, tabs, message extensions, and multi-tenant distribution
  out of the MVP.

## Capabilities

### New Capabilities

- `netclaw-teams-channel`: Secure Teams personal and channel conversation
  ingress, session routing, ACL policy, output delivery, and operational health.
- `teams-interactive-approvals`: Teams Adaptive Card approval delivery and
  restart-safe validation.
- `teams-proactive-delivery`: Teams reminder target resolution and proactive
  delivery using persisted, non-secret conversation addresses.

### Modified Capabilities

- `netclaw-input-adapters`: Add Teams as a transport-agnostic inbound adapter
  with explicit trust context and interactive-approval capability.
- `netclaw-acl`: Apply the default-deny channel ACL model to Teams tenant,
  team, channel, and user identities.

## Impact

- New `Netclaw.Channels.Teams` project and daemon HTTP endpoint/DI wiring.
- `ChannelType`, channel descriptors, reminder routing, configuration schema,
  diagnostics, and tests gain Teams support.
- Adds centrally pinned Microsoft Teams SDK dependencies after a compatibility
  spike validates their net10.0 and Linux-container behavior. Offline SDK
  compatibility is complete; tenant-backed transport behavior remains an
  explicit opt-in release gate.
- Security impact: Microsoft SDK types remain at the transport edge; inbound
  authentication, tenant/ACL gates, attachment validation, approval
  correlation, and endpoint registration fail closed. No Graph permissions are
  requested for the MVP.
