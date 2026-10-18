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
- Keep each decision in its current owner.

**Non-Goals:**

- A new limit, a new persistence event, or a new actor message.
- A change to the first correction or to evidence counts.

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
  return Correct if refused else Execute
```

### 2. The batch owner adds the nudge after the last result

The parent nudge text comes from `ToolCycleMessages`. After `SessionToolWorkflow.StartDispatch`, the session calls `SessionToolWorkflow.AddRepeatNudgeAfterBatch`. `ActiveToolBatchTracker` (actor-local) holds the nudge with the batch, and a new batch start clears it. `CompleteIfReady` adds it with `SessionState.AddSystemNudge` after the media nudge. The nudge is then the last history entry before the next model request.

Alternative: add the nudge before dispatch. History would then hold the nudge before the assistant tool-call message, not at the end.

The child adds its nudge with its own `AddSystemNudge` after it appends the tool results and the media nudge.

Persistence: the nudge is transient history, like the empty-response nudge. Process recovery does not restore it. Recovery also does not restore a detector checkpoint, so a restarted session gets no stale nudge.

### 3. The parent and the child get different nudge text

A child has no background job manager. `SubAgentActor` ignores `_background`. The child nudge therefore offers no background job. It tells the child to stop and give its result to the parent, with the command that did not finish. The parent can then run that command as a background job.

The child can lack `skill_read_resource`, so the child nudge names no skill page.

### 4. A valid background request is part of the call identity

`ToolCycleSignatureFactory.Prepare` adds a `background` field to the identity hash when a valid call has `_background: true`. The parent nudge offers a background job. Without this field, the background call has the same identity and gets the same refusal.

Positive example: the guard refused `shell_execute` with `Command="node render.mjs"`. The same call with `_background: true` starts a job after normal approval.

Negative example: the same call with only `_timeout_seconds=600` keeps the identity and gets a refusal and the nudge.

In a child, `_background` does not start a job, so the field gives one more identity for that command. That identity needs two equal rounds before the guard refuses it. The bound is two more executions for each command, and each execution passes normal authorization.

Alternative: add the field only when a job manager exists. That needs another value in `Prepare` for every caller. The bounded cost does not justify the plumbing.

### 5. Person-facing and child stop texts go away

`ToolCycleMessages.TurnStopped` and `ToolCycleMessages.ChildStopped` lose their only callers, so the change removes them. `SubAgentOutcomeReason.ToolCycleStopped` stays, because old child run records can contain the value.

`ErrorCategory.ModelBehavior` stays for repeated empty replies.

## Risks / Trade-offs

- [A model repeats a refused call without end] → Each repeat costs one model request and no tool execution. The person can cancel. A caller wait limit, child timeouts, compaction, and provider budgets stop the work. Aaron did not ask for a new limit.
- [Two nudges in a row] → Each repeated batch adds one nudge. The text is short and the same each time.
- [The active change `simplify-agent-coordination` has an older delta for the same requirement] → Its archive must take this requirement text. The PR notes the conflict.
- [Eval expectations] → `tool_cycle_terminal` becomes `tool_cycle_repeat_nudge`. The new case `long_command_background_job` covers the live pattern.

## Migration Plan

No data migration. A rollback restores the stop behavior. In-flight sessions keep their live state.
