## Context

The shell policy now has a typed projection, one actor batch, per-candidate coverage, and an ordered coordinator. The behavior is correct, but policy ownership remains spread across several large classes.

The measured baseline covers these production files:

| File | Lines | Control-flow lines |
| --- | ---: | ---: |
| `ToolAccessPolicy.cs` | 1,016 | 59 |
| `ShellPolicyCoordinator.cs` | 671 | 48 |
| `ShellPolicyProjection.cs` | 331 | 11 |
| `IToolApprovalMatcher.cs` | 1,864 | 156 |
| `ScopedShellSafeVerbPolicy.cs` | 366 | 27 |
| `PlatformTemporaryScopePolicy.cs` | 617 | 54 |
| `BashCausalApprovalIntent.cs` | 271 | 18 |
| **Total** | **5,136** | **373** |

The baseline uses `wc -l` for lines. This command counts control-flow statement lines:

```bash
rg -c '^\s*(if|for|foreach|while|switch)\b' \
  src/Netclaw.Actors/Tools/{ToolAccessPolicy,ShellPolicyCoordinator,ShellPolicyProjection,ScopedShellSafeVerbPolicy,PlatformTemporaryScopePolicy,BashCausalApprovalIntent}.cs \
  src/Netclaw.Security/IToolApprovalMatcher.cs \
  | awk -F: '{ total += $NF } END { print total }'
```

This count is a stable structure metric, not cyclomatic complexity. The final audit also reports method complexity and coverage risk.

The policy path has two phases today. `ToolAccessPolicy` performs synchronous preflight, then caches analysis inside the tool context. `ShellPolicyCoordinator` retrieves that analysis and completes asynchronous policy.

`ShellPolicyCoordinator.CompleteAsync` currently owns many concerns. It validates candidates, checks causal paths, requests actor evidence, mutates coverage, narrows prompts, applies one-time authority, and emits terminal trace facts.

The actor owns session and persistent grant snapshots. The coordinator owns one-time authority and final policy. This ownership split remains correct.

The exact D-case fixtures, 12 adversarial cases, 11 live cases, and the full policy matrix form the behavior contract. The refactor must keep their outcomes and traces unchanged.

## Goals / Non-Goals

**Goals:**

- Replace the context analysis cache with one explicit preflight result.
- Give each ordered policy stage one clear owner.
- Keep one call-local state for candidates, coverage, evidence, trace facts, and terminal status.
- Validate actor output once before any grant coverage applies.
- Compute parser-derived path facts once and reuse them across policy stages.
- Reduce total production lines and control-flow lines below the measured baseline.
- Preserve all public, wire, persistence, prompt, trace, and operator contracts.
- Keep every unknown or invalid internal state fail-closed.

**Non-Goals:**

- Change any allow, prompt, deny, correction, or approval option.
- Expand the reviewed-safe catalog.
- Parse executable-private arguments inside Netclaw.
- Add ShellSyntaxTree grammar or public API.
- Change approval entry schema, actor persistence, or session events.
- Change channel prompts, model guidance, or eval prompts.
- Combine shell policy with generic MCP approval policy.

## Decisions

### 1. One explicit authorization result replaces the context cache

Add an internal `ShellPolicyPreflightResult` family:

```csharp
internal abstract record ShellPolicyPreflightResult
{
    internal sealed record Complete(
        ToolAccessDecision Decision,
        ShellCommandAnalysis? AuthorizedAnalysis)
        : ShellPolicyPreflightResult;

    internal sealed record Continue(
        ShellCommandAnalysis Analysis,
        ToolApprovalContext ApprovalContext,
        ShellExecutionEnvironment Environment)
        : ShellPolicyPreflightResult;
}
```

`ToolAccessPolicy` will return this result for shell calls. The coordinator will receive the exact `Continue.Analysis` value.

An immediate preflight allow may carry `AuthorizedAnalysis`. A preflight prompt or deny must carry no analysis.

The coordinator will return an internal `ShellPolicyAuthorization` value:

```csharp
internal sealed record ShellPolicyAuthorization(
    ToolAuthorizationDecision Decision,
    ShellCommandAnalysis? AuthorizedAnalysis);
```

Only an allow result may carry `AuthorizedAnalysis`. Every allowed parsed shell execution must carry the exact preflight analysis.

`DispatchingToolExecutor` will pass that value directly to `ShellTool`. The stream and non-stream paths will use the same transfer rule.

The current internal `EvaluateAuthorizationAsync` method will return only `Decision` for tests. It will not retain analysis after it returns.

Authorization-only calls will also discard the analysis. No method will read or write analysis through `ToolExecutionContext` cache methods.

Why:

