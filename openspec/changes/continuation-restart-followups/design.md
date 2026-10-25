## Context

See `proposal.md` (Why). Terms follow [the engineering glossary](../../../docs/spec/GLOSSARY.md): continuation input,
restart reminder, child consent pause, parked child run, drain park. This change builds on
`durable-child-park-recovery` (PR 2) and `running-child-drain-park` (#2655). Both are on `dev` and not archived.

Current state on `dev` (84b922a06):

1. `RestartRecoveryService.RegisterAsync` drops a reminder whose `ExpiresAt` is past, with one Warning, and reports
   success, so startup deletes the manifest. No session hears of it.
2. Recovery calls `QueueInputsHeldByThePause` while a parked request is open, and the legacy `StartHeldInputs` path
   calls it after a beta.2 request was withdrawn. Both queue every held input, also an automation continuation.
   `PersistReplacingInput` keeps a buffered continuation for an approval notice and a child result notice, so the
   continuation runs after the last answer with no restart reminder.
3. `SessionInputFlow.IsContinuation` and the inline check in `ApplyBufferedBatch` find the continuation by its empty
   content. A real empty person input matches both.
4. `TryResumeParkedRun` calls `TakeParkedAnswers` first. That call removes the answered entries and records them as
   answered. A later `ResumeStart` mismatch or `PrepareResume` failure ends the run, but `WithdrawChildApprovals`
   finds no entry to withdraw, so a late click gets `approval_already_answered`.
5. `ChildEventRejected` for a `Parked` record calls `EndParkedRun` with `DrainParkOf(Accepted)`, which is null, so a
   rejected drain park ends with the consent text "The parked calls did not run."

## Goals / Non-Goals

**Goals:**

- One expiry owner (the daemon) and one closing owner (the session) for a restart reminder.
- One authority for a continuation run: a fresh restart reminder.
- One durable marker read by every continuation rule.

**Non-Goals:**

- A deadline for a child park. Parent expiry and child parks stay separate (running-child-drain-park A3).
- A notice when the session never wakes. The expiry signal wakes it through the reminder route.
- A new command between the daemon and the session.

## Decisions

### D1. The expiry signal is the restart reminder with the expiry instruction

The daemon keeps the expiry comparison. For an expired reminder it registers
`stored with { Instructions = SessionProtocol.RestartExpiredInstruction, FireAt = now + start delay, ExpiresAt > FireAt }`.
The session recognizes the restart reminder ID prefix with either instruction. The resume instruction resumes, and the
expiry instruction closes.

- Why the reminder route: the gateway delivery binds the session output to the channel thread. A notice that the
  session emits with no channel binding reaches nobody.
- Alternative: the session closes an orphaned continuation at its next activation. Rejected: an admission already
  does that today, so the person would still get no notice after a long stop.
- Alternative: a new `CloseExpiredRestartWork` message from the daemon to the session manager. Rejected: no channel
  binding, so the notice reaches nobody, and it is a second route.
- Alternative: the session compares `InputAdmitted.AdmittedAtMs` of the continuation with the lifetime. Rejected as the
  only rule: an input that the drain stopped at its first model call has no durable interruption time.

### D2. What the expiry signal closes

The signal closes the inputs that the resume would take (`ValidateRestartResume`: the leading pending inputs of one
requester that no turn selected), in one `InputClosed` write, and emits the stopped-turn notice in the journal
callback. It closes a person input that the drain stopped at its first model call as well as a continuation. The
signal starts no model call, writes no `InputAdmitted`, and replies `RestartResumeNotNeeded`. With nothing to close it
posts nothing.

### D3. Only a fresh restart reminder queues a continuation

- `QueueInputsHeldByThePause` (both callers) leaves out each continuation input.
- `TryResumeInterruptedInput` during the pause queues the held continuation that it would resume. That keeps the PR 2
  scenario "The pause holds an automation continuation until the last answer".
- `PersistReplacingInput` keeps a continuation for an approval notice or a child result notice only when the queue
  holds it (unchanged rule). With no reminder, the queue does not hold it, so the approval notice after the last
  answer closes it and posts the notice.
- After a fresh reminder, the continuation waits for the last answer with no deadline. The requester who answers is
  present, and the reminder already authorized the resume.

### D4. The marker

`InputAdmitted.ContinuesStoppedTurn` (bool). Proto: `optional bool continues_stopped_turn = 15` on
`InputAdmittedProto`. The writer always writes the field. The reader maps an absent field with the legacy shape rule
(empty content, no media, no executable text, no child notice). `IsContinuation` returns the marker.
`ApplyBufferedBatch` skips a buffered message only when its admitted input is a marked continuation.

Why the existing records cannot express it:

| Field | Its meaning today | Why it cannot be the marker |
| --- | --- | --- |
| `UserMessage.Content` empty | the text of the input | a real empty person input has the same shape (the bug) |
| `ExecutableText` | the slash or executable form | a null value is the common case; a value starts a command |
| `SourceMessageId` | deduplication and the fire key | a value joins deduplication and fire replacement |
| `ChildNotice` | the reducer marks a child run reported | a value changes the reducer |
| `TurnContext` | the recorded authority of the turn | the continuation must carry exactly that authority |
| `UserMessage.Name` | the author name for the model | one word, one meaning |

Presence tracking (`optional`) is the cheapest way to keep old records correct. Without it, a beta.3 continuation and
a new empty person input have the same bytes.

### D5. A refused resume withdraws its answers

The answers count as used only after the `Resumed` record commits. `TryResumeParkedRun` either reads the answers
without closing the entries until the `Resumed` callback, or marks the taken calls as withdrawn when it ends the run.
Each refusal after the read then gives `WithdrawReason.ChildEnded` to a late click. The implementer picks the shape.
The PR 2 crash rule does not change: a crash before `Resumed` keeps the answers in the run record, and recovery
resumes.

### D6. A rejected drain park is a restart loss

`ChildEventRejected` carries whether the rejected park had zero requests (actor-local, no serialization). For a drain
park, `EndParkedRun` takes the restart-loss branch. The rejected park is not in the state, so the run keeps the file
activity of its last checkpoint. The tests check the outcome only.

### Owners and data

| Decision | Owner | Data |
| --- | --- | --- |
| Expired or fresh | `RestartRecoveryService` | the manifest (durable); the clock |
| The expiry signal text | `SessionProtocol.RestartExpiredInstruction` | static |
| Close on the expiry signal | `LlmSessionActor` restart-resume path | durable `InputClosed` |
| Queue a held continuation | `LlmSessionActor.TryResumeInterruptedInput` (reminder) | actor-local queue |
| Leave out a continuation at recovery | `QueueInputsHeldByThePause` | actor-local queue |
| Continuation or ordinary | `SessionInputFlow.IsContinuation` over the marker | durable marker |
| Legacy read | `NetclawProtoMapper.FromProto(InputAdmittedProto)` | durable bytes |
| Answers used | `TryResumeParkedRun`, `SessionToolWorkflow` | actor-local; durable `Resumed` |
| Rejected drain park outcome | `ChildEventRejected` handler | durable `TerminalRecorded` |

### Flows (schematic: they omit the ACL and tool gates, which do not change)

```text
startup
  for each manifest reminder:
    fresh   -> SaveReminderCommand(stored, FireAt = now + delay)
    expired -> SaveReminderCommand(stored with expiry instruction, FireAt = now + delay, ExpiresAt > FireAt)
  delete the manifest after all saves succeed

session on a restart reminder (ID prefix "restart-resume-")
  resume instruction -> existing resume path; during the pause, queue the held continuation, then ack no-op
  expiry instruction -> closes := the inputs that the resume would take
                        if closes: Persist InputClosed(closes); callback: post the stopped-turn notice once
                        reply RestartResumeNotNeeded; no model call; no InputAdmitted

recovery
  queue held inputs, without continuation inputs
last consent answer
  admit the approval notice: PersistReplacingInput closes an unqueued continuation and posts the notice
```

### Boundaries with examples

| Boundary | Positive example | Negative example |
| --- | --- | --- |
| Expiry signal | The daemon stops for 40 minutes. At startup Alice's thread gets the restart notice once, and her stopped turn does not run. | The expiry signal starts a model call, or the session admits the signal text as a reminder fire. |
| Continuation authority | A reminder turn stops after its tool batch while a child park is open. The restart reminder arrives. After the last answer the reminder turn runs once. | The same, with no restart reminder. After the last answer the reminder turn runs. It must close with the notice. |
| Child park | The expiry signal closes the parent continuation, and the consent prompt of the child stays open. | The expiry signal withdraws the consent request or ends the child run. |
| Marker | Alice's empty message (an attachment download failed) reaches the model as one user message. | Alice's empty message is skipped, or a second message closes it and posts a restart notice. |
| Legacy read | A beta.3 continuation in the journal resumes with no user message after the upgrade. | Every legacy text input reads as a continuation. |
| Refused resume | A damaged park ends `park_damaged`, and a late click gets the withdrawal text. | The late click gets "already answered", although no call ran. |
| Rejected drain park | The journal rejects a drain park. The run ends `Partial`, `owner_restarted`, "effects uncertain". | The run ends `Failed` with "The parked calls did not run." |

## Risks / Trade-offs

- [The expiry signal is a reminder that a rollback binary does not know] → A 0.28.0-beta.3 binary delivers it as an
  ordinary reminder fire with the expiry text, and runs one automation turn. The signal fires 250 ms after startup,
  so the window is the startup of the new binary. The release notes name it.
- [The notice can be lost after the close commits] → The notice is transient output after the journal callback. A
  crash between the two loses the notice. The input stays closed, so nothing runs twice.
- [Closing a person input after expiry] → The person loses the queued question, and the notice tells them to send a
  message. Before this change the input stayed pending and never ran.
- [Archive order] → This delta modifies "A fresh restart reminder resumes stored work", which `durable-child-park-recovery`
  also modifies. Archive this change after that change. This delta holds the full PR 2 text.

## Migration Plan

- Upgrade from 0.28.0-beta.3: a pending continuation that beta.3 wrote has no marker. The legacy read rule keeps it a
  continuation. A beta.3 restart manifest with an expired reminder becomes an expiry signal.
- Downgrade to 0.28.0-beta.3: beta.3 ignores field 15 and keeps its shape rule. No journal format break.
- Ship in a prerelease first.
