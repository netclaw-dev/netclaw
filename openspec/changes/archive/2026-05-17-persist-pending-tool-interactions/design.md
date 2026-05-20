## Context

`LlmSessionActor` parks a turn when a tool call needs human approval: it records
the call in an in-memory `Dictionary<string, PendingToolInteraction>`
(`_pendingToolInteractions`), emits a `ToolInteractionRequest` to the channel,
and the tool-loop pipeline task blocks on an in-memory `ApprovalChannel` TCS.
The user's Slack/Discord click returns as a `ToolInteractionResponse`.

Two gaps break recovery:

1. `_pendingToolInteractions` is **not** persisted — `BuildSnapshot()` returns
   `_state.ToSnapshot()` only. A cold-respawned session recovers into `Ready`
   with an empty pending set.
2. `CommandAsync<ToolInteractionResponse>` is handled **only** in the
   `Processing` behavior. `Ready`, `Passivating`, and `Compacting` have no
   handler, so a response arriving there is unhandled and dead-lettered.

The passivation-deferral guard (defer while `_pendingToolInteractions` is
non-empty) only sees the in-memory set, which `FailCurrentTurn` clears and
recovery never restores. The channel/gateway layer already forwards the
approval to the session correctly; the session-actor side is the unfixed half.

Constraint: persistence types are framework-owned and serialization-safe
(protobuf via `NetclawProtoMapper`); actor boundaries stay transport-agnostic;
no silent fallbacks.

## Goals / Non-Goals

**Goals:**

- A pending tool-approval interaction survives idle passivation, turn failure,
  and actor restart by being persisted in the session snapshot.
- An approval response is honored in every session phase: re-driven from
  `Ready`, aborts-then-re-drives from `Passivating`, buffered-then-replayed from
  `Compacting`.
- A cold-recovered session re-drives the parked tool batch and continues the
  turn when the approval arrives.
- An approval response for a genuinely unknown/expired call fails loud with a
  user-visible channel message.
- Backward compatible: pre-change snapshots recover with an empty pending set
  (the pre-change behavior).

**Non-Goals:**

- Proactively expiring stale Slack/Discord prompt messages on turn failure
  (separate follow-up — touches the channel output protocol and every adapter).
- Persisting partial results of sibling tool calls that finished before the
  approval pause — re-drive replays the whole batch.
- Changing the approval decision logic, ACL, or grant-persistence rules.

## Decisions

**D1 — Persist pending interactions on `SessionSnapshot`, not `SessionState`.**
`_pendingToolInteractions` is actor-transient state, kept deliberately separate
from immutable `SessionState`. Keep that separation: add a
`PendingToolInteractions` list to `SessionSnapshot` and layer it in via
`BuildSnapshot()`, exactly as `EligibleDeliveryTurnNumber` is layered today.
The runtime `PendingToolInteraction` record stays private; a public nested
`SessionSnapshot.PendingToolInteractionRecord` (mirroring
`AdoptedContextSnapshotRecord`) is the persisted form, with the dictionary key
promoted to an explicit `CallId` field. Alternative — moving the pending set
into `SessionState` — was rejected: it would entangle approval bookkeeping with
the immutable conversation model and the journal-event apply path.

**D2 — Append-only proto evolution.** Add `PendingToolInteractionProto` and a
new `repeated` field to `SessionSnapshotProto`. New field number, no reuse →
old snapshots deserialize with an empty list (proto3 default), which is the
exact pre-change behavior. No migration step, no data rewrite.

**D3 — Re-drive the whole tool batch, not a single call.** The tool pipeline
runs all batched calls under one `Task.WhenAll` and writes results to history
only on `ToolExecutionCompleted`. While an approval is pending, history's tail
is the assistant message with the unanswered `tool_use` block and there are no
tool-result messages for that batch — a consistent state to replay. Sibling
results that completed in memory are lost with the actor and cannot be
salvaged, so single-call resume is impossible. The re-drive reconstructs
`FunctionCallContent`s from the persisted assistant message (tool arguments are
already durable in `SerializableToolCall.ArgumentsJson`) and re-dispatches
through the same path `HandleToolCallResponse` uses, factored into a shared
`DispatchToolBatch`.

**D4 — Per-phase response handling.**
- `Ready`: new `CommandAsync<ToolInteractionResponse>` → validate against the
  restored pending set, run the same `CanApprove` requester check and grant
  persistence as `Processing`, then re-drive (`Ready → Processing`, already a
  legal transition).
