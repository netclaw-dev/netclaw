## Why

The current memory redesign fixes storage ownership and checkpointing, but two quality gaps remain: automatic recall still starts from weak lexical guesses, and durable fact formation still misses strong user assertions while research passages have no first-class evidence layer. We need bounded LLM help for observation and recall planning now, but writes must stay deterministic, policy-gated, and session-owned rather than becoming direct model writes.

## What Changes

- Add a `MemoryObservationSidecar` that consumes sanitized turn summaries and returns structured `MemoryProposal` items classed as `durable_fact`, `evidence`, or `trace`.
- Add a `RecallPlanningSidecar` that consumes the current user turn plus recent context and returns a structured `RecallQueryPlan` instead of relying on raw lexical query generation.
- Insert deterministic policy and schema gates between sidecar proposals and SQLite writes so sidecars never write durable memory directly.
- Split recall behavior into two paths: automatic recall stays bounded and `durable_fact` only; intentional search can search `durable_fact` plus `evidence`.
- Add freshness and expiry semantics for `evidence` and short-lived `trace`, and clarify that `SOUL.md` is only for narrow identity/profile updates rather than general facts or evidence capture.
- Reuse the existing lightweight session sidecar pattern first for these one-shot structured calls; keep `SubAgentActor` as a later option for multi-step, tool-using memory workflows.
- Redesign evals around formation-then-recall, evidence-vs-durable separation, and deterministic write-gate correctness rather than only pre-seeded recall fixtures.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-agent-memory`: add sidecar-planned observation/recall contracts, memory classes, evidence expiry, deterministic write gates, and `SOUL.md` boundary rules.
- `netclaw-session`: add bounded sidecar execution to the turn pipeline for recall planning and post-turn memory observation while preserving degraded-mode behavior.
- `netclaw-testing`: redesign memory evals to cover formation, recall, evidence separation, policy-gate rejection, and stability thresholds.

## Impact

- Affected systems: `LlmSessionActor` turn orchestration, sidecar execution helpers, checkpoint enqueue flow, `MemoryCurationPipeline`, SQLite memory schema/query layer, explicit memory search tools, and memory eval harnesses.
- Data/model impact: new structured sidecar contracts, memory-class metadata, evidence expiry metadata, and recall-plan execution clamps.
- Security/privacy impact: sidecars receive sanitized summaries only; deterministic gates remain the only path to SQLite writes; `SOUL.md` updates remain narrow identity/profile operations and never absorb tool passages or general evidence.
- Operational impact: adds new bounded sidecar calls to hot paths, requires degraded-mode handling on timeout/schema failure, and adds rollout gates for schema validity, write-gate correctness, recall quality, and eval stability.
- Out of scope: direct LLM durable writes, free-form sidecar tool access, broad `SOUL.md` self-editing from observed facts, and replacing the existing explicit memory tools with a new user-facing API.

### PRD Traceability

- `PRD-007` (persistent local memory, reliable cross-session recall, and identity boundaries)
- `PRD-001` (predictable MVP behavior and bounded autonomous assistance)
- `PRD-002` (default-deny, fail-closed, policy-gated persistence)