- Data flow becomes explicit.
- One parse result reaches projection directly.
- The same parse result reaches execution directly.
- Context state cannot survive beyond the authorization call.
- Tests can construct and inspect the exact preflight result.

Alternative: retain the cache and wrap it. Rejected because the hidden side channel remains.

### 2. One call-local evaluation state owns mutable policy facts

Add one internal `ShellPolicyEvaluation` class. It exists for one authorization call and never crosses an actor boundary.

It owns:

- the preflight result and `ShellPolicyProjection`;
- candidate IDs and immutable candidate facts;
- one coverage slot per candidate;
- validated actor evidence;
- approval matches;
- persistent-store status;
- the trace builder;
- the terminal decision, when present.

Only methods on this type may change coverage. A stage cannot replace candidate facts or IDs.

Why:

- Mutation stays local and auditable.
- Stages no longer pass parallel lists and maps.
- Coverage and trace updates can occur through one atomic method.
- The class avoids a new immutable array allocation after each stage.

Alternative: return a new immutable state after every stage. Rejected because it adds allocation and code without stronger call-local safety.

### 3. Ordered stages return one closed result family

Each stage returns `ShellPolicyStageResult`:

```csharp
internal abstract record ShellPolicyStageResult
{
    internal sealed record Continue : ShellPolicyStageResult;

    internal sealed record Complete(ToolAuthorizationDecision Decision)
        : ShellPolicyStageResult;

    internal sealed record Fault(ShellPolicyFault Reason)
        : ShellPolicyStageResult;
}
```

The sketch omits constructor guards. Production exposes `Complete.ExactOneTime` as the sole uncovered allow marker.

That marker requires all of these facts:

- the exact one-time key matches the tool and complete approval context;
- syntax or causal eligibility would otherwise return that context as a prompt;
- the decision uses `OneTimeApproval`;
- completion adds no invented candidate coverage row.

No session, persistent, reviewed-safe, or other allow reason may bypass candidate coverage.

The pipeline invokes stages in this order:

1. syntax and candidate validation;
2. protected real and fallback paths;
3. causal directory eligibility;
4. actor grant evidence;
5. approval-exempt side effects;
6. reviewed-safe real-scope coverage;
7. reviewed-safe intent coverage;
8. exact one-time coverage;
9. persistent-store availability;
10. prompt or allow completion.

Actor evidence stays before approval-exempt trace rows to preserve the frozen trace order. Pure side effects never enter the actor request.

Synchronous preflight keeps its current order before these stages:

1. parse validation;
2. hard deny;
3. protected paths;
4. approval mode;
5. candidate construction;
6. noninteractive trust zones.

A terminal result stops the pipeline. A later stage cannot revise an earlier deny or prompt.

Why:

- Order becomes data, not incidental control flow.
- Each stage has a narrow test surface.
- Terminal precedence is visible.
- Internal faults map through one fail-closed path.

Alternative: retain one large method with regions. Rejected because regions do not enforce ownership or terminal precedence.

### 4. Actor evidence has one validation boundary

Add an internal `ValidatedShellGrantEvidence` value. A factory validates the complete `ShellApprovalMatchResult` against projected candidates.

The factory validates:

- persistent-store status and enum values;
- candidate count, IDs, and uniqueness;
- candidate fact identity;
- grant coverage and source consistency;
- canonical session and persistent scopes;
- grant timestamps;
- near-miss count and enum values;
- the unavailable-store restrictions.

No grant enters coverage before this factory succeeds. The coordinator receives only validated evidence or a typed fault.

Why:

- Redundant actor fields remain contained at the protocol edge.
- The coordinator no longer repeats actor consistency branches.
- Test doubles must satisfy the same contract as the actor.

Alternative: remove redundant actor fields now. Rejected because that changes the actor protocol as part of a behavior refactor.

### 5. Parser facts remain separate from policy decisions

`ShellApprovalMatcher` will produce syntax facts and approval candidates only. It will not decide safe policy, grant authority, audience, or prompt options.

`ShellPolicyProjection` will remain the sole bridge from ShellSyntaxTree facts into policy candidates. It will not parse command-specific arguments.

Add one internal `ShellPolicyPathFacts` projection. It contains resolved real scopes, intent scopes, fallback scopes, redirects, and authored filesystem values.

Policy stages consume these facts through `ToolPathPolicy`, temporary-scope policy, and reviewed-safe policy. They do not rescan command text.

Why:

- ShellSyntaxTree remains the syntax authority.
- Netclaw remains the trust and containment authority.
- One projection prevents path-rule drift.
- Command-specific exceptions cannot hide inside completion logic.

Alternative: move executable argument rules into the evaluator. Rejected by the Netclaw constitution and the approved threat model.

### 6. Prompt context has one constructor

