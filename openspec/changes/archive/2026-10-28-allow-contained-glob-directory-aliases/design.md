## Context

See [the proposal](proposal.md) for the source PRDs and scope.
Use [the engineering glossary](../../../../docs/spec/GLOSSARY.md) for shared terms.

The current glob walker returns one boolean.
The approval matcher, redirect matcher, and reviewed policy consume that boolean.
The protected-path screen checks the authored pattern lexically.
The Actors path view reduces each pattern to its covering directory.
These consumers need one shared representation of bounded target reach.

## Goals / Non-Goals

The design keeps filesystem facts separate from permission decisions.
The design reuses existing canonical paths, pattern domains, and path-view lists.
The design adds no public API, durable state, grant kind, or root configuration.
The design does not reproduce executable grammar or enumerate exact shell results.

## Decisions

### One call-local reach result owns the walk

`ShellGlobScope` returns complete or unresolved reach with all known target facts.
The facts contain the authored scope, target scopes, and residual patterns.
An unresolved result retains known reach so protection can still deny it.
The existing R13 rule denies failed link resolution, including a cycle.
This denial does not identify a credential path.
Only a complete result can support reusable grants or reviewed-safe coverage.
Explicit Auto consent retains its existing authority and protected-path screens.

`FileSystemAuthority` remains the only reader of link-target text.
The walk reuses its chain, parent-after-link, inspection, and membership rules.
Each target stays inside the original covering boundary.
Only a matched intermediate directory alias uses that wider boundary.
All other final targets retain their immediate directory containment rule.
Known hops retain the original covering boundary.
A leaf chain can cross a sibling and return if its final target retains local containment.
Reusable scope requires a combined directory-visit and link-hop count of 4096 or less.
Ordinary directory-entry scans are outside this count and can increase inspection cost.
Its visited key includes the effective directory and remaining segment index.
It retains authored alias identity separately for grant checks.

### Existing decision owners consume the result

`ToolPathPolicy` owns protection for the authored pattern and known target reach.
`ShellApprovalMatcher` owns argument and redirect grant scopes.
`ShellPolicyPathFacts` carries targets through its existing resolved path list.
`ReviewedSafeShellPolicy` checks existing read authority for all source and target scopes.
`RepositoryIdentity` retains authority over repository and registered-worktree identity.

The ordered flow is schematic and omits unrelated tool gates:

```text
parser pattern facts
  -> bounded filesystem reach, with known facts even if incomplete
  -> authored and target protected-path checks
  -> explicit Auto consent retains its existing policy and protected-path denials
  -> Approval mode keeps unresolved reach under exact consent or unattended denial
  -> complete reach supplies source and target path scopes
  -> existing reviewed policy or grants cover every scope
  -> existing final launch checks
```

All reach data is call-local.
The existing protected sets remain process-local.
The existing grants remain durable without a format change.
No actor message, actor ownership, or persistence contract changes.

### Reject boolean-only relaxation and policy callbacks

A boolean cannot expose target scopes to all consumers.
Policy callbacks make the walker decide permission and allow incomplete callers.
Literal expansion of every match creates a second shell glob engine.
Residual patterns preserve the parser's conservative reach without exact expansion.

## Risks / Trade-offs

- Filesystem mutation after authorization can change reach. The existing final launch checks remain; kernel containment stays out of scope.
- A broad tree can exhaust the bound. The result stays unresolved and cannot support reusable permission.
- An incomplete branch can hide later targets. Known protection still applies, and incomplete reach cannot grant permission.
- A target can reveal an old protection gap. The coordinator reviews every changed approval result before merge.
- Platform link semantics differ. Unsupported host or path styles stay unresolved, and native platform proof stays explicit.

## Migration Plan

Deliver traversal, target protection, all consumer updates, tests, and the specification in one atomic PR.
Update the canonical architecture description only after the code implements this contract.
Update the mapped operations skill and its version in the same diff.
Retain the targeted guidance result and the user-canceled broad attempts.
No stored grant migration or operator action is required.
Revert the atomic PR to restore the prior conservative intermediate-link refusal.
