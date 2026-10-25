## Context

See proposal.md for the motivation. The current code has these facts:

- `SessionInputFlow.AdvanceAfterToolBatch` returns `ToolBatchNextAction.EndTurn` when `PersonWaitsBehindAutomation`
  is true. It returns before `ChooseNext(compactionDue)`.
- `LlmSessionActor.ContinueAfterToolBatch` handles `EndTurn` with `CloseActiveInputs(FinishTurnWithoutReply)`.
  `FinishTurnWithoutReply` also serves failed turns. It drains the buffer at once and has no compaction check.
- `SessionInputFlow.AdvanceAfterCompaction` gives restart priority, then resumes the tool loop. It has no person check.
- The per-turn context block (`SessionMessageAssembler.BuildVolatileContextBlock`) already carries a one-turn fact,
  the restart notice (`_turnRestartNotice`). `SessionState.AddVolatileContextNudge` puts the block before the last
  real user message.

## Goals / Non-Goals

**Goals:**

- Use the existing stop path, the existing compaction path, and the existing per-turn context path.
- Keep one stop decision owner (`SessionInputFlow`) for both safe boundaries.

**Non-Goals:**

- No person check during a model call or during an overflow replay.
- No compaction check in `FinishTurnWithoutReply` for failed turns.
- No new journal event, no protobuf change, and no durable note. A restart drops a pending note. The restart path
  posts its own notice.
- No round limit (#2656).

## Decisions

### D1: The compaction end returns `InputProgressAction.EndTurn`

`AdvanceAfterCompaction` returns `CompactionContinuation(EndTurn, ResumeToolLoop: false, Batch: null, ...)` when the
compaction resumes a tool loop, the current owner is automation, and `PersonWaitsBehindAutomation` is true. The restart
check stays first. The flow resets the tool-loop flag. `AdvanceAfterTurn` never returns `EndTurn`.

The actor handles `EndTurn` as it handles `ToolBatchNextAction.EndTurn`: it clears the tool batch tracking, closes the
selected inputs, and completes the turn quietly. The deferred approval replay stays as it is.

Alternative: a separate bool `EndsTurn` on `CompactionContinuation`. Rejected: the action already names the next step,
and a bool can contradict `FollowUp`.

Alternative: run the check for an overflow replay too. Rejected: the replay is the turn's own message with no recorded
authority. A stop would leave it in the buffer with no owner.

### D2: The end point compacts after the close

At `ToolBatchNextAction.EndTurn`, the actor closes the selected inputs and completes the turn quietly. Then it checks
`ShouldCompact()`. When a compaction is due, the actor enters `Compacting` instead of the drain. The compaction end then
drains through `AdvanceAfterCompaction` with no tool-loop flag, so the person's input adopts its own authority. When no
compaction is due, the actor drains at once, as today.

The flow keeps its `EndTurn` decision. The actor owns the compaction check, as it does after a recorded turn.

Alternative: a new `ToolBatchNextAction.EndTurnThenCompact`. Possible, but the flow already ignores `compactionDue` for
the stop, and the actor already owns the turn-end compaction check.

### D3: The note uses the per-turn context path

The actor keeps one actor-local pending note, as it keeps `_pendingRestartNotice`. A stop at either boundary sets it.
The first model call of the next turn whose requester is a person passes it into the per-turn context block. The
actor then clears it. The block sits in history before the person's message, so later calls of the turn keep one copy
and add none. A compaction does not clear the pending note, because the note goes into history only after the
compaction.

The note text is fixed (see the spec). It names no kind. The source kind of a webhook comes from the webhook sender,
so a kind label from the source kind would put sender text into a system context message. A trusted kind label needs a
mapping from several provenance facts for little gain, because the stopped turn's prompt is in the history above.

Alternative: `AddSystemNudge` at the end of history. Rejected: a trailing system message after the person's text reads
as a new user turn to strict chat templates (see `AddVolatileContextNudge`).

### D4: The compaction spec follows the code

The pipeline never had a threshold check after tool-result clearance. The trigger uses the input token count that the
provider reports. The pipeline has only a character estimate, and the estimate leaves out the tool schemas. A skip on
that estimate could keep the history over the threshold. Then each turn end starts a new compaction that reduces
nothing. A clearance that lands just under the threshold also starts a new compaction at the next turn. So the spec
changes, and the code stays.

## Flow

```text
schematic: tool-batch end (omits the ACL and provider steps)
  restart requested -> StopTurnForRestart
  flow.AdvanceAfterToolBatch -> EndTurn
    close inputs; TurnCompleted(Completed, QuietEnd); pending note = fixed note
    ShouldCompact() ? Compacting -> compaction end -> AdvanceAfterCompaction(no tool loop) -> person turn
                    : drain -> person turn
  person turn first call: per-turn context block += pending note; pending note = null

schematic: tool-loop compaction end
  restart requested -> restart path
  automation owner and a person waits -> EndTurn: close, quiet completion, pending note, drain -> person turn
  otherwise -> resume the tool loop (FollowUp, ResumeToolLoop)
```

## Test contract

The tests are the contract. The implementer must not edit them.

- `src/Netclaw.Actors.Tests/Sessions/PersonInputEndsAutomationTurnSpecs.cs`: one theory with a shared pre/post-condition
  helper. The rows `ReminderTurnThenPerson`, `NoticeTurnThenPerson`, `BackgroundJobTurnThenPerson`,
  `DueCompactionAtTheEndPoint`, and `PersonDuringToolLoopCompaction` require the stop note. The other rows require no
  note.
- `src/Netclaw.Actors.MutationTests/ChildConsentPauseMutationTests.cs`: the pure decision table gets the compaction
  boundary rows.
- `evals/run-evals.sh`: `automation_stop_note_person_first`. It reads the note literal from the source under test.

## Risks / Trade-offs

- [A pending note can attach to the wrong turn] → The note attaches only to the next turn whose requester is a person.
  An automation input that runs first in FIFO order does not take it.
- [A restart drops the pending note] → Accepted. The restart notice tells the person that a turn stopped.
- [The end-point compaction delays the person's first call] → It runs only when the threshold is reached. Without it,
  the person's first call can overflow and then compact anyway.

## Migration Plan

No data migration. The change adds no event and no configuration. A rollback restores the old prompt and the old
compaction order.