Add one method on the evaluation state that returns the prompt context for current uncovered candidates. It preserves the full causal context when required.

This method owns:

- candidate order;
- session-scratch option limits;
- reusable phrase eligibility;
- path-style depth rules;
- causal full-context retention;
- exact one-time key input.

The one-time stage and prompt stage call the same method. They cannot derive different candidate sets.

Alternative: keep two calls to `NarrowShellApprovalContext`. Rejected because future edits can create one-time and prompt drift.

### 7. Trace output observes state transitions

Coverage changes will add their trace row through the same state method. Terminal completion will append exactly one completion row.

The trace builder remains bounded and redacted. Stages cannot write raw commands, arguments, paths, session values, or secrets.

Why:

- Coverage and trace cannot disagree.
- Tests can compare state transitions with trace rows.
- Redaction remains centralized.

Alternative: let each stage write trace rows directly. Rejected because coverage and trace can diverge.

### 8. Compatibility code remains an isolated adapter

The public `IToolApprovalService` shape remains for generic tools and compatibility. One adapter converts its result into raw typed actor evidence before validation.

The main coordinator will depend on the typed grant-evidence interface. New code cannot call the legacy aggregate path directly.

The adapter cannot infer shell identity or widen a phrase. It must preserve exact candidate facts.

Runtime shell calls already enter one coordinator through `DispatchingToolExecutor`. The internal `IShellApprovalMatchService` remains the preferred actor contract.

This change will not remove the public compatibility interface. A later generic approval API version may remove it.

### 9. Complexity reduction is an acceptance gate

The final change must reduce aggregate lines and control-flow lines below 5,136 and 373. The task report will include both counts.

The report will also include method complexity, line coverage, branch coverage, and CRAP risk. It will state the exact tool version and command.

The final review will also inspect:

- production file count;
- duplicate path and coverage helpers;
- coordinator method size;
- public API changes;
- command-name branches;
- test duplication.

The corpus, not a metric, remains the behavior authority. A smaller implementation cannot pass if it removes required checks.

## Actor and persistence boundaries

The actor still owns one immutable session and persistent snapshot per request. The coordinator still sends one candidate batch and performs no second store scan.

One-time authority remains in `ToolApprovalAttempt`. It does not enter the actor request or persistent store.

No new event, snapshot, manifest, or approval entry is added. Recovery and passivation behavior remain unchanged.

The compatibility adapter is process-local. It creates no durable state.

## Failure modes and recovery

| Failure | Required result |
| --- | --- |
| Parser or projection fault | `internal_policy_failure` deny |
| Invalid stage transition | `internal_policy_failure` deny |
| Invalid actor result | `internal_policy_failure` deny |
| Required persistent state unavailable | `approval_store_unavailable` deny |
| Expected unresolved shell input | one-time or deny prompt only |
| Caller cancellation | propagate cancellation |
| Trace limit reached | bounded trace with current terminal result |
| Process restart | current actor and session recovery behavior |

No failure path creates session or persistent authority. No rollback needs data repair.

## Risks / Trade-offs

- **Risk: stage extraction changes precedence** → Lock each terminal overlap with exact matrix cases before code moves.
- **Risk: a new state class hides mutation** → Keep mutation methods narrow and expose immutable candidate views.
- **Risk: metric goals reward compressed code** → Require readable stages, corpus parity, and adversarial review.
- **Risk: the compatibility adapter survives indefinitely** → Add a removal inventory and make new callers depend on typed evidence.
- **Risk: path facts lose source provenance** → Carry parser occurrence identity only inside call-local projection types.
- **Risk: a broad slice becomes hard to review** → Deliver dependency-ordered slices with full parity after each slice.

## Migration Plan

1. Merge the live corpus before production refactor code.
2. Add stage-state types and parity tests with no route changes.
3. Replace the context analysis cache with explicit preflight data.
4. Move actor-result validation behind the typed evidence boundary.
5. Route reviewed-safe, one-time, store, and completion logic through stages.
6. Consolidate path and prompt-context helpers.
7. Remove dead compatibility branches and duplicate helpers.
8. Run full local, Linux, macOS, and native Windows gates after each production slice.

Each slice must preserve corpus bytes and expected outcomes. Normal Git revert rolls back a slice because no durable schema changes.

## Resolved boundaries

- Keep `IToolApprovalService` for generic tools and public compatibility.
- Keep the exact shell adapter until a separate generic approval API version.
- Keep `ShellPolicyPathFacts` internal to `Netclaw.Actors` because it contains causal policy facts.
- Reuse `ToolPathPolicy` and `PathUtility` from `Netclaw.Security`.
- Do not move actor-owned causal or prompt facts into `Netclaw.Security`.
