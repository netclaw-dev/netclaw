## Why

Phase A memory behavior currently over-admits low-value conversational trace into durable memory and auto-recall, which reduces recall precision and can mislead users about what was actually saved. We need deterministic memory hygiene rules now so recall quality and save acknowledgements are trustworthy before introducing a larger candidate-promotion ledger in Phase B.

## What Changes

- Tighten memory classing and eligibility so explicit durable memories are recall-eligible while low-value conversational snapshots are excluded or strongly deprioritized.
- Add deterministic admission rules for automatic memory formation focused on structure and metadata (source, intent, confidence, novelty, policy class), not brittle literal phrase matching.
- Add deterministic auto-recall filtering/ranking rules that gate out conversational trace by default and prioritize durable explicit/documented facts.
- Require explicit save acknowledgements to be emitted only after successful durable write confirmation; failed writes produce failure/degraded acknowledgements instead of success claims.
- Define Phase A rollout/evaluation gates, including smoke vs realistic suite expectations, stability thresholds, and pass/fail criteria.
- Document deferred Phase B work for candidate/promotion-ledger mechanics so Phase A scope remains implementation-focused and bounded.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-agent-memory`: Tighten deterministic auto-save admission, memory class eligibility, auto-recall ranking/filtering, and truthful explicit-save acknowledgement behavior for Phase A.
- `netclaw-testing`: Add/clarify eval and stability gate expectations for memory smoke suites versus realistic suites, including sanitized fixture requirements.

## Impact

- Affected systems: session memory formation pipeline, checkpoint/candidate extraction rules, recall query/ranking pipeline, and explicit memory tool acknowledgement path.
- Affected docs/specs: memory architecture/design docs, memory eval docs, and OpenSpec deltas for `netclaw-agent-memory` and `netclaw-testing`.
- Security/privacy impact: no relaxation of policy envelope; tests and examples must use synthetic/sanitized fixtures only (no real PII or secrets).
- Operational impact: adds measurable rollout gates for recall-noise suppression, write-truthfulness, and suite stability before enabling Phase A defaults.
- Out of scope (Phase A): candidate/promotion ledger, long-horizon promotion lifecycle tooling, and ledger-driven analytics/reporting (deferred to Phase B).

### PRD Traceability

- `PRD-007` (agent local memory quality, persistence trust, and memory behavior expectations).
- `PRD-001` (MVP reliability and deterministic behavior posture).
- `PRD-002` (security/privacy defaults and fail-closed operational stance).
