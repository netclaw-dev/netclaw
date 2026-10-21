## Context

See proposal.md - Why. The requirement is in `specs/turn-loop-governance/spec.md`.

Today `TurnStateTracker.EvaluateBeforeDispatch` returns a `Stop` kind when a refused identity or a refused adjacent action repeats.

- The parent (`LlmSessionActor.HandleToolCallResponse`) calls `FailCurrentTurn` with `ToolCycleMessages.TurnStopped` and `ErrorCategory.ModelBehavior`.
- The child (`SubAgentActor.HandleToolCalls`) completes `Partial` with `ToolCycleMessages.ChildStopped` and `SubAgentOutcomeReason.ToolCycleStopped`.

The first refusal already has a complete path. The pipeline returns a synthetic correction result for each refused call ID. That result does not enter recurrence evidence, so the identity stays refused.

## Goals / Non-Goals

**Goals:**

- Use the existing first-refusal path for a repeated refusal.
- Add one model-facing nudge after the tool results of the batch.
- Give the refused loop an end that the person does not see as an error (Aaron, 18 October 2026).
- Keep each decision in its current owner.

**Non-Goals:**

- A new persistence event, a new actor message, or a configuration property.
- A change to the first correction or to evidence counts.
- A limit on distinct work. Only refused repeats count.

## Decisions

### 1. The tracker refuses a repeat as a correction and marks it repeated

`TurnStateTracker` (actor-local) owns the decision. The `Stop` kind goes away.

- A group with a refused episode adds its call IDs to the refused set and to a new repeated set. Evaluation continues with the other groups.
- A repeat of the last blocked adjacent action refuses every call in the batch and marks them repeated.
- The decision kind is `Correct` when any call is refused.

Alternative: keep a separate `Repeat` kind. A mixed batch can hold first refusals, repeated refusals, and new calls together, so one kind cannot describe it. A set of call IDs can.

Schematic flow. It omits authorization, validation, and persistence:

```text
EvaluateBeforeDispatch(batch):
  for each identity group:
    pending job        -> exempt
    episode.Corrected  -> refused += ids; repeated += ids
    equalRounds >= 2   -> refused += ids
  if no exempt call:
    adjacent repeat    -> refused += all; repeated += all
    adjacent cycle     -> refused += all
  mark refused groups Corrected
  if repeated: refusedRepeats += 1
  refusedRepeat = none      if not repeated
                  Nudge     if refusedRepeats < 5
                  Escalated if refusedRepeats == 5
                  EndTurn   if refusedRepeats > 5
  return Correct if refused else Execute
```

The count `refusedRepeats` is actor-local state of `TurnStateTracker`. One model response with repeated calls adds one to the count. A completed different action does not change it. `ResetForNewTurn` clears the recurrence evidence but not the count. `ResetRefusedRepeatCount` clears the count, and decision 8 lists its callers.

### 2. The batch owner adds the nudge after the last result

The parent nudge text comes from `ToolCycleMessages`. After `SessionToolWorkflow.StartDispatch`, the session calls `SessionToolWorkflow.AddRepeatNudgeAfterBatch`. `ActiveToolBatchTracker` (actor-local) holds the nudge with the batch, and a new batch start clears it. `CompleteIfReady` adds it with `SessionState.AddSystemNudge` after the media nudge. The nudge is then the last history entry before the next model request.

Alternative: add the nudge before dispatch. History would then hold the nudge before the assistant tool-call message, not at the end.

The child adds its nudge with its own `AddSystemNudge` after it appends the tool results and the media nudge.

The history keeps at most one repeat nudge. Before `CompleteIfReady` adds a repeat nudge, it removes the earlier repeat nudge with `SessionState.ReplaceSystemNudge`. The child removes its earlier repeat nudge from its own history in the same way. Each repeat nudge starts with the same text, `Netclaw refused your repeated call to`, so the match is exact. The removal changes the history before the end, so a byte-prefix cache misses after that point. Aaron accepted this cost: a stack of equal nudges gives the model no new information.

Persistence: no journal event holds the nudge, the same as the empty-response nudge. A snapshot holds `SessionState.History`, so a snapshot that the session takes during the loop keeps the latest nudge. Journal replay without that snapshot drops the nudge. Recovery does not restore a detector checkpoint or the count, so a restarted session starts a new count.

### 3. The parent and the child get different nudge text

A child has no background job manager. `SubAgentActor` ignores `_background`. The child nudge therefore offers no background job. It tells the child to stop and give its result to the parent, with the command that did not finish. The parent can then run that command as a background job.

The child can lack `skill_read_resource`, so the child nudge names no skill page.

### 4. A valid background request is part of the call identity

`ToolCycleSignatureFactory.Prepare` adds a `background` field to the identity hash when a valid call has `_background: true`. The parent nudge offers a background job. Without this field, the background call has the same identity and gets the same refusal.

`Prepare` adds the field only when the canonical tool name is `shell_execute`. `SessionToolExecutionPipeline` runs `_background` in the foreground for every other tool. If another tool got the field, a refused `web_fetch` with `_background: true` would get a new identity and two more real runs.

