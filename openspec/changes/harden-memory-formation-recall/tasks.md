## 1. Memory classing and admission gates

- [ ] 1.1 Add deterministic memory class model (`durable_explicit`, `durable_inferred`, `conversation_trace`) and persistence metadata wiring in memory pipeline contracts.
- [ ] 1.2 Implement structure-first auto-save admission gates (schema/policy validation, class eligibility, confidence and novelty thresholds) before durable write enqueue.
- [ ] 1.3 Ensure turn-completion conversational snapshots are classed as `conversation_trace` and rejected from durable auto-save path unless explicit save intent exists.
- [ ] 1.4 Add unit/integration tests for classing boundaries and admission outcomes using synthetic/sanitized fixtures only.

## 2. Auto-recall filtering and ranking hardening

- [ ] 2.1 Implement deterministic auto-recall hard filters by policy envelope and eligibility class, excluding `conversation_trace` from default automatic injection.
- [ ] 2.2 Implement deterministic ranking over remaining candidates (explicitness, confidence, recency, anchor relevance, novelty-to-turn) within existing latency budgets.
- [ ] 2.3 Add recall pipeline tests that verify pollution suppression and preserved recall for durable explicit/inferred memories with sanitized fixture datasets.
- [ ] 2.4 Update memory context/status guidance to reflect degraded behavior accurately when recall filtering or memory substrate faults occur.

## 3. Truthful explicit save acknowledgement path

- [ ] 3.1 Refactor explicit save flow so session success acknowledgement is emitted only after durable write confirmation from persistence actor/tool result.
- [ ] 3.2 Implement explicit failure/degraded acknowledgements for timeout/error outcomes and preserve retry guidance.
- [ ] 3.3 Add actor-level tests covering success-after-commit, timeout/failure handling, and prevention of optimistic pre-commit success claims.

## 4. Eval gates and rollout criteria

- [ ] 4.1 Add/update memory hygiene smoke and realistic eval suites with deterministic, synthetic/sanitized fixtures only and fixture validation checks.
- [ ] 4.2 Implement metrics/assertions for recall precision, conversation-trace leakage, acknowledgement truthfulness, and stability streak gating.
- [ ] 4.3 Document and wire rollout thresholds (smoke vs realistic pass thresholds, required consecutive pass count) in eval docs/config used by the change.

## 5. Documentation and phase boundaries

- [ ] 5.1 Update memory architecture/design docs for Phase A classing, eligibility, admission, recall hard-filtering, and ack truthfulness behavior.
- [ ] 5.2 Add explicit Phase B deferred-items section describing candidate/promotion ledger scope excluded from this change.
- [ ] 5.3 Run validation (`openspec validate --change harden-memory-formation-recall --strict`) and resolve any artifact/schema issues.
