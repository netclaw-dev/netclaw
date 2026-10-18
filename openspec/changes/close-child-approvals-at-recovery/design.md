## Context

See proposal.md for the cause. Replay applies `ToolApprovalRequested` through `ApplyToolApprovalRequested`, and that method writes the log line. Child approvals are durable (`PersistApprovalState = true`). Today the session closes them in memory only: `ExpireChildApprovals` (the live cancel and terminal callbacks) and `ExpireRecoveredChildApprovals` (in `RecoverChildren`, after the `RecoverChildRuns` self message). `Recover<ChildRunEvent>` applies the record to the session state and does not touch the approval table. `AllowsSnapshot` needs an empty approval table.

Data ownership:

- `LlmSessionActor`: owns the log decision and the decision to close. It runs the recovery sequence.
- `SessionToolWorkflow._calls`: actor-local approval table. Replay rebuilds it.
- Journal: durable. It already holds `ToolApprovalRequested`, `ToolApprovalResolved`, and the child run records. Nothing is added.

## Goals / Non-Goals

**Goals:**

- A recovery log that lists only approvals that stay open.
- Replay that closes child approvals from the records that the journal already holds, so a journal of any age rebuilds a closed table.
- No persisted format change.

**Non-Goals:**

- Show a prompt again at recovery. The chat client owns that.
- Change the answer path, the authorization rules, or the grant scopes.
- Close resolved parent calls. `ClearCalls` and `TurnRecorded` already do that.
- Add a journal event, a proto message, or a manifest. See decision 3.

## Decisions

1. **Skip the log while recovering, log open entries once after.** `ApplyToolApprovalRequested` logs only when the phase is not `Recovering`. `RecoveryCompleted` closes child entries first, then logs each open entry with `recovered=True`. The template gets a `recovered={Recovered}` field, and its prefix stays `Tool approval requested`. A tool that counts the prefix still works.
   Alternative: keep the log in replay and filter later. Rejected, because replay cannot know whether a later record closes the entry.
2. **One close rule, shared by live code and replay.** `SessionToolWorkflow.CloseChildApprovals(ChildRunEvent)` closes every entry of the run for a `CancellationRequested` or a `TerminalRecorded` record, and nothing for other records. The live Persist callbacks call it in place of `ExpireChildApprovals(runId)`. `Recover<ChildRunEvent>` calls it after it applies the record to the state. One function means that live code and replay cannot drift.
3. **No new journal event (repo rule: reuse before you add).** A first draft added `ChildApprovalsClosed`. The records `CancellationRequested` and `TerminalRecorded` already say that a run no longer waits for an answer. Recovery also writes a `TerminalRecorded` with `Lost: true` for a run that was live at the restart. A new event would duplicate this fact, add a persisted type, a proto message, and a manifest for ever, and it would not repair a journal that was written before it. Replay of the existing records repairs old journals.
4. **Close what remains in `RecoveryCompleted`.** A run that was live at the restart has no terminal record yet. Its owner is dead, so the session closes every remaining child entry in `RecoveryCompleted`, before a stashed command runs (`ExpireRecoveredChildApprovals`, moved from `RecoverChildren`). This removes the window in which a stale entry passes validation and only the per-incarnation approval channel stops the answer. Recovery then records the lost terminal, and the next replay closes the entry by decision 2.
5. **Resolved child entries also close.** The rule removes pending and resolved entries, as `ExpireChildApprovals` does today. A resolved child entry would otherwise keep the table non-empty.

## Risks / Trade-offs

- A child entry that never gets a cancel or terminal record in the journal -> `RecoveryCompleted` closes it. The table is then correct at the end of recovery, though replay holds it open for a while. No log line is written during replay, so the open entry does no harm.
- The rule must ignore a checkpoint -> a unit test and a mutation target pin this.
- Moving the close into `RecoveryCompleted` changes the order of recovery steps -> the late-answer tests pin the order.