Positive example: the guard refused `shell_execute` with `Command="node render.mjs"`. The same call with `_background: true` starts a job after normal approval.

Negative examples:

- The same call with only `_timeout_seconds=600` keeps the identity and gets a refusal and the nudge.
- A refused `web_fetch` call with `_background: true` keeps the identity and gets a refusal and the nudge.

In a child, `_background` does not start a job, so the field gives one more identity for a shell command. That identity needs two equal rounds before the guard refuses it. The bound is two more executions for each command, and each execution passes normal authorization.

Alternative: add the field only when a job manager exists. That needs another value in `Prepare` for every caller. The bounded cost does not justify the plumbing.

### 5. Person-facing and child stop texts go away

`ToolCycleMessages.TurnStopped` and `ToolCycleMessages.ChildStopped` lose their only callers, so the change removes them. `SubAgentOutcomeReason.ToolCycleStopped` stays, because old child run records can contain the value.

`ErrorCategory.ModelBehavior` stays for repeated empty replies.

`SubAgentOutcomeReason.ToolCycleStopped` gets a caller again: the quiet end of a child run (decision 7).

### 6. The fifth refused repeat escalates, and the sixth ends the parent turn quietly

`ToolCycleBatchDecision.RefusedRepeat` tells the turn owner which of these applies: `None`, `Nudge`, `EscalatedNudge`, or `EndTurn`. `LlmSessionActor` (actor-local) owns the end of the turn.

- `Nudge`: `ToolCycleMessages.RepeatNudge`.
- `EscalatedNudge`: `ToolCycleMessages.EscalatedRepeatNudge`. The call stays refused.
- `EndTurn`: `HandleToolCallResponse` does not persist `ToolBatchStarted` and does not dispatch.
  - If the response has text, the session removes the tool calls from the response and uses the normal text path (`HandleTextResponse`). That path persists `TurnRecorded`, emits the text, and completes the turn. The history then ends with an assistant text message, which every provider accepts.
  - If the response has no text, the session closes the active inputs and completes the turn with `TurnOutcome.Completed` and `QuietEnd` true (decision 9). It adds no reply and emits no error. An empty assistant message is not safe, because some providers reject an empty text block. The history then ends with the escalated nudge, a user-role `[system: ...]` message after the last tool results.

Schematic flow. It omits authorization and the child continuation:

```text
HandleToolCallResponse(response):
  decision = PrepareFreshBatch(response)
  if decision.RefusedRepeat == EndTurn:
     if response has text: HandleTextResponse(response without tool calls)
     else: CloseActiveInputs -> TurnCompleted(Completed, QuietEnd = true)
     return
  persist ToolBatchStarted; dispatch; nudge = Nudge or EscalatedNudge text
```

Why the turn ends with the text of the last response, and not with an earlier text: the session already sent each earlier preamble text to the person. A second copy would repeat a Slack post.

Why person input that joins the turn resets the count: a new Slack message in the same thread joins the running turn (`turn_mid_loop_buffer_drain`) and calls `ResetForNewTurn`. That call already clears the refusals, so the model can run the call again. The person can then ask the model to try again, and the count starts again with that new instruction.

### 7. A child ends quietly with its last text

`SubAgentActor` (actor-local) uses the same decision. Refused repeat 5 gets `ToolCycleMessages.EscalatedChildRepeatNudge`. Refused repeat 6 completes the run with `success: true`, `SubAgentRunOutcome.Partial`, and `SubAgentOutcomeReason.ToolCycleStopped`. The output is the text of the final response, or else the latest earlier assistant text in the child history. The parent did not see that earlier text, so the child returns it. `SubAgentSpawner` treats `Partial` as a successful run, so the parent gets no error.

### 8. Only a new turn and person input reset the count

`ResetForNewTurn` has more callers than new person input. The re-review found two that must not reset the count:

- `FireLlmCall` calls it when the session delivers a child result in the middle of a tool loop. The model can start a background child next to each refused repeat. If each child result reset the count, the model would never reach repeat 6.
- `ApplyBufferedBatch` calls it when buffered input joins a running turn. For an automation turn, the requester is a fixed system sender, so every later reminder fire, job result, or webhook call of the same kind joins the turn. If each join reset the count, the automation fire rate would set the bound.

The count therefore has its own reset, `TurnStateTracker.ResetRefusedRepeatCount`. The existing evidence reset stays as it was.

| Event | Evidence reset | Count reset |
|-------|----------------|-------------|
| New turn from a person or an automation input (`PrepareFreshTurnState`, or a buffered batch that starts a turn) | yes | yes |
| Person input that joins a running turn (`ApplyBufferedBatch`) | yes | yes |
| Automation input that joins a running automation turn (`ApplyBufferedBatch`) | yes | no |
| Child result delivered in the middle of a turn (`FireLlmCall`) | yes | no |
| Child result that wakes an idle session (`WakeForChild`) | yes | no |

