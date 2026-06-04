## Why

Sub-agents inherit the parent session's tool policy and can encounter interactive approval gates while running as child actors. The current behavior needs an explicit lifecycle contract so approved, denied, cancelled, timed-out, or parent-terminated approval waits cannot leave hung sub-agents, orphaned prompts, or incomplete `spawn_agent` tool calls.

Source PRDs: PRD-001, PRD-002, PRD-006, PRD-007, PRD-009.

This change builds on the merged `redesign-session-approval-state-machine` OpenSpec for #1213. It reuses the execution-authority context boundary where semantics match, but keeps sub-agent approval waiting as a separate actor/watchdog lifecycle rather than session journal redrive state.

## What Changes

- Define the sub-agent approval lifecycle for tool calls that require parent-user approval while a `SubAgentActor` is running.
- Require pending sub-agent approvals to be owned by the parent session's `spawn_agent` call and to settle exactly once on approve, deny, cancellation, timeout, or actor termination.
- Require sub-agent inactivity watchdogs to treat approval waits as intentional suspension rather than progress or a wedge.
- Require parent-session notifications and terminal tool results for every approval outcome, including expired prompts and abandoned runs.
- Require implementation tests for approve, deny, cancellation, parent stop, timeout, and late approval response paths.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `netclaw-subagents`: define sub-agent approval wait states, parent ownership, watchdog behavior, and terminal result semantics.
- `tool-approval-gates`: define how approval prompts and responses are correlated for sub-agent tool calls without reusing session recovery/redrive state.

## Impact

- Affected code: `SubAgentActor`, `spawn_agent` tool execution, sub-agent tool execution context, parent session notification/result handling, approval response routing, sub-agent lifecycle tests.
- Security impact: sub-agent tools must never execute without the same requester-only approval checks and grant-persistence rules as parent-session tools; missing authority context must fail loudly.
- Operational impact: operators should see clear completion or expiration behavior instead of sub-agents silently hanging behind stale approval prompts.
- Compatibility impact: no persistence migration is expected for this change; sub-agent approval waits are scoped to live actor execution and parent `spawn_agent` call lifecycle.
- Out of scope: redesigning `LlmSessionActor` durable approval recovery, changing channel approval rendering, changing persistent approval grant matching, adding sub-agent session persistence, or merging session and sub-agent approval state machines.
