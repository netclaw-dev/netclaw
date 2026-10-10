# SPEC-016: Tool Liveness And Stall Detection

Source PRDs: `PRD-001`, `PRD-006`

Use [the engineering glossary](GLOSSARY.md) for shared terms.
This document describes operation health and the background child lifecycle.
Testable rules live in [netclaw-tools](../../openspec/specs/netclaw-tools/spec.md),
[netclaw-subagents](../../openspec/specs/netclaw-subagents/spec.md),
[background-subagent-runs](../../openspec/specs/background-subagent-runs/spec.md), and
[turn-loop-governance](../../openspec/specs/turn-loop-governance/spec.md).

## Purpose

Long tasks can continue while each operation remains healthy.
Task age, token totals, and tool-call counts do not define a task-lifetime deadline.
Explicit cancellation, operation health checks, authorization, and exact recurrence protection still apply.

## Decision Owners

| Owner | Decision | Data lifetime |
| --- | --- | --- |
| Tool execution pipeline | Apply the resolved tool's operation-health contract | Call-local |
| `SubAgentActor` | Detect stalled child model/tool operations and close task dispatch | Actor-local |
| `LlmSessionActor` | Commit acceptance, cancellation, dispatch closure, terminal receipt, and later result delivery | Durable journal and snapshots |
| `ChildRunDispatch` | Reject new model and tool dispatch after local closure | Run-local; live-only |
| `LlmSessionActor` child approval handler | Retain the request and resolution under the accepted run's original authority | Durable `BackgroundChildRun.Approvals` ledger |
| `ParentSessionApprovalBridge` | Correlate a live child approval wait with its owner and original requester | Run-local waiter |
| Inference backend | Queue and admit provider requests according to backend capacity | Backend-owned |

Netclaw adds no inference-slot limits, reservations, parent preemption, or cooperative provider scheduler.
A held child request does not prevent the parent from issuing its own request.
The backend can queue either request. Local parent status and cancellation do not require an inference slot.

## Opaque Operations

Opaque tools retain a wall-clock operation deadline.
Output does not extend that deadline.
Most MCP calls, `web_fetch`, and `shell_execute` use this contract.
For shell, `_timeout_seconds` or `Session.ToolExecutionTimeoutSeconds` bounds the process operation.

Positive example: a quiet shell operation completes before its deadline.
Negative example: a shell process prints forever but still stops at its operation deadline.

## Child Operation Health

A child owns its operation health rather than the parent's generic tool watchdog.
The pipeline does not add a first-item or inter-item watchdog for a self-monitoring operation.
The child distinguishes:

- Wait for the first substantive model output.
- Inactivity between model deltas after output starts.
- A no-progress deadline that content-free keepalives cannot refresh.
- Tool operation deadlines and explicit cancellation.
- An authorized approval wait, which is intentional suspension rather than a model stall.

A completed healthy operation can lead to another operation without a total task-time limit.
A stalled operation returns an explicit failed terminal result through the owner.
Exact recurrence can produce a partial terminal result without a final model request.
No static parent or child tool-call ceiling remains.

Positive example: a child completes useful work through more than the former 30 feedback rounds.
Negative example: heartbeat-only output cannot keep a child model operation alive indefinitely.

## Acceptance And Lifetime

All explicit and routed child starts use durable acceptance.
The owner commits acceptance before child execution and before the accepted response.
The response identifies the accepted run; it does not wait for child completion.
After acceptance, a run-owned lifetime replaces dependence on the start call's token.
An ordinary later parent message does not cancel that run or its approval prompt.

Schematic sequence; normal authorization and persistence gates remain required:

```text
prepare the scoped start from its admitted input and call
commit acceptance
start the run-owned child
return canonical accepted identifiers
continue independent parent work
receive and durably acknowledge the child's terminal result
admit one attributed parent continuation after the original start batch settles
```

A failed acceptance commit starts no child and returns no false acceptance.
Equivalent start retries return the same run. A conflicting start digest fails without mutation.
A start-tool timeout after acceptance does not cancel the accepted run.

## Cancellation And Partial Evidence

The parent explicitly loads `check_agent_run` for authorized status or cancellation.
The owner commits cancellation admission before it reports that fact.
The owner closes the run's local model, tool, and approval-retry admission gate.
It requests token cancellation and waits for any synchronous dispatch prefix to exit.
The owner then commits the dispatch closure fact.

That committed fact proves that no new local task work can start.
It does not prove that an earlier external effect stopped or that its outcome is known.

After closure, framework-only finalization uses a separate token and a five-second grace deadline.
It can preserve confirmed checkpoints and atomically write a report inside the assigned run artifact directory.
It cannot request model output, run task tools, edit the project, call an external service, or create approval authority.
A failed write or grace expiry preserves the last confirmed checkpoint and an explicit reason.
Cancellation remains a failure outcome even when useful partial evidence exists.

The owner commits any unresolved child prompt disposition before the terminal receipt.
Token cancellation can resolve a prompt earlier. Durable closure does not require prompt resolution to commit first.
A late child terminal payload cannot replace the last committed checkpoint or supply a successful parent activity merge.

Positive example: cancellation returns confirmed file activity and an existing partial artifact without another model call.
Negative example: late provider output or an approval answer cannot start task work after dispatch closure.

The first committed terminal or cancellation admission determines the terminal outcome.
Duplicate child results create no second delivery or parent continuation.
Terminal persistence failure produces no successful receipt acknowledgement.

## Recovery, Passivation, And Drain

Idle passivation defers live children and pending delivery.
`PrepareForDaemonRestart` admits child cancellation and waits for bounded framework finalization before the drain acknowledgement.
An abrupt actor stop closes live dispatch and requests token cancellation without a durable finalization guarantee.
Owner restart marks unresolved accepted runs `Lost`; it does not recreate or resume child execution.
A prior durable cancellation retains its cancelled outcome.
Committed terminal facts and pending delivery remain available through journal and snapshot recovery.
Old child approval prompts expire visibly after loss or restart.

The framework restores committed evidence and truthful unknown outcomes.
The model decides its next action from that recorded session.
The framework does not automatically replay uncertain effects or impose an operator-review policy.
No physical external exactly-once guarantee follows from local closure or journal recovery.

## Verification

Use deterministic provider and tool barriers. Do not use sleeps to infer closure.
Required evidence includes:

- Acceptance commit before execution or accepted output, including failed and held writes.
- Equivalent start deduplication and conflicting-digest rejection.
- A held child plus independent parent work and usable local controls.
- Distinct cancellation-admission and dispatch-closure acknowledgements.
- No late model, tool, grant, or approval retry after closure.
- Confirmed partial evidence after report failure or grace expiry.
- Both durable terminal/cancel race orders and duplicate terminal delivery.
- Snapshot and journal recovery with explicit loss and no child relaunch.
- Original authority and parent detector evidence across later input and sibling results.
- Opaque wall-clock deadlines and child health checks that retain their distinct owners.

Targeted model evals supplement these deterministic contracts.
Retain raw evidence and classify instrumentation, prompt, capability, and infrastructure failures separately.
A model claim or an artifact path does not replace an independent effect or file-content check.
