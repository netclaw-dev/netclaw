## Context

See [proposal.md](proposal.md) for the objective and source PRDs.
The base branch already contains an exact adjacent guard, paired corrections, and MCP error receipts.
`TurnStateTracker` stores six completed batch signatures. A different completed action clears its blocked marker.
Both actor paths currently substitute `Success` when a tool result lacks a receipt.
The parent currently resets the tracker during automatic restart recovery.

## Goals / Non-Goals

**Goals:**

- Reuse the current tracker, signature factory, and result pipelines.
- Detect an unchanged call across unrelated feedback rounds without a total task limit.
- Preserve call-result pairs, authority, useful repairs, and durable parent evidence.
- Use one correction and then a runtime terminal result.

**Non-Goals:**

- Detect semantic equivalence, measure useful progress, or promise finite total cost.
- Add a poll-rate policy, timestamp normalization, or a fixed wait interval.
- Resume an ephemeral child after restart.

## Decisions

### 1. Identity and duplicate outcomes

`ToolCycleSignatureFactory` remains the owner of canonical identity.
A recurrence key contains the canonical tool name and the existing prepared argument hash.
Valid metadata does not affect identity. Rejected metadata and validation state still affect identity.
Call identifiers and authorization attempt identifiers provide correlation, never recurrence identity or authority.

One model feedback round contributes at most one completion observation for each recurrence key.
A group outcome contains the distinct sorted tuples `(receipt category, exact bounded model-visible result hash)`.
The actor retains tuple multiplicities separately as bounded diagnostic counts.
A changed duplicate count cannot reset the per-call episode.
A new distinct outcome changes the episode. No outcome hash normalizes timestamps, paths, identifiers, or result prose.
The adjacent batch guard keeps its current multiplicity-sensitive signature.

Alternative: include duplicate counts in the per-call outcome hash. A model could clear suspicion through batch-size changes.
Alternative: discard duplicate receipts. Distinct sibling failures would disappear.

### 2. Intervention and precedence

The actor prepares every group decision before any call from that model response starts.
Two equal completed group observations establish exact recurrence.
Candidate three returns `Correct`; every matching call receives a synthetic correction and does not execute.
The next prohibited candidate returns `Stop`, even after an unrelated call completes.
There is no separate firmer-refusal stage.

The adjacent guard still checks periods one through three for eligible complete batches.
Its existing correction-then-stop rule remains active.
A valid repeat exception precedes both guards. Among remaining decisions, `Stop` precedes `Correct`, which precedes `Execute`.
A stop prevents all calls from the current response. A per-call correction leaves unrelated eligible calls available.
The actor must not apply the adjacent guard to a batch with an exempt group or a synthetic result.
This prevents the batch guard from overriding a valid repeat exception or counting a refused group as actual work.

Schematic pseudocode; normal validation, authorization, approval prompt, and persistence gates remain required:

```text
prepare response:
  groups = prepared calls grouped by canonical name and argument hash
  for each group:
    if exact group has a trusted pending-job exception:
      decision[group] = Execute
    else if group has an unresolved correction:
      decision[group] = Stop
    else if group has two equal completed rounds:
      decision[group] = Correct
    else:
      decision[group] = Execute

  if no group has an exception:
    combine decisions with the existing adjacent guard
  if any decision is Stop:
    settle the actor without another model request
  else:
    persist admission and one paired result for each refused call
    dispatch eligible calls through the existing pipeline

complete group:
  require exactly one final result for every requested member
  if any actual member lacks a receipt:
    record a runtime contract failure; do not infer Success
    preserve paired results and prior evidence
    settle partial/failure without another model request
  else if the actor recorded these members as synthetic refusals:
    preserve the correction; do not record actual completion
  else:
    hash distinct exact outcome tuples
    equal outcome -> increment once for this feedback round
    changed outcome -> begin a new episode for this key
    retain other keys and their unresolved corrections
```

An approval redrive belongs to its original round and authorization attempt.
It cannot count as another model decision or clear a correction.
A final actual result without its mandatory receipt triggers an explicit runtime contract failure.
The actor preserves known results and prior evidence, completes required call-result pairs, and settles Partial or Failed without another model request.
A pending approval, an in-flight call, or external cancellation has no final actual result and does not trigger this defect rule.

Receipt producer audit:

