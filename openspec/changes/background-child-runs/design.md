## Context

The original baseline is `2e6bc4f014dc96b606566709df1bd11f90ecf34a`.
The branch now includes PR 1 at `893435238b7f18e1ba7f45e181255bab95225ad1`.
The dependency is `per-call-tool-recurrence`. Its runtime types remain unchanged by this contract clarification.
PR 1 retains its open proof gates. This rebase does not establish background runtime behavior.
See [proposal.md](proposal.md) for the objective and PRD traceability.
The approved [plan](../../../.systematize/plans/background-subagents/plan.html) owns delivery sequencing when that artifact is present in the combined branch.

Current code establishes these seams:

- `SubAgentSpawner.SpawnRunAsync` creates `ChildRunScope`, waits through `Ask`, and enriches the terminal result.
- `ToolRunScope.SpawnChildActor` delegates actor creation to the parent. It does not express durable run acceptance.
- `SkillLoadTool` and the parent's slash route await final child output.
- `ParentSessionApprovalBridge` binds the original requester, but its child prompt waits remain live-only in the start scope.
- `SubAgentActor` stops immediately after its terminal reply. It has no durable resume state or compaction.
- `SessionState.ActiveBackgroundJobs` belongs to shell jobs. Its delivery dedup is not a durable child-result contract.
- `RoleBasedFailoverRouter` shares the main pipeline with compaction unless a distinct compaction model exists.

Relevant engineering references: `SPEC-002`, `SPEC-016`, and the [glossary](../../../docs/spec/GLOSSARY.md).
The old subagent wall-clock timeout statement conflicts with the implemented per-operation watchdog.
This package changes that statement without introducing a task-lifetime ceiling.

## Goals / Non-Goals

**Goals:**

- Free the parent tool phase after durable start acceptance.
- Preserve original run authority through every later parent turn and approval prompt.
- Guarantee duplicate start prevention and durable result admission within one session journal.
- Stop new child dispatch before a confirmed cancellation cutover.
- Preserve framework-recorded evidence without another model call.
- Report explicit restart loss and truthful delivery failures.

**Non-Goals:**

- A child that resumes after a daemon restart.
- Agent messages, questions, peer discovery, or live steering.
- A workflow engine or a new test platform.
- Provider-slot limits, request-capacity reservations, or parent/child inference preemption.
- Exactly-once external side effects from a model continuation that crashes midway.
- Another tool-count, task-age, or task-token ceiling.

## Decisions

### 1. Reuse authority and the owner-session seam

Evolve the existing owner-session callback into typed start and control operations.
Keep actor references and prepared actor properties inside the adapter.
Reuse `ChildRunScope.Authority`, the admitted `TurnContextRecord`, and existing session storage bindings.
Do not add parallel audience, path-root, requester, or channel fields to the live start contract.

Persist the existing serializable turn record plus run identifiers, the initial working snapshot, and lifecycle facts.
Do not serialize `ToolRunScope`, actor references, callbacks, cancellation tokens, or provider clients.
The child retains the same inherited policy and profile contracts as PR 1.

Alternative: put agents in the shell-job manager. Its shell launch and grant contracts do not describe child authority or results.

### 2. Durable acceptance fixes one start identity

The parent owns run identifiers and the acceptance ledger.
The pipeline stamps correlation before asynchronous setup. The model cannot supply or replace it.

Use these exact logical keys:

| Start path | Durable key |
| --- | --- |
| `spawn_agent` or routed `skill_load` | Owner session ID, original admitted turn ID, original tool call ID |
| Slash activation | Owner session ID, admitted input ID, original turn ID, framework activation slot `0` |
| Scheduled slash activation | The slash key from the already admitted scheduled input; reuse its persisted occurrence correlation |

Do not derive keys from task text, current turn state, actor names, or a fresh ID on retry.
Retain the original admitted input IDs as provenance even when a tool start belongs to a buffered multi-input turn.
Compare a bounded digest of canonical start arguments and resolved profile/overlay content against the committed acceptance.
A reused key with conflicting content returns `start_conflict`. It cannot mutate or duplicate the accepted run.
Duplicate scheduled ingress follows existing ingress authority. This contract does not promise indefinite dedup of separately admitted schedule occurrences.

The parent persists acceptance before actor creation, model/tool execution, or an accepted response.
Concurrent duplicates share one pending persistence operation and receive the same committed run ID.
After persistence, a start failure becomes a terminal failure of that accepted run.
Before persistence, failure returns an explicit rejection and creates no child.
If the accepted response disappears, a retry returns the recorded run. Recovery never creates the child again.

