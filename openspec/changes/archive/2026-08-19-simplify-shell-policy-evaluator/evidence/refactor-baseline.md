# Shell Policy Refactor Baseline

This baseline freezes behavior before production edits.

## Source and fixtures

The live-regression corpus commit is `8b4108aa92a229f4727377299d9dd2ed19f70e07`.
The design commit above it is `3e6f9bb805fd33bc9f8cd5198de2c117d80838dc`.

| Artifact | SHA-256 |
| --- | --- |
| `approval-matrix.json` | `0169105efe87b345d9a82d777ef86909e31fa81a5255cc0cc30f32fbe4d0d6b0` |
| `netclaw-policy-fixtures.json` | `bdfdc56d509bf0783db15cb468b4cb01ee3eca9dc01c517590c83d832c2be25c` |
| `post-1890-approval-harvest.json` | `4f40850ede2cd2b334f2ae5bcc207473df70cc9d39e0812b23b6558c9357095a` |
| `post-1925-binary-swap-approval-harvest.json` | `09420e69e50d06204934c01f3856103ea546b9e10c529e156e67594eaad6aaa5` |
| `post-1925-extended-approval-harvest.json` | `87048616083017f80e8a2cb35fadcc6a145715e7acc9ee95ad04f481f6818554` |

The actor fixture and disposition tests passed 362 cases.
The security evidence tests passed 15 cases.

## Terminal precedence

| Boundary | Exact regression |
| --- | --- |
| Protected path before causal authority | `Causal_intent_cannot_bypass_protected_path_policy` |
| Protected path before symlink rejection | `Protected_path_denial_precedes_symlink_fallback_rejection` |
| Duplicate actor identity denies | `Authorization_evaluation_denies_duplicate_actor_candidate_id` |
| Changed actor facts deny | `Authorization_evaluation_denies_mismatched_actor_match` |
| Malformed persistent scope denies | `Authorization_evaluation_denies_malformed_persistent_actor_scope` |
| Invalid store failure denies | `Authorization_evaluation_denies_invalid_store_failure_enum` |
| Unavailable store denies uncovered work | `Authorization_evaluation_denies_uncovered_candidate_when_store_is_unavailable` |
| Exact one-time authority survives store failure | `One_time_approval_remains_valid_when_persistent_store_is_unavailable` |
| Scope correction precedes a prompt | `Reviewed_safe_external_cwd_exposes_project_scope_correction` |
| Unsafe external scope keeps the approval path | `Unsafe_external_cwd_does_not_expose_project_scope_correction` |
| Prompt and allow parity | `Shell_approval_cases_match_review_table` |

## Source size

The control-flow count uses line starts for `if`, `for`, `foreach`, `while`, and `switch`.

| File | Lines | Control-flow lines |
| --- | ---: | ---: |
| `ToolAccessPolicy.cs` | 1,016 | 59 |
| `ShellPolicyCoordinator.cs` | 671 | 48 |
| `ShellPolicyProjection.cs` | 331 | 11 |
| `IToolApprovalMatcher.cs` | 1,864 | 156 |
| `ScopedShellSafeVerbPolicy.cs` | 366 | 27 |
| `PlatformTemporaryScopePolicy.cs` | 617 | 54 |
| `BashCausalApprovalIntent.cs` | 271 | 18 |
| Total | 5,136 | 373 |

`DispatchingToolExecutor` is the sole coordinator caller.
`ToolAccessPolicy` owns parser, safe-catalog, and temporary-scope dependencies.
`ShellPolicyProjection` alone invokes `BashCausalApprovalIntent`.

`ShellPolicyCoordinator` derives the narrowed approval context twice.
It pairs coverage and trace writes across four separate loops.
`ShellCoverageSet` and the trace builder accept independent writes.

## Complexity and coverage

The baseline uses `dotnet-coverage` 18.10.0 and `crap4dotnet` 0.1.1.
The actor suite passed 3,296 cases with one expected skip.
The security suite passed 913 cases.

Actors had 68.17% line coverage and 44.70% branch coverage.
Security had 61.86% line coverage and 51.79% branch coverage.

| File | Methods | Total CRAP | Average CRAP | Risk methods | Worst method | Complexity | Coverage |
| --- | ---: | ---: | ---: | ---: | --- | ---: | ---: |
| `BashCausalApprovalIntent.cs` | 7 | 55.26 | 7.89 | 0 | `TryProject` | 27 | 96.30% |
| `IToolApprovalMatcher.cs` | 89 | 568.88 | 6.39 | 2 | `ResolveGlobCoveringDirectory` | 13 | 50.00% |
| `PlatformTemporaryScopePolicy.cs` | 33 | 211.05 | 6.40 | 1 | `AllScopesStayWithinTemporaryRoot` | 21 | 57.50% |
| `ScopedShellSafeVerbPolicy.cs` | 10 | 112.80 | 11.28 | 1 | `AllEffectivePathsStayWithinIntent` | 13 | 42.31% |
| `ShellPolicyCoordinator.cs` | 16 | 2,701.01 | 168.81 | 1 | `CompleteAsync` | 50 | 0.00%* |
| `ShellPolicyProjection.cs` | 14 | 37.94 | 2.71 | 0 | `TryCreate` | 9 | 81.25% |
| `ToolAccessPolicy.cs` | 55 | 194.05 | 3.53 | 0 | `AuthorizeInvocationCore` | 22 | 91.67% |

The CRAP tool does not bind async state-machine coverage back to `CompleteAsync`.
The raw value remains a repeatable comparison point.

The actor coverage file reports 90.57% line coverage and 73.88% branch coverage for the coordinator class.

## Compatibility surfaces

The public API baseline is each Release assembly from corpus commit `8b4108aa`.
`dotnet-inspect diff --library BASELINE.dll..CURRENT.dll` performs the comparison.
The first internal-state slice reports no public API change.

| Surface | Baseline contract |
| --- | --- |
| Approval store | V3 global, folder, migration-backup, custom-tool, and detached-snapshot tests remain exact. |
| Actor event | `ToolApprovalRequested_round_trips_all_persisted_context` remains exact. |
| Legacy event | `ToolApprovalRequested_legacy_event_round_trips_without_turn_context` remains exact. |
| Snapshot and history | Approval recovery and passivation tests retain prompt state and one-time semantics. |
| Configuration | Approval modes, safe catalogs, and store JSON retain their current wire values. |
| Prompt | Slack, Discord, Mattermost, CLI, and session prompt tests retain exact options and text. |
| Trace | Fixture rows and `Shell_approval_cases_match_review_table` retain exact order, reason, scope, and outcome. |

No refactor slice may alter these public or durable outcomes without a new approved change.

## Commands

```bash
dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~ShellPolicyEvidenceFixtureTests|FullyQualifiedName~ShellApprovalDispositionMatrixTests|FullyQualifiedName~DispatchingToolExecutorTests'
dotnet test src/Netclaw.Security.Tests/Netclaw.Security.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~ShellApprovalEvidenceContractTests'

dotnet-coverage collect 'dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj -c Release --no-build --no-restore' \
  -f cobertura -o /tmp/netclaw-actors.cobertura.xml
dotnet-coverage collect 'dotnet test src/Netclaw.Security.Tests/Netclaw.Security.Tests.csproj -c Release --no-build --no-restore' \
  -f cobertura -o /tmp/netclaw-security.cobertura.xml
dotnet-crap analyze SOURCE.cs --coverage COVERAGE.xml --min-crap 0 --output REPORT.json
```
