## Context

Source PRDs: [PRD-007](../../../../docs/prd/PRD-007-agent-personality-and-local-memory.md), product outcome 2.
See [the proposal](proposal.md) for the problem and [the capability delta](specs/netclaw-agent-memory/spec.md) for acceptance scenarios.
Use [the engineering glossary](../../../../docs/spec/GLOSSARY.md) for shared terms.

Before this change, `MemoryCurationEvaluator` clears `IsExactAnchorMatch` after `GuardDestructiveUpdate` rejects a direct update.
The candidate still carries its canonical name and document identity.
The previous rules treat low overlap after that demotion as `Create`.
The store can then append the proposal after an anchor collision.

Two additional paths require care.
The previous evaluator converts unresolved ambiguity to `Create` after an unavailable or invalid LLM decision.
The previous `ApplyDecisionAsync` also maps `Ambiguous` to a write operation.
These paths must not authorize this unresolved same-name proposal.

## Goals / Non-Goals

**Goals:**

- Use the canonical name already present on the proposal and candidates to retain exact-name evidence after guard rejection.
- Keep actor and checkpoint-worker decisions equivalent through their shared evaluator.
- Block unresolved same-name writes through the existing failure path.
- Preserve safe deterministic decisions and the existing LLM merge safety contract.

**Non-Goals:**

- No new decision tier, config knob, schema, message type, data format, or store algorithm.
- No changes to audience, boundary scope, immutable-record admission, or session authority.
- No live data inspection, migration, or cleanup.
- No change to existing lossless append behavior after a valid LLM decision whose merge body fails validation.

## Decisions

### Retain same-name evidence within the existing rules seam

Use canonical-name equality when a candidate no longer claims `IsExactAnchorMatch`.
Low overlap alone cannot prove that a same-name proposal concerns a separate document.
Return `Ambiguous` for that unresolved case and reuse the existing LLM decision tier.
Keep different-name low-overlap `Create` and safe deterministic update, consolidation, and duplicate-skip decisions.

Alternative: retain unconditional low-overlap `Create`.
This preserves the defect because store deduplication cannot decide whether two complete bodies describe the same facts.
Alternative: remove the overwrite guard.
This permits a narrower proposal to discard existing facts.

### Preserve the producer and consumer contract

`CurationPromptBuilder` supplies the candidate identities and accepts the LLM response through the existing parser.
`MemoryCurationEvaluator` converts the decision into the operation consumed by `SQLiteMemoryStore`.
A valid LLM update must name a current candidate and preserve its canonical document identity in `MemoryId`.
`MergeGuard` validates the merged body against the source bodies before a `merge-document` overwrite.
`SKIP` produces no operation.
Do not bypass content validation or direct an accepted merge through an identity-free create operation.

Positive example: one stored document and one same-name rewrite produce one validated merged body at the existing identity.
Negative example: a demoted exact-name candidate reaches `Create`, then store deduplication appends both full bodies.
Guard example: a proposal with one of three stored facts cannot directly overwrite that document.
Control example: a different-name candidate with low overlap still permits deterministic `Create`.

### Keep unresolved writes blocked

If safe deterministic evidence cannot resolve the proposal, retain `Ambiguous` when the LLM is unavailable or invalid.
The apply boundary must block that unresolved proposal through the existing failure path before it creates a write operation.
Reuse the actor's existing `CurationFailed` result and the checkpoint worker's existing failure/retry path.
The implementation must confirm both consumers report the failure without an accepted write or a false success.
Do not convert the unresolved result to `SKIP`, `Create`, or an append.
Keep unrelated nominee and fuzzy fallback behavior outside this change.

### Preserve the deployment boundary

`MemoryCurationEngine` constructs the shared evaluator with `llmClient: null` today.
This change does not add a model route to that engine.
Its unresolved same-name proposals fail at apply and reach the worker's existing retry path.
The 12-row theory supplies a fake LLM to both logger adapters and uses both store batch methods.
Those rows prove the shared seam when the parent verifies them; they do not prove engine LLM runtime behavior.
Actor failure replies and worker retry state require separate runtime evidence.

