## 1. Structured sidecar foundation

- [x] 1.1 Extract the existing title-generation pattern into a reusable session sidecar runner for one-shot JSON-schema-bound calls with timeout, logging, and typed result handling.
- [x] 1.2 Add configuration and observability for memory sidecars (planner/observer invocation counts, timeout/failure counters, degraded-mode reasons) using the existing session sidecar timeout model.
- [x] 1.3 Add contract types and serializers for `MemoryObservationRequest`, `MemoryProposal`, `RecallPlanningRequest`, and `RecallQueryPlan`.

## 2. Memory observation and deterministic write gating

- [x] 2.1 Build sanitized turn-summary assembly for observation inputs from current turn summaries, tool findings, accepted subagent findings, and session context.
- [x] 2.2 Implement `MemoryObservationSidecar` and `MemoryProposalGate`, including schema validation, source-to-class rules, dedupe, policy checks, expiry derivation, and `SOUL.md` boundary rejection.
- [x] 2.3 Route accepted observed proposals through the existing checkpoint sink and memory curation worker without introducing a direct sidecar write path.
- [x] 2.4 Extend SQLite memory persistence to store `memory_class`, expiry, and evidence provenance metadata, with tests for `durable_fact`, `evidence`, and `trace` handling.

## 3. Recall planning and search-path separation

- [x] 3.1 Build sanitized recall-planning inputs from the current user turn, recent session summary, active anchors, and policy scope.
- [x] 3.2 Implement `RecallPlanningSidecar` and `RecallPlanGate`, including hard clamps that force automatic recall to `durable_fact` only and intentional search to `durable_fact + evidence`.
- [x] 3.3 Update automatic recall execution in `LlmSessionActor` to use planned queries with degraded lexical fallback on timeout/schema failure.
- [x] 3.4 Update explicit `find_memories` / `get_memories` behavior to use intentional-search planning and evidence-aware hydration while keeping `trace` out of normal results.

## 4. Identity boundary and freshness semantics

- [x] 4.1 Enforce narrow `SOUL.md` eligibility so only identity/profile changes can route to identity-file workflows and general facts/evidence remain in SQLite memory.
- [x] 4.2 Implement expiry defaults and stale-result handling for `evidence` and `trace`, including automatic exclusion from auto recall and optional stale markers for intentional search.
- [x] 4.3 Add cleanup and query tests proving expired `evidence`/`trace` do not leak into automatic recall and only appear in intentional/debug paths when policy allows.

## 5. Eval redesign and rollout gates

- [x] 5.1 Add end-to-end eval suites for `formation_then_auto_recall`, `formation_then_intentional_search`, `evidence_vs_durable_separation`, `proposal_gate_rejection`, `soul_boundary`, and `expiry_and_staleness` using synthetic/sanitized fixtures only.
- [x] 5.2 Implement reporting and assertions for proposal schema validity, gate correctness, durable-fact formation precision, auto-recall hit rate, evidence leakage, intentional-search evidence hit rate, and explicit write truthfulness.
- [x] 5.3 Wire smoke and realistic stability gates with the required consecutive-pass thresholds and local-Ollama primary gate configuration.

## 6. Specs, docs, and validation

- [x] 6.1 Update memory/session guidance and relevant docs to explain the new sidecar-assisted memory model, recall-path split, evidence layer, and `SOUL.md` boundary.
- [x] 6.2 Sync implementation details with the `netclaw-agent-memory`, `netclaw-session`, and `netclaw-testing` spec deltas for this change.
- [x] 6.3 Run `openspec validate --change add-memory-observer-and-recall-planner --strict` and resolve all validation issues.
