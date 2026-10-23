## Context

See proposal.md - Why. A tool batch consumes the turn input (`ToolBatchStarted.ConsumedInputIds`). A snapshot after a tool batch then deletes the `InputAdmitted` record from the journal. After the restart, no durable record holds the authority of a turn that stopped after its tool batch.

## Goals / Non-Goals

**Goals:**
- Resume a turn that the drain stopped after its tool batch, under its own recorded authority.
- Keep one decision for the reminder and the notice, so a turn that cannot resume always gets the notice.

**Non-Goals:**
- Resume a turn whose tool still runs at the coordinator deadline.
- Add a journal event type. An old binary cannot read an unknown manifest, so a rollback would break recovery.

## Decisions

Owners and data:

| Decision | Owner | Data |
|---|---|---|
| The stopped turn can resume | `LlmSessionActor` (`ResumableStoppedTurn`) and `SessionExecutionLifetime.MarkRestartReminderEligible` | actor-local |
| Reminder authority, and the notice | `SessionExecutionLifetime` (`RecordedRestartContext`) | actor-local |
| Authority of the stopped turn | `SessionState.RestartStoppedTurn` | durable (final snapshot, field 22) |
| Resume after the restart | `LlmSessionActor.TryResumeInterruptedInput` | durable read, then cleared |

Flow (schematic):

```
drain stops turn T (model stop, tool batch end, or compaction end)
  if no text reached the person, no input is pending, and the snapshot can save:
      record = T.authority.ToRecord()
  eligibility = Mark(pending, children, record)       # ResumesStoppedTurn when record builds a reminder
  notice posts unless eligibility resumes T
  state.RestartStoppedTurn = record when eligibility == ResumesStoppedTurn
  passivate -> save final snapshot -> wait for SaveSnapshotSuccess -> ack with reminder
              (SaveSnapshotFailure -> notice, no turn reminder, ack)

after restart: recover from snapshot (RestartStoppedTurn set)
restart reminder arrives in Ready, no pending input:
  clear RestartStoppedTurn; restore T authority; next model call over history; CommandAck
```

- The snapshot carries the authority, and not a journal event. The final snapshot already deletes earlier events, so an event alone does not survive it. A new snapshot field is safe for a rollback, because an old binary ignores an unknown field.
- The drain waits for the snapshot store. Without the wait, a shutdown can lose the write, and the reminder then resumes nothing while the person got no notice.
- The eligibility uses the same check as the snapshot (`CanSaveSnapshot`). That check needs no pending input and a result for each tool call, so the snapshot always saves when the turn is eligible.
- An overflow replay took the user message out of the history. When that turn resumes, the session puts the message back before the final snapshot, so the resumed model call sees the question.
- The resumed turn runs no memory recall, as each model call after a tool batch does.

## Risks / Trade-offs

- [The final snapshot is the only durable copy] → The drain waits for the store, and a failure posts the notice.
- [An older snapshot at the same sequence number confirms first] → The window is a snapshot write that is still open when the drain starts. The final write still follows.
- [Rollback to an older binary] → The old binary ignores field 22. The stopped turn does not resume, and the person gets no notice for it.
- [A background job started by the turn] → Passivation reaps it. The resumed model call sees the job as `reaped` in `[active-background-jobs]`, except for a Public audience, where that block is not shown.