### Validate the exact decision targets before acceptance

The parser must reject a keyword prefix such as `CREATE_EXTRA` or `SKIPPER`.
`MemoryCurationEvaluator` owns target validation against the call-local candidate identities.
An `UPDATE` target must identify one candidate exactly.
Every `CONSOLIDATE` target must identify a candidate exactly.
Reject the whole decision when one target is unknown, including a stored document outside the candidate set.
Perform this check before acceptance, append construction, or a persistent consolidation effect.
An invalid decision leaves a same-name rewrite unresolved.
A valid target with an invalid merged body still permits the existing lossless append.

The shared evaluator's `ValidateDecision` method checks the decision and its exact candidate list without database access.
Model acceptance and direct application use that method; both consumers use it for batch preflight.
The method requires a candidate target for `UPDATE` and candidate membership for every `CONSOLIDATE` target.
It also requires the primary consolidation target to appear in that target list.
An invalid decision throws through the existing model-failure or consumer-failure path.
No merged-body check occurs during preflight; the existing `MergeGuard` path still decides between a merge and a lossless append.

Positive example: a valid candidate ID and incomplete merged body produce an append at that candidate ID.
Negative example: a valid ID plus an unknown ID cannot authorize any consolidation effect.

### Preflight each consumer batch before persistent effects

The actor evaluates the full batch before its apply loop.
Its apply loop can reanchor documents before a later unresolved decision throws.
The checkpoint engine also evaluates and applies operations in sequence.
Both consumers must reject unresolved decisions and invalid targets before the first persistent effect.
Reuse the shared evaluator contract; do not add a parallel decision mechanism.

The actor preflights its existing evaluation list before its apply loop.
The engine collects operation/evaluation pairs in call-local data within its existing internal method.
It evaluates and preflights those pairs before its apply loop.
The actor uses `CurationFailed`; the engine propagates failure to the worker's existing retry path.
The changed actor completion callbacks use explicit `ActorRefs.NoSender` outside the actor thread.
Preflight prevents decision rejection after an earlier consolidation effect; it adds no transaction or rollback for later storage failures.

Positive example: a batch with only resolved decisions reaches the existing store batch.
Negative example: consolidation followed by an unresolved rewrite reports failure and preserves bodies, anchor IDs, and anchor status.
The actor must accept a subsequent safe proposal after that failure.

The current public engine path cannot supply this multi-proposal batch.
`MemoryRulesFirstExtractor` emits one proposal; observed checkpoint operations return before evaluation.
The parent selected internal visibility for the existing `EvaluateAndApplyAsync` method as the minimal test seam.
This reversible visibility change adds no method, interface, or runtime behavior.
The engine-batch row directly supplies two operations to that method through the friend test assembly.
The row shares the actor-batch fixture and the storage assertion for bodies, anchor IDs, and anchor status.
It requires `InvalidOperationException` after consolidation precedes an unresolved rewrite.
The parent must prove both batch rows red before a different implementer adds preflight checks to both consumers.
Do not change observed checkpoint behavior solely to create a test path.

### Resolve the rejected same-name candidate before high-overlap consolidation

A different-name candidate with higher confidence cannot authorize consolidation of a rejected same-name candidate.
Retain canonical-name evidence before the high-overlap branch can return `Consolidate`.
Preserve ordinary different-name high-overlap consolidation and different-name low-overlap `Create`.
The pure tables cover case, outer whitespace, and space-to-hyphen normalization through `MemoryTypedId.AnchorId`.
Nominee controls cover unavailable and invalid model decisions, plus preservation of distinct documents for different names.

### Prove the worker failure path with the existing runtime

