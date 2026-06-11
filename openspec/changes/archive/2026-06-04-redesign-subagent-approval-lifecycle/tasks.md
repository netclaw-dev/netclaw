## 1. Context And Contract

- [x] 1.1 Verify current sub-agent approval code maps the parent session authority context from the #1213 field list without synthesizing requester or audience defaults.
- [x] 1.2 Keep parent-to-child context copying isolated at the `SubAgentSpawner` / `RunSubAgent` boundary so a future `TurnContext` implementation can replace the interim mapping cleanly.

## 2. Approval Lifecycle Implementation

- [x] 2.1 Ensure approval-gated sub-agent calls without a parent approval bridge fail closed with a terminal failed result, and never execute the gated tool.
- [x] 2.1a Ensure approval-gated sub-agent calls with incomplete parent requester/principal context fail closed before emitting prompts.
- [x] 2.2 Ensure approved decisions retry only the original blocked tool call with retry-local approval state.
- [x] 2.3 Ensure denied and timed-out decisions produce tool-result messages and do not execute the gated tool.
- [x] 2.4 Ensure external cancellation, parent stop, and stale terminal messages complete the sub-agent at most once and cancel pending approval waits.
- [x] 2.5 Ensure the inactivity watchdog remains paused while one or more parent approval waits are active and is re-baselined when the last wait settles.
- [x] 2.6 Ensure the parent `spawn_agent` streaming tool watchdog is suspended while the child is waiting for human approval and resumes afterward.
- [x] 2.7 Ensure live-session approval responses for prompts with no live approval wait are rejected as expired before stale work can execute.
- [x] 2.8 Ensure bridged sub-agent approval call ids are parent-scoped and unique per request, even when child-local tool call ids collide.
- [x] 2.9 Ensure durable approval grants are written only after the live approval wait is atomically claimed.
- [x] 2.10 Ensure direct parent-session approval waits are cancellable by the active tool-execution token.

## 3. Tests

- [x] 3.1 Add or update sub-agent actor tests for no-bridge fail-closed approval behavior.
- [x] 3.2 Add or update sub-agent actor tests for approve-once isolation across sibling calls, later iterations, and later sub-agent runs.
- [x] 3.3 Add or update sub-agent actor tests for denied and timed-out approval decisions.
- [x] 3.4 Add or update sub-agent actor tests for cancellation and terminal-race idempotence during approval waits.
- [x] 3.5 Add or update sub-agent actor tests proving parallel approval waits pause the watchdog until all waits settle.
- [x] 3.6 Add or update tests for missing parent authority context, cancelled approval-channel waits, parent watchdog suspension, and session-integrated sub-agent approval authority.
- [x] 3.7 Add or update tests for duplicate approval wait rejection, parent-scoped sub-agent call ids, claimed wait lifecycle, and cancellable direct approval waits.

## 4. Validation

- [x] 4.1 Run targeted sub-agent and approval-gate tests.
- [x] 4.2 Run `openspec validate redesign-subagent-approval-lifecycle --strict`.
- [x] 4.3 Run `dotnet slopwatch analyze`.
- [x] 4.4 Run `./scripts/Add-FileHeaders.ps1 -Verify`.
