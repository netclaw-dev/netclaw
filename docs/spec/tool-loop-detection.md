# Tool-Loop Detection Contract

Status: design for PR #2710 (issue #2706). Replaces the single "progress axis" with three distinct detectors.

## The problem

A run of failures is not a loop. The current progress axis collapses three different things into one weighted number, so it cannot tell "stuck repeating the same ineffective cause" from "many distinct misses during legitimate exploration." It is a failure budget wearing a loop-detector's name.

## Three detectors, one owner

`TurnStateTracker` owns all decision state and returns one consolidated intervention per evaluation. No second actor, no LLM judge.

| Detector | Question it answers | Evidence | Confidence |
|---|---|---|---|
| **Action recurrence** | Is the model repeating the same action? | `(ToolName, ArgumentsHash)` and adjacent-cycle outcome hashes | High |
| **Failure-cause recurrence** | Is the model repeating the same root failure across different tools/arguments? | stable `ToolFailureKey(Source, Code, Scope)` | High (when the key is structured) |
| **Failure budget** | Has the turn failed too many times to keep going, with no recognized recurrence? | weighted failure count since last success | Low — conservative backstop only |

Priority when more than one fires: action recurrence > failure-cause recurrence > failure budget.

## Result shape

```csharp
internal enum LoopInterventionReason
{
    None,
    ActionRecurrence,
    FailureCauseRecurrence,
    FailureBudget
}
```

The action is the existing `RefusedRepeatAction` ladder: `None`, `Nudge`, `EscalatedNudge`, `EndTurn`. `TurnStateTracker` returns `(reason, action, cause?)` so tests and telemetry can see *which* detector fired and *why*, not just that something intervened.

## Failure-cause identity

A failure cause is a stable key, not a category:

```csharp
internal readonly record struct ToolFailureKey(
    string Source,
    string Code,
    string? Scope = null);
```

- The key never contains tool name or argument hash.
- `invalid_rationale` (front-door validation) recurs across every tool: `Source=netclaw`, `Code=invalid_rationale`, no scope.
- Resource- or tool-specific failures include a stable scope. Two unrelated `NotFound` paths are two causes, not one.
- `Category` alone is never a recurrence key. Unknown/unstructured failures fall to the budget.

The key must survive persistence (journal recovery, park/restore) or be explicitly unavailable — never guessed. Absence of a key in an old record means "no known cause," not "a known identical cause."

## Feedback-aware escalation

A model cannot ignore a correction it never saw. Escalation advances only across real model-feedback boundaries.

- Multiple same-cause failures in one parallel batch establish the problem but consume exactly one feedback opportunity.
- After the model receives a cause nudge and repeats the cause → escalate.
- After it repeats an escalated correction → end the next attempted dispatch (existing quiet-stop semantics). A final text response stays allowed.
- Re-readiness checks and repeated dispatch evaluations must not advance a stage twice.

## Failure budget

Retained as a conservative backstop, not loop detection. Weighted `NotFound` (half) vs other failures (full). The 4/6/8 full-unit thresholds stay. It reports the true failure count and must never claim a repeated cause. An unrelated success does not automatically prove task progress, but it does reset the budget.

## What does not count as a failure

- Synthetic guard corrections (the detector's own output).
- Pending background-job receipts.

## Test matrix (LoopGuardShapeTests)

Three groups, each asserting the **reason** and **stage** independently:

1. **ActionRecurrenceShapes** — the existing controls (same call twice, `A B A B A`). Assert reason = `ActionRecurrence`.
2. **FailureCauseRecurrenceShapes** — red until Prompt 3. Assert reason = `FailureCauseRecurrence`:
   - rotating tools, same structured `invalid_rationale` → nudge at 3 occurrences;
   - same tool, varying args, same code → recur;
   - different codes, same `InvalidInput` category → must NOT conflate;
   - distinct `NotFound` paths → must NOT conflate;
   - cause with unrelated successes interleaved → still tracked (bounded window);
   - one parallel batch of identical failures → one feedback opportunity;
   - nudge → repeat → escalate → repeat → end turn (final text still allowed);
   - a corrected call / changed cause → stops escalating.
3. **FailureBudgetShapes** — the existing weighted-budget cases. Assert reason = `FailureBudget`, and that a streak of distinct failures stays conservative.

## Out of scope

- Fixing the `_rationale` gate itself — #2707 owns that.
- Task-level progress judgment — belongs to the model, not this guard.
