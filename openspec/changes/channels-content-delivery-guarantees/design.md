## Context

Three silent file-drop bugs in the Slack channel pipeline in the last month.
The most recent — session `D0AC6CKBK5K/1776260335.865619` on 2026-04-15 —
produced exactly one useful log line: `slack_event_filtered reason=routing_policy_ignore`.
`SlackRoutingPolicy.Evaluate` has **seven** distinct `Ignore`-returning
branches and the single opaque reason string prevented us from identifying
which branch fired. Hours of debugging followed, with no conclusive root
cause from logs alone.

This change closes that diagnostic gap and nothing else. The original
proposal included additional layers (a content-delivery invariant at the
LLM-invoke boundary, an opt-in raw-event capture facility, a fixture
replay harness). All three were dropped during implementation after
repeated explicit "reduce, reduce, reduce" guidance — see `tasks.md` for
the full list of reverts and the rationale for each.

## Goals / Non-Goals

**Goals:**

1. Every `Ignore` return from `SlackRoutingPolicy.Evaluate` carries a
   specific `SlackRoutingIgnoreReason` enum value, logged through the
   existing telemetry sink.
2. Unit tests in `SlackRoutingPolicyTests` assert the exact reason on
   every branch, so adding a new `Ignore` branch without matching test
   coverage fails the build.
3. Regression test coverage exists for realistic Slack DM file-upload
   shapes (`file_share` subtype + files, modern upload with no subtype,
   hidden file_share re-delivery) which the previous suite did not cover.

**Non-Goals:**

- Content-delivery invariant at the session-actor LLM-invoke boundary.
  Drops upstream of the session actor (like the 13:38 incident) are
  outside its reach; drops downstream duplicate existing
  `SlackFileFlowIntegrationTests` pipeline coverage.
- Raw inbound event capture. Seeding fixtures from a running instance
  is not a reusable contributor workflow.
- Fixture corpus or replay harness. SlackNet's public type layout is
  the authoritative schema — no JSON fixtures needed.
- Cross-channel abstraction. Discord will get its own structured reason
  enum when it lands; sharing one now would be premature.

## Decisions

### D1: `SlackRoutingDecision` becomes a record struct, not an enum

**Choice:** Convert `SlackRoutingDecision` from an enum to a
`readonly record struct` with `Kind` and `IgnoreReason`. Factories
`Ignore(reason)`, `StartOrContinue()`, `ContinueOnly()` keep call sites
self-documenting.

**Alternative considered:** Out-parameter on `Evaluate`
(`Evaluate(..., out SlackRoutingIgnoreReason? reason)`). Rejected: uglier
call sites, forces every test to deal with the out param.

**Alternative considered:** Tuple return. Rejected: loses self-documenting
factory names.

### D2: Per-channel reason enum, not a shared cross-channel abstraction

**Choice:** `SlackRoutingIgnoreReason` is Slack-specific. When Discord
lands it will get its own `DiscordRoutingIgnoreReason`.
`ChannelTelemetry.RecordSlackEventFiltered` continues to take a string
reason; we broaden the vocabulary without changing the signature.

**Why:** Each channel's routing rules are shaped by its own transport.
A shared enum would force the lowest common denominator and lose the
diagnostic value of knowing which specific branch fired.

### D3: Telemetry extends existing sink, not a new metric

**Choice:** `ChannelTelemetry.RecordSlackEventFiltered(reason)` is
already there. We pass a richer reason string (e.g.
`routing_policy_ignore:UnsupportedSubtype`) instead of the existing
opaque `routing_policy_ignore` value. Existing dashboards keep working.

**Alternative considered:** Add a new
`ChannelTelemetry.RecordInboundDropped(channel, reason, detail)` method.
Rejected: renames break dashboards and the existing method already
accepts a string reason — no reason to add a parallel surface.

### D4: Test coverage focuses on realistic DM file-upload shapes

**Choice:** Three new regression tests model the exact class of message
that the 13:38 incident was — DM + file_share subtype + image, DM +
files without subtype (modern upload path), DM + hidden file_share (drop
ordering). All three pass against current code, which narrows the
residual 13:38 bug to a specific field on the incoming `MessageEvent`
that the synthetic `SlackInboundMessage` doesn't capture. The next
repro with Group 1's structured `ignoreReason` log will identify the
exact branch.

