# Design: memory-query-prefix

## Context

`OnnxMemoryEmbedder` runs every input — recall queries, embed-on-write
documents, backfill documents, dedup-nominator proposals — through one
`EmbedAsync(text)` path with no notion of purpose. `snowflake-arctic-embed-m`
is an asymmetric retrieval model: its model card (verified at the pinned HF
commit) instructs prefixing **queries** with
`Represent this sentence for searching relevant passages: ` and embedding
**documents** raw. Production has never applied the prefix, so all shipped
retrieval calibration (`MinCosineSimilarity` 0.68, memory-core-redesign D6)
measures the model off its intended operating mode.

Measured on `gold-prod-2026-07` (93 real-traffic queries, 1,216-doc
production snapshot, production-faithful fp32 ONNX replica —
`~/recall-research-local/2026-07/arctic-prefix-eval/`):

| configuration | optimal τ | F0.5 | recall@3 | zero-injection |
|---|:---:|---:|---:|---:|
| no prefix (shipped) | 0.68 | 0.141 | 0.146 | 13.3% |
| with prefix | 0.24 | 0.239 | 0.318 | 26.7% |

The prefix compresses arctic's cosine distribution downward (top-1 median
0.789 → 0.392). At the shipped 0.68 floor, prefixed queries measure
**F0.5 = 0.0** — the two changes are inseparable.

The relevance gate (memory-relevance-gate) already established the pattern
this change needs: model-specific calibration travels in the provisioner's
pinned manifest (`CalibratedThreshold`), and config exposes a nullable
override that follows the manifest when null.

## Goals / Non-Goals

**Goals:**

- Recall queries embed with the active model's documented prefix; all
  document-side embedding is byte-identical to today (no re-embed, no
  migration).
- Floor and prefix change atomically and cannot be recombined incorrectly by
  configuration alone.
- Per-model-variant calibration lives in the allowlist manifest so the int8
  variant (different floor) is an allowlist entry, not a code change.

**Non-Goals:**

- Model swaps (e5 declined; see proposal), symmetric/dual prefixes, reranker
  threshold recalibration, any change to `memory_embeddings` rows at rest.

## Decisions

### D1. Purpose enum on the seam, not a second interface

`IMemoryEmbedder.EmbedAsync` gains an `EmbeddingPurpose` parameter
(`Passage` | `RetrievalQuery`); `EmbedBatchAsync` likewise (a batch has one
purpose). All existing callers pass `Passage` explicitly — embed-on-write,
backfill CLI, gap repair, and the dedup nominator (proposal↔document
comparison is document-space by design; the audit's nominator calibration
τ=0.86 was measured unprefixed and stays valid). Only
`SQLiteMemoryRecallCoordinator`'s turn-query embedding passes
`RetrievalQuery`. No optional parameter: call sites are updated, per the
constitution's required-dependency rule. `UnavailableMemoryEmbedder` and all
test fakes implement the same signature; the fixture fake treats purposes
identically unless a test opts into distinct maps.

*Rejected*: separate `EmbedQueryAsync` method — duplicates the batch/
concurrency plumbing for one string concat; a mis-typed call reads the same
either way. Rejected: prefixing inside the coordinator — the prefix is a
property of the model, not of recall; the embedder owns model semantics.

### D2. Prefix is manifest data, applied inside `OnnxMemoryEmbedder`

`EmbeddingModelManifestEntry` gains `QueryPrefix` (string, may be empty) and
`CalibratedMinCosineSimilarity` (double). `OnnxMemoryEmbedder` prepends
`QueryPrefix` when purpose is `RetrievalQuery` before tokenization; token
budget unchanged (`only_first`-style truncation still applies to the combined
string — the 12-token prefix is negligible against 512). Arctic fp32 entry:
prefix as documented, `CalibratedMinCosineSimilarity = 0.24`. The mxbai
fallback entry gets its documented prefix
(`Represent this sentence for searching relevant passages: ` is
arctic-specific; mxbai documents its own retrieval prompt) and a floor
calibrated before that entry is ever flipped to — until calibrated, the
fallback entry carries no retrieval calibration and the coordinator treats a
missing calibration as "hybrid recall unavailable, lexical-only + degraded
log" rather than guessing (no silent fallback).

### D3. Floor resolution: config-nullable follows manifest

`MemoryRecallConfig.MinCosineSimilarity` becomes `double?`, default null →
resolve from the active model's `CalibratedMinCosineSimilarity` at scorer
load (carried on `MemoryVectorIndexHolder`/embedder holder the same way
`RelevanceScorerHolder` carries `CalibratedThreshold`). Explicit config value
overrides (operator experimentation), with the schema description warning
that the meaning is model-and-prefix-specific. This makes
prefix-without-recalibration unrepresentable by default: both ride the same
manifest entry.

### D4. Calibration of record

This change supersedes the 0.68 no-prefix calibration. The prefixed fp32
sweep (`floor-calibration-prefix.json`, 116-point τ∈[−0.20, 0.95]) is the
calibration of record; design lineage: memory-core-redesign D6 records the
no-prefix history, this doc records the prefixed result, and the
calibration-verification procedure in memory-relevance-gate's design.md is
the documented re-run path (same harness family). Zero-injection residual
(73–87% of nothing-relevant queries still inject at the floor alone) remains
the relevance gate's job — gate numbers are cosine-independent and unaffected.

## Risks / Trade-offs

- **[Floor semantics change under operators' feet]** → nullable-follows-
  manifest default means only operators who explicitly pinned 0.68 are
  affected; schema description + skill guidance call it out; doctor and
  `memory_retrieval_final` log the active floor and whether it came from
  config or manifest.
- **[Actor/persistence boundaries]** → none moved: the purpose enum lives on
  the existing seam interface in `Netclaw.Actors/Memory`; no persistence
  shape changes; no new actor messages. Recall stays inside the existing
  coordinator timeout envelope; failure modes and recovery are the Slice 4
  ones (sub-budget miss → lexical-only + `memory_recall_vector_degraded`),
  now plus missing-calibration → lexical-only + the same degraded log with a
  distinct reason.
- **[Prefix drift vs model]** → prefix is pinned next to the model hash in
  the same allowlist entry; a model bump forces the author past the prefix
  field. The fixture cross-check test asserts the arctic entry's prefix
  matches the model-card string verbatim.
- **[Gold-set overfit]** → same risk profile as the 0.68 calibration it
  replaces; mitigated identically (gold-set regression suite, documented
  re-calibration procedure, config override escape hatch).