- `DispatchingToolExecutor` supplies typed success, unknown-tool, validation, authorization denial, and exception receipts.
- `RouteToBackgroundJobAsync` lacks receipts on successful submission and submission errors. Both completed paths need canonical typed evidence.
- The parent normal-return path and child `BuildToolResult` currently substitute Success for absent executor evidence. Remove these substitutions.
- External cancellation propagates as a lifecycle failure. It does not create a completed detector outcome.
- Fakes that emulate a completed tool must emit an explicit receipt. A fake that tests a contract defect must omit it deliberately.

A tool-supplied `BreakToolCycle` code alone cannot mark a result as an actor-issued refusal.
The actor's recorded refused call identifiers establish that provenance.

### 3. Explicit repeat exception

The initial exception applies only to a valid status query for one accessible background job.
`BackgroundJobManagerActor` already supplies `JobId`, `Found`, and `BackgroundJobStatus` after exact authority checks.
`CheckBackgroundJobTool` currently discards those typed facts when it returns text.
The existing generic success receipt contains no pending-operation fact.

Add one closed internal receipt case for an accessible `Pending` or `Running` status response.
It carries the existing typed status fact through `ToolExecutionOutputs.TryComplete`.
The actor binds the fact to the prepared identity of that exact invocation.
The receipt adds no new controller, authority field, callback, clock, or state store.
It grants no authority and supplies no deadline or rate permission.

The exception applies only when validation accepts the query and `cancel` is false.
A different identity, cancellation request, denied lookup, error, and terminal status receive ordinary treatment.
Normal pipeline authority checks still precede every actual query.
The exception ends when the same identity produces a nonpending outcome or fresh authorized work resets the task.
Recovery restores the fact as tool evidence, not as a reusable grant.

Changing elapsed text already changes the exact outcome hash.
The PR does not strip that text or classify rapid polls as a loop.
Model guidance and targeted evals must prefer useful work or completion notices over repeated status queries.
A semantic detector or poll-rate policy needs separate evidence and a later contract.

### 4. State lifetime, retention, and resource behavior

The actor owns all live decisions. `TurnStateTracker` stores only fixed-size digests, closed states, and counters for each key.
The adjacent guard retains its existing six-entry history.
A 256-key LRU retains cold keys with one completed observation.
Two equal observations or one correction pin a key until an actual outcome changes or authorized fresh input resets the task.
Compaction, unrelated calls, tool retries, status notices, and automatic continuations cannot evict pinned evidence.

The cold horizon is a detection limit, not a task limit.
A first observation that leaves that horizon can cause a distant recurrence miss.
The PR exposes this limitation and verifies it explicitly.

Pinned keys have no count or age eviction. Their total size can grow during a pathological task.
Each entry remains fixed-size; counters saturate rather than overflow.
Checkpoint events carry deltas for changed keys and explicit cold evictions.
Snapshots contain the full retained state. Journal size grows with observations under the existing session persistence policy.
The runtime cannot claim a fixed memory or spend guarantee for a task of unlimited duration.
A resource or persistence failure must surface an error. It cannot silently erase evidence or restart the detector.
No detector count becomes a hidden lifetime ceiling.

Alternative: evict pinned keys at a fixed total size. This would clear known unresolved loops.
Alternative: retain all first observations forever. This would grow memory during useful long tasks without stronger proof.

### 5. Durable parent continuity

The parent stores only framework-owned serialized detector evidence.
It never journals `FunctionCallContent`, `ToolInvocationReceipt`, actor references, delegates, tasks, or raw detector payloads.
The private journal can contain digests. Public diagnostics and checked-in evidence cannot contain private digests.

Required data changes:

- Add optional detector admission metadata to `ToolBatchStarted` for the round and prepared group identities.
- Add optional closed detector observation metadata to `ToolCallRecorded` for category, outcome digest, synthetic provenance, and pending-job fact.
- Carry checkpoint deltas on `ToolBatchStarted`; reconstruct completed evidence through typed `ToolCallRecorded` observations.
- Add `ToolTaskAdopted` with canonical admitted context and input identifiers; commit it at task consumption before continuation.
- Add retained detector state to `SessionState` and `SessionSnapshot` with additive protocol fields and converters.
- Reuse the original admitted `TurnContextRecord.TurnId` as the task identity; retain it through automatic continuation and recovery.

