## Context

Reminder execution already supports channel-post and current-session delivery,
but current requirements are Slack-centric and do not explicitly define Discord
DM behavior end to end (target validation, routing, authorization parity, and
operator onboarding). At the same time, Discord adapter requirements already
establish deterministic session identity and ACL-gated ingress for normal turns.
This change extends those guarantees to reminder delivery and `netclaw init`
without introducing channel-specific behavior inside session actors.

Constraints:

- Keep actor boundaries transport-agnostic at the session layer.
- Preserve default-deny ACL behavior and audience-bound reminder minting.
- Keep reminder delivery fail-loud; no silent fallback from DM to other targets.
- Keep onboarding as one guided `netclaw init` pipeline, not per-channel forks.

## Goals / Non-Goals

**Goals:**

- Define a deterministic Discord DM reminder-delivery contract for both
  `delivery.kind = channel` and `delivery.kind = current_session` paths.
- Require Slack-like authorization controls for Discord reminder creation and
  execution (sender/channel allow checks, audience bounds, grant checks).
- Extend `netclaw init` requirements so operators can configure Discord adapter
  credentials and baseline Discord ACL policy in wizard flow.
- Preserve existing reminder delivery observability and retry semantics when
  Discord DM delivery is required and not observed.

**Non-Goals:**

- Designing Discord guild-channel reminder UX beyond DM scope.
- Changing reminder execution state machine semantics unrelated to Discord.
- Introducing new transport-specific session actor types.

## Decisions

### Decision 1: Reuse existing delivery kinds; add Discord-specific validation and routing constraints

`ReminderDefinition.Delivery` remains the contract (`kind`, `transport`,
`address`, `sessionId`, `originChannelType`) and Discord support is expressed by:

- `transport = "discord"` for channel-kind delivery with canonical DM target ID.
- `originChannelType = Discord` for current-session delivery.

This avoids schema sprawl and keeps transport plugging consistent.

**Alternatives considered**

- Add a `DiscordDm` delivery kind: rejected because it duplicates information
  already represented by `kind + transport + originChannelType`.
- Auto-detect Discord DM from free-text target hints: rejected because it
  weakens validation guarantees and creates ambiguous routing behavior.

### Decision 2: Keep reminder routing in gateways, not session actors

Current-session Discord reminder turns route through Discord gateway hierarchy
using trusted delivery messages, matching Slack/SignalR patterns. Session
actors continue to receive normalized `SendUserMessage` only.

**Alternatives considered**

- Send reminder turns directly to session actors for Discord: rejected because it
  bypasses gateway-level normalization and channel delivery hooks.

### Decision 3: Enforce Slack-like ACL parity via source metadata, not parallel policy systems

Discord reminder behavior uses the existing ACL model (channel/sender allow,
audience limits, tool grants) with Discord source metadata and DM channel IDs.
No Discord-only ACL language is introduced.

**Alternatives considered**

- Separate Discord ACL subsystem: rejected due to policy drift and higher
  operator complexity.

### Decision 4: Extend `netclaw init` pipeline with optional Discord setup path

`netclaw init` remains a single pipeline that can collect Discord credentials,
validate required fields, and write baseline Discord ACL entries when enabled.
Validation failures are terminal for chosen Discord mode (fail closed).

**Alternatives considered**

- Separate `netclaw init-discord`: rejected because it fragments onboarding and
  creates inconsistent baseline config outcomes.

## Risks / Trade-offs

- [Risk] Discord DM target canonicalization differs from Slack channel
  canonicalization and could produce invalid persisted addresses.
  -> Mitigation: require transport-specific resolver canonical output and reject
  persistence on unresolved IDs.
- [Risk] Current-session Discord reminder delivery may appear successful if
  gateway ack happens before outbound user-visible delivery.
  -> Mitigation: keep delivery-observed handshake semantics for required
  deliveries and fail execution when observation timeout expires.
- [Risk] Additional init prompts increase wizard complexity.
  -> Mitigation: gate Discord prompts behind an explicit channel-enable choice
  and keep defaults aligned with existing Slack-first setup.

## Migration Plan

1. Add OpenSpec deltas for scheduling, ACL, input adapters, and onboarding.
2. Implement resolver/routing updates for Discord DM reminder delivery.
3. Implement ACL and init-wizard updates with startup validation rules.
4. Add tests for Discord DM reminder success/failure and init pipeline behavior.
5. Rollout with feature disabled by default unless Discord is configured.

Rollback strategy:

- If Discord reminder delivery regresses, disable Discord adapter configuration
  and continue Slack/TUI/SignalR reminder paths unchanged.

## Open Questions

- Should Discord DM target canonical storage prefer user ID, DM channel ID, or
  a composite format for long-lived stability across reconnects?
