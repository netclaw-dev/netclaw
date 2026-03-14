## 1. Findings envelope contract

- [x] 1.1 Add a findings-capable subagent definition flag or equivalent contract so only selected subagents may emit structured findings envelopes.
- [x] 1.2 Define the findings envelope schema for durable conclusion candidates, including provenance/evidence references and review metadata for `domain`, `sensitivity`, `confidence`, `durability`, and `reusability`.
- [x] 1.3 Add tests covering findings-capable versus default subagents and rejection of raw work-log or transcript-shaped findings envelopes.

## 2. Parent-session acceptance heuristics

- [x] 2.1 Implement deterministic parent-session review that classifies each subagent finding candidate as `accept`, `defer`, or `reject` before any checkpoint enqueue occurs.
- [x] 2.2 Ensure only accepted subagent findings become durable checkpoints, while deferred and rejected findings do not write durable memory in MVP-now.
- [x] 2.3 Add actor-level tests for accepted, deferred, rejected, policy-denied, and metadata-incomplete findings scenarios.

## 3. Memory and checkpoint integration

- [x] 3.1 Integrate subagent findings review with the existing memory candidate extraction and checkpoint scheduling pipeline without broadening the simpler verified-tool heuristic path.
- [ ] 3.2 Preserve existing retry and restart recovery behavior for accepted checkpoints and verify that only queued accepted findings survive daemon restart.
- [x] 3.3 Update session or prompt guidance so findings are described as conservative, parent-reviewed durable-memory candidates rather than direct writes.

## 4. Documentation and validation

- [x] 4.1 Document deferred follow-up work for reviewer UX, richer scoring, and persisted deferred-review queues so they remain out of scope for the MVP implementation.
- [ ] 4.2 Validate the change with `openspec validate --change subagent-findings-checkpoint-heuristics --strict` and resolve any artifact issues before implementation starts.
