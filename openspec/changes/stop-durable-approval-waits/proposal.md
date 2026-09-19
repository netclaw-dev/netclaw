## Why

Source PRD: [PRD-001 FR-016](../../../docs/prd/PRD-001-netclaw-mvp.md#fr-016-config-change-restart-coordination).

A session can wait for a tool approval until the daemon reaches its 190-second stop limit. The approval already has a journal record. Netclaw can stop that wait after the tool task stops.

## What Changes

- Let a session stop promptly when every unfinished tool call waits on a durable approval.
- Require each completed sibling tool call to have a journaled result before the session stops.
- Require the tool task to stop before the actor acknowledges drain.
- Keep the current bounded drain path for model calls, active tools, unresolved results, accepted buffered input, and deferred approval responses.
- Preserve the original approval and its turn authority after cold recovery.

This slice does not add automatic session wakeups, new input admission records, or a shorter global stop limit.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: A graceful daemon stop can passivate a session that waits only for durable tool approvals.

## Impact

The change affects `LlmSessionActor`, its tool task boundary, the session-resume contract, and actor tests. It adds no public API or configuration property.

### Security and operational impact

The actor cannot stop before every unfinished call has a journaled approval. It cannot replay a call with an uncertain effect. The daemon retains its current timeout for other states.
