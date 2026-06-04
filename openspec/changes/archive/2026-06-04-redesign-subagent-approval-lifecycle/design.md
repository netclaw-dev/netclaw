## Context

The merged `redesign-session-approval-state-machine` change for #1213 defines durable approval recovery for `LlmSessionActor`. It also draws the boundary this change should preserve: session approval state is journal/recovery driven, while sub-agent approval waiting is live child-actor lifecycle management.

`SubAgentActor` is ephemeral: it receives one `RunSubAgent`, runs an autonomous LLM/tool loop, returns one `SubAgentResult`, and stops. When one of its tools is approval-gated, the tool executor raises `ToolApprovalRequiredException`. If the parent session supplied an `IParentApprovalBridge`, the sub-agent can route the prompt back to the parent session's interactive approval channel and wait for the human decision.

Recent tactical fixes already introduced the bridge, cancellation tokens, and watchdog suppression while approval is pending. This change formalizes that behavior so implementation can close the remaining gaps without blending sub-agents into the session recovery state machine.

The session `TurnContext`/execution-authority model from #1213 is the intended source of truth for trust-bearing data. If that model is available when this change is implemented, sub-agent spawn should receive the relevant `TurnContext` subset directly. If this change lands before the #1213 implementation, the code should keep the current `RunSubAgent`/`ToolExecutionContext` field mapping aligned with the #1213 field list and avoid inventing a second incompatible authority model.

## Goals / Non-Goals

**Goals:**

- Define a small sub-agent approval lifecycle that is owned by `SubAgentActor`, not by `LlmSessionActor` journal recovery.
- Preserve the parent session's execution authority context when a sub-agent approval prompt is emitted.
- Ensure every pending sub-agent approval wait settles exactly once as approved, denied, timed out, cancelled, or abandoned by actor termination.
- Keep the sub-agent inactivity watchdog from cancelling a legitimate human approval wait, while resuming watchdog enforcement after approval settles.
- Return a terminal `SubAgentResult` and parent `spawn_agent` tool result for every non-crash outcome.
- Add tests for the approval lifecycle's meaningful state transitions and race-prone paths.

**Non-Goals:**

- Persisting `SubAgentActor` state or making sub-agents recover after daemon restart.
- Reusing the session approval redrive state machine from #1213.
- Changing channel-specific approval rendering for Slack, Discord, or Mattermost.
- Changing persistent approval grant matching or storage semantics.
- Allowing sub-agents to spawn other sub-agents.

## Decisions

### Decision 1: Keep sub-agent approval lifecycle actor-local

Sub-agent approval lifecycle state stays inside `SubAgentActor`. The actor can track whether it is running approval-capable tools, waiting for parent approval, resolving the post-approval retry, or completing. It should not write approval wait state to the session journal and should not participate in session cold redrive.

Rationale: sub-agents are child actors tied to a live `spawn_agent` tool call. If the parent session stops, the child should be cancelled and the prompt should expire; it should not become independent durable work.

Alternative considered: reuse the #1213 session approval state model. Rejected because that state exists to recover persisted parent turns, while a sub-agent wait is scoped to a live child actor and parent tool call.

### Decision 2: Reuse session turn authority context, not lifecycle state

The reusable boundary from #1213 is the authority/provenance data needed to emit and authorize a prompt: session id, requester identity, audience, boundary, channel type, cwd/project/session directories, and adopted-context safety flags.

`RunSubAgent` and `ToolExecutionContext` may carry the subset the child needs, and `ParentSessionApprovalBridge` should project it into `ToolInteractionRequest`. That projection is shared in meaning with session approval prompts, but the child actor's wait counters, watchdog state, and cancellation behavior remain separate.

When the session `TurnContext` implementation exists, `SubAgentSpawner` should prefer mapping from that object (or a shared `ExecutionAuthorityContext` inside it) over copying individual `MessageSource` fields. Until then, the implementation should keep all context-copying isolated at the parent-to-child boundary.

Alternative considered: let the sub-agent synthesize a fresh requester or audience. Rejected because it can drift from the parent turn and silently elevate or misroute approval authority.

### Decision 3: Parent bridge is the only interactive approval path

