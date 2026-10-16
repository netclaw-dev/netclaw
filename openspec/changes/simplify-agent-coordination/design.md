Canonical execution plan: [Coordination replacement plan](../../../.systematize/plans/coordination-replacement/plan.html)

## Context

See `proposal.md` for the reason and required behavior. The canonical plan gives the three-slice execution sequence and test recipes.

The target base provides `SessionInputFlow`, `SessionToolWorkflow`, `SessionExecutionLifetime`, and `SessionState`. The deployed draft writes child and coordination records that the target cannot fully read. Issue #2453 also contains an invalid adoption event. Its missing source identity remains unexplained.

## Goals / Non-Goals

**Goals:**

- Keep each durable decision with its current session or tool owner.
- Return child acceptance after validation and durable acceptance, before child completion.
- Deliver each prepared result at the next safe boundary, without child-finish ordering.
- Preserve ordinary user-input order, authority, and durable progress.
- Preserve useful coordination guidance and evidence-based review.
- Preserve deployed data or give an explicit safe disposition before new writes.

**Non-Goals:**

- Add a universal job engine, recursive children, or child shell-job submission.
- Restart an uncertain child action after recovery.
- Add another queue for child results or a second approval owner.
- Settle the unresolved cause of missing source identity by inference.

## Decisions

### The parent session owns the durable child run

The session records validated acceptance before it starts a child. The worker stays ephemeral. The durable run keeps the acceptance identity, original authority, approval facts, cancellation state, terminal result, and result-consumption status.

The start returns its accepted run identity after the write. A retry with that same identity returns the existing run. It does not start another child. Do not derive identity from task text. The legacy migration must define an explicit disposition when the source identity is absent.

The worker does not restart after an owner restart when an external action has no terminal record. The session reports an explicit uncertain or failed state. This avoids a false exactly-once claim.

### The tool workflow delivers prepared results when they become ready

The session stores raw terminal output before it prepares model output. Preparation failure leaves the raw result available. A prepared result becomes eligible at once. The workflow can select it ahead of a result that is not ready, regardless of child finish time.

The workflow waits for the current model call and tool batch to reach a safe boundary. The parent then records the result and its correlated decision. Ordinary inputs run in order. Each turn takes only the leading inputs of one requester, and the turn keeps the authority of that requester.

### Ordinary input has its own durable progress boundary

The input remains pending until accepted durable progress or explicit closure. `ToolBatchStarted` consumes the input into durable history. `TurnRecorded` records turn completion. `InputClosed` records explicit closure. The session checks a transition before it writes its event.

Child results use the tool-result path. They do not become ordinary user inputs.

Superseded by change `s2-child-result-notices`: a child result is an admitted notice input, and the model reads `result.md` with a real `file_read` call.

### The existing recurrence owner controls tool loops

Use `TurnStateTracker` and current tool outcome facts for recurrence and cycle rules. Do not add recurrence fields to child state. Keep trusted pending-job exceptions narrow and subject to normal authorization. Do not claim that a fixed iteration limit proves a loop.

### The coordination skill remains guidance

Expose the skill by its logical name. The skill teaches bounded tasks, artifact ownership, independent review, and honest result reporting. Runtime policy remains the authority source.

The spill hint uses the current retained-output path only when the child can read it under current policy. It asks for evidence about a specific question. It does not start a child or add a threshold.

### Legacy reads precede new-format writes

The reader or converter must cover `tta-v1`, `cra-v1`, `cre-v1`, and extended existing records from the deployed writer. The test corpus must include valid records, snapshots, and the reported invalid adoption. Keep the original journal and restore path until the converted data replays under the candidate.

A decoder cannot make an invalid adoption valid. The migration must preserve known identities and results. It must not execute uncertain input or infer identity from equal text. An ambiguous record needs an operator-visible disposition.

## Risks / Trade-offs

- **Legacy identity is missing:** the incident does not explain why. Preserve the evidence and block automatic settlement until a safe disposition exists.
- **A writer outruns the reader:** a new journal may block rollback to an older binary. Gate writes on compatibility proof and retain a paired restore copy.
- **Ready-first delivery changes order:** a later-finished child can reach the parent first. The parent still receives only prepared results at a safe boundary.
- **The split spreads contracts:** keep the skill contract in its capability spec and runtime behavior in the run specs. Use the canonical plan for slice order and recipes.

## Migration Plan

1. Capture and hash a consistent copy of deployed journals. Keep the restore instructions with the copy.
2. Test the exact old record shapes with the candidate reader or converter. Do not use only new-writer fixtures.
3. Convert valid records while preserving their durable facts. Quarantine ambiguous or invalid records without executing them.
4. Replay every converted journal. Enable new-format writes only after replay and restore checks pass.
5. Keep the old journal until the converted session and rollback path pass review. A binary rollback alone does not restore old data.
