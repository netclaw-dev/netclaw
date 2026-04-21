## Context

Netclaw currently treats Slack as the only fully wired interactive channel, while PRD direction already defines channel adapters as a transport-agnostic boundary (`PRD-009`). This change introduces Discord with Slack parity and keeps session actors unchanged: channels normalize ingress into `SendUserMessage`, ACL gates run before dispatch, and adapters consume session broadcasts for delivery.

This scope also adds two UX-sensitive requirements that affect architecture:

- slash commands must work in text-first form (`/name ...`) without mandatory Discord app-command registration in MVP
- tool approvals should use Discord interactions when available, with deterministic text fallback when interactions are unavailable or fail

Stakeholders are owner-operators who run Netclaw in Discord-first environments and maintainers responsible for preserving fail-closed security, deterministic routing, and CI-safe validation.

## Goals / Non-Goals

**Goals:**

1. Add a Discord channel adapter that reaches Slack parity for ingress normalization, ACL-gated dispatch, and thread-aware reply delivery.
2. Preserve transport-agnostic actor boundaries and persistence-safe deterministic session identity for Discord.
3. Guarantee text-first slash command compatibility for Discord without requiring Discord-native app-command registration in MVP.
4. Support interaction-based approval UX with deterministic text fallback for tool approvals.
5. Define offline test strategy so required CI does not need a live Discord instance.
6. Produce planning mockups for Discord approval UX (desktop and mobile) in `docs/ui/`.

**Non-Goals:**

- Mandatory Discord app-command registration for MVP.
- Discord voice/stage events, role administration automation, or moderation-specific features.
- Cross-channel identity/session merging across Slack and Discord.
- Production web UI implementation; only planning artifacts are included.

## Decisions

### D1. Keep Discord slash commands text-first in MVP

**Choice:** Parse `/name ...` from inbound Discord message content via existing slash-command dispatch path, independent of Discord app-command registration.

**Why:** This preserves portability across channels and avoids platform registration friction while still enabling deterministic command invocation.

**Alternative considered:** require Discord app-command registration before slash-command use. Rejected for MVP because it introduces external provisioning dependencies and blocks local/offline validation.

### D2. Use interaction-first approvals with hard fallback to text options

**Choice:** Discord adapter renders `ToolInteractionRequest` as interaction controls when available; otherwise it always emits deterministic text options (A/B/C/D) and parses text replies.

**Why:** This keeps richer UX where possible while preserving reliability in clients/environments where interaction callbacks are unavailable.

**Alternative considered:** interaction-only approvals with timeout fallback. Rejected because timeouts are non-deterministic UX and increase failure ambiguity.

**MVP status:** Interaction rendering (Discord buttons/components) is deferred until the concrete `IDiscordReplyClient` implementation supports message components. The deterministic text fallback path is fully implemented and functional — it serves as the only approval UX in MVP. The inbound interaction response path is wired (via `DiscordGatewayInteraction` and `DiscordApprovalResponse`), so when interaction rendering is added, the response handling is already in place.

### D3. Extend adapter contracts, not session contracts

**Choice:** Add Discord-specific requirements under new `netclaw-discord-socket` capability and extend `netclaw-input-adapters` metadata/routing requirements, while keeping session actor contracts unchanged.

**Why:** Session actors remain transport-agnostic and persistence model remains stable.

**Alternative considered:** Discord-specific session actor flow. Rejected because it duplicates routing logic and risks behavior drift versus Slack.

### D4. CI remains provider/channel independent

**Choice:** Discord-required tests use deterministic fakes and contract harnesses (event fixtures, gateway stubs, approval interaction simulators) and run in standard CI without Discord network calls.

**Why:** Maintains existing CI principle that required suites do not depend on external live systems.

**Alternative considered:** gated live Discord smoke tests in required CI. Rejected due to flakiness and credential/network requirements.

## Risks / Trade-offs

- **Discord thread/reply semantics mismatch Slack** -> define explicit Discord entity-key derivation and add parity tests for thread vs non-thread mapping.
- **Interaction callback outages degrade approval flow** -> deterministic text fallback is always available and must produce equivalent approval decisions.
- **Security regression from new ingress path** -> enforce ACL before dispatch for all Discord events and fail closed on config/startup errors.
- **Text-first slash commands may conflict with normal slash-like content** -> require deterministic dispatch behavior and explicit unrecognized-command errors (no model interpretation fallback).
- **Additional adapter complexity** -> isolate channel-specific behavior in adapter layer and test via offline contract harnesses.

## Migration Plan

1. Add OpenSpec deltas for Discord socket capability and modified adapter/approval/slash/testing capabilities.
2. Add Discord approval UX planning artifact in `docs/ui/` for desktop+mobile.
3. Implement Discord adapter wiring, ACL-gated ingress, and reply delivery parity.
4. Implement text-first slash-command interception compatibility on Discord inbound content.
5. Implement interaction-first approval rendering with deterministic text fallback parsing.
6. Add offline test harness coverage for ingress, slash dispatch, approvals, and fallback paths.
7. Validate through `openspec validate` and required test gates.

Rollback: disable Discord adapter in config and remove runtime wiring; no persistence schema migration is needed because session IDs remain string-based and transport-agnostic.

## Failure modes and recovery behavior

- **Invalid Discord config/startup state:** daemon fails startup with explicit validation diagnostics; operator corrects config and restarts.
- **Discord gateway disconnect:** adapter reconnect strategy resumes ingress; session identity continuity is preserved.
- **Interaction payload failure:** adapter emits deterministic text fallback prompt in-channel; approval flow continues without hanging.
- **ACL denial:** event is rejected pre-dispatch with structured deny reason and audit visibility.
- **Unrecognized slash command:** deterministic command error is returned; message is not forwarded to model interpretation.

## Open Questions

- Should MVP include a configurable preference for forcing text-only approval prompts on Discord even when interactions are available?
