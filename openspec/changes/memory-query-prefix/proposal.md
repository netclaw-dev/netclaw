# Proposal: memory-query-prefix

## Why

Production embeds recall queries with `snowflake-arctic-embed-m` but omits the
model's documented retrieval query prefix, silently forfeiting most of the
model's retrieval quality: measured on `gold-prod-2026-07`, the prefix lifts
F0.5 at the optimal floor from 0.141 to 0.239 (+69% relative), recall@3 from
0.146 to 0.318, and zero-injection accuracy from 13.3% to 26.7%
(`~/recall-research-local/2026-07/arctic-prefix-eval/RESULTS.md`, model card
confirmed for the exact pinned HF commit). The fix is query-side only — no
stored document vectors change — but it is **not drop-in**: the prefix
compresses arctic's cosine distribution downward (optimal floor shifts
0.68 → ~0.24), and prefixed queries against the current 0.68 floor measure
F0.5 = 0.0. Prefix and floor recalibration must ship atomically.

Source PRD: PRD-007 (agent personality and local memory) — same lineage as
memory-core-redesign, which this change amends at the embedding-foundation
seam.

## What Changes

- `IMemoryEmbedder` gains a query-vs-passage distinction (embedding purpose)
  so retrieval queries can carry a model-specific prefix while embed-on-write,
  backfill, and the dedup nominator (document↔document comparisons) remain
  unprefixed. Stored vectors are unaffected; no re-embed or backfill is
  required.
- `EmbeddingModelProvisioner`'s allowlist entries carry per-model retrieval
  metadata: the documented `QueryPrefix` (empty for models that use none) and
  a `CalibratedMinCosineSimilarity` for the prefixed configuration — the same
  manifest-carries-calibration pattern the relevance gate established with
  `CalibratedThreshold`. This prepares the int8 variant, whose floor differs.
- `Memory.Recall.MinCosineSimilarity` becomes nullable: null (default) follows
  the active model's manifest calibration; an explicit value overrides it.
  **BREAKING** for configs that pinned the old 0.68 default explicitly — the
  numeric meaning of the floor changes under the prefixed embedder, and the
  schema description must say so.
- The recall coordinator applies the active model's query prefix when
  embedding the turn query; the floor it enforces resolves from config-or-
  manifest.
- Recalibration recorded: the prefixed fp32 sweep becomes the calibration of
  record in memory-core-redesign's design.md D6 lineage (superseding the
  no-prefix 0.68 calibration) via this change's own design doc; the
  calibration-verification procedure documented by memory-relevance-gate
  covers re-running it.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `memory-embeddings`: embedding runtime requirement gains
  query-vs-passage purpose semantics and manifest-carried retrieval
  calibration (query prefix + calibrated floor) per allowlisted model.
  Note: this capability's base spec is currently the pending delta in
  `openspec/changes/memory-core-redesign/specs/memory-embeddings/spec.md`
  (not yet archived to main specs); this change's delta applies on top of it.
- `netclaw-agent-memory`: the automatic pre-turn recall requirement's
  absolute relevance floor becomes calibration-carried per model variant
  (config override optional) instead of a single static default.

## Impact

- **Code**: `Netclaw.Actors/Memory` (`IMemoryEmbedder`, holder), `Netclaw.
  Embeddings` (`OnnxMemoryEmbedder`, `EmbeddingModelProvisioner` allowlist),
  `Netclaw.Actors/Sessions` (`SQLiteMemoryRecallCoordinator`),
  `Netclaw.Configuration` (`MemoryRecallConfig`, schema), warmup service
  unchanged except plumb-through; doctor output gains the active
  prefix/floor.
- **Data**: none at rest — document vectors unchanged; query embeddings are
  never persisted. No migration, no backfill.
- **Config**: `MinCosineSimilarity` default changes semantics
  (nullable-follows-manifest); schema sync in the same PR per the
  configuration schema sync rule.
- **Security/operational impact**: no new network or supply-chain surface
  (prefix is a string constant in the pinned allowlist; no new artifacts).
  Recall behavior changes for embeddings-enabled deployments only —
  currently experimental/opt-in installs of the 0.25.0-alpha.onnx line; the
  floor-follows-manifest default prevents the catastrophic
  prefix-without-recalibration combination by construction. Doctor and
  `memory_retrieval_final` logging expose the active prefix + floor so a
  mismatch is diagnosable.
- **Evals**: recall-affecting change → eval suite run required (memory
  category); gold-set regression suite thresholds unaffected (fixture
  embedder is prefix-agnostic), but the fixture fake embedder must implement
  the new seam.

### In scope (MVP)

- Prefix + purpose seam + manifest-carried floor for the two allowlisted
  arctic variants (fp32 now; int8 entry lands with its own calibration in the
  int8 productionization task).
- Atomic floor recalibration and config nullability.

### Out of scope

- Switching embedding models (e5-small-v2 evaluated and declined —
  `~/recall-research-local/2026-07/e5-eval/RESULTS.md`).
- Re-running the relevance-gate threshold calibration (S* is measured on
  gate scores, not cosine floors; unchanged).
- Symmetric-task prefixes (arctic documents none; e5-style dual prefixes are
  a model-swap concern).
