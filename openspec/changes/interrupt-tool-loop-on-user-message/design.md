## Context

Netclaw session actors currently treat every inbound `SendUserMessage` during
`Processing` as buffered work. That is correct while the actor is waiting for a
normal turn to finish, but it is unsafe during an active tool-call continuation:
after a tool result arrives, `LlmSessionActor` drains `_buffer`, appends those
messages to `SessionState.History`, and fires another tool-enabled LLM call for
the same turn.

This creates a prompt shape where the user's correction or interruption is the
latest user-role content, but the actor is still continuing an old tool loop.
Small-model and strict ChatML templates can respond by acknowledging the
correction and requesting another tool, which appears as repeated Slack preambles
plus tool calls.

The session actor already has persistence machinery for abandoned tool batches:
`ToolBatchAbandoned`, `BuildToolBatchAbandonedEvent`,
`ApplyToolBatchAbandoned`, and `ParkedToolBatchHistory.BuildSyntheticAbandonResults`.
The fix should reuse those primitives instead of introducing a parallel
interrupt persistence model.

## Goals / Non-Goals

**Goals:**

- Let a real user message interrupt an active tool-loop continuation.
- Preserve provider-valid history by closing abandoned assistant tool calls with
  synthetic tool results.
- Start the interrupting message as a fresh turn with reset per-turn counters.
- Prevent late callbacks from abandoned LLM/tool work from continuing the old
  turn.
- Preserve existing Slack preamble delivery behavior.

**Non-Goals:**

- Add a user-facing cancel command or UI button.
- Change Slack delivery semantics for `TextOutput` or suppress preamble text.
- Change tool approval policy or trust derivation.
- Change restart-drain semantics; restart drain remains non-interruptible.

## Decisions

### Decision: interrupt instead of mid-loop drain

When `Processing` receives a real `SendUserMessage` while the actor has an
active tool continuation, the actor will treat it as an interrupt. It will not
append the message into the current tool-loop history and will not call
`FireLlmCall()` for the old turn after the interrupt.

Alternative considered: defer `_buffer` until the current turn produces final
text. That avoids the ChatML tail issue, but it prevents users from stopping a
harmful or expensive tool loop mid-turn.

### Decision: reuse `ToolBatchAbandoned`

If an assistant tool-call message is open, the interrupt path will persist a
`ToolBatchAbandoned` event with synthetic tool results for unanswered tool calls.
This keeps history valid for providers that reject assistant tool calls without
matching tool results.

Alternative considered: delete or skip the open assistant tool call. That would
make recovered history diverge from what happened and could corrupt auditability.

### Decision: process the interrupting message as a fresh turn

After abandonment/cleanup, the interrupting message will flow through the same
fresh-turn path as a normal `Ready` message: source binding, trust derivation,
per-turn counter reset, recall reset, and LLM invocation.

Alternative considered: inject a system nudge into the existing turn telling the
model to stop. The production failure mode already shows that adding tail input
inside the active tool turn is the risky shape.

### Decision: guard stale callbacks

Cancellation is best-effort. The actor must also ignore late `LlmResponseReceived`,
`ToolExecutionCompleted`, and batch completion callbacks that belong to abandoned
work. The active call id and active batch tracking remain the boundary for what
can advance the current turn.

Alternative considered: rely only on cancellation tokens. That is insufficient
because shell/tool work can finish after cancellation is requested and still
send actor messages.

## Risks / Trade-offs

- [Risk] A late tool completion could be mistaken for current work and restart
  the abandoned loop. -> Mitigation: clear active batch tracking during
  abandonment and ignore completions that no longer match active expected call
  ids.
- [Risk] Approval recovery behavior could regress if the interrupt path clears
  approval state too broadly. -> Mitigation: reuse `ApplyToolBatchAbandoned`,
  which already clears pending/resolved approval state for abandoned tool
  batches, and add regression coverage.
- [Risk] Multiple user messages can arrive while interruption cleanup is running.
  -> Mitigation: preserve existing buffering semantics after the first interrupt;
  once cleanup completes, drain buffered messages as fresh-turn work, never as
  the old tool continuation.
- [Risk] A restarted actor could recover an interrupted assistant tool-call tail.
  -> Mitigation: persisted `ToolBatchAbandoned` creates synthetic tool results
  before the fresh user turn is processed.

## Migration Plan

No data migration is required. The change is additive behavior for live session
processing. Existing persisted histories remain valid. Future interrupted tool
batches will include synthetic abandon results in the journal.

Rollback strategy: revert the actor behavior and tests. Persisted
`ToolBatchAbandoned` events are already supported by current recovery code, so
rollback does not require journal migration.

## Open Questions

None for MVP. If users later need explicit cancellation without sending a normal
message, that should be a separate command/protocol change.
