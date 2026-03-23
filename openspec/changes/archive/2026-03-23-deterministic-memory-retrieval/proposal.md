## Why

The current memory hot path still depends on sidecar-planned recall and weak degraded lexical fallback, which makes recall quality sensitive to timeouts, JSON drift, and planner instability. We now have deterministic retrieval research and PoC results showing Netclaw can move automatic recall onto a faster, explainable, runtime-owned path without giving up policy controls or bounded behavior.

## What Changes

- Replace per-turn LLM recall planning on the automatic recall path with deterministic request planning, candidate selection, reranking, and bundle assembly owned by runtime code.
- Add a write-time deterministic retrieval metadata contract so durable memories carry anchors, aliases, facets, slots, and sparse relations needed for reliable read-time recall.
- Clarify retrieval modes so automatic recall remains bounded and policy-filtered while intentional search can use the same deterministic planner with broader retrieval classes where allowed.
- Add explainability and degraded-mode requirements for request plans, candidate selection, ranking reasons, and fallback behavior.
- Define rollout and validation gates for deterministic retrieval quality, latency, and policy-safe behavior.
- Keep direct durable writes, vector-store dependency, and new user-facing memory tool names out of scope for this MVP slice.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-agent-memory`: change retrieval behavior to a deterministic, SQLite-native request-planning and ranking pipeline, and require write-time metadata that makes deterministic recall viable.
- `netclaw-session`: replace automatic recall sidecar planning in the user-facing turn pipeline with deterministic request planning, bounded execution, and explainable degraded fallback.
- `netclaw-testing`: add deterministic retrieval evals and rollout gates for latency, recall quality, noise suppression, and policy-safe degradation.

## Impact

- Affected systems: `LlmSessionActor` turn orchestration, recall planning/execution helpers, SQLite memory query layer, memory formation pipeline, and eval harnesses.
- Data/model impact: durable memory records need stable retrieval metadata such as aliases, facets, anchor hints, optional slots, and sparse relations.
- Security/privacy impact: hard scope remains runtime-owned, policy filtering stays deterministic, and automatic recall must fail closed when scope, sensitivity, or expiry checks fail.
- Operational impact: adds debug surfaces for retrieval plans/candidates/reasons and rollout gates for deterministic recall latency and quality before default enablement.
- In scope for MVP: deterministic automatic recall over SQLite with explainable ranking and shared intentional-search planning.
- Out of scope for MVP: vector embeddings, direct LLM recall planning on the hot path, new public memory APIs, and policy-bypassing retrieval shortcuts.

### PRD Traceability

- `PRD-007` (persistent local memory, reliable cross-session recall, and local-memory behavior)
- `PRD-001` (predictable MVP behavior, bounded latency, and dependable recall)
- `PRD-002` (default-deny, fail-closed, policy-gated memory access)
