## Why

`PRD-009` defines the input-adapter principle: every input creates the same kind
of session turn, with source metadata and instructions carrying the differences.
The current implementation has largely achieved that at the `ChannelInput` seam,
but delivery still drifts by adapter.

The problem is not that all transports need the same lifecycle implementation.
The problem is that Netclaw does not yet have a standard model for addressable
channels as output-capable delivery surfaces.

Current gaps include:

- Slack, Discord, and Mattermost expose different LLM-facing send and lookup
  tool shapes.
- User and destination lookup is inconsistent and often ID-first instead of
  name-searchable.
- Runtime status and stats are partially hard-coded to specific channel adapters.
- `ChannelType` currently mixes conversation channels, daemon endpoints, local
  clients, and trigger sources, which makes channel delivery semantics unclear.
- Reminders and webhooks can create input turns, but they need real channel
  delivery targets when output should be emitted.
- Stateful remote chat channels expose different lifecycle health semantics.

Source PRDs/specs: `PRD-009-input-adapters-and-unified-input.md`,
`SPEC-011-daemon-architecture.md`, `openspec/specs/netclaw-input-adapters/spec.md`.

## What Changes

- Define a channel as an addressable output-capable delivery surface that MAY
  also produce input.
- Separate input sources from output channels: Slack/Discord/Mattermost/TUI can
  be both, while reminders and webhooks are trigger sources that consume the
  channel delivery model.
- Add a standard channel descriptor contract for output-capable channels.
- Add a standard channel runtime snapshot contract for health, readiness,
  enabled state, endpoint identity, bot identity, and activity reporting.
- Add a standard channel delivery target model for sending output through a
  channel destination and optional thread/root.
- Add descriptor-scoped address resolution so users, rooms, channels, DMs,
  threads, and destinations can be resolved by stable IDs or user-facing names.
- Add standard LLM-facing tool intent schemas for channel send and lookup tools,
  allowing current per-channel tool names to be renamed to the standardized
  surface during migration.
- Change daemon runtime status and stats to enumerate registered channel
  descriptors instead of hard-coding individual channel adapters.
- Define stateful remote chat channel lifecycle requirements that Mattermost,
  Discord, Slack, and future remote chat channels can satisfy without requiring
  a shared base actor.

## Capabilities

### New Capabilities

<!-- None. This extends the existing input-adapter capability. -->

### Modified Capabilities

- `netclaw-input-adapters`: Add requirements for standardized channel delivery
  descriptors, runtime snapshots, delivery targets, address resolution,
  LLM-facing channel tool intents, descriptor-driven observability, trigger-source
  output routing, and reliable stateful channel lifecycle reporting.

## Impact

- **Affected systems:** channel abstractions, daemon runtime status, daemon
  stats, Slack/Discord/Mattermost LLM tools, channel user/destination lookup,
  reminder/webhook delivery target handling, channel contract tests, and
  stateful channel lifecycle tests.
- **Security:** no new ACL bypass. Channel descriptors and delivery targets must
  preserve the explicit audience, principal, boundary, and provenance already
  required by `ChannelInput` and `MessageSource`.
- **Reliability:** health and readiness become comparable across output-capable
  channels. Stateful remote chat channels expose reconnect and not-ready states
  through a common snapshot shape.
- **Compatibility:** no session identity change is required. Existing
  LLM-facing channel tool names may change as part of standardization; system
  skills and evals must be updated when tool names change.
