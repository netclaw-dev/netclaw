## Context

See proposal.md for the cause. Replay applies `ToolApprovalRequested` through `ApplyToolApprovalRequested`, and that method writes the log line. Child approvals are durable (`PersistApprovalState = true`). Today the session closes them in memory only: `ExpireChildApprovals` (cancel and terminal) and `ExpireRecoveredChildApprovals` (in `RecoverChildren`, after the `RecoverChildRuns` self message). `AllowsSnapshot` needs an empty approval table.

Data ownership:

- `LlmSessionActor`: owns the log decision and the decision to close. It runs the recovery sequence.
- `SessionToolWorkflow._calls`: actor-local approval table. Replay rebuilds it.
- Journal: durable. It holds `ToolApprovalRequested`, `ToolApprovalResolved`, the child run events, and the new `ChildApprovalsClosed`.

## Goals / Non-Goals

**Goals:**

- A recovery log that lists only approvals that stay open.
- A durable close for child approvals, so replay does not re-open them.
- A recovery that reads old journals.

**Non-Goals:**

- Show a prompt again at recovery. The chat client owns that.
- Change the answer path, the authorization rules, or the grant scopes.
- Close resolved parent calls. `ClearCalls` and `TurnRecorded` already do that.

## Decisions

1. **Skip the log while recovering, log open entries once after.** `ApplyToolApprovalRequested` logs only when the phase is not `Recovering`. `RecoveryCompleted` closes child entries first, then logs each open entry with `recovered=True`. The template gets a `recovered={Recovered}` field, and its prefix stays `Tool approval requested`. A tool that counts the prefix still works.
   Alternative: keep the log in replay and filter later. Rejected, because replay cannot know whether a later event closes the entry.
2. **Move the recovery close into `RecoveryCompleted`.** The close then runs before a stashed command. This removes the window between `RecoveryCompleted` and `RecoverChildRuns`, in which a stale child entry passes validation and only the per-incarnation approval channel stops the answer.
3. **Add `ChildApprovalsClosed` as a top-level event, not as a `ChildRunEvent`.** `SessionState.Apply(ChildRunEvent)` throws for an event that does not fit the run lifecycle, and a close event fits no lifecycle step. The new type needs one manifest, `cac-v1`, and one proto message with its own fields. No existing oneof or field number changes.
   Alternative: close from the replay of `CancellationRequested` and `TerminalRecorded`, with no new event. It needs no format change and also fixes old journals. The owner chose an explicit event. The behavior tests do not depend on this choice, except the tests that name the event.
4. **One event for each run, not for each call.** The session closes all entries of a run at once (`ExpireChildApprovals` has the same grain). Replay is idempotent: a missing entry is not an error.
5. **Persist only when an entry existed.** `ExpireChildApprovals` returns whether it removed an entry. A cancel followed by a terminal result writes one event.
6. **Recovery keeps its close step for old journals.** This is not a silent fallback. Old journals are a normal input, and the step is the defined path for them. It uses the same event.

## Risks / Trade-offs

- A persist inside `RecoveryCompleted` -> the implementer may send a self message and persist in its handler. The tests await the journal, not a delay.
- A close event for a run that the journal does not know -> replay ignores the unknown run id, like an absent entry. A malformed event (empty run id) fails loudly in the serializer.
- A new persisted type cannot be removed later -> keep the type small, and give it a reason enum with an explicit zero `UNSPECIFIED` that the reader rejects.
