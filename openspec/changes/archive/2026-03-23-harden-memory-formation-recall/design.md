## Context

Phase A targets memory hygiene defects in the current memory pipeline: low-value turn-completion snapshots are admitted too often, auto-recall includes conversational noise, and explicit save acknowledgements can claim success before durable write completion. The existing architecture already has checkpoint-driven curation, policy envelopes, automatic pre-turn recall, and explicit memory tools; this change hardens deterministic admission and retrieval behavior without adding a new persistence ledger.

The change spans multiple modules: session turn lifecycle, checkpoint candidate extraction, memory persistence acknowledgement flow, and recall ranking/filtering. It also changes eval expectations and operational rollout gates. Slack thread sessions (`{channelId}/{threadTs}`) and default-deny policy behavior remain unchanged.

## Goals / Non-Goals

**Goals:**
- Define deterministic Phase A memory classes and eligibility tiers that separate durable memory from conversational trace.
- Enforce structure-first auto-save admission rules for automatic checkpoint extraction.
- Enforce deterministic auto-recall filtering and ranking that prioritizes durable explicit memories and suppresses turn-completion chatter.
- Make explicit save acknowledgements truthful by tying success responses to confirmed durable writes.
- Add measurable rollout and evaluation thresholds (smoke vs realistic suites, stability gates) to block unsafe promotion.

**Non-Goals:**
- No candidate/promotion ledger, promotion state machine, or promotion analytics (Phase B).
- No new model-dependent phrase heuristics as primary admission signal.
- No policy scope broadening, ACL bypasses, or sensitive-data recall relaxation.
- No migration to new external memory backend; this is behavior hardening on current architecture.

## Decisions

### Decision: Introduce deterministic memory classes and eligibility states

Memory items are classified at formation time into deterministic classes derived from metadata and source path, not phrase-matching:
- `durable_explicit`: explicit save intent (`store_memory` or equivalent explicit save command path), valid schema, policy-allowed.
- `durable_inferred`: non-explicit but structured high-value checkpoint candidate that passes novelty/confidence gates.
- `conversation_trace`: turn-local conversational snapshots, acknowledgements, politeness, and execution trace summaries.

Eligibility rules:
- Auto-recall eligible by default: `durable_explicit` and `durable_inferred` (subject to policy envelope).
- Auto-recall ineligible by default: `conversation_trace`.
- Explicit/manual retrieval may still access `conversation_trace` when authorized for diagnostics.

Rationale: Class-based rules are deterministic, testable, and avoid brittle linguistic matching.

Alternative considered: keep a single memory type and tune rank weights only. Rejected because ranking alone does not prevent polluted recall bundles and is harder to reason about under drift.

### Decision: Structure-oriented auto-save admission gate before persistence enqueue

Automatic checkpoint extraction adds a strict admission pipeline:
1. Validate candidate shape and mandatory metadata (anchor/context, source type, policy envelope).
2. Reject conversational trace class for durable auto-save path.
3. Enforce minimum confidence threshold and novelty threshold against near-duplicate recent durable entries.
4. Enforce policy constraints (`domain`, `sensitivity`, `recallMode`) before persistence enqueue.

Explicit saves bypass conversational-trace rejection but still require schema+policy validation.

Rationale: deterministic ordered gates provide predictable behavior and reduce storage pollution.

Alternative considered: curator-only LLM selection. Rejected for non-determinism and poor repeatability.

### Decision: Recall pipeline hard filter then deterministic ranking

Auto-recall execution order:
1. Hard filter by policy envelope and eligibility class.
2. Exclude `conversation_trace` unless a specific diagnostic mode is active.
3. Rank remaining items by deterministic score components: explicitness class, recency, confidence, anchor relevance, and novelty-to-current-turn.
4. Apply bounded recall budget and inject top-N results.

Rationale: hard filters prevent accidental contamination; rank tuning then optimizes useful ordering.

Alternative considered: similarity-only top-K retrieval. Rejected because high-similarity conversational trace can dominate without class guards.

### Decision: Truthful explicit-save acknowledgement protocol

Session actor acknowledgement semantics:
- `save_success` response is emitted only after durable write confirmation from memory persistence actor/tool result.
- If write fails/timeouts, user receives `save_failed` (or degraded) acknowledgement with retry guidance.
- No optimistic success text before durable outcome.

Actor boundaries:
- Session actor owns user-visible acknowledgement.
- Memory/persistence actor owns commit result and error classification.
- Subagents cannot emit durable-save success directly; parent session owns final acknowledgement.

Rationale: prevents user trust violations and aligns acknowledgement with real durability.

Alternative considered: optimistic success with later correction. Rejected as misleading and hard to reason about in async failures.

### Decision: Two-tier eval and stability gates for rollout

Define required gates:
- Smoke suite: deterministic synthetic fixtures for fast regressions.
- Realistic suite: larger sanitized fixture set approximating real conversational flows.

Promotion thresholds (Phase A):
- Auto-recall precision on durable targets >= 0.85 (smoke) and >= 0.78 (realistic).
- Conversation-trace leakage in auto-recall <= 0.05 (smoke) and <= 0.10 (realistic).
- Explicit save acknowledgement truthfulness = 1.00 in both suites.
- Stability gate: thresholds must pass in 3 consecutive runs on main branch evaluation profile.

Rationale: balances fast feedback and robustness while avoiding overfitting to tiny smoke fixtures.

## Risks / Trade-offs

- [Risk] Over-filtering may hide useful context that was previously recalled. -> Mitigation: manual explicit retrieval remains available; monitor misses in realistic suite.
- [Risk] Deterministic thresholds may require tuning across domains. -> Mitigation: keep threshold config centralized and versioned; gate changes with eval reruns.
- [Risk] Ack truthfulness may increase visible failure responses during transient outages. -> Mitigation: clear retry language and degraded-memory diagnostics in prompt context.
- [Risk] Classifier mistakes at formation time can suppress relevant memory. -> Mitigation: deterministic classing tests with synthetic boundary fixtures; Phase B ledger for richer lifecycle correction.
- [Risk] Added gating may increase recall/write latency. -> Mitigation: keep classing and rank features metadata-driven; enforce bounded budgets and async processing.

## Migration Plan

1. Implement classing schema and admission gates behind a feature flag (`MemoryHygienePhaseAEnabled`).
2. Add truthful save acknowledgement path and failure-mode responses.
3. Enable deterministic recall hard filters/ranking while keeping legacy telemetry comparison.
4. Run smoke suite in CI-required path and realistic suite in required pre-merge or nightly gate.
5. Promote Phase A defaults only after 3 consecutive passing runs and no policy regressions.
6. Rollback strategy: disable `MemoryHygienePhaseAEnabled` to restore prior behavior while retaining added diagnostics.

## Open Questions

- Should realistic-suite stability be gated pre-merge only or also as a release branch check?
- Should conversation-trace diagnostics be exposed only to operator/debug mode or all manual retrieval workflows?
- What exact numeric confidence/novelty thresholds best fit default MVP profile without domain-specific overrides?

## Deferred to Phase B

- Candidate/promotion ledger with promotion states and audit timeline.
- Promotion-aware ranking signals and lifecycle analytics.
- Automated promotion/demotion tooling and long-horizon memory maintenance workflows.
