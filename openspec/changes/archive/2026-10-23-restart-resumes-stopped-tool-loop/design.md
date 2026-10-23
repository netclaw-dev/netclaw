## Context

See proposal.md - Why. A tool batch consumes the turn input. A snapshot after the batch then deletes the `InputAdmitted` record from the journal.

## Goals / Non-Goals

**Goals:**
- Resume the stopped turn through the existing pending-input path, with no new durable shape.

**Non-Goals:**
- A snapshot field, a new event type, or a wait for the snapshot store. A new event type breaks recovery on a rollback, because an older binary cannot read an unknown manifest.

## Decisions

| Decision | Owner | Data |
|---|---|---|
| The stopped turn is kept | `SessionExecutionLifetime.ContinuesStoppedTurn` (pure) | call-local facts from the actor |
| Continuation input | `LlmSessionActor.KeepStoppedTurnThen` | durable (`InputAdmitted`) |
| Reminder authority and resume | existing `ValidateRestartResume` and `TryResumeInterruptedInput` | durable read |

Flow (schematic):

```
drain stops turn T after its tool batch
  if ContinuesStoppedTurn(no text, no pending input, no replay, no live job, all results recorded, channel type):
      Persist(InputAdmitted { content = "", TurnContext = T.authority })   # before the drain acknowledges
  eligibility and notice as before; one pending input of T means no notice
after restart: the reminder resumes the pending input; no empty user message enters the history
```

- The journal write completes before the drain goes on, so the acknowledgement follows a durable record. A pending input also blocks the final snapshot, so the journal keeps the record.
- An input without text adds no user message to the model history, in the resume path and in the buffered path.

## Risks / Trade-offs

- [Rollback to an older binary] → The older binary resumes the continuation input as an ordinary input. It adds a user message with empty content to the history. See the PR for the provider effect.
- [Persist rejection at the drain] → The callback does not run, the session does not acknowledge, and the shutdown alert names it.
