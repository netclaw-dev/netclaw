## Context

See [the proposal](proposal.md) for motivation and scope.
The baseline is `76071cfe9e721dee864d60d1379a497d97f19414`.
The [canonical execution plan](../../../.systematize/plans/net11-typed-decisions/plan.md) owns PR boundaries, tasks, verification, and handoff authority.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## Goals / Non-Goals

Use .NET 11 and C# 15 for selected closed outcome types.
Consolidate authorization results and separate retry decisions from their effects.
Preserve the current actor runtime, public contracts, configuration, serialization, and journal behavior.
Add no decider framework, new persistence boundary, or new consent policy.

## Decisions

### Keep one owner for each decision

The actor owns execution. A pure function owns the selected retry or reconnect policy.
The function consumes explicit values and returns a typed decision.
The current owner retains state unless a state replacement removes more complexity.
The actor performs I/O, updates its owned state, and converts completion signals into correlated inputs.
Preserve commit-before-effect order wherever the existing contract requires it.

The following pseudocode is schematic. It omits authorization, journal callbacks, and transport details.
The proposed names describe a shape and do not require new public APIs.

```text
Before
  actor checks phase and failure
  retry helper changes counters
  actor selects another model call or a failure report

After
  plan = decide(current retry facts, phase, failure)
  actor applies the decision through its existing state owner
  actor executes the required effects through its existing methods
```

For example, current retryable feedback within the existing budget requests the same retry.
Feedback for an earlier turn requests no retry and cannot change the current turn's budget.
A gateway completion for an earlier attempt cannot make a newer connection Ready.
An explicit disconnect cannot schedule another automatic reconnect.

### Preserve the lifetime of each fact

| Owner | Data lifetime | Responsibility |
|---|---|---|
| `SessionState` and existing snapshots | Durable | Restore current journal and snapshot facts without a format change. |
| Session actor and retry owner | Actor-local | Hold live retry state and execute the selected actions. |
| Retry decision function | Call-local | Derive an outcome from explicit values without I/O or hidden mutation. |
| Gateway actor | Actor-local | Own clients, subscriptions, timers, and pending replies. |
| Reconnect decision function | Call-local | Select transitions and actions from state, input identity, and time. |
| `ToolAuthorizer` and its current rule owners | Call-local | Preserve ordered authority decisions and exact evidence. |
| Public authorization facade | Call-local | Preserve released factory, property, and exception contracts. |

A decision state replaces its current policy state. It must not become a second authoritative copy.
Do not make a new record durable solely because a test can inspect it.

### Preserve the public authorization boundary

Use one closed internal result family. Keep the original public record as a compatibility boundary.
Do not change its generated equality, hash, clone, or text behavior.
Preserve the public `ToolAuthorizationDecision`, `IToolExecutor`, factories, properties, and executor exception behavior.
In particular, preserve the existing public `Allowed` meaning while internal code uses explicit cases.
An internal cleanup does not authorize a public API break.
Keep adapters only at released boundaries and delete replaced internal state at cutover.
Preserve the distinct pre-coverage consent candidate that can carry temporary-path advice.
Grant coverage consumes that candidate before the final consent or correction result exists.
Only a terminal consent result excludes advice. Test covered and uncovered paths before changing that intermediate shape.

### Reuse the existing proof infrastructure

Use the existing authorization corpus, actor contracts, native smoke driver, and focused mutation gates.
Move policy permutations into direct tests within each production slice.
Retain actual persistence, recovery, cancellation, publication, and transport effect proof.
Map each removed integration case to its retained guarantee and a replacement that rejects the same unsafe change.

## Risks / Trade-offs

- PR1 can expose SDK, analyzer, native dependency, or source-generator incompatibility. Verify actual release artifacts before acceptance.
- PR2 can accidentally change public facade semantics or rule order. Require public consumer proof and zero corpus differences.
- PR3 can lose reminder attribution or snapshot recovery. Preserve existing durable facts and real restore tests.
- PR4 overlaps the gateway stop-timeout PR. Reconcile the actual base and retain the bounded stop guarantee.
- Pure tests cannot prove effect order across persistence or the real transport. Retain those boundary tests.

## Migration Plan

Deliver PR1 through PR4 through the actual `gh stack` extension as the canonical plan specifies.
Verify each combined head before starting its successor.
Keep every slice behavior-compatible and remove its replaced internal path in the same diff.
No stored-data migration or release publication belongs to this change.
If runtime discovery requires a durable format change, stop that slice and revise the proposal before implementation.
The operator owns all merges and deployments.

## Open Questions

The exact baseline-database upgrade fixture and native release-host access require execution-time proof.
These gaps block PR1 acceptance, not plan authorship.
The canonical plan records the existing infrastructure to use and all unrun checks.
The user accepted focused verification instead of the blanket ten-live-lane template.
Reuse existing actor fixtures and native smoke. Add no standalone process probe solely for a refactor.