Schematic; validation, authorization, and persistence failure paths are abbreviated:

```text
Start(key, preparedScope, argumentsDigest)
  -> reject conflicting key or unavailable admission
  -> persist Accepted(runId, originalContext, key, argumentsDigest)
  -> create child with a run-owned lifetime
  -> reply Accepted(runId, state, controlTool)

Retry(same key, same digest)
  -> reply with the existing run and current recorded state
```

### 3. The accepted run outlives its start call

The start token governs setup and acceptance only.
After acceptance commits, its cancellation or disposal cannot cancel the run.
A later parent turn also cannot replace the run's authority or lifetime token.
Explicit run cancellation, explicit parent stop, and owner loss remain terminal boundaries.
Idle passivation defers while a run remains live or a durable result still needs continuation admission.

All first-party routed starts use this contract. There is no permanent synchronous routed exception.
Inline skills remain inline. Routed failures remain explicit and cannot fall back to inline execution.
No later terminal result uses the completed start tool's activity channel.

### 4. Machine-actionable outputs describe lifecycle separately from outcome

The accepted output contains `run_id`, `scope_id`, `state`, and `control_tool`.
Its canonical tool body is one JSON object with four required string fields.
Each required field occurs once. Run and scope identifiers use their nonempty canonical owner values.
Those strings use the existing `SubAgentRunId.Value` and `SubAgentScopeId.Value` representations.
The first response reports the committed initial state `Accepted`.
That state describes admission. The current status can advance before the parent observes this response.
An equivalent retry can report the current recorded state with the same identifiers.

Example; identifier values are illustrative:

```json
{
  "run_id": "e1c231a7977f4024903660ad451bb044",
  "scope_id": "console/example/subagent/summarizer/e1c231a7977f4024903660ad451bb044",
  "state": "Accepted",
  "control_tool": "check_agent_run"
}
```

The owner supplies these facts only after acceptance commits. Child text cannot supply them.
`spawn_agent` and routed `skill_load` return the bare JSON body without prose or Markdown fences.
The adapter formats it once for `ToolResultOutput.Result` and the model's `FunctionResultContent.Result` string.
Both consumers receive the same body. Field order and insignificant whitespace have no semantic meaning.
Direct slash adapters format human acknowledgement prose from the same owner facts.
They cannot reconstruct the owner facts from that prose or replace identifiers, state, or the control name.
This tool representation does not replace framework-owned journal or snapshot serialization.

Negative example: `{"run_id":"","state":"Completed"}` is not a valid first acceptance.
An uncommitted object with valid-looking fields is also not acceptance.
The consumer reports the invalid acceptance rather than inferring it from a child's final output.
Later terminal results retain their distinct delivery correlation; they do not replace this completed start result.

The control tool is `check_agent_run(run_id, cancel=false)` and uses the existing parameter-name conventions.
It remains Deferred under the existing non-shell tool policy. Children cannot discover, load, or dispatch it.
The current parent invocation must pass normal policy and match the owner session and original eligible requester context.
A mismatched child, session, or requester receives a non-disclosing denial.
The original context is the one authority source for this comparison; do not create a second requester ledger.
Root confirmed this conservative first-release boundary under the approved other-speaker negative controls.
Broader control by another eligible same-session user requires a later explicit policy decision.

States are `Accepted`, `Running`, `Cancelling`, `Completed`, `Partial`, `Failed`, `Cancelled`, and `Lost`.
These states are lifecycle facts. They do not expand `SubAgentRunOutcome` implicitly.
Status includes the state, cancellation admission, dispatch-closure state, and the recorded terminal result when present.
Accepted and live status outputs do not claim task completion.

Preserve the existing wire outcome names `Completed`, `Partial`, and `Failed`.
A cancelled completion keeps `Success = false`, wire outcome `Failed`, and reason `CancelledByParent`.
Extend the typed cancelled completion with a confirmed working-context delta.
Its lifecycle state is `Cancelled`; its useful evidence does not turn it into `Completed` or successful `Partial`.
`Lost` also uses a failed wire outcome with an explicit owner-loss reason.
Add new reason fields and durable record variants through the framework schema and conversion contracts.

### 4a. Reuse existing owners for lifecycle acknowledgements

The table names current mechanisms. It does not prescribe new C# message types.
New acknowledgements need the run correlation that a session-only `CommandAck` cannot carry.
The owner acceptance and terminal records are durable. The JSON body is a call-local view of those committed facts.
Child results, dispatch admission state, actor references, and cancellation sources remain live runtime state.

