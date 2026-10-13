## Why

The `netclaw-session` spec says `ApproveOnce` creates a session grant. It also assigns approval channel ownership and wait completion to the actor. The [consent policy](../../specs/tool-authorization/spec.md) and current workflow code contradict those statements.

## What Changes

- Correct the approval paragraph in the persisted turn lifecycle requirement.
- State that `ApproveOnce` creates no session or persistent grant.
- Assign the approval channel and typed response outcome to `SessionToolWorkflow`.
- Keep actor journal authority before release of a parent tool call.
- Preserve current parent approval recovery and live-only subagent consent rules.
- Link the canonical [session contract test plan](../../../docs/research/session-contract-test-plan.md).

This change aligns the spec with current behavior for PRD-001 FR-002, FR-003, and FR-011. It adds no MVP capability. Runtime code, tests, and approval policy are out of scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: Correct approval scope, channel ownership, and response order in the persisted turn lifecycle requirement.

## Impact

The change affects only the `netclaw-session` OpenSpec requirement. It changes no code, API, dependency, or stored format. `ApproveOnce` stays exact-call. Parent approval recovery stays durable; subagent approval waits stay live-only.
