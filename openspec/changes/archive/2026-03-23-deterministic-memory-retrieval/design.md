## Context

Netclaw's current memory direction is already SQLite-first, policy-gated, and session-owned, but the automatic recall hot path still carries planner-style fragility: sidecar latency, JSON contract drift, and degraded lexical fallback can all suppress useful recall at the exact moment a user turn needs it. The deterministic retrieval PoCs and research notes show a better shape: keep scope, planning, candidate selection, ranking, and bundle assembly in runtime-owned code, and push semantic cost toward write time where latency is less sensitive.

This change cuts across session turn orchestration, memory write contracts, SQLite query/ranking behavior, and the eval harness. Slack thread identity, default-deny policy, explicit memory tools, and session-owned durable writes remain unchanged.

## Goals / Non-Goals

**Goals:**
- Move automatic recall off per-turn LLM planning and onto a deterministic pipeline with bounded latency.
- Make retrieval scope runtime-owned so legal memory boundaries come from Slack/session metadata and policy, not model inference.
- Require write-time retrieval metadata so SQLite-backed recall has stable anchors, aliases, facets, slots, and sparse relations.
- Support both ranked retrieval and bundle retrieval while keeping automatic recall policy-safe and explainable.
- Preserve degraded behavior so user-facing turns continue when retrieval planning, query, or ranking fails.
- Add measurable rollout gates for recall quality, noise suppression, latency, and policy-safe failure behavior.

**Non-Goals:**
- No new user-facing memory tool names or replacement of the explicit 4-tool surface.
- No vector store, embeddings, or ANN dependency in this slice.
- No direct sidecar or subagent writes into durable memory.
- No policy broadening, ACL bypass, or sensitivity relaxation.
- No requirement that every turn perform expensive bundle assembly when deterministic activation is low.

## Decisions

### Decision: Automatic recall uses a four-tier deterministic pipeline

Automatic recall will run as a runtime-owned pipeline:
1. resolve hard scope from Slack/session/runtime metadata and policy
2. build a deterministic retrieval request plan from prompt text, thread/topic hints, and active anchors
3. run cheap SQLite candidate selection with policy and freshness filters
4. rerank candidates deterministically and optionally assemble a bounded bundle before prompt injection

Rationale: this keeps the hot path explainable, bounded, and independent of sidecar JSON quality.

Alternative considered: keep `RecallPlanningSidecar` on the hot path with a stronger fallback. Rejected because planner timeout and schema drift remain first-order failure modes, and fallback quality is still too weak.

### Decision: Hard scope is security-boundary-owned; subject scope is runtime/project-owned; soft scope is conversation-owned

The legal retrieval universe comes from a runtime-owned security boundary plus policy envelope. Subject scope then comes from configured project bindings, anchor mappings, known repo/entity identities, and other runtime metadata. Channel/session identity is only a hint for deriving the boundary or subject scope and SHALL NOT be the durable hard scope for reusable project knowledge. The conversation can only influence soft narrowing signals such as named entities, thread title, recent topic, active anchors, and speaker profile.

Rationale: this preserves fail-closed behavior and prevents the model from inferring itself into unauthorized memory domains.

Alternative considered: infer scope entirely from prompt semantics. Rejected because it weakens policy guarantees and makes recall behavior harder to debug.

Additional evidence: issue #203 shows that deriving hard scope from raw Slack channel identity hides DM-learned Netclaw repository memories from later private-channel sessions, even though both contexts should have been able to reuse the knowledge inside the same personal/private boundary.

### Decision: Write-time metadata becomes a required contract for durable memory

Durable memory formation must emit stable retrieval metadata including memory class, subject, anchor, aliases, coarse facets, optional bundle slots, sparse relations, recall mode, sensitivity, confidence, and freshness/expiry. Automatic recall will depend on that metadata instead of raw body-text search alone.

Rationale: semantic work is cheaper and safer at write time than in the user-facing hot path, and deterministic retrieval quality depends on stable structure.

Alternative considered: keep read-time heuristics over existing text fields only. Rejected because flat text retrieval is too noisy for explainable ranked and bundle retrieval.