A new turn must reset the count, whoever starts it. Otherwise a session that only automation writes to would keep the count of earlier turns. Its first refused repeat after the count reached 6 would end the turn at once, with no nudge.

An automation input is an input whose requester principal is `VerifiedAutomation` or `SystemProcess`, the same test as `SessionToolWorkflow.IsAutomation`. The join test uses the principal of the running turn. `HasSameRequester` guarantees that a joining input has the same principal.

A turn that a child result wakes has no new input, so it continues the count of the earlier turn. That count can be 6 after a quiet end. The first refused repeat of the woken turn then ends the turn at once, because the model already ignored the escalated nudge. The woken turn first needs two runs and one correction before the guard refuses a call, so the model does not lose its first attempts.

Positive example: the count is 3, and the model starts a child next to the repeat. The child result arrives in the middle of the loop. The refused call runs twice again, and the count reaches 5 after two more refused repeats.

Negative example: a reminder turn has count 4, and another reminder fire joins the turn. The count stays 4. Without this rule, the join would reset it to 0, and a reminder that fires every minute would keep the loop alive.

### 9. A flag on `TurnCompleted` keeps the channels quiet

`ChannelOutputEngine.CompleteTurnAsync` posts the empty-turn fallback (":warning: I didn't manage to produce a reply. Please try rephrasing or sending your message again.") when a turn delivered nothing. For a quiet end with no text, that is a Netclaw error shown to the person. Its advice to send the message again also starts the same loop again.

`TurnCompleted.QuietEnd` (bool, default false) marks this end. The session sets it only in `FinishTurnWithoutReply`, the no-text quiet end. The engine (owner of the fallback decision, call-local) skips the fallback when it is true. It still clears the pending approvals and the typing indicator. A failed post still reports to the session, because that path does not use the fallback.

The flag crosses the hub in `SessionOutputDto.QuietEnd` (`bool?`, wire name `quietEnd`). The mapper writes it for every `turn_completed` output. A missing or null field reads as false, so an old daemon keeps the fallback for every empty turn. An old client ignores the field.

Alternatives:

- Keep the fallback and state the exception in the spec. Rejected: the person sees a Netclaw error, which the rule forbids.
- Post a neutral line that the model did not write. Rejected: it is still Netclaw text.
- Add a `TurnOutcome` value. Rejected: every consumer switches on `TurnOutcome`, and the quiet end is a normal `Completed` turn. A flag changes no existing consumer.

Positive example: a looping model sends only tool calls and no text, and ignores the escalated nudge. Slack shows no new post and no warning. The typing indicator stops.

Negative example: a provider returns an empty reply, and the turn completes with `QuietEnd` false. The Slack thread shows the fallback text, as before.

## Risks / Trade-offs

- [A model repeats a refused call] → The worst case for one turn is five refused repeats with a nudge and one more response that ends the turn. That is six model requests after the first correction, and no refused call executes. Within one turn, no other runtime bound stops the loop: the session and the TUI have no cancel command, a caller wait limit does not stop the turn, a child inactivity watchdog resets on each model response, and compaction resumes the loop. A new turn resets the count, whoever starts it (decision 8). Person input that joins a running turn also resets it, so a person who keeps writing in the thread can extend the loop. Each message then gives the model a new instruction. The model can start a new turn itself: a `_background` shell job or a reminder that it creates delivers its result as new input. Each such turn stays bounded at about nine model requests. A chain of self-started turns is not bounded by this rule. That self-wake chain existed before this PR and is tracked separately.
- [A reminder turn that ends quietly reports success] → The reminder records success with empty output and sends no failure alert. This follows the rule that no Netclaw error reaches the person. The exception: a reminder that requires channel delivery, and whose loop never called the notify tool, records a failure in reminder history and the logs, not in the thread. The only trace of a quiet end is the warning `turn_refused_repeat_end` in the session log. The runbook tells the operator where to look.
- [The removal of an earlier nudge changes history before its end] → A byte-prefix cache misses after that point. The loop has at most five nudges for each count, so the cost is small.
- [Open changes still hold the stop rule] → Three open OpenSpec changes keep the old stop rule in their deltas. If one of them archives after this change, it puts the stop rule back into the main specs. Before each one archives, update its delta with the opsx skills, or archive this change last:
  - `stop-repeated-tool-cycles`: `turn-loop-governance` "Cycle intervention is paired and terminal on repetition" ("A repeated blocked action forces completion"), and `netclaw-subagents` "Subagents stop exact repeated tool cycles" ("its final outcome is partial").
  - `simplify-agent-coordination`: `turn-loop-governance` "Tool recurrence governs repeated work", scenario "Repeated call after correction settles without another model request".
  - `report-headless-turn-error-category`: `netclaw-cli` scenario "Model ignores the cycle stop", which requires `[turn:failed] category=model kind=ModelBehavior`.
- [Eval expectations] → `tool_cycle_terminal` becomes `tool_cycle_repeat_nudge`. The new case `long_command_background_job` covers the live pattern.

## Migration Plan

No data migration. A rollback restores the stop behavior. In-flight sessions keep their live state.
