## Context

See `proposal.md` for the reason. Use the [engineering glossary](../../../docs/spec/GLOSSARY.md#requester) for "Requester".

The session actor (`LlmSessionActor`) owns the input buffer and the turn
authority. `SessionInputFlow` decides which buffered inputs form the next batch.
`SessionToolWorkflow` (recovery part) decides what a new input does to a parked
approval. The journal holds each accepted input as `InputAdmitted` with its
`TurnContext`.

## Goals / Non-Goals

**Goals:**

- One requester per turn. No input runs under an authority it did not carry.
- Automated input never closes an approval that a person has not answered.
- Waiting input is not lost and not run twice.

**Non-Goals:**

- Reminder admission, dedup, and the delivery backstop (D2).
- Child result delivery (S2).
- A journal-only rebuild that removes `AdmittedAuthority` from `SendUserMessage`.
  It needs a change to protected tests.
- A hard stop while a tool runs: its pending inputs are not rebuilt (same as `dev`).
- The Expired close that can start a turn over an unanswered `tool_use`
  (unreachable in normal flow; follow-up).

## Decisions

### Decision owners and data scope

| Decision | Owner | Data |
| --- | --- | --- |
| Which buffered inputs form a batch | `SessionInputFlow` (`DrainBufferedBatch`) | actor-local buffer; each entry holds its recorded authority |
| Same requester | `TurnContext.HasSameRequester` | call-local |
| Same full authority for a restart resume | `TurnContext.HasSameAuthority` | call-local |
| New input vs parked approval | `PlanNewInputAbandonment` | call-local plan over durable approval state |
| Rebuild of waiting input | `LlmSessionActor.RestoreWaitingInputs` at `RecoveryCompleted` | durable (`InputAdmitted` minus closed inputs) |
| Deferred idle passivation | `SessionExecutionLifetime` flag, claimed by the actor | actor-local, one flag |

### D1. Group by requester, not by full authority

The batch rule uses `HasSameRequester` (session, audience, trust boundary,
channel type, sender, principal). Turn ID, provenance, and delivery targets
differ for each message and would split a batch of one person. The restart
resume keeps `HasSameAuthority`, which is stricter. Alternative: compare the
full authority for batching. Rejected: it splits turns of one person.

### D2. Each waiting input keeps its recorded authority

A joined input never runs under the authority of another input. A new turn
adopts the recorded authority of its first input, and the actor derives trust,
crash context, and prompt audience from it. `SendUserMessage.AdmittedAuthority`
carries the recorded authority across an actor restart. Alternative: read the
authority from the journal at drain time. Rejected for D1: the drain
signatures are pinned by protected tests.

### D3. Automated input waits; a person abandons

`PlanNewInputAbandonment` returns `Activate`, `AbandonThenActivate`, or
`WaitForApproval`. Automated principals (`VerifiedAutomation`, `SystemProcess`)
get `WaitForApproval` while an approval is open. A null source counts as a
person, so the old abandonment holds. Alternative: let a reminder expire the
approval. Rejected: it cancels work that a person has not answered.

### D4. Rebuild waiting input from the journal only when an approval is parked

A parked batch closes the inputs of its own turn. Pending inputs at recovery
with a parked approval are therefore the inputs that arrived after the
approval. The rebuild adds no store. Without a parked approval, pending inputs
belong to the restart resume, so the rebuild stays off. Alternative: rebuild on
every recovery. Rejected: recovery cannot tell a waiting input from an input
that a restart resume owns or that a turn already emitted.

### D5. Idle abandonment runs the waiting input first

The idle handler is the only place that destroys the parked state while input
waits. It closes the batch, runs the waiting input, and defers passivation with
a single flag. `CompleteDeferredIdlePassivation` claims the flag when a turn
returns to `Ready` with an empty buffer. It uses the normal `HasLiveBackgroundJobs`
check, so a job that the waiting turn starts is not killed.

### D6. An actor restart collapses duplicate copies

`ActivateInput` removes a queued entry with the same `AdmittedInputId`. A
rebuilt copy and a requeued copy of one input then reach the model once.

### Ordered flow (schematic: omits the ACL and consent checks)

```text
new input arrives
  admit -> journal InputAdmitted -> ack
  approval open?
    no  -> turn running? same requester ? join at tool boundary : buffer
    yes -> automated ? buffer (wait) : abandon approval, then run
turn ends
  drain: take leading inputs with one requester -> start turn under their recorded authority
  rest stay buffered, in order
idle timeout with a half-answered batch
  abandon batch -> input waiting ? run it, passivate when work ends (no live job) : passivate
recovery with a parked approval
  rebuild buffer from pending InputAdmitted (recorded authority, reminder id) -> wait
```

### Examples

Positive: Alice's approval is open, a reminder fires, and Alice approves. The
batch finishes, then the reminder runs once.

Negative: Alice's approval is open, a reminder fires, and the reminder
abandons the approval. This must not happen.

Positive: Bob's Public input waits behind Alice's turn. It runs later with
Bob's tool scope.

Negative: Bob's input joins Alice's turn and runs with Alice's Personal tools.
This must not happen.

## Risks / Trade-offs

- A reminder can wait a long time behind an unanswered approval. The 1-hour
  `DeliveryRequired` backstop may report it late or as a failure. → D2 removes the wait.
- The Expired branch can start a waiting turn while a `tool_use` has no result.
  The state is not reachable in normal flow, and a failed turn heals on the next input. → Follow-up.
- A delivery retry in `Ready` with a parked approval can drain a waiting input.
  This edge exists on `dev`. → Follow-up.
- A restart resume refused mixed authority (on `dev` at that time). #2515 changed the resume: it takes the leading inputs of the first requester and queues the other inputs. → Done in #2515.
- The restart-drain branch of the idle path leaves the deferral flag set in a stopping actor. → Harmless.
- `AdmittedAuthority` duplicates journal data. → Remove it in a follow-up that may edit the protected tests.

## Migration Plan

No schema, config, or wire change. Rollback is a code revert. A journal that
holds pending inputs stays valid.

## Open Questions

- PRD-001 FR-017 exists only on the `feature/coordination-cooperation-v2`
  stack. Its last sentence must change when the stack rebases onto this
  change (see `tasks.md` 5.1 for the text).
- Whether the delivery backstop must change for a long wait: D2 decides.