| Boundary | Existing owner and mechanism | Required extension |
| --- | --- | --- |
| Acceptance before execution | `LlmSessionActor` owns the journal. Its `AdmitInput` callback applies a committed event before work and acknowledgement. | Use the same post-commit pattern for child acceptance through the existing owner-session adapter. Create no child or accepted body before that callback. |
| Start-token independence | The owner adapter already creates the child. `SubAgentActor` already owns execution and external cancellation sources. | Bind accepted child cancellation to the owner run lifetime. Remove the start token from that lifetime after commit. Keep pre-admission setup cancellation effective. |
| Durable terminal acknowledgement | `SubAgentActor.Complete` already creates one typed result. The parent already applies events in `Persist` callbacks. | Retain the child result and acknowledge its run correlation only after the owner commits its terminal receipt. Reuse sender/owner boundaries and existing state conversion. |
| Cancellation closure | The child owns provider calls and async tool workers. The parent owns approval answers and authorization grants. | Close run dispatch at actual provider/tool/retry entry points. Acknowledge closure only after local entry admission closes. Include the parent approval path in that boundary. |

`CommandAck` proves only the semantics of its specific acknowledged command.
An input acknowledgement does not acknowledge child acceptance, terminal persistence, or dispatch closure.
`JoinSession` proves a mailbox response. It does not prove a journal commit or a model reply.
`IApprovalChannel.TryClaim` rejects an absent live wait before a broader authorization grant.
Keep that check. Also retain run-liveness checks across asynchronous grant persistence and the exact child retry.
Cancellation tokens alone do not prove that a queued worker cannot dispatch after closure acknowledgement.

The current test base selects its journal in sealed host setup and has no journal-write fault barrier.
Add only a narrow test-owned journal barrier that holds the real acceptance or terminal write and returns its real acknowledgement.
Keep the existing in-memory journal, real owner actor, normal policy, and supported serializer setup around that barrier.
Do not replace the owner with a probe that emits the desired acceptance or terminal acknowledgement.

### 5. Terminal precedence follows durable owner order

The parent accepts terminal messages only from the current recorded child generation.
The first committed terminal receipt wins over later cancellation requests.
If cancellation admission commits first, later child success cannot become the terminal outcome.
The parent can retain its confirmed evidence inside the cancelled result.
Actor mailbox arrival alone is insufficient: journal commit determines the winner.

The child closes normal dispatch, sends one terminal result, and waits for the owner acknowledgement.
The owner acknowledges only after terminal persistence succeeds.
A duplicate terminal message receives the same acknowledgement and cannot schedule a second continuation.
If persistence fails, the owner does not acknowledge successful receipt.
Existing actor/persistence failure policy remains visible; it must not silently discard the result.
Owner termination stops the child. Recovery resolves an accepted run without a terminal receipt as `Lost`.

Alternative: acknowledge before persistence. A crash can then lose a result after the child discards its only copy.

### 6. Cancellation distinguishes admission from dispatch closure

The parent persists cancellation admission before it reports `Cancelling`.
Then it closes child work admission, cancels active calls, and settles run-owned approval prompts.
Closure must cover pending thread-pool tool dispatch and queued provider requests, not just the actor mailbox.
The child/runtime adapter sends an explicit dispatch-closure acknowledgement after no new task dispatch can start.
The control response reports `dispatch_closed = false` until that acknowledgement exists.
It must never describe cancellation admission alone as proof of stopped dispatch.

Once closure is acknowledged, no fresh model request, tool launch, or approval retry can start for that run.
Already launched remote work can ignore cancellation. Record its external effect as unknown when confirmation is absent.
The runtime does not claim that it reverses existing effects or stops remote computation.
If a child dies before closure acknowledgement, owner termination establishes local closure and preserves the cancellation outcome.

Five seconds is the accepted initial finalization default after local dispatch closure. This change adds no operator configuration knob.
The finalization token is separate from the cancelled execution token.
Use injected time and explicitly controllable actor timers; `FakeTimeProvider` alone does not advance Akka timers.
Finalization permits only framework-owned checkpoint preservation and an atomic run-local report write.
It permits no model call, task tool, project edit, external request, or new authorization grant.

Preserve confirmed tool receipts and file activity during normal execution after each completed feedback round.
Bound inline evidence through existing output rules. Store retained reports inside the assigned run artifact directory.
On grace expiry or report-write failure, retain the last committed checkpoint and expose an explicit finalization reason.
Do not claim a report exists before its atomic write commits. Do not invent findings from interrupted model text.

