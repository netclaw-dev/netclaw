# Proposal: memory-core-redesign

Source PRD: PRD-007 (agent personality and local memory). Evidence base:
`docs/research/memory-audit-2026-07.md` (July 2026 measured audit) and
`docs/research/memory-recall-findings-2026-05.md` (May 2026 autoresearch).

## Why

The memory system's two intelligence layers have never functioned — the LLM
curation tier has zero successful decisions in its production lifetime and
recall ranks by a lexical score measured to carry no relevance signal — so the
corpus accretes near-duplicates (14% redundant, doubling in five weeks) while
automatic recall injects 46% pollution into live turns (19% of recall events
actively misleading). The quick-win slice (July 2026) stopped the worst
bleeding; this change adds the missing semantic judgment and lifecycle, and
removes the dead structure that three redesign cycles left behind.

## What Changes

- **Semantic judgment (embeddings).** In-process ONNX embedding infrastructure
  (snowflake-arctic-embed 137M int8, CPU, zero sidecars): embed-on-write, a
  brute-force in-memory vector index, and model provisioning with a pinned
  hash-verified allowlist, downloaded at daemon initialization.
- **Write-side dedup becomes nominate→decide.** Embedding kNN nominates
  near-duplicates (τ≈0.86, k=5, config); any nominee forces the LLM curation
  tier to decide merge/enrich/keep. No cosine auto-merge tier (measured: no
  threshold separates duplicates from siblings). One shared curation evaluator
  replaces the two divergent pipelines.
- **Lossless merges.** CONSOLIDATE/UPDATE produce an LLM-synthesized merged
  body validated by a deterministic MergeGuard (load-bearing-token retention)
  with a structural-append fallback — the raw `markdown_body` overwrite path
  becomes unreachable. **BREAKING** for any consumer that assumed merge ==
  replace.
- **Read-side hybrid recall with an absolute relevance floor.** Query
  embedding + FTS5 union, weighted fusion, and a cosine floor calibrated
  against the real-traffic gold set — turns where nothing is relevant inject
  nothing (measured: 65% of real queries).
- **Taxonomy rebalance around real behaviors.** `Searchable` recall mode is
  removed from the automatic pool (**BREAKING** semantic fix: today
  searchable ⊂ auto); durable-fact formation defaults to searchable with auto
  reserved for identity/preferences/environment; `Trace` (72 h short-lived
  memory) gets a reachable producer and a recallable-while-fresh mode; an
  expiry sweep actually deletes expired rows.
- **Tool-use lessons.** New `tool_lesson` memory class (Document/merge/
  searchable) anchored per tool, captured explicitly (`store_memory`) and by
  the sidecar distillation prompt; recalled via per-tool context injection on
  first tool use per session — outside the pre-turn recall budget.
- **Maintenance tooling.** `netclaw memory` CLI group: `backfill-embeddings`,
  `consolidate --dry-run` (ratification plan file) / `--apply --plan` (gated,
  backup-first), `status`; maintenance-run ledger table.
- **Subtraction.** Remove: the unused `memory_edges` graph, the inert
  4-demo-facet planner inference, the dead `verified-tool-finding` +25 recall
  bonus, and gate the checkpoint worker's turn-complete lane at enqueue time
  (95% of its intake is dropped by design today). **BREAKING** only at the
  schema-surface level; no functional behavior depends on any of these
  (verified by audit).

## Capabilities

### New Capabilities

- `memory-embeddings` — ONNX embedding runtime: model provisioning
  (allowlist, SHA-256, atomic download), embed-on-write, vector index, and
  loud-degradation semantics (doctor check, daemon status, structured logs;
  lexical recall keeps serving but never silently).
- `memory-maintenance` — operator-driven corpus lifecycle: embedding
  backfill, consolidation dry-run/apply with human ratification and
  backup-first apply, expiry sweep, `netclaw memory status`, maintenance
  ledger.

### Modified Capabilities

