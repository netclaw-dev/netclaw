## Why

Callers still select project corrections, and shell Auto skips temporary and project advice.
The common coordinator must own these decisions before callers deliver the result.

## What Changes

- Collect compatible shell corrections from existing policy and invocation facts in the coordinator.
- Deliver project advice through the same result as native and temporary advice.
- Apply corrections before shell Auto approval. Preserve existing grant and exact retry checks.
- Remove replaced caller policy branches. Keep actor lifecycle and persistence duties.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: coordinator-owned correction collection and precedence before Auto.

## Impact

Source PRDs: PRD-002 and PRD-006. Implementation plan: tasks 4.2 and 4.3.
Affected code includes the coordinator, access policy, correction delivery, parent pipeline, and child actor.
Shared process startup, storage migration, public API changes, and rollout are outside this PR.

## Security and operational impact

Hard denials remain authoritative. Advice grants no authority and performs no tool action.
Parent and child retain their distinct recovery and approval lifetimes.
The runbook and operational skill will describe correction collections under Auto.
