## Why

PRD-002 and PRD-006 require fail-closed, explainable shell authorization. Before this refactor, seven core files contained 5,136 lines and about 373 branch points.

The live corpus now supplies a stable contract for a behavior-compatible refactor. This change reduces policy complexity before more exceptions make the evaluator harder to audit.

## What Changes

- Introduce one typed evaluation state for candidates, coverage, actor evidence, trace facts, and the persistent-store result.
- Make one direct evaluator own the documented policy order.
- Move actor-result validation behind one protocol boundary.
- Consolidate repeated prompt-context, path-fact, coverage, and terminal-decision logic.
- Shrink shell-specific branches inside `ToolAccessPolicy` and `ShellApprovalMatcher`.
- Preserve every current allow, prompt, deny, correction, trace, and grant outcome.
- Use the exact D-case, adversarial, and live regression fixtures as equivalence tests.
- Deliver the refactor as small dependency-ordered production slices.
- Report the frozen baseline, post-corpus peak, and safe final footprint without hiding new files.

In scope:

- `ShellPolicyCoordinator`, `ShellPolicyProjection`, and their internal policy state.
- Shell preflight seams inside `ToolAccessPolicy`.
- Shell candidate projection seams inside `ShellApprovalMatcher`.
- Shared path, coverage, actor-result, and terminal-decision helpers.
- Tests and operator documentation that explain the internal flow.

Out of scope:

- New reviewed-safe phrases or changes to current catalog authority.
- New grant, persistence, prompt, audience, or approval-mode semantics.
- New ShellSyntaxTree grammar or public API.
- Approval-store migration or wire-format changes.
- Channel UI changes, new eval behavior, or command-specific production rules.

## Capabilities

### New Capabilities

- `shell-policy-evaluator-architecture`: Defines the direct evaluator, ownership boundaries, equivalence contract, and fail-closed internal protocol.

### Modified Capabilities

None. The refactor preserves current `tool-approval-gates` behavior.

## Impact

The change affects internal code in `Netclaw.Actors` and `Netclaw.Security`. Public tool APIs, persisted events, configuration, approval entries, prompts, and traces retain their current contracts.

Security impact is neutral by design. Unknown facts, invalid call-local invariants, internal faults, protected paths, and unavailable required authority remain terminal deny or prompt under the current contract.

Operational impact is limited to ordinary binary rollout. No configuration edit, approval-store reset, database migration, or session migration is required.