If `IParentApprovalBridge` is present, approval-gated sub-agent tools route through it. If absent, the sub-agent must fail closed: the gated tool must not execute, and the child run completes with a terminal failed result rather than hanging or continuing as if the denial were user-driven.

The bridge emits the same channel-agnostic `ToolInteractionRequest` shape used by parent session tools, including the computed button options, candidate verbs, per-candidate directories, cwd, requester identity, principal, and adopted-context metadata.

Alternative considered: let sub-agents display their own approval UI. Rejected because channels and requester authorization belong to the parent session.

### Decision 4: Approval wait pauses the inactivity watchdog

Waiting for a human approval decision is intentional suspension, not sub-agent inactivity. The sub-agent should cancel or suppress the inactivity timer while one or more approval waits are active, then re-baseline the timer when the last wait completes. The streaming `spawn_agent` tool call in the parent session must receive the same signal, otherwise the parent tool-call watchdog can cancel a healthy child that is only waiting on a human.

Parallel tool calls may produce parallel approval waits. The actor therefore needs a count or equivalent keyed state rather than a single boolean. Underflow or double-completion is a bug and should be logged loudly rather than silently clamped. The parent stream watchdog should suspend on the child "awaiting approval" activity and resume on the next non-suspending activity or terminal completion.

Alternative considered: keep the watchdog active while waiting. Rejected because it turns slow human approval into a false sub-agent timeout.

### Decision 5: Approval outcomes produce normal tool-loop inputs

Approved decisions retry the exact blocked tool call with retry-local approval state. Denied and timed-out decisions produce tool-result messages that explain the denial and are returned to the sub-agent LLM like any other tool result. External cancellation, parent stop, and actor termination cancel pending waits and complete the sub-agent once with failure if the caller is still awaiting a result.

Approved-once retry state must be per tool call and per retry. It must not leak to sibling calls, later iterations, or later sub-agent runs.

Alternative considered: terminate the sub-agent immediately on denial. Rejected because a denied tool is still a valid tool result; the sub-agent may be able to finish with a useful explanation or alternate approach.

### Decision 6: Completion is idempotent

Sub-agent completion should be guarded so stale timeout, cancellation, or tool-failure messages cannot send duplicate `SubAgentResult` messages. The first terminal path wins; later terminal messages are ignored.

Alternative considered: rely on actor stop ordering. Rejected because thread-pool continuations and mailbox messages can race around actor stop.

## Risks / Trade-offs

- [Risk] A parent-session approval response can arrive after the sub-agent has been cancelled. -> Mitigation: cancellation must cancel the bridge wait; parent response handling should treat missing pending calls as expired rather than executing the child tool.
- [Risk] Approval wait counters can drift on exception paths. -> Mitigation: start/complete notifications must be paired with try/finally and tested for cancellation and parallel waits.
- [Risk] Denial as a tool result lets the sub-agent continue and potentially ask again. -> Mitigation: the approval policy still gates every retry, and max tool iterations bound loops.
- [Risk] Sharing too much context with #1213 creates a common model that fits neither actor. -> Mitigation: share only execution authority data; do not share lifecycle state or persistence mechanics.

## Migration Plan

1. Add the #1212 OpenSpec delta requirements for `netclaw-subagents` and `tool-approval-gates`.
2. Tighten `SubAgentActor` approval-gated no-bridge and denied/timed-out paths so missing bridges produce terminal failed results, denied/timed-out approvals produce explicit tool results, and gated tools never execute without approval.
3. Add or update tests for approve, deny, timeout, no bridge, cancellation, parallel waits, and approve-once isolation.
4. Update runbook or operational guidance only if user-visible sub-agent approval behavior changes.

Rollback is straightforward: revert the actor/test changes and remove this change directory before archive. No persisted data migration is expected because sub-agent approval waits are not durable.

## Open Questions

- Should the implementation expose a named `SubAgentApprovalLifecycleState` type, or is the existing wait counter plus watchdog enum sufficient once covered by tests?
- Should a denied sub-agent approval always continue the LLM loop, or should some high-risk denial categories terminate the sub-agent immediately in a future change?
