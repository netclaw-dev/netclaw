# Design: consolidate-binding-actor-engines

## Context

The three channel binding actors each own a private copy of the same orchestration algorithms. A structural diff shows Discord and Mattermost are ~78% line-identical; Slack shares the same algorithms with different file organization. PR #2002 already extracted the small shared helpers (`PendingApprovalRequest<TPromptId>`, `PendingApprovalLookup`, `PendingApprovalRecovery`, `MessageChunker`). PR #2004 fixed one drift bug this duplication caused. This change extracts the four remaining large duplicated regions.

Constraints from the constitution: actor boundaries stay transport-agnostic, security dependencies are required (non-nullable), no silent fallbacks, persisted types stay framework-owned and unchanged.

## Goals / Non-Goals

**Goals:**

- One implementation each for gap hydration, approval-response flow, output-completion bookkeeping, and safe transport calls.
- Zero behavior change, proven by the existing cross-channel contract suite plus new parity tests.
- Per-channel hooks exist only for genuine transport differences.

**Non-Goals:**

- No shared binding-actor base class. The actors keep their own FSM, persistence handlers, and receive wiring.
- No change to persisted events (`CursorAdvanced`, `PendingApprovalPromptTracked/Cleared`).
- No new channel features (Mattermost processing indicator stays a separate product decision).
- No change to the generic approval API (issue #1944).

## Decisions

### D1: Engines are plain classes, not actors and not a base class

Each engine is a non-actor class in `Netclaw.Channels`, constructed by the binding actor with required dependencies (history fetcher, injection classifier, turn-enqueue callback, logger adapter). Actors call engine methods from inside their existing `CommandAsync` handlers.

Rationale: a base actor class couples lifecycle, persistence, and supervision across transports and violates the transport-agnostic boundary rule. Plain classes keep the actors' Akka semantics untouched and make the algorithms unit-testable without TestKit. Alternative considered: template-method base actor — rejected for the coupling above and because Akka.NET receive registration in a base class hides message wiring from the concrete actor.

### D2: Cursor comparison is an injected comparator; Discord uses length-then-ordinal

The persisted `CursorAdvanced.Cursor` is already a `string` for every channel. Discord's in-memory `ulong` round-trip is the main textual difference blocking hydration consolidation. The engine stores cursors as strings and compares with an injected `IComparer<string>`:

- Mattermost and Slack: ordinal comparison (current behavior).
- Discord: length-then-ordinal comparison. Plain ordinal is WRONG for snowflakes across digit-length boundaries (`"999..."` 18 digits vs `"1000..."` 19 digits), so Discord SHALL NOT use plain ordinal. Length-then-ordinal equals numeric order for all non-negative integer strings without leading zeros.

A unit test SHALL prove the Discord comparator matches `ulong` comparison, and MUST include cross-digit-length pairs and boundary values (`ulong.MaxValue`, adjacent powers of ten). Alternative considered: keep `ulong` inside Discord and make the engine generic over the cursor type — rejected because it preserves the asymmetry the consolidation exists to remove.

### D3: Approval flow is a shared class with two hooks

`ApprovalResponseFlow` owns text-approval parsing, cold-spawn forwarding, and prompt resolution (via `PendingApprovalLookup` from PR #2002). Hooks:

- `RenderResolvedPromptAsync(...)` — required; each channel redraws its own prompt message.
- `RespondSynchronously(replyTo, ack)` — optional hook used only by Mattermost, whose interactive-message webhook requires a synchronous HTTP reply. Discord (gateway events) and Slack do not register it.

The requester identity check stays inside the shared flow so it cannot drift per channel.

### D4: Output handling is a shared engine with a channel-output hook

The shared engine owns the `TurnCompleted` bookkeeping (cursor advance, turn-in-flight flag, reminder observer settlement, empty-turn fallback, prompt clearing) and delegates unrecognized or channel-specific outputs (`SessionTitleOutput`, `ProcessingStateOutput`) to a `HandleChannelSpecificOutput` hook. A channel that does not support an output type ignores it in its hook; this is a capability difference, not a silent fallback.

Persistence stays in the actor: the engine returns the events to persist (e.g., cleared prompts); the actor calls `Persist`/`PersistAll` and applies via the PR #2002 recovery helpers. The engine never touches Akka persistence.

### D5: Failure modes keep PR #2004 semantics

Engines do not catch-and-swallow. An exception from an engine method escapes the actor's `CommandAsync` handler, supervision restarts the actor, and recovery re-creates the pipeline. The `Feedback_send_failure_faults_the_actor` contract test pins this. The safe transport-call skeleton records telemetry and calls the delivery-failure notifier exactly as each channel does today.

### D6: Extraction lands in four reviewable steps

Order: (1) Discord cursor stringization + comparator test, (2) gap-hydration engine, (3) approval-response flow, (4) output template + safe-call skeleton. Each step compiles, passes the full `Netclaw.Actors.Tests` suite, and is a separate commit. If a step uncovers a real behavioral difference between channels, the step stops and the difference is surfaced for a decision instead of being silently normalized.

## Risks / Trade-offs

- [Discord cursor ordering breaks on short synthetic IDs in tests] → the comparator test covers cross-digit-length pairs; test fixtures with short numeric IDs order correctly under length-then-ordinal.
- [Hydration consolidation changes turn-enqueue timing] → the engine is a mechanical transplant; the contract suite's hydration tests (fetch-once, stash-during-hydration, restart-re-runs) run per channel and must stay green at every step.
- [Approval flow consolidation weakens a per-channel security check] → the requester check already has one shared implementation (`PendingApprovalLookup`, PR #2002); this change only moves its callers. New parity tests assert wrong-requester rejection per channel.
- [Hidden per-channel differences get normalized away] → D6 stop rule: any discovered difference halts that step and is reported, mirroring how the PR #2004 drift was handled.
- [Larger blast radius for a single engine bug] → trade-off accepted: one visible bug beats three drifting copies; the cross-channel contract suite runs every scenario against all three channels.

## Migration Plan

No deployment migration: no config, persistence, or wire change. Rollback is a revert of the stacked PR. The four commits in D6 allow partial revert per engine.

## Open Questions

- Mattermost lacks the processing-indicator output handling Slack and Discord have. Feature gap or intentional? Needs a product decision; out of scope here (hook default: ignore).
- Slack's pending-approval lookup shape differs slightly (`FindIndex`, no call-id-first branch). If step 3 confirms a real semantic difference, Slack keeps its lookup and only shares the outer flow; the difference gets documented in the parity spec.