- `Passivating`: abort passivation (`AbortPassivationTimers`) → `Ready` →
  handle — structurally identical to the existing abort-on-`SendUserMessage`
  handler. This composes with the post-snapshot grace window: a response in the
  100 ms `PassivationFinalStop` window cancels the stop; a response after
  `Context.Stop` is handled by the cold-respawn + restore path.
- `Compacting`: buffer in a transient field, replay via `Self.Tell` after
  compaction completes. Re-driving mid-compaction is unsafe because compaction
  rewrites history. Dropping it would be a silent fallback (rejected).

**D5 — Re-drive at the parked turn's audience; persist nothing new.**
`MessageSource` / `_currentTurnSource` is transient and null after cold
recovery, so `BuildToolExecutionContext` would fall closed to
`TrustAudience.Public`. The re-driven call must instead run under the audience
its persisted approval grant was recorded against, or the gate would re-prompt.
The audience is *not* derivable from a Slack session id (the id prefix is the
opaque Slack channel id, not a channel-type string — `ResolveAudienceFromSessionId`
returns the fail-closed `Public`), so it must be persisted — but `Audience` was
already a field on the pending interaction (the grant is keyed by it). So the
re-drive passes the persisted `pending.Audience` to the pipeline as an explicit
`audienceOverride` on `BuildToolExecutionContext`; **no new persisted state, no
synthesized `MessageSource`.** `Boundary`/`ChannelType` are left at their
existing null-source defaults — the re-drive's correctness hinges only on
`Audience` + the grant. The one consequence: a re-driven background job (rare —
an approval-gated `shell_execute` with `Background:true`) hits the existing
null-source guard and fails loud, which is acceptable (constitution: fail loud).

**D6 — `ApprovedOnce` re-drive via context pre-seed.** For
session/always/everywhere scopes the persisted grant lets the re-driven call
pass the gate naturally. For `ApprovedOnce` there is no grant; pre-seed
`ToolExecutionContext.OneTimeApprovedToolName` + patterns for the specific call
so the re-driven batch skips the gate exactly once and emits no duplicate
prompt. The scope-widening shortcut (persist `ApprovedOnce` as a session grant)
was rejected on security-posture grounds.

**D7 — Snapshot the moment an approval is created.** Optionally
`SaveSnapshot(BuildSnapshot())` inside `Command<ToolInteractionRequest>` so a
pending approval is durable immediately, closing the window between request and
the next periodic snapshot. Approvals are human-paced, so the extra write is
negligible.

## Risks / Trade-offs

- [Whole-batch re-drive re-runs already-completed sibling tools] → Harmless for
  idempotent tools (search, file read). The LLM typically isolates an
  approval-gated `shell_execute` in its own batch. Documented; persisting
  partial sibling results is a deliberate non-goal.
- [Pre-snapshot crash window: approval requested, session crashes before any
  snapshot] → D7 (snapshot on request) closes it. If the crash still precedes
  the write, recovery has no pending entry and the response hits the fail-loud
  expired-prompt path — degraded but safe, never a silent drop.
- [Snapshot superseded by journal replay] → After `SnapshotOffer`,
  `TurnRecorded` / `SessionCompacted` events replay; either means no batch is
  mid-flight, so the recovery handlers for those events clear the restored
  pending set. Same "snapshot then journal supersedes" discipline already used
  for `ProcessedReminderIds`.
- [Compaction summarizes the tail assistant tool_use message] → Low
  probability — `KeepRecentMessages` preserves the recent tail. If it happens,
  the post-compaction replay finds no batch and falls to the fail-loud path.
- [Re-drive transitions `Ready → Processing` across an `await`] → Akka stashes
  messages during a `CommandAsync` continuation, so no interleaving; phase is
  still `Ready` when `TransitionTo(Processing)` runs.

## Migration Plan

No migration step. The proto change is append-only; existing snapshots and
journals are read unchanged and recover with an empty `PendingToolInteractions`
list (identical to current behavior). Rollback is a plain code revert — a
snapshot written by the new code is still readable by old code (the unknown
proto field is ignored), so a downgrade loses only the new durability guarantee,
not session integrity.

## Open Questions

- Whether `PrincipalClassification` already has a proto enum to reuse, or the
  record stores it as `optional int32` — resolve when editing the proto file.
- Whether to also replace the existing silent `return` for unknown calls in the
  `Processing` handler with the same expired-prompt message for consistency
  (low-risk, optional).
