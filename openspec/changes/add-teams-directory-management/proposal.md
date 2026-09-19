## Why

Teams is already a secure runtime adapter, but operators must configure opaque
tenant, team, channel, and Entra user identifiers by hand.  That makes
least-privilege rollout error-prone and prevents organizations from expressing
the group-based access controls they use elsewhere.

## What Changes

- Add a bounded Microsoft Graph tenant-directory capability for Teams teams,
  channels, users, and supported Entra groups, using the existing Teams
  application credential and only the required application permissions.
- Add canonical-ID-based global and per-channel Teams user/group authorization,
  with explicit user access preserved as a Graph-independent fast path and
  group lookup failures denied safely.
- Add Teams to the native Channels TUI, including first connection, masked
  secret rotation, Graph-backed directory search, per-channel principals,
  status diagnostics, reset, and non-destructive reopen behavior.
- Extend Teams configuration validation, doctor diagnostics, documentation, and
  tests. Existing `AllowedTeamIds`, `AllowedChannelIds`, `AllowedUserIds`, and
  audience override shapes remain supported.
- Add the Microsoft Graph and Azure Identity dependencies behind an SDK-free
  Teams contract. No Graph SDK type crosses into channel policy or TUI models.

## Capabilities

### New Capabilities

- `teams-directory-management`: Least-privilege, bounded Teams tenant directory
  lookup and canonical configuration of teams, channels, users, and groups.
- `teams-principal-authorization`: Default-deny Teams authorization using
  global and per-channel Entra user/group principals.
- `teams-configuration-tui`: Native Teams setup and ongoing administration in
  the Channels configuration experience.

### Modified Capabilities

- `netclaw-input-adapters`: Teams executable ingress gains the additional
  principal authorization gate while retaining the existing tenant, channel,
  mention, trust, and classifier boundaries.

## Impact

Source PRDs: `PRD-003`, `PRD-004`, `PRD-006`, and `PRD-009`.

This affects the Teams contracts and policies, a new Graph infrastructure
project, daemon registration, the CLI Channels TUI and doctor checks, config
schema, deployment documentation, and focused tests. It adds `Microsoft.Graph`
and `Azure.Identity` packages but reuses the existing Tenant ID, Client ID, and
secret overlay; no second credential or persisted access token is introduced.

Security impact: application permissions are limited to `Team.ReadBasic.All`,
`Channel.ReadBasic.All`, `GroupMember.Read.All`, and `User.Read.All`; the
feature does not request `Directory.Read.All`.  Group membership is fail closed
for unavailable, malformed, denied, throttled-after-retry, and timed-out
lookups. Configuration and diagnostic output never contain a client secret,
token, or raw directory principal ID.

Operational impact: directory lookups use bounded cache-aside storage, a
five-second operation budget, cancellation, small result pages, and bounded
retry that honors service retry-after advice. No live tenant test, deployment,
or merge is part of this change.