The actor persists admission before tool execution or synthetic feedback.
It persists result evidence with the existing result event, before the result can advance the next model round.
Typed result records reconstruct completed evidence before the next model request.
The next admission persists its checkpoint delta before tool dispatch.
Recovery applies checkpoints, then reconstructs any later incomplete checkpoint from durable admission and result evidence.
It counts a fully paired round once. It does not fabricate missing receipts or replay completed side effects.
An interrupted partial batch retains existing completed result evidence without a false completed-group observation.

A snapshot preserves the same retained state as journal replay.
Automatic restart continuation restores the original task identity and detector state.
A genuinely fresh authorized user input or scheduled task starts a new task and clears prior evidence at durable adoption.
Ingress only records pending intent. Old batch results and approval attempts retain their original context until adoption.
`ToolTaskAdopted` commits before a buffered continuation starts and derives its context from the canonical admitted record.
The session uses the existing context deriver after adoption. It does not accept model-supplied authority.
Recovery applies adoption once and retains its task identity; older pending work cannot overwrite its detector state.
Overflow replay, delivery retry, and restart notices do not constitute fresh tasks.
The child retains equivalent actor-local state for its one task; restart still loses that child.

A captured pre-change record lacks detector evidence. Recovery starts an explicit empty baseline and records an upgrade evidence gap.
It cannot retroactively infer trusted outcomes from old result text.
It still preserves old conversation and approval state without a migration that replays tools.

### 6. Terminal settlement

`Stop` invokes the existing framework turn/result settlement path with a bounded factual report.
The report states that the runtime stopped an unchanged tool recurrence and that some work can remain incomplete.
It uses recorded results and confirmed file activity; a model statement cannot supply success evidence.
The child returns `Partial` with `ToolCycleStopped` and its confirmed delta.
The parent persists its terminal turn reply and closes its input through the existing completion path.
No further model response, tool dispatch, approval prompt, or new grant is necessary for settlement.
The unadmitted terminal candidate creates no orphaned provider tool-call message.
One terminal outcome wins; a late provider reply cannot reopen the turn.

Alternative: request a text-only final model response. A stalled or noncompliant model would retain control of termination.

### 7. Configuration migration and activation

Remove the parent configuration property and schema entry together.
Remove the child constructor argument, constant, and budget branches; update every caller.
Keep usage counts for telemetry and empty-response phase selection.
Keep operation liveness, explicit cancellation, and empty-response retry guards.

A legacy configuration with `MaxToolIterationsPerTurn` fails schema validation before persistence or activation.
The existing doctor correction removes that deprecated key and preserves unrelated configuration.
No startup path silently ignores the old setting.
No optional compatibility parameter preserves the removed runtime policy.

The PR activates the detector and removes the limits together after the evidence gates pass.
It adds no runtime switch. A private disposable shadow harness compares immutable traces without tool execution.
Shadow evidence must exist before activation or merge approval.
Rollback restores the prior binary and schema; a configuration without the key receives the prior default ceiling.
Older binaries cannot read new journal event variants without compatibility proof.
Therefore rollback of a database that contains new events requires a tested compatible reader or restoration of its pre-upgrade backup.
This task authorizes code and PRs, not a daemon rollout or database replacement.

### 8. Proof before acceptance

Preserve all unfinished proof obligations from `stop-repeated-tool-cycles`.
Run the fifteen laboratory cases and at least 10,000 fixed-seed sequences.
For the broader nonadjacent detector, also run one million deterministic fuzz sequences and the 3,000-turn productive holdout gate.
Zero hard false blocks in independent holdout turns and independent review of every shadow block remain required.
Keep session groups in one data split. Synthetic fuzz does not establish a production false-positive rate.
Absent private replay or independent holdout evidence remains an unpassed gate, never an inferred pass.

Use controlled actors, providers, acknowledgements, barriers, and timers for deterministic fault tests.
A separate test agent derives assertions from this contract and targets named faulty variants.
Require actual dispatch counts, durable records, artifact contents, and parent/child parity.
A fixture failure or compiler error does not count as a rejected behavioral mutant.
Run only the targeted tool-cycle and relevant subagent eval cases with strict independent assertions.
The orchestration owner supplies the approved provider target outside commit and PR metadata.

## Risks / Trade-offs

