## Why

A child currently holds its start call until completion and delays the parent's response to later input.
The approved second PR gives the parent durable child ownership, explicit controls, and later results.

Source requirements: [PRD-001](../../../docs/prd/PRD-001-netclaw-mvp.md), FR-001, FR-002, FR-003, FR-005, FR-007, FR-011, and FR-017.
Routed scheduled skills also trace to FR-012 and [PRD-008](../../../docs/prd/PRD-008-scheduling-and-periodic-tasks.md).
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## What Changes

- **BREAKING:** `spawn_agent` and every routed skill activation return accepted run identifiers instead of final child output.
- Persist child acceptance, original authority, terminal receipts, delivery admission, and the identifiers that prevent duplicate work.
- Give each accepted run a lifetime independent of its start-tool token and later parent turns.
- Return later child results as attributed tool-origin content under the original authority.
- Add one deferred parent control: `check_agent_run(run_id, cancel=false)`.
- Retain the original requester for child approval prompts after the start call ends.
- Cancel task execution before a five-second framework-only grace period preserves recorded partial evidence.
- Defer idle passivation while live children or undelivered terminal results require their owner.
- Report restart loss explicitly. Do not replay interrupted child tools or relaunch lost children.
- Preserve the first PR's recurrence contract and removed tool ceilings.
- Use **approval prompt** as the glossary's displayed term. Keep existing code identifiers and the legacy anchor unchanged.

In scope: background execution, parent status/cancel, minimum runtime instructions, lifecycle tests, and targeted subagent evals.
Out of scope: private agent messages, questions, peer discovery, steering, automatic relaunch, and general workflow infrastructure.
The third PR owns the coordination skill and general worker profile.

## Capabilities

### New Capabilities

- `background-subagent-runs`: Durable start acceptance, later terminal delivery, cancellation, recovery, and their evidence gates.

### Modified Capabilities

- `netclaw-subagents`: Child execution ends through the owner acknowledgement contract and operation health checks.
- `skill-execution-routing`: Every routed activation uses the same background start and later-result contract.
- `session-state-machine`: Live child ownership and terminal delivery defer idle passivation.
- `tool-authorization`: Child prompts retain original authority; parent controls enforce session and role ownership.
- `progressive-tool-disclosure`: The child control stays deferred and remains unavailable to children.
- `netclaw-session`: Session context exposes attributed run status without creating a new authority source.

## Impact

Reuse `ChildRunScope.Authority`, `ToolRunScope`, `TurnContextRecord`, the owner-session seam, and existing session storage.
Update the spawner, child actor, parent actor, routed skill consumers, approval bridge, session state, snapshots, and framework wire schema.
Update runtime `AGENTS.md`, `subagent-authoring`, `netclaw-operations`, and the authorization architecture document in the runtime PR.
The first PR's frozen `per-call-tool-recurrence` contract is a dependency. This package does not redefine its detector.

### Security and operational impact

Acceptance does not grant extra tool or path authority. Child output cannot answer an approval prompt or create a trusted automation input.
New lifecycle records require upgrade and rollback proof. No actor reference, token, callback, or prepared actor property enters durable records.
Five seconds is the accepted initial framework-only cancellation grace default. This change adds no operator configuration knob.