### 7. Durable delivery has one idempotent admission boundary

Use a deterministic delivery ID derived from owner session ID and run ID.
There is one terminal delivery per run. An enrichment retry keeps the same delivery ID.

```text
child terminal
  -> persist terminal receipt and acknowledge child
  -> validate/enrich the recorded result
  -> persist delivery-ready payload
  -> persist continuation admission and its input/transcript item atomically
  -> run parent review under original context
```

The last persistence operation writes both the dedup marker and the continuation item in one authoritative event.
Two unrelated records with a crash gap do not establish reliable admission.
Recovery derives pending continuations from that event and the existing input-completion records.
An admitted but unfinished parent continuation remains pending. Do not create another admission.
Completed parent input cannot be reintroduced by terminal delivery replay.

The original start call already received its acceptance result.
The later result uses a fresh framework delivery correlation and a valid synthetic tool-call/result pair.
It retains the originating `spawn_agent` or routed activation attribution without executing that source call again.
The pair is framework-origin materialization, not a newly requested tool dispatch or an authorization receipt.
Do not attach a second result to the completed original provider call ID.
Provider compatibility tests must inspect the actual paired messages.

Review can run after the active parent turn or compaction finishes.
Use a stable queue in terminal journal order.
Coalesce only records with compatible original authority and the same original detector task identity.
Progress and status changes do not start model turns. Terminal receipt schedules one attributed continuation.
Parent continuation failure remains visible through run status and the existing turn-failure path.
Do not automatically retry completed external effects or relaunch children.

If enrichment fails, retain the original terminal outcome and expose a delivery warning.
The framework can admit a bounded factual result from that durable receipt without a model or unsafe path inference.
Failed persistence remains an explicit undelivered state until recovery or a recorded retry succeeds.

#### Parent detector evidence remains separate from child partial results

The owner stores parent detector evidence in the already planned durable child run ledger.
Reuse `ToolLoopCheckpoint` and the existing receipt-failure fact. Do not add another task ledger or use shell-job records for children.
These facts differ from the child's partial-result checkpoint and its actor-local detector state.
The checkpoint's `TaskId` identifies the original detector task. Do not derive it from `TurnContextRecord.TurnId`.
Delivery authority can identify a later continuation while its detector task still identifies the original causal task.
`HasSameAuthority` alone cannot establish detector identity.

Committed parent admissions and results refresh outstanding runs that belong to the same detector task.
For tool-based starts, continuation restoration waits until the original batch settles and commits its detector evidence.
A normal batch includes the start receipt and completed feedback round.
A missing mandatory receipt retains the receipt-failure fact and invokes PR 1's terminal settlement without another model request.
Direct activation retains canonical task evidence without a fabricated tool receipt.
Parent completion and fresh input retain evidence that an outstanding child continuation needs.
Sibling runs use the latest retained parent checkpoint for their task.
Canonical continuation adoption validates the accepted run and commits restored evidence before the next model request.
The synthetic delivery pair supplies no execution receipt. It cannot clear receipt failure or reopen the completed start call.

Schematic; authorization, admission, and persistence failure paths are abbreviated:

```text
Parent task A accepts child X
  -> run ledger retains A's ToolLoopCheckpoint and receipt-failure fact
Committed A admission or result
  -> refresh every outstanding run whose checkpoint TaskId matches A
  -> tool start: wait for the original batch's committed settlement
  -> normal settlement: retain the start receipt and completed round
  -> missing mandatory receipt: retain failure; settle without another model request
  -> direct activation: retain canonical task evidence without a fabricated receipt
Parent completes A; fresh user task B starts
  -> retain A's evidence in X; keep B's current checkpoint separate
Child X terminal receipt
  -> admit one attributed continuation through durable delivery
  -> validate X; durably restore the latest retained A checkpoint
  -> if evidence permits, request the model under X's original authority
Continuation completes
  -> settle only this delivery; retain evidence for outstanding siblings
```

Positive example: two children from A share its latest committed checkpoint before either continuation requests the model.
Negative example: children from A and B cannot coalesce solely because the same requester owns both tasks.
Recovery preserves parent detector evidence without child relaunch or a fresh-task reset.
Result admission preserves the parent's current directory, project, and branch.

### 8. Original approval prompts remain run-owned

Create the child bridge from the immutable original turn context, not the parent's later current turn.
Keep the prompt identifier, exact child call, authorization attempt, and run ID correlated for the run lifetime.
Persist prompt lifecycle facts before display so recovery can visibly expire child prompts.
Do not persist live waiters, retry authorization objects, or actor references.
Unlike parent prompts, child prompts cannot resume an interrupted child after recovery.

