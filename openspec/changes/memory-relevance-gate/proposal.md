# Proposal: memory-relevance-gate

Source PRD: PRD-007 (agent personality and local memory), continuing
`memory-core-redesign` (`openspec/changes/memory-core-redesign/`, PR #1570;
Slice 4 shipped the hybrid recall + calibrated cosine floor this change builds
on). Evidence base: `~/recall-research-local/2026-07/gate-shootout/` (4-design
gate shoot-out, 2026-07-06) and `~/recall-research-local/2026-07/gold-expansion/`
(450-query out-of-sample gold expansion + gate re-validation, 2026-07-06) —
operator-local research stores holding real (PII) traffic data, never
committed, per the same convention documented in
`docs/research/memory-audit-2026-07.md`.

## Why

Even with hybrid recall and the calibrated per-model cosine floor
(memory-core-redesign Slice 4), most nothing-relevant queries still cause an
injection: floor-only zero-injection accuracy measured **16.7%** on the July
gold set (`gold-prod-2026-07`, 93 queries) and **7.3%** on the 450-query
out-of-sample expanded gold set. Cosine similarity measures topical
"aboutness," not usefulness-for-answering — a candidate can clear the floor
and still be the wrong thing to inject. This is the dominant remaining
recall-quality defect because **60–65% of real queries have nothing relevant**
to recall at all (replicated across 543 labeled real-traffic queries: 93 July
+ 450 expansion), so the floor's residual miss rate lands on the majority
case, not the tail.

## What Changes

- **New relevance-gate stage after the cosine floor.** A tiny cross-encoder
  scores `(query, candidate)` jointly for each of the (≤`AutoRecallMaxItems`
  = 3) floor-surviving candidates; anything below a calibrated threshold S* is
  dropped. Zero survivors after the gate ⇒ inject nothing, same as zero
  survivors at the floor today.
- **Winner of a 4-design measured shoot-out, out-of-sample validated**:
  `Xenova/ms-marco-MiniLM-L-6-v2`, `model_quantized.onnx` (int8, 22.07 MB,
  SHA-256 `e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe`).
  Out-of-sample (450-query expanded gold set, disjoint from the calibration
  set) at S*=0.02: zero-injection accuracy **86.8%** (95% CI 82.3–90.3) vs
  7.3% floor-only, recall retention **98.3%**, F0.5 **0.130** vs 0.100
  floor-only, mean injected **0.251** vs 2.538 floor-only.
- **Reuses memory-core-redesign's infrastructure wholesale** — this is the
  change's selling point, not an afterthought: the same consumer-defined-seam
  pattern (`IMemoryEmbedder` → `IRelevanceScorer`), the same
  allowlist-manifest provisioning pattern (`EmbeddingModelProvisioner` gains a
  relevance-model manifest entry kind carrying pinned URL/SHA-256/size *and*
  the calibrated operating threshold), the same warmup hosted service, and the
  same loud-degradation contract (rate-limited log marker + doctor
  visibility) — no new machinery class, only a new manifest entry and a new
  scoring step in an existing pipeline.