- [Exact result noise hides loops] -> Record misses separately; do not invent result normalization.
- [A valid external recheck resembles recurrence] -> Apply only the explicit typed pending-job exception before both guards.
- [Pending-job polls remain unbounded] -> State the miss; use guidance and evals; defer a rate policy.
- [Cold eviction misses distant repeats] -> Publish the exact horizon; never evict established suspicion.
- [Pinned evidence and private journals grow] -> Use fixed-size entries and delta events; expose resource failures without a reset.
- [Checkpoint races erase results] -> Persist typed evidence with each result; test every admission, result, and checkpoint cut point.
- [A missing receipt looks successful] -> Repair normal producers; settle an actual missing-receipt defect without another model request.
- [Terminal summaries overstate progress] -> Use framework facts and a Partial child outcome.
- [Removed ceilings expose semantic loops] -> Keep the agreed replay, shadow, sensitivity, and holdout merge gates.
- [New events complicate rollback] -> Test old-reader compatibility or require the recorded pre-upgrade backup procedure.

## Migration Plan

1. Review this exact contract before production edits.
2. Add the deterministic reference corpus and independent adversarial assertions.
3. Implement the shared detector, durable evidence, and runtime terminal path.
4. Remove both ceilings and update schema, docs, and operational guidance.
5. Run the deterministic, replay, shadow, sensitivity, and targeted model gates.
6. Review all failures, missing evidence, and private-data boundaries before the PR stack advances.
7. Sync specifications through the OpenSpec skill after implementation agrees with this contract.

### Adopted task context after input consumption

`SessionState` retains the canonical context from `ToolTaskAdopted` until the task ends.
Snapshots retain the same framework-owned context.
A tool batch can close its input ledger entries without closing the task.
Automatic recovery uses that context when the input ledger is empty.
A terminal task event clears the context.

The actor adopts one compatible prefix of buffered inputs.
The actor preserves later inputs in arrival order.
Each later prefix receives its own canonical requester after the previous prefix completes.
Recovery uses the same authority comparison and prefix order.
Admission cannot change an in-flight attempt or its approval requester.

The adoption consumer verifies the ordered canonical input prefix.
It rejects a different requester, a skipped input, or a reordered group.
It verifies the latest prefix context and retains the event's admitted input IDs.
A repeated canonical adoption event is idempotent and preserves current detector evidence.

### Background job lineage contract

`RouteToBackgroundJobAsync` receives the canonical `SessionToolBatch.TurnContext`.
The producer stamps a required origin `TurnId` and `ToolCallId` into `StartBackgroundJob`.
This single-process request retains the runtime `ShellProcessLaunch` policy and delegate facts.
Only this launch request uses `INoSerializationVerificationNeeded`; the runtime does not serialize or persist its launch object.
The manager persists canonical serializable definition data, including the closed origin, before process start.
`BackgroundJobDefinition` persists that closed origin before it starts the process.
An absent origin in a pre-change definition identifies the explicit legacy case.
A new-format definition cannot omit its origin.
The manager carries its stored origin in the existing trusted delivery source.
It does not derive an origin from result text.

`ToolCallRecorded` persists the started `ActiveJobInfo` before the actor continues.
That existing job record retains its origin and the matching `ToolLoopCheckpoint`.
Admission and completed-result application refresh records that match the current recurrence task.
A fresh task preserves those records before it replaces the current checkpoint.
Two jobs from one task share immutable checkpoint references in memory.
Their snapshot representations can repeat those bytes.
The records remain until their existing job lifecycle removes them.
No count cap discards their pinned evidence.

A delivery matches the framework job key, session, and stored origin.
`ToolTaskAdopted` names that job key only for a validated internal continuation.
Its consumer validates the committed job record before it restores the origin checkpoint.
The event retains the canonical automation authority of the admitted delivery.
The detector uses the checkpoint's origin `TurnId` independently from that authority context's `TurnId`.
The actor commits the restored checkpoint before the next model request.
The terminal delivery event removes the job record only after that commit.
A duplicate delivery uses the existing deduplication contract.

A fast job result remains buffered until the parent commits its start receipt.
A crash after process launch can precede that receipt commit.
A new-format delivery without its mandatory committed parent record causes an explicit contract failure.
The runtime preserves known results and settles without a new model request.
It does not rerun the process or substitute the current checkpoint.
Only a genuinely pre-change job can use the explicit legacy evidence-gap baseline.
A forged, stale, or foreign origin cannot restore a checkpoint.

Verification includes fast completion, the launch/receipt crash, duplicate delivery, a fresh-user task switch, two jobs, and invalid origins.

The job definition uses an explicit lineage format version.
Version zero means a pre-change record without origin evidence.
Version one requires a valid closed origin before persistence and process launch.
A version-one record with no origin is malformed, not legacy.
The store and runtime reject unknown versions and malformed version-one origins.

