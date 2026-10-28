# Tool-Loop Detection Contract

PRD references: PRD-001 and PRD-006.
See [the engineering glossary](GLOSSARY.md) for shared terms.

## Decisions and owners

`TurnStateTracker` owns actor-local detector state. Both actors use its decisions.
The existing action detector compares exact action and outcome identities.
The cause detector compares structured source, code, and optional scope values.
The failure budget counts failed calls since the latest batch with a success.
A failure budget limits repeated failure. It does not prove a loop.

| Detector | Evidence | Response |
|---|---|---|
| Action recurrence | Equal action and outcome identities | Refuse matching calls and advise the model |
| Failure-cause recurrence | Three occurrences in six complete feedback batches | Give cause advice, escalate, then end the next attempted dispatch |
| Failure budget | Weighted failure score | Give advice at 4, escalate at 6, and end at 8 full units |

The advice selector compares severity first.
At equal severity, action advice wins over cause advice. Cause advice wins over budget advice.
The actor appends one selected nudge after the results.
The tracker records only the cause advice that the actor selects for the next model request.
A cause cannot escalate because another cause qualified for advice.
Repeated evaluations do not advance the stage.
A final text response remains available at the final stage.

## Cause evidence

`ToolFailureKey(Source, Code, Scope)` carries a stable cause identity.
The key contains no raw error text or argument hash.
The shared mapper recognizes `invalid_rationale`, `unrecognized_argument`, `args_parse_error`, and `invalid_meta_value`.
These front-door validation codes use `Source=netclaw` and no scope.
Other codes have no known cause identity. They affect the failure budget through their receipt category.
The detector never guesses a cause from an outcome category.

The tracker retains the latest six complete feedback batches.
Each batch retains at most three occurrences per cause.
A new user turn clears the history.
A batch without a cause retires that cause's active advice.
Recent occurrence evidence remains available until it leaves the six-batch window.
Thus an unrelated success does not erase evidence, but a successful correction removes stale stop advice.
A later recurrence starts at the first nudge.

Positive example: three distinct tool calls lack `_rationale`.
Netclaw gives the rationale nudge after their results.
If the next batch repeats that cause, Netclaw escalates the advice.

Negative example: the model corrects `_rationale` and the next call succeeds.
Netclaw removes the cause advice. One later mistake cannot inherit the old escalation.

## Canonical result facts

`ToolCycleResult` converts live receipts to category, pending-job, synthetic, and cause facts.
The parent journal and the child transcript preserve the same representation.
The protobuf mapper preserves source, code, and optional scope without reinterpretation.
A legacy result without category facts cannot support safe parent batch reconstruction.
The parent abandons that recovered batch explicitly.

Synthetic cycle corrections and pending-job receipts neither add failures nor reset the failure budget.
A successful receipt resets the budget for the whole logical batch.
`NotFound` adds one half-unit. Each other failure adds two half-units.
The model sees the actual failed-call count, not the weighted score.
A batch advances the budget at most one stage.
Shell exit status zero produces a success receipt. A nonzero status produces a failure receipt.
The shell does not parse executable-specific exit semantics.

## Consent and recovery

A consent pause divides execution, not model feedback.
The tracker preserves unfinished cause facts until the logical batch completes.
The child park preserves the bounded cause history and the advice selected for each batch.
The park also preserves the existing budget and incomplete success or failure facts.
The protobuf fields are additive. Older records restore empty cause history.
A cold parent recovery starts fresh turn-local history and observes the reconstructed in-flight batch once.

```text
collect canonical result facts
  -> preserve unfinished facts at consent
  -> complete the logical batch once
  -> select severity, then detector priority
  -> append one nudge
  -> record the selected cause advice for the next feedback batch
```

## Verification

`LoopGuardShapeTests` represents complete batches, including real parallel bursts.
Positive catalog cases assert the full stage sequence and the detector reason.
`LoopFeedbackTests` covers repairs, expiry, consent splits, competing causes, and repeated evaluations.
Parent and child tests prove that useful calls still execute after successful repairs.
Serialization theories preserve cause advice and unfinished facts through the real park codec.
These deterministic checks do not establish model-task completion rates or optimal thresholds.