**Alternative considered:** Full-pipeline integration test constructing
a `SlackNet.Events.MessageEvent` and feeding it through
`SlackChannel.Handle(MessageEvent)`. Rejected for scope reduction:
the existing `SlackFileFlowIntegrationTests` already covers the
pipeline downstream of `Handle`, and the synthetic unit tests above
cover the routing policy directly. If a future incident shows the bug
is in `SlackChannel.Handle`'s mapping layer and not in the policy
itself, that full-pipeline test is cheap to add later.

## Risks / Trade-offs

- **Risk:** The record struct refactor touches every caller in
  `SlackConversationActor` and every test in `SlackRoutingPolicyTests`.
  → **Mitigation:** Scoped to two files in production, one in tests.
  All 164 `Channels`-category tests pass after the refactor.

- **Risk:** The three new DM + file_share regression tests pass against
  current code, so they don't reproduce the 13:38 incident.
  → **Mitigation:** This is the expected outcome given the routing
  policy's logic is provably correct for the synthetic case. The
  residual bug must be in either SlackNet's deserialization of
  `MessageEvent` fields for the real incident payload (e.g., `Hidden`
  getting set when we don't expect it) or in a pipeline site between
  SlackNet and the policy call. Group 1's structured `ignoreReason`
  log is the diagnostic that tells us which on the next repro. At
  that point either the bug is in the policy (a new test case
  captures it) or upstream (a new test case is added at the
  `SlackChannel.Handle` layer).

- **Trade-off:** We accept that this change does not, by itself,
  prove the 13:38 incident is fixed. What it does is make the *next*
  incident instantly diagnosable. Without it, the opaque reason
  string burns debugging time every cycle.

## Failure modes and recovery behavior

- **`SlackRoutingPolicy` new reason enum value added, tests not updated**:
  `SlackRoutingPolicyTests` asserts the exact `IgnoreReason` on every
  branch, so any new `Ignore`-returning branch without matching test
  coverage fails the build. Regression-safe by construction.

- **Record struct default-value semantics**: `default(SlackRoutingDecision)`
  has `Kind = Ignore` and `IgnoreReason = null`, which would be a bug
  if any caller ever accidentally uses the default. No caller in the
  codebase does; all paths go through `Evaluate` which always returns
  a fully-initialized value via a factory method.

## Migration plan

1. Ship as a single PR. No feature flags.
2. Existing `ChannelTelemetry.RecordSlackEventFiltered` continues to
   accept any string reason — dashboards see the richer vocabulary
   immediately but old reason strings keep working for any caller we
   might miss.
3. No config changes. No data migration.

**Rollback:** Single `git revert`. No persisted state changes.

## What was rejected and why

This section exists because the implementation went through multiple
rounds of scope reduction. The record of rejected alternatives is
explicit so future readers understand why the final shape is so narrow.

- **ContentDeliveryInvariant at session-actor LLM-invoke boundary**
  (~130 LOC, fully implemented and reverted): Detect file-count
  conservation between inbound `ChannelInput` and outbound
  `ChatMessage[]`. Catches drops between turn-accept and LLM-dispatch.
  **Rejected** because the 13:38 bug drops upstream of the session
  actor entirely, so the invariant never runs on it. Forward-looking
  regression protection duplicates `SlackFileFlowIntegrationTests`
  coverage of the same pipeline.

- **InboundEventCapture opt-in forensics** (~150 LOC, fully implemented
  and reverted): Write raw payloads to disk for fixture seeding.
  **Rejected** because it required operators to run Netclaw connected
  to live Slack to produce fixtures, which isn't a reusable contributor
  workflow. The owner's explicit objection was "I'm sorry, you want
  people to capture the Slack events from a live instance?"

- **JSON fixture corpus** (~60 LOC, never implemented): Hand-authored or
  captured JSON files replayed through SlackNet's deserializer.
  **Rejected** because SlackNet's public type layout IS the authoritative
  schema — the decompile of `FileShare : MessageEvent` and `MessageEvent`
  in the SlackNet NuGet package gives us every field we need. Fixtures
  add a dispute between captured reality, Slack docs, and SlackNet types
  without paying any rent.

- **New `Netclaw.Channels.TestFixtures` test project**: **Rejected** for
  YAGNI. Promote to a new csproj only when a second channel test project
  shows up and needs the helpers.

- **Cross-channel shared `ChannelDropReason` enum**: **Rejected** as
  premature abstraction. Each channel's routing rules are shaped by its
  own transport; sharing an enum now would force the lowest common
  denominator.

- **Live Slack smoke test against a real workspace**: **Rejected** by
  the owner because it requires deployed software and secrets.

- **Property-based fuzzing of `SlackRoutingPolicy`**: Deferred.
  Nice-to-have, not load-bearing.

## Open questions

None. Scope is final.