The manager uses the prefixed delivery key in `MessageSource.BackgroundJobId`.
The session keys `ActiveBackgroundJobs` by that same prefixed delivery key.
`ActiveJobInfo.JobId` retains the raw manager ID.
The consumer compares those existing representations explicitly.
It does not strip or infer a prefix from result text.
`InputAdmitted` persists the trusted closed origin and format version because `SendUserMessage.Source` is ephemeral.
A terminal delivery removes only its own prefixed entry.
Other jobs retain their checkpoints until their existing lifecycle ends.

An invalid delivery settles only its own admitted input.
The actor commits `InputClosed` with that input ID and canonical authority `TurnId`.
The actor preserves another active task, its checkpoint, and every later pending input.
It emits a bounded explicit contract-failure report through the normal text renderer.
It does not emit `TurnCompleted` for another active user task.
At Ready it does not activate the invalid delivery.
At a buffer boundary it removes only that delivery and resumes the unrelated task or prefix.
The closed delivery key enters the existing background-job deduplication ledger.
Snapshots preserve that ledger so a closed delivery cannot reopen after recovery.
An interrupted report emission cannot change the prior durable closure.
The no-model-request assertion applies to the invalid delivery, not to unrelated authorized work.

The invalid delivery closure stores its bounded factual report as an ordinary assistant history message.
The consumer validates the canonical input ID, job key, and authority turn before it appends that report.
The report identifies job output as result data and grants no tool authority.
Journal replay and snapshots preserve the report if output emission stops after the commit.
Pre-change snapshots omit the job deduplication ledger and retain an explicit legacy evidence gap.

A job delivery's authority `TurnId` equals its prefixed delivery key.
The manager emits this canonical representation, and the adoption consumer requires it.
Recovery uses the adopted context's `SourceKind` and `TurnId` for terminal job bookkeeping.
It does not use the restart reminder's delivery identity.
An incoming version-zero claim cannot downgrade a tracked version-one job.
The store reuses its rejected-definition diagnostics and operational alert seam for malformed lineage.
The manager returns an explicit contract failure to a rejected document's canonical session, audience, and boundary.
A foreign caller receives the existing opaque not-found result.
A rejected document never enters the process queue or receives a legacy recurrence baseline.

Each buffer-drain caller supplies an explicit continuation when every selected input is rejected.
A completed task returns to Ready without another model request.
An unfinished authorized task retains its existing continuation.
Compaction retains its existing tool-resume decision.
The actor does not infer unfinished work from a stale current context.

### Legacy approval redrive baseline

A pre-change parked batch has no `LoopAdmission` or checkpoint.
Its existing pending approval owns the original canonical `TurnContext` and authorization attempt.
Before its first redrive, the actor prepares only unanswered calls from the parked assistant message.
The actor commits that baseline before tool dispatch.
Completed siblings retain their results and never receive a new invocation.
Historical results receive no fabricated receipt or detector observation.
A new-format batch retains its existing admission and checkpoint.

The existing `ToolBatchStarted` consumer also appends history and restores batch bookkeeping.
Its fields cannot safely describe a metadata-only baseline across deserialized list payloads.
An additive `MetadataOnly` flag names that missing representation on the existing event.
Its consumer validates the original approval context and exact unanswered call identities before state application.
The event does not consume input IDs, append user or assistant messages, or replay completed effects.
A genuine legacy baseline requires absent admission and empty detector evidence.
An identical duplicate leaves the current checkpoint unchanged.
A mismatched duplicate or an attempted reset of new-format evidence is a contract failure.
The baseline retains the original approval requester, scope, and authorization attempt.
Verification covers journal replay, snapshot recovery, completed siblings, and duplicate redrive.

The metadata-only event carries the original canonical `TurnContextRecord` from the existing approval owner.
Its consumer compares the full canonical record with that owner before it applies the baseline.
`SessionState.AdoptedTaskContext` retains that same record for restart continuity after approval state expires.
The task uses no invented input ID and no separate requester ledger.
An active identical duplicate must retain its canonical adopted context and current evidence.
A closed or mismatched task cannot reactivate through a metadata-only event.
When redrive finishes the original fully paired admission, the live tracker observes its typed evidence once.
The normal completed-cycle path and that recovery path are mutually exclusive.
Both paths exclude a receipt defect and an all-refused group.
This operation preserves telemetry counters and never fabricates a historical receipt.
