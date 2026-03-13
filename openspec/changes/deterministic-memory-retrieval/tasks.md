## 1. Retrieval planning foundation

- [x] 1.1 Add deterministic retrieval planning types and runtime hard-scope resolution for automatic recall.
- [x] 1.2 Implement conversation-owned soft-scope derivation, retrieval-mode selection, and request-plan logging behind a feature flag.
- [x] 1.3 Add structured observability and degraded reason codes for scope resolution, planning, and fallback stages.

## 2. Write-time retrieval metadata

- [x] 2.1 Extend durable memory proposal validation and persistence to store anchors, aliases, facets, recall metadata, and freshness fields needed for deterministic retrieval.
- [ ] 2.2 Add optional bundle slots and sparse relation persistence with strict confidence and policy gates.
- [x] 2.3 Add contract validation tests that fail malformed or incomplete retrieval metadata before it reaches SQLite.

## 3. Deterministic recall execution

- [ ] 3.1 Implement SQLite candidate selection with hard-scope, policy, sensitivity, recall-mode, and expiry filters.
- [ ] 3.2 Implement deterministic reranking and bounded bundle assembly using stored aliases, facets, anchors, and slots.
- [ ] 3.3 Replace automatic recall sidecar planning in `LlmSessionActor` with the deterministic pipeline and minimal in-scope fallback behavior.

## 4. Intentional search alignment and diagnostics

- [ ] 4.1 Update explicit memory search flow to reuse deterministic planning where appropriate while preserving the existing 4-tool surface.
- [ ] 4.2 Add operator diagnostics for request plans, candidate sets, ranking reasons, retrieval mode, and degraded-stage reporting with policy-safe redaction.
- [ ] 4.3 Verify older low-structure memories degrade safely in ranked search and do not break bundle assembly.

## 5. Evaluation and rollout gates

- [ ] 5.1 Add smoke evals covering request planning, recall precision, noise suppression, and degraded fallback without live providers.
- [ ] 5.2 Add sanitized realistic evals covering ranked retrieval, bundle retrieval, scope safety, and latency thresholds on the default profile.
- [ ] 5.3 Wire deterministic retrieval stability thresholds into the rollout path and document feature-flag enablement and rollback behavior.
