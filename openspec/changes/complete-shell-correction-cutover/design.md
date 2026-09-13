## Context

At 77f825a3, parent and child select project advice from an approval exception.
Auto completes before temporary and project detection. See proposal.md for scope.

## Decisions

ShellPolicyCoordinator collects facts through the existing detectors and registry.
It reuses canonical shell analysis, invocation scope, and the current declaration tool.
ToolCorrectionDelivery handles project advice alongside native and temporary advice.
No caller supplies a correction list. No new authority store or optional security dependency is required.

Schematic flow; each stage retains its existing validation and cancellation checks:

```text
preflight hard checks -> collect compatible facts
  native replacement -> correction collection
  Auto -> correction collection or allow
  Approval -> existing grant, reviewed-safe, and exact one-time checks
    uncovered -> correction collection or approval
    covered -> allow
```

The coordinator owns call-local decisions. Parent persistence commits results and approval state.
The child retains live approval transport and its lifecycle state.
An exact temporary retry suppresses that advice once; it grants no authority.
Native advice starts a new authorization attempt and arms no shell retry key.

## Risks / Trade-offs

Project-only migration is smaller but leaves Auto inconsistent and requires another change to the same policy path.
Capability eligibility differs: project declaration can work without a bridge; temporary advice requires its existing interactive capability.
Preserve full and partial grant evidence. Do not expand correction eligibility through executable-specific syntax.
Pure collection tests cannot prove process containment. Shared checked startup remains a separate PR.

## Delivery

One cohesive PR changes the coordinator, delivery consumer, tests, runbook, and operational skill.
Required evidence includes parent/child parity, hard denial, Auto, exact retry, grants, and recovery.
Run focused tests, the actor suite, Slopwatch, headers, and applicable evals.
Open a draft PR for review. Merge and rollout require later authorization.
No persistence migration applies. A source revert cannot undo earlier external actions or grants.