### Decision: Ranked and bundle retrieval share one request-planning contract

The deterministic planner will select either ranked-hit retrieval or bundle retrieval. Direct prompts use ranked mode; composite prompts use bundle mode so the runtime can assemble multiple answer ingredients without relying on the LLM to perform ad hoc memory search orchestration.

Rationale: some prompts ask for one best fact while others ask for a composed answer; using one retrieval shape for both underperforms.

Alternative considered: top-N ranked results only. Rejected because composite prompts often need slot-like assembly rather than one best document.

### Decision: Intentional search reuses deterministic planning but remains a separate path

The explicit memory tools keep their current names and deliberate/manual role, but intentional search can reuse the deterministic planner with broader allowed memory classes where policy permits. Automatic recall remains tighter and more latency-sensitive than intentional search.

Rationale: one planner contract reduces duplicated logic while preserving distinct policy and UX behavior between auto recall and manual search.

Alternative considered: split automatic and intentional search into unrelated implementations. Rejected because it duplicates scope and ranking logic and makes evals harder to compare.

### Decision: Explainability is a product requirement, not just a debug convenience

The runtime must be able to surface the request plan, candidate set, ranking reasons, selected retrieval mode, and degraded reason codes for diagnostics and offline tuning.

Rationale: explainability is the main advantage of deterministic retrieval over hot-path LLM planning and is necessary for rollout confidence.

Alternative considered: log only final injected memories. Rejected because it hides whether failures happen in planning, selection, or reranking.

## Risks / Trade-offs

- [Risk] Deterministic planning may miss useful recall on ambiguous prompts. -> Mitigation: keep intentional search available, invest in write-time aliases/facets, and gate rollout on realistic eval suites.
- [Risk] Metadata extraction quality may become the new bottleneck. -> Mitigation: make the extractor contract explicit, validate it independently, and keep fields small and testable.
- [Risk] Bundle retrieval adds hot-path complexity. -> Mitigation: keep activation conservative, clamp candidate and token budgets, and allow ranked-only fallback.
- [Risk] Strong hard-scope rules could hide reusable project facts behind channel-local scope IDs. -> Mitigation: derive hard scope from security boundary and subject bindings, then use soft scopes and policy filters for narrowing.
- [Risk] Debug surfaces could leak sensitive retrieval context. -> Mitigation: apply the same policy and redaction rules to diagnostics, and keep sensitive bodies out of routine logs.

## Migration Plan

1. Introduce deterministic request-planning types and logging behind a feature flag without changing injected recall behavior.
2. Extend write-time memory extraction and SQLite persistence to store retrieval metadata required by deterministic planning and ranking.
3. Add deterministic candidate selection, reranking, and bundle assembly in parallel with the current hot path.
4. Route automatic recall through the deterministic pipeline behind a feature flag while retaining degraded fallback and observability.
5. Update intentional search to reuse deterministic planning where appropriate.
6. Run smoke and realistic eval gates until thresholds pass, then enable deterministic recall by default.
7. Roll back by disabling the feature flag and returning to the legacy recall path while retaining logged retrieval plans for analysis.

## Failure Modes And Recovery Behavior

- Scope resolution failure: treat memory as degraded, skip automatic recall, and continue the turn without widening scope.
- Request-planning failure: fall back to a minimal deterministic lexical/anchor plan constrained to the resolved hard scope.
- Candidate-selection or SQLite failure: continue the turn without recall injection and record degraded diagnostics.
- Reranking or bundle-assembly failure: fall back to ranked candidates if safe, otherwise continue without recall.
- Missing retrieval metadata on older memories: treat those rows as lower-confidence candidates or exclude them from bundle assembly until rewritten or refreshed.

## Open Questions

- Should bundle mode run only on clearly composite prompts, or should it also activate for some thread-title-driven workflows?
- How aggressively should older low-structure memories be excluded versus tolerated during rollout?
- Which retrieval-plan and ranking diagnostics belong in `netclaw status` or operator tooling versus debug-only logs?
