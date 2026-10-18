## Why

PRD-001 requires reliable long agent turns. PRD-006 requires factual tool results.

The recurrence guard from PR #2466 stops the whole turn when the model repeats a refused call. The person then sees a runtime error, and the work stops.

Live example, 18 October 2026, 12:17 to 12:32 UTC:

1. A `node render.mjs` command timed out at 90 seconds two times.
2. The guard refused the third run.
3. The model sent the same call again.
4. Netclaw failed the turn and showed "Netclaw stopped this turn because the model repeated a tool call that was already refused."

Aaron's rule: do not stop the turn. Refuse the call again and give the model a nudge with alternatives. A person must not see this error text unless the model decides to tell the person.

Aaron's second decision, also 18 October 2026: the loop gets an end, and the person still never sees a Netclaw error. After five refused repeats in one turn, the nudge escalates. If the model repeats a refused call after that, the turn ends quietly with the text of the model.

## What Changes

- A repeated refused call gets a refusal tool result again. The call does not execute.
- The session adds one model-facing system nudge after the tool results of that batch.
- The parent nudge names the refused tool and gives alternatives: a background shell job, a changed command or arguments, smaller steps, and a question to the person.
- The parent nudge names `skill_read_resource('netclaw-operations', 'references/long-running-commands.md')`.
- The turn continues with the next model request.
- The session counts refused repeats in each turn. Refused repeat 5 gets an escalated nudge: stop this action, end the turn, and tell the person what blocked the work. The call stays refused.
- Refused repeat 6 ends the turn quietly. No call of that response runs, and no model request follows. The turn completes with the text of that response, or with no new reply. The person sees no error, and a headless run exits with code 0.
- New person input resets the count, also input that joins a running turn.
- The history keeps only the latest repeat nudge.
- The parent does not fail the turn, does not send an error to the person, and does not use `ModelBehavior` for this case.
- A child run follows the same rule. The child nudge does not offer a background job, because a child has no background job manager. Refused repeat 6 ends the child as a successful partial run (`tool_cycle_stopped`) with the last text of the child.
- A valid `_background: true` request on `shell_execute` becomes part of the call identity. A background run of a refused foreground shell command is a different action, so the guard permits it. Other tools ignore `_background`, so it does not change their identity.
- A different call in the same batch runs normally.
- **BREAKING** (observable contract): a headless run that repeats a refused call no longer exits with `[turn:failed] category=model kind=ModelBehavior`. The eval case `tool_cycle_terminal` becomes `tool_cycle_repeat_nudge` and checks the new behavior.
- Add the `netclaw-operations` reference page `references/long-running-commands.md`. Move the background job details to that page.
- Add the regression eval case `long_command_background_job`.

In scope:

- Parent session and child run tool loops.
- The refusal result, the nudge, call identity, and the skill page.
- Specs, docs, the eval recipe, and tests.

Out of scope:

- A limit on distinct work or a time limit. Only refused repeats count.
- Changes to the first correction, the per-call evidence count, or the adjacent-cycle rule.
- Changes to empty-response failures. They keep `ModelBehavior`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `turn-loop-governance`: A repeated refused call is refused again with a nudge, and the turn continues. A valid background request is part of the call identity.

## Impact

Code: `TurnStateTracker`, `ToolCycleSignatureFactory`, `SessionToolWorkflow`, `ActiveToolBatchTracker`, `LlmSessionActor`, and `SubAgentActor`.

The change adds no actor, persistence event, configuration property, or package.

The nudge is transient history in the live owner, the same as other system nudges. It is not a durable event.

Security: a refused call still executes no tool and grants no authority. Every executed call still passes authorization and validation. The background identity rule permits at most one more identity for each command, and that call still needs normal authorization.

Operations: the person no longer sees a runtime error for this case. The end at refused repeat 6 is the runtime bound for a refused loop. No other runtime bound applies: the session has no cancel command, a caller wait limit does not stop the turn, a child inactivity watchdog resets on each model response, and compaction resumes the loop. The `netclaw-operations` skill version increases.

Caution: the open changes `stop-repeated-tool-cycles`, `simplify-agent-coordination`, and `report-headless-turn-error-category` still keep the stop rule. Each one must take this requirement before it archives. See design.md, Risks.
