# Shell Policy Reduction Revision

This evidence records the preliminary audit after PR #1947.

## Reason for revision

The original measure covered seven files. Those files changed from 5,136 to 4,589 lines.

They also changed from 373 to 305 control-flow lines. This result did not count code that moved into eight new policy files.

The complete changed production footprint grew. It changed from 6,680 to 8,164 lines.

It also changed from 452 to 504 control-flow lines. The refactor therefore added 1,484 lines and 52 control-flow lines.

The change cannot pass its reduction gate in this state. Task 8.9 required a design revision before more implementation work.

## Revised measure

The final audit uses corpus commit `8b4108aa92a229f4727377299d9dd2ed19f70e07` as its baseline.

It counts every changed production C# file in these roots:

- `src/Netclaw.Actors/Tools/`
- `src/Netclaw.Security/`

An added file has zero baseline lines. A removed file has zero final lines.

The final state must satisfy both conditions:

- The original seven files remain below 5,136 lines and 373 control-flow lines.
- The complete changed production footprint has fewer lines and control-flow lines than the baseline.

## Direct-stage reduction slice

The merged corpus commit is `d2186d83e0ce2fe0d51ac67ea029eefa579abca3`.

This slice removes the delegate pipeline and its stage array. The coordinator now calls the same typed stages in one fixed order.

The slice removes 117 production lines and three control-flow lines. It also removes 257 stage-test lines for states that production cannot construct.

The expanded changed-file footprint now uses 7,240 baseline lines and 479 baseline control-flow lines. This set includes files changed after PR #1947.

The current slice uses 8,665 lines and 530 control-flow lines. The complete reduction gate remains open.

## Closed coverage-source slice

This slice replaces the coverage-kind, reason, and scope tuple with one closed internal source. Trace fields now derive from that source once.

Validated actor evidence is bound to its originating candidate snapshot. The evaluation no longer revalidates the same actor batch after the boundary accepts it.

The slice removes another 100 production lines and nine control-flow lines. It also removes 28 redundant state-test lines.

The cumulative footprint uses 8,565 lines and 521 control-flow lines. The complete reduction gate remains open.

## Derived path-metadata slice

This slice removes path-domain tags already expressed by the closed domain type. It also removes base tags already expressed by separate real, intent, and fallback views.

The slice removes another 45 production lines and eight test lines. It does not change the control-flow count.

The cumulative footprint uses 8,520 lines and 521 control-flow lines. The complete reduction gate remains open.

## Unified reviewed-safe path slice

This slice removes the unused aggregate reviewed-safe route. The compatibility entry now projects candidate-scoped path facts and calls the same reviewed-safe method as the coordinator.

The evaluation also reuses the projection's immutable candidate snapshot, and the path-fact projection returns its immutable candidate list directly instead of wrapping a second indexed container.

The slice removes another 57 production lines and four control-flow lines. It adds 19 test lines while moving the existing direct safe-policy cases onto the shared typed route.

The cumulative footprint uses 8,463 lines and 517 control-flow lines. The complete reduction gate remains open.

## Consolidated path and actor snapshot slice

This slice removes candidate identity and causal scope copies from path facts. Each intent and fallback view now owns its resolution base, while the candidate remains the sole owner of its ID and parser occurrence.

The actor adapter also reuses one empty-result constructor. Validation snapshots the candidate and near-miss collections without cloning their sealed immutable elements.

The slice removes another 35 production lines without changing the control-flow count. It removes one test line while preserving the mutable-list, malformed-evidence, path-base, and causal-fallback regressions.

The cumulative footprint uses 8,428 lines and 517 control-flow lines. The complete reduction gate remains open.

## Closed stage-outcome slice

This slice removes the allocation-backed stage result hierarchy left behind by the deleted delegate pipeline. Stages now mutate only `ShellPolicyEvaluation` and return a closed `Continue` or `Complete` outcome.

The coordinator verifies that the outcome agrees with terminal state. Invalid enums, completion without a terminal decision, and continuation after completion fail closed.

The slice removes another 42 production lines without changing the control-flow count. The cumulative footprint uses 8,386 lines and 517 control-flow lines. The complete reduction gate remains open.

## Preliminary coverage and risk

The audit used `dotnet-coverage` 18.10.0 and `crap4dotnet` 0.1.1.

The actor suite passed 3,411 cases with one expected Windows-only skip. It reported 68.81% line coverage and 45.54% branch coverage.

The security suite passed 927 cases. It reported 62.04% line coverage and 51.99% branch coverage.

The largest new risk values came from these methods:

| File | Method | Complexity | Coverage | CRAP |
| --- | --- | ---: | ---: | ---: |
| `ShellPolicyEvaluation.cs` | `RunAsync` | 12 | 0.00%* | 156.00 |
| `ShellPathRules.cs` | `TryGetWindowsDepth` | 12 | 0.00% | 156.00 |
| `ShellPolicyCoordinator.cs` | `EvaluateCoreAsync` | 9 | 0.00%* | 90.00 |
| `ShellPolicyPathFacts.cs` | `Resolve` | 8 | 0.00% | 72.00 |

`crap4dotnet` does not map async state-machine coverage to the source method. The zero values remain repeatable comparison data.

## Commands

```bash
dotnet-coverage collect 'dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj -c Release --no-build --no-restore' \
  -f cobertura -o /tmp/netclaw-final-actors.cobertura.xml
dotnet-coverage collect 'dotnet test src/Netclaw.Security.Tests/Netclaw.Security.Tests.csproj -c Release --no-build --no-restore' \
  -f cobertura -o /tmp/netclaw-final-security.cobertura.xml
dotnet-crap analyze SOURCE.cs --coverage COVERAGE.xml --min-crap 0 --output REPORT.json
```
