## Why

Netclaw's input architecture treats channels as a pluggable, transport-agnostic
boundary, and ships first-party Slack and Discord channels. Mattermost is a
common self-hosted chat platform for exactly the owner-operator audience Netclaw
targets, but has no integration. A prior attempt (PR #877) went stale and
predates the current channel contract surface — the conformance test bases, the
value-object refactor, and roughly a dozen per-channel bug fixes hardened into
Slack and Discord since. Adding Mattermost now, built against current contracts,
delivers channel parity without re-introducing those resolved defects.

Source PRDs: `PRD-009-input-adapters-and-unified-input.md`,
`PRD-001-netclaw-mvp.md`, `PRD-002-gateway-security-envelope.md`,
`PRD-008-scheduling-and-periodic-tasks.md`, `PRD-003-operator-ux-ops-console.md`.

## What Changes

- Add a first-party Mattermost channel with Slack/Discord parity: gateway
  lifecycle and health, ACL-gated ingress normalization, deterministic
  thread-aware session identity, and thread-bound reply delivery.
- Add Mattermost thread-history backfill that hydrates bot-authored messages
  only at the thread root (no re-adoption of the agent's own output below root)
  and re-arms deferred hydration on the first authorized inbound.
- Add proactive Mattermost sends (`send_mattermost_message` tool) with an
  acknowledged thread-initialization handshake.
- Add Mattermost scheduled-reminder delivery, including delivery to direct
  messages — Mattermost DMs are addressable channels, so DM reminder targets are
  supported (an improvement over Discord, which rejects them).
- Add interactive tool-approval UX for Mattermost via a channel-owned,
  token-authenticated inbound HTTP callback endpoint (`/api/mattermost/actions`), with
  a deterministic text-reply fallback. Mattermost — unlike Slack Socket Mode and
  the Discord gateway — delivers interactive button clicks only over inbound
  HTTP, so a new authenticated inbound surface is required.
- Add CI-safe offline tests (channel conformance contract suites, unit tests)
  and Testcontainers-based integration tests against a real Mattermost server.

**In scope (MVP):** Mattermost gateway transport (WebSocket events + REST
replies/outbound), ACL-gated ingress parity, deterministic session identity,
thread-bound reply delivery, thread-history backfill with root-only bot dedup,
proactive sends with ack, scheduled-reminder delivery to channels and DMs,
reminder-spawned interactive sessions, interactive approvals via an
token-authenticated callback endpoint with deterministic text fallback, channel
conformance contract suites, and offline + Testcontainers test coverage.

**Out of scope:** Mattermost plugin packaging, slash-command app registration on
the Mattermost server, voice/call features, cross-channel session merging, and
any change to session actor or persistence contracts.

## Capabilities

### New Capabilities

- `netclaw-mattermost-socket`: Mattermost channel requirements — gateway
  lifecycle and health, ingress normalization and ACL-gated dispatch,
  deterministic thread-aware session identity, thread-bound reply delivery,
  thread-history backfill with root-only bot dedup and deferred-hydration
  re-arm, proactive sends with an acknowledged handshake, scheduled-reminder
  delivery to channels and DMs, and reminder-spawned interactive sessions.

### Modified Capabilities

- `netclaw-input-adapters`: Add Mattermost source metadata and entity-key
  routing semantics while preserving transport-agnostic actor/session
  boundaries.
- `tool-approval-gates`: Add Mattermost interactive approval rendering and
  response handling via a channel-owned token-authenticated callback endpoint, with a
  deterministic text-reply fallback that produces equivalent approval outcomes.
- `netclaw-gateway-security`: Add requirements for a channel-owned,
  token-authenticated, ACL-checked inbound HTTP callback endpoint for Mattermost
  interactive actions — the first channel to require an inbound HTTP surface.
- `netclaw-testing`: Add CI-safe offline test requirements for the Mattermost
  adapter (conformance contract suites, fallback behavior) plus optional
  Testcontainers integration coverage that does not run in required CI.

## Impact

- **Affected systems:** channel runtime wiring, session routing metadata,
  reminder target resolution, tool-interaction request/response rendering,
  daemon HTTP route registration, and channel test harnesses.
- **New code:** `Netclaw.Channels.Mattermost` and
  `Netclaw.Channels.Mattermost.IntegrationTests` projects; Mattermost contract
  test subclasses.
- **APIs/config:** adds a Mattermost channel configuration section and the
  config-schema entry; adds the `/api/mattermost/actions` callback route; no
  breaking Slack or Discord config change.
- **Dependencies:** adds the Mattermost.NET client library and Testcontainers
  (test-only).
- **Security impact:** introduces the first channel-owned inbound HTTP endpoint.
  It is authenticated by single-use opaque action token (server-stored,
  channel-bound, 12h TTL) and ACL-checked, fails closed on invalid configuration,
  and is disabled unless the Mattermost channel is enabled with interactive
  approvals configured. Default-deny ACL and fail-closed startup posture are
  preserved for all Mattermost paths.
- **Operational impact:** adds Mattermost connection/approval-callback
  diagnostics and setup runbook guidance; no live Mattermost dependency in
  required CI suites.