- **One mental switch.** Gate activation is tied to
  `Memory.Embeddings.Enabled` — there is no separate "turn semantic recall
  quality on" knob. `Memory.Recall.RelevanceGate { Enabled (nullable, follows
  Embeddings), Threshold (nullable, follows the manifest's calibrated S*) }`
  exists only for an explicit operator override.
- **Logging.** `memory_retrieval_final` gains `gateScores` and `droppedByGate`
  fields. A new eval case asserts the zero-injection behavior end-to-end:
  seeded corpus, off-topic question, assert no `[memory-recall]` block and a
  gate marker in the logs.
- **Rejected alternatives** (recorded for provenance; not shipped):
  - *Distribution-shape statistical gate* (`z_top50 ≥ 2.80`): looked viable
    in-sample (70% zero-injection) but failed out-of-sample — 65.2%
    zero-injection accuracy, **86.5% recall retention (below the ≥90%
    constraint)**, F0.5 0.089, *worse* than the 0.100 floor-only baseline.
  - *Learned feature gate* (candidate-/query-level logistic regression and
    GBM over cosine/margin/z-score/length/age features): query-level variant
    measured out-of-fold AUC 0.545 (chance = 0.500, i.e. no signal);
    candidate-level variant's positive-class support grew only 8→39 across
    the gold expansion — still not enough to certify signal over
    small-sample luck, and it showed an 80%-relative recall collapse on a
    differently-composed transfer set.
  - *Per-memory offender priors* (`pollution_count`/`injection_count` per
    `docId`): structurally cold-start-bound — only 1.1% of top-3 recall
    candidates carry 3+ injection observations to build a prior from, 5.3%
    even at a relaxed 2+ threshold; 80.6% of top-3 candidates are cold-start
    with no addressable history at all.

## Capabilities

### New Capabilities

- `memory-relevance-gate`: the `IRelevanceScorer` seam and
  `OnnxCrossEncoderScorer` implementation, the relevance-model provisioning
  manifest kind (pinned URL/SHA-256/size + calibrated threshold), and the
  post-floor gate stage wired into automatic recall.

### Modified Capabilities

- `netclaw-agent-memory`: the automatic pre-turn recall requirement gains a
  post-floor relevance-gate stage — floor-surviving candidates are scored and
  filtered before injection; zero survivors after the gate is a "nothing
  injected" outcome exactly like zero survivors at the floor; gate
  unavailability or sub-budget timeout degrades to floor-only behavior with a
  loud marker.
- `memory-embeddings`: the pinned-allowlist provisioning requirement is
  generalized to a manifest entry *kind* so it can provision relevance
  (cross-encoder) models alongside embedding models, and the warmup hosted
  service provisions/warms both.

## Impact

- **Code**: new `IRelevanceScorer` seam (`Netclaw.Actors/Memory`), new
  `OnnxCrossEncoderScorer` (`Netclaw.Embeddings`, pair encoding `[CLS] q [SEP]
  d [SEP]` with `token_type_ids`, sigmoid over the single-logit head, dynamic
  sequence length bucket-of-8 matching the embedder's convention);
  `EmbeddingModelProvisioner`'s allowlist gains a relevance-model manifest
  kind; `SQLiteMemoryRecallCoordinator` gains the post-floor gate stage under
  a CE sub-budget; `Netclaw.Configuration` gains
  `Memory.Recall.RelevanceGate`; doctor/status surfaces extend to cover the
  relevance model; `netclaw-memory` skill update.
- **Dependencies**: none new — reuses the `Microsoft.ML.OnnxRuntime` +
  managed-tokenizer stack memory-core-redesign Slice 2 already adopted. One
  new pinned model artifact (~22 MB int8), never embedded in the binary,
  downloaded and hash-verified at provisioning time exactly like the
  embedding model is today.
- **Data/config**: `netclaw-config.v1.schema.json` gains the new nodes, all
  nullable with manifest-derived defaults — additive, non-breaking.
- **Evals**: new zero-injection gate eval case; `memory_retrieval_final`'s
  log schema gains two additive fields (`gateScores`, `droppedByGate`).
- **Target branch**: implementation lands on `feature/memory-embeddings` (the
  in-flight branch carrying memory-core-redesign's embedding and recall
  slices), not directly on `dev` — this change's tasks assume that branch's
  `IMemoryEmbedder`/`MemoryEmbedderHolder`/`SQLiteMemoryRecallCoordinator`
  hybrid-recall code as their starting point.

### In scope (MVP)

- The cross-encoder scorer, its provisioning manifest entry, the coordinator
  wiring (score → threshold → drop), the config surface, degradation
  semantics, logging fields, and the zero-injection eval case.
- Recording the shoot-out's rejected alternatives and residual failure modes
  in `design.md` for provenance.

### Out of scope

- Domain-calibrated or class-conditional thresholds for the measured MS
  MARCO under-scoring of procedural/command-style memories (residual, ~1.7%
  of retained recall at S*=0.02) — future work, not this change.
- Consolidating `MemoryEmbedderHolder` and a prospective relevance-scorer
  holder into one combined embedding-runtime holder — noted as an optional
  simplification in `design.md`, not required for this change to ship.
- Any change to the cosine floor itself, the embedding model, or the fusion
  weights (memory-core-redesign Slice 4 territory; this change only adds a
  stage after that pipeline's existing output).
- Re-running or expanding the judged gold sets further; this change consumes
  the existing gate-shootout and gold-expansion results as already-ratified
  inputs.

## Security and Operational Impact

- **Model supply chain**: the relevance model is provisioned through the
  same pinned-allowlist mechanism as the embedding model — id → URL + byte
  size + SHA-256, arbitrary URLs rejected, atomic download (temp + rename),
  hash-verified before load. No new supply-chain surface, only a new
  manifest entry kind on the existing one.
- **Resource envelope**: measured on the reference CPU — ~11 ms p50 / ~35 ms
  p95 to score 3 pairs (quantized int8), ~103 MB incremental RSS. Combined
  with int8 embeddings (263 MB) and daemon peak (397 MB), the operator's
  measured total is ≈763 MB — inside the 1 GB K8s pod limit, with headroom
  noted rather than assumed.
- **Degradation**: gate unavailability (model not provisioned) or exceeding
  its CE sub-budget (~60 ms, linked CTS) degrades to floor-only behavior — the
  pre-existing, already-shipped recall path — plus a rate-limited
  `memory_recall_gate_degraded` log marker and doctor visibility. Never a
  silent fallback, matching memory-core-redesign's degradation contract.
- **Operations**: no new operator action required — gate activation follows
  `Memory.Embeddings.Enabled`; the existing warmup hosted service and doctor
  checks extend to cover the new model without a new CLI verb.