The worker test queues one explicit-request checkpoint before `StartAsync`.
The test derives the expected anchor through the real extractor.
The real engine rejects the unresolved rewrite; the worker retries it immediately.
The test uses TestKit bounded assertions against SQLite until `retry_count=5` and `status=failed`.
It requires unchanged memory and no completion log, then stops only its own worker.
The worker uses a wall-clock delay only when its queue is empty.
The test needs no clock advance because each failure immediately returns the checkpoint to the queue.
No new scheduler, daemon host, service framework, or model route is required.

### Limit the mutation proposal to the same-name condition

Target only the condition line `if (sameNameMatch is not null)` in `CurationRulesEvaluator`.
Exclude the reason string and the branch body.
Link the unchanged `CurationRulesEvaluatorTests` and the new xUnit 2-compatible `CurationAdversarialRulesTests` into the existing mutation project.
The paired same-name and different-name rows reject removal or reversal of that gate.
The condition must also precede the high-overlap decision; runtime and table proof enforce that order.
The parent selects the gate after the new red proof.

The previous throw-span proposal does not establish the two return-operation/null mutations with standard Stryker.
Keep those alternatives as separate fault-injection checks if the parent requires them.
Do not wire a mutation gate in this test-author stage.
Measure an exact nonzero mutant count and source span before the expected count is set.
Require every expected mutant to have `Killed` status.
Missing, surviving, uncovered, and compile-error mutants must fail the gate.
Measure cold-run cost before placement within the 15-minute group budget in `TOOLING.md`.

### Decision owners and data lifetimes

| Owner | Decision or data | Lifetime |
| --- | --- | --- |
| Main session | Authority, audience, boundary, accepted proposal | Actor-local; existing durable checkpoint rules apply |
| `CurationRulesEvaluator` | Deterministic classification and direct overwrite guard | Call-local |
| `MemoryCurationEvaluator` | Candidate set, LLM escalation, unresolved result, write conversion | Call-local |
| Existing LLM tier | Semantic decision and proposed merged body | Call-local |
| `MergeGuard` | Content preservation before overwrite | Call-local |
| Actor and checkpoint worker | Failure report and existing retry policy | Actor-local or existing durable checkpoint state |
| `SQLiteMemoryStore` | Canonical document identity, body, semantics, and version history | Durable |

No new actor state or durable field carries the guard result.
The canonical name and candidate identity already cross the evaluator seam.

### Ordered flow

Schematic flow: the diagram omits runtime admission, candidate nomination, and transport steps.
Existing audience, boundary, and record admission checks remain mandatory.

```text
1. Rules evaluate an admitted document proposal against candidates.
2. A safe deterministic decision keeps its existing path.
3. The overwrite guard rejects a lossy exact-name update.
4. Rules retain canonical-name evidence after candidate demotion.
5. Insufficient deterministic evidence yields Ambiguous.
6. The existing LLM tier supplies a decision with valid syntax and candidate IDs, or the proposal stays unresolved.
7. Each consumer preflights its whole batch before any persistent apply effect; an unresolved decision fails the batch.
8. SKIP: emit no write.
9. UPDATE: validate the merged body and use canonical MemoryId plus merge-document.
10. The store applies only an accepted operation.
```

## Risks / Trade-offs

- Extra LLM calls replace previous `Create` fallbacks; token cost and curation latency increase for these proposals.
- LLM failure can defer an explicit rewrite; use existing failure reports and retry policy without a new persistence fallback.
- A broad equality check can alter safe decisions; preserve decision order and prove the different-name control and deterministic cases.
- A result that remains `Ambiguous` can still become a write at apply time; verify the apply boundary before acceptance.
- A valid LLM decision can still produce an invalid merged body; preserve existing content validation and lossless append behavior after that decision.
- The prior 51-test result excludes the three adversarial findings; new test execution and consumer proof remain open.

## Migration Plan

No schema or data migration applies.
The parent first proves both batch rows fail before any runtime behavior change.
A separate implementer uses `openspec-apply-change` and leaves the test-author fixtures unchanged.
The parent owns the review, CI, commit, and PR steps.
A binary rollback restores prior behavior; this change does not repair existing duplicate content.