- `netclaw-agent-memory` — hybrid recall + absolute cosine floor and
  injection semantics; nominate→decide curation with lossless merge; recall-
  mode semantics fix (searchable leaves the automatic pool); trace revival
  (producer, fresh-recall, deletion); tool-lesson class + per-tool context
  injection; formation-side recall-mode assignment; removal of graph-edge and
  facet-inference requirements (the spec's flagged open decisions — "keyword
  vs vector search, embedding strategy, injection budgets" — are resolved by
  this change).

## Impact

- **Code**: `src/Netclaw.Embeddings` (new project), `Netclaw.Actors/Memory`
  (curation evaluator, store schema + vector queries, policy gates, enums),
  `Netclaw.Actors/Sessions` (recall coordinator, tool-execution pipeline for
  lessons), `Netclaw.Daemon` (DI, warmup hosted service, checkpoint gating),
  `Netclaw.Cli` (memory command group, doctor checks),
  `Netclaw.Configuration` (Memory.Embeddings/Recall/Curation config objects +
  schema sync), observer sidecar distillation prompt, `netclaw-memory` system
  skill.
- **Dependencies**: `Microsoft.ML.OnnxRuntime` (CPU; linux-x64 + linux-arm64),
  `FastBertTokenizer`, `System.Numerics.Tensors`. Model artifact (~90–140 MB)
  distributed at runtime, never embedded in the binary.
- **Data**: new `memory_embeddings` and `memory_maintenance_runs` tables
  (owned by `SQLiteMemoryStore.InitializeAsync`, not daemon migrations);
  one-time ratified consolidation pass over the existing corpus (operator-
  gated; out of automatic paths); expiry sweep begins deleting expired
  records.
- **Evals**: recall-quality gold-set regression suite (real-traffic gold from
  the audit); eval cases for tool lessons and zero-injection behavior; the
  scenario suite's paraphrase-gap case (P09) flips back to expected-recall.

### In scope (MVP)

Slices 2–6 as designed: embedding foundation; write-side nominate→decide +
lossless merge; read-side hybrid + cosine floor; taxonomy rebalance + tool
lessons + trace revival + expiry sweep; maintenance CLI + subtraction items.

### Out of scope

- Multilingual embedding models (future pass; vectors are keyed by
  `model_id`, thresholds live in config next to `ModelId`, so a model swap is
  `config change + backfill --force`; re-evaluate .NET SentencePiece
  tokenizer support then).
- ANN indexes (brute-force cosine is sub-ms at ≤50k vectors).
- Mirroring the model artifact into the R2 feeds infra (post-PoC decision;
  pinned HF URLs first).
- Structural (code-level) detection of tool-use corrections (explicit +
  sidecar capture only).
- Applying consolidation to any live corpus as part of this change's
  implementation (tooling ships; each apply run remains an operator decision).

## Security and Operational Impact

- **Model supply chain**: `ModelId` selects from a pinned in-code allowlist
  (id → URL + size + SHA-256); arbitrary URLs are rejected; downloads are
  atomic (temp + rename) and hash-verified before load. A failed or missing
  model is a **loud** degraded state (doctor Error, daemon status
  `embeddings: degraded`, rate-limited structured logs) — lexical recall
  keeps serving; no silent fallback.
- **Policy parity**: vector-sourced recall candidates pass the identical
  audience/boundary/sensitivity/recall-mode gates as lexical candidates
  (scenario-tested requirement, not an implementation detail).
- **Destructive-operation gating**: consolidation `--apply` executes only a
  previously written, human-editable plan file, refuses a live daemon by
  default, and takes a `VACUUM INTO` backup first. The expiry sweep deletes
  only rows already invisible to every recall/search path.
- **Resource envelope**: measured on the reference box (i9-9900K) — full
  1,216-doc backfill 4.5–8.3 min, <0.5 GB RSS; steady-state embed-on-write
  ~13 docs/day. The recall-time query-embedding sub-budget must be
  re-measured on the ONNX int8 path before the hybrid slice ships (Ollama
  measurements ran 4–30× above the design assumption for full documents;
  queries are far shorter).
- **Operations**: new doctor checks (embedding provisioning/coverage,
  curation-LLM health — the latter shipped with the quick-win slice);
  `netclaw memory status` becomes the corpus-health surface; runbook
  `docs/runbooks/memory-health-and-evals.md` gains embedding/consolidation
  sections.
