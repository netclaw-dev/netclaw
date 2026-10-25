## Why

PR #2651 resumes a turn that the restart drain stopped after its tool batch. Its second review left the follow-ups of
issue #2659: a stop longer than the reminder lifetime gives the person no notice, a continuation that the child consent
pause held can run with no restart reminder, and the session finds a continuation by its empty content, so a real empty
person input is lost. The PR 2 (#2670) and #2655 (#2672) reviews found two more wrong outcomes in the same recovery
code: a late click after a refused resume gets "already answered", and a drain park whose park record the journal
rejects ends with consent text.

Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-002 and FR-003 (durable turns and recovery), FR-016 (restart), and FR-017
(durable child delegation). Issue #2659. Base: `dev` at 84b922a06 (PR 1 #2618, PR 2 #2670, #2655 #2672).

## What Changes

- **An expired restart reminder closes the stopped turn with a notice.** Startup no longer drops an expired restart
  reminder. It registers the reminder as an expiry signal through the same `current_session` path. The session closes
  the pending inputs that the reminder would resume, in one journal write, and posts the stopped-turn notice once. No
  model call runs.
- **Only a restart reminder lets a continuation run.** Recovery does not queue a continuation input behind the child
  consent pause. A fresh restart reminder queues it. With no fresh reminder, the next admission closes it with the
  notice, also the approval notice after the last answer.
- **The continuation has an explicit durable marker.** `InputAdmitted` gets one marker for the continuation of a stopped
  turn. Every check that skips or closes a continuation reads the marker. An empty person input is an ordinary input:
  it adds a user message, and no admission closes it as a continuation. A record with no marker keeps the old shape rule.
- **A refused resume does not use its answers.** When the resume of a parked run fails after the session took its
  answers, a late click gets the withdrawal reason and not "already answered".
- **A rejected drain park is a restart loss.** A drain park whose `Parked` record the journal rejects ends `Partial`
  with `owner_restarted` and the restart-loss text, as running-child-drain-park design R6 says.

In scope: the restart reminder of a graceful drain, the continuation input, the child consent pause at recovery, and
the refusal outcomes of a parked child run.

Out of scope: a deadline for a drain park or a consent park (an expired parent continuation never cancels a child
park), a crash with no drain, the `AdoptedSpeakerIds` copy (a consistency nit with no behavior change), and new
restart reminder kinds beyond the expiry signal.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: an expired restart reminder closes the stopped turn with a notice; only a fresh restart reminder
  lets a held continuation run; the continuation has an explicit marker, and an empty person input stays ordinary.
- `background-subagent-runs`: a refused resume withdraws its answers, and a rejected drain park ends as a restart loss.

## Impact

- Code: `RestartRecoveryService` (daemon), `LlmSessionActor` (restart resume, pause release, `KeepStoppedTurnThen`,
  `ApplyBufferedBatch`, `PersistReplacingInput`), `SessionInputFlow.IsContinuation`, `SessionToolWorkflow`
  (`TakeParkedAnswers`), the `Children` partial (`TryResumeParkedRun`, `ChildEventRejected`), `InputAdmitted`, the
  protobuf mapper, and `netclaw_messages.proto` (one optional field on `InputAdmittedProto`).
- Protocol: one public constant, `SessionProtocol.RestartExpiredInstruction`, that the daemon writes and the session reads.
- Docs: `netclaw-operations` skill (restart reminder expiry text), `docs/spec/GLOSSARY.md` if "continuation input" is
  not there.

Security impact:

- An expired continuation never runs. The expiry signal closes work and grants nothing. It starts no model call and
  carries no authority into a turn.
- An approval notice or a child result notice never stands in for the restart reminder.
- A refused resume grants nothing: no parked call runs, and the late-click reply is the true reason.

Operational impact:

- The person gets the stopped-turn notice after a stop longer than ten minutes, at the next daemon start.
- Wire: the format is forward-only. 0.28.0-beta.3 ignores the new optional field 15 and finds the continuation by its
  shape. It cannot read the new `cau-v1` record or the new snapshot field, the same as the park records. A record
  that beta.3 wrote reads with the shape rule.