A later ordinary user message cannot abandon a child prompt merely because the start call ended.
A valid original requester can answer while the parent handles other input.
The exact retry repeats the full authorization and run-liveness checks.
Cancellation and terminal loss close the prompt. A stale approval prompt answer creates neither a retry nor a persistent authorization grant.
An answer that wins before cancellation remains subject to the later dispatch-closure gate.
The documentation term becomes **approval prompt**. Code identifiers such as `ToolApprovalRequested` retain their names.

### 9. Recovery, upgrade, and rollback do not replay a child

Snapshot and journal recovery preserve acceptance keys, original contexts, parent detector evidence, child partial checkpoints, terminal receipts, and delivery markers.
After recovery, an accepted run without a durable terminal receipt becomes `Lost` exactly once.
A previously admitted cancellation remains `Cancelled` with retained evidence; recovery does not replace it with successful output.
Recovered child prompts receive visible expired/cancelled disposition.
No recovery branch recreates the child, replays task tools, or adopts the current requester.
The parent model decides further work from the recorded session and the retained loss or partial-result facts.
The framework imposes no retry or operator-review policy for uncertain external effects.
A new model decision can start a new child under ordinary authority. It cannot revive a lost run or duplicate its acceptance key.

Captured pre-change records contain no background child ledger.
Read them as an explicit empty ledger and retain conversation, storage bindings, and parent approval state.
Do not infer accepted children from historical text or transform old unresolved synchronous tool calls into live background runs.
Use the existing healing path to close interrupted legacy calls with explicit loss.

New journal variants need an old-reader compatibility probe.
If the prior binary cannot read them, rollback requires the pre-upgrade database backup.
Stop new admissions and settle active runs before rollback. Restoring the database cannot undo external effects.
This package authorizes no daemon restart, rollout, or data replacement.

### 10. Proof and delivery boundary

PR 2 depends on PR 1. Validate the combined stack, not the old baseline alone.
The runtime, every routed consumer, cancellation, prompt lifetime, and recovery form one coherent PR.
Do not release the intermediate state that frees the parent but loses results or control.

Reuse actor persistence fixtures, provider barriers, session subscribers, and the existing background/cycle eval relays.
Add child-request correlation to the existing relay before it claims background-agent coverage.
Use actual dispatch counts, persisted state, path contents, and prompt settlement as assertions.
Keep scripted protocol evidence separate from real-model evidence.
Keep the inherited PR 1 recurrence gates. This change cannot reset their state through child-result admission.

Use paired controls and fault injection at acceptance, closure, terminal receipt, enrichment, continuation admission, and restart.
Require five successful trials per critical real-model case on the owner-authorized target.
Do not put that target or credentials in commit messages or PR descriptions.
Require independent review of integrated evidence before the background behavior is called verified.

## Risks / Trade-offs

- [Acceptance acknowledgement loss duplicates work] -> Persist one stamped start key and reject digest conflicts.
- [A late dispatch follows cancellation] -> Acknowledge closure only after all local dispatch paths close.
- [Cancellation loses useful results] -> Commit checkpoints during normal work and preserve them without another model call.
- [A late success wins after cancellation] -> Select terminal precedence by durable owner order.
- [A crash loses terminal delivery] -> Atomically admit the dedup marker and continuation item.
- [A result creates trusted automation authority] -> Materialize attributed tool-origin content under the original context.
- [A later speaker answers an approval prompt] -> Compare the live prompt with its immutable original requester.
- [Cancelled output masquerades as success] -> Keep failed wire outcome and explicit cancelled lifecycle state.
- [Old readers reject new records] -> Prove compatibility or use the pre-upgrade backup procedure.
- [Days-long claims exceed implementation] -> State that this child cannot resume after restart or compact its context.

## Migration Plan

1. Apply the confirmed implementation decisions. Retain the accepted five-second framework-only grace default.
2. Rebase this branch on PR 1 and reconcile its frozen wire and detector contracts.
3. Prepare independent lifecycle fixtures and captured pre-change records before runtime edits.
4. Implement the complete background contract with the minimum runtime guidance.
5. Run targeted deterministic checks, fault controls, compatibility probes, and real-model cases.
6. Review missing evidence and false failures before merge approval.
7. Sync accepted specification deltas through the OpenSpec skill.
8. Prepare a database backup and approved rollback procedure before a separately authorized rollout.
