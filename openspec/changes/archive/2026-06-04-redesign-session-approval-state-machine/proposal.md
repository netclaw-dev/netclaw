## Why

Netclaw approval-paused session work should resume after idle passivation or daemon restart as the same original user request, with the same requester, audience, trust boundary, channel capabilities, and adopted-context safety state. Recent approval hardening has made this work, but the state is now copied through too many shapes in `LlmSessionActor`, making regressions hard to reason about and expensive to test outside live channel integrations.

Source PRDs: PRD-001, PRD-002, PRD-006, PRD-007, PRD-009.

## What Changes

- Define an explicit session approval state machine for live approval waits, idle/cold recovery, redrive, continuation LLM calls, and abandonment.
- Define a durable turn authority context for session turns so approval recovery restores the original request context instead of synthesizing transport metadata after recovery.
- Require approval-paused session work to preserve memory-safety inputs, including third-party adopted-context state, across live and recovered paths.
- Require test seams that verify turn-context construction, persistence, restoration, and projection directly, so every authority field does not need a full cold-recovery integration test.
- Keep sub-agent approval lifecycle redesign out of this change except for identifying context data that can safely be shared with #1212 without sharing actor-specific lifecycle state.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `session-state-machine`: define approval-paused session states and legal resume/redrive/abandon transitions.
- `session-resume`: require cold recovery to restore pending approval turn context and continue under the original request context.
- `tool-approval-gates`: require approval pause persistence to carry durable turn authority context and use it during approval response handling.
- `trust-context-integrity`: require session turn authority context to be explicit, typed, and fail-loud when required fields are missing.
- `netclaw-agent-memory`: require memory recall and curation safety decisions after approval recovery to use the restored turn context.
- `netclaw-session`: update persisted turn lifecycle behavior around approval pause and resumed continuation calls.

## Impact

- Affected code: `LlmSessionActor`, session approval state records, approval events/protobuf mapping, `SessionToolExecutionPipeline`, memory recall/curation entry points, approval recovery tests.
- Security impact: reduces authority-context drift across approval pause/resume; resumed tools and memory curation must use the same trust context as the original request.
- Operational impact: approval prompts should behave the same before and after restart; failures caused by missing required turn context should fail loudly rather than silently falling back to Public or another derived value.
- Compatibility impact: any persistence changes must be additive and backward-compatible with existing journals; old pending approval events must continue to recover safely.
- Out of scope: redesigning `SubAgentActor` approval waiting, changing channel approval rendering, changing persistent approval storage semantics, or doing a broad session actor rewrite.
