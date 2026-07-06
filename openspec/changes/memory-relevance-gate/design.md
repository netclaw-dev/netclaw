# Design: memory-relevance-gate

## Context

memory-core-redesign Slice 4 (`openspec/changes/memory-core-redesign/`, design
D6) shipped hybrid recall with an absolute cosine floor
(`Memory.Recall.MinCosineSimilarity`, calibrated per embedding model against
`gold-prod-2026-07`): a query embeds once per turn, FTS5 and vector top-k
candidates are unioned and fused, and any candidate below the floor is
dropped before ranking — zero survivors means zero injection. That floor is
real and measured (τ=0.67 for the current uint8-quantized embedding
variant), but a floor sweep across the gate-shootout's checksum run shows it
still injects *something* for the great majority of nothing-relevant queries:
**16.7% zero-injection accuracy** on `gold-prod-2026-07` (93 queries, in-sample
calibration set) and **7.3%** on a 450-query out-of-sample expansion
(`~/recall-research-local/2026-07/gold-expansion/`, disjoint from the
calibration set by normalized-text exclusion). The reason is structural, not
a mistuned constant: cosine similarity measures topical "aboutness" between a
query and a candidate, not "does this candidate help answer the question" —
a memory can be comfortably on-topic (cosine 0.74, well above a 0.67 floor)
and still be useless for the turn (e.g. an unrelated project fact that
happens to share vocabulary with the query).

Four designs were measured head-to-head against this residual, then the
winner was re-validated out-of-sample on a gold set 4.8x larger than the one
used to pick it (`~/recall-research-local/2026-07/gate-shootout/` and
`gold-expansion/` respectively — both operator-local research stores holding
real, PII-bearing traffic; never committed, per the convention in
`docs/research/memory-audit-2026-07.md`). This design records that shoot-out,
the winning architecture, and the residuals that remain.

**Actor/persistence context** (unchanged from memory-core-redesign): recall
runs on the session actor's turn path under `Memory.RecallTimeoutMs` (default
300 ms), executed by `SQLiteMemoryRecallCoordinator`
(`Netclaw.Actors/Sessions`). The embedding runtime lives behind the
consumer-defined `IMemoryEmbedder` seam (`Netclaw.Actors/Memory`), implemented
by `OnnxMemoryEmbedder` in `Netclaw.Embeddings`, resolved at call time through
the mutable `MemoryEmbedderHolder` (a plain DI singleton cannot hold a value
that is only known after `EmbeddingWarmupHostedService` finishes
provisioning, which necessarily runs after the DI container is built).
`EmbeddingModelProvisioner`'s pinned in-code allowlist (model id → URL, byte
size, SHA-256) is the supply-chain boundary: arbitrary URLs are never
accepted, only ids present in the allowlist.

**Layering note**: this change's implementation targets the
`feature/memory-embeddings` branch, which carries memory-core-redesign's
embedding foundation, write-side nominate→decide, and read-side hybrid
recall slices ahead of `dev`. Because memory-core-redesign has not yet been
archived, the `memory-embeddings` capability does not yet exist under
`openspec/specs/`; this change's `specs/memory-embeddings/spec.md` delta is
therefore written against memory-core-redesign's own proposed spec
(`openspec/changes/memory-core-redesign/specs/memory-embeddings/spec.md`) as
its base, not against a synced main spec. If memory-core-redesign archives
(and syncs `memory-embeddings` into `openspec/specs/`) before this change
does, `opsx-sync` will need both deltas applied in dependency order —
memory-core-redesign's first, then this one.

## Goals / Non-Goals

**Goals**

1. Close the measured residual: most nothing-relevant queries should inject
   nothing, not "something topically adjacent." Target the validated
   operating point (86.8% zero-injection accuracy out-of-sample), not just
   the in-sample number.
2. Preserve recall: a query that has something genuinely relevant to say
   should keep getting it. 98.3% recall retention out-of-sample is the
   accepted cost, not zero cost — record this honestly.
3. Reuse memory-core-redesign's machinery wholesale — provisioning,
   holder-and-warmup lifecycle, degradation contract, doctor/status surfaces
   — so this change is a new manifest entry and a new scoring stage, not a
   parallel subsystem.
4. Loud degradation: gate unavailability must never silently change recall
   behavior without a marker.

**Non-Goals**

- Recalibrating the cosine floor, the fusion weights, or swapping the
  embedding model — this change adds a stage strictly after that pipeline's
  existing output.
- Domain-calibrated or class-conditional thresholds for the measured MS
  MARCO under-scoring of procedural/command-style memories — recorded as a
  residual, deferred.
- Re-running or expanding the judged gold sets further; the shoot-out and
  gold-expansion results are consumed as already-ratified inputs.
- Ensembling multiple relevance models or scoring schemes.
- Collapsing `MemoryEmbedderHolder` and the new relevance-scorer holder into
  a single combined holder — noted as an optional simplification (D4), not
  required for this change.

## Decisions

### D1. Scorer seam + ONNX cross-encoder implementation, mirroring `IMemoryEmbedder` exactly

`IRelevanceScorer` lives in `Netclaw.Actors/Memory` — a consumer-defined seam
in the same spirit as `IMemoryEmbedder`, so actor code never references
OnnxRuntime. Shape:

- `string ModelId` — the allowlisted relevance-model id (vectors and scores
  are never compared across models, same rule as embeddings).
- `bool IsAvailable` — real, expected false state (not provisioned, hash
  failure, runtime load error); only calling the scoring method while
  unavailable throws (matches `IMemoryEmbedder`'s contract exactly — no
  garbage score silently corrupting the gate).
- `ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)`
  — batch, order-preserving, one call per turn for the ≤`AutoRecallMaxItems`
  (3) floor survivors, mirroring `EmbedBatchAsync`'s batching rationale.

`OnnxCrossEncoderScorer` (`Netclaw.Embeddings`) implements it: pair encoding
`[CLS] query [SEP] candidate [SEP]` with correct `token_type_ids` (0 for
query+CLS+SEP, 1 for candidate+final SEP), truncation strategy `only_second`
(caps the total at the model's max length by truncating only the candidate
side — a query is never truncated), dynamic sequence length bucketed to
multiples of 8 (the same bucketing convention `OnnxMemoryEmbedder` already
uses, avoiding a proliferation of ORT graph re-optimizations for arbitrary
lengths). The model's single `logits` output (shape `[batch,1]`) is passed
through a sigmoid host-side — the upstream model ships
`sbert_ce_default_activation_function: Identity`, so the activation is
explicitly not baked into the graph and must be applied by the caller.
`UnavailableRelevanceScorer` is the degraded-mode stub, matching
`UnavailableMemoryEmbedder`'s throw-on-call contract byte for byte.

*Alternative considered*: extend `OnnxMemoryEmbedder`'s existing
`InferenceSession` to also serve cross-encoder inference — rejected: the
cross-encoder is a materially different model (a `BertForSequenceClassification`
pair-input head, not the bi-encoder's single-input pooling graph) with its
own tokenizer vocabulary; sharing a session would couple two independently
lifecycled models for no benefit. A second dedicated session, following the
exact same holder/warmup pattern, is simpler to reason about.

### D2. Model selection: `Xenova/ms-marco-MiniLM-L-6-v2`, int8, chosen from a 4-design measured shoot-out

| design | mechanism | in-sample verdict | out-of-sample verdict |
|---|---|---|---|
| A — distribution-shape | `z_top50 ≥ 2.80` (local-neighborhood outlier score) | 70.0% zero-inj, 100% retention — looked like a clean win | **Fails**: 65.2% zero-inj, **86.5% retention (below the ≥90% constraint)**, F0.5 0.089 < 0.100 floor-only |
| **B — cross-encoder (winner)** | `Xenova/ms-marco-MiniLM-L-6-v2`, pair scoring | 91.7% zero-inj (S*=0.08), 100% retention | **86.8% zero-inj (S*=0.02, 95% CI 82.3–90.3), 98.3% retention**, F0.5 0.130 vs 0.100 |
| C — learned feature gate | logistic/GBM over cosine/margin/z/length/age | candidate-level: 86.7% zero-inj across 10 CV folds (8 positive instances) | **Not viable**: query-level OOF AUC 0.545 (chance); candidate-level positives grew only 8→39 across the expansion, still insufficient, 80%-relative recall collapse on a differently-composed transfer set |
| D — per-memory offender priors | `pollution_count/injection_count` per docId | coverage ceiling measured directly, no separate OOS pass needed | **Structurally dead**: only 1.1% of top-3 candidates have 3+ injection history to build a prior from (5.3% even at a relaxed 2+ threshold); 80.6% of top-3 candidates are cold-start |

Gate B is the only design whose out-of-sample result both replicates its
in-sample claim *and* clears the ≥90% recall-retention constraint. Its
in-sample recommended threshold (S*=0.08, chosen because gold-prod showed a
flat 100%-retention plateau from 0.02–0.08) turned out to be an artifact of
having only 8 floor-surviving true positives to calibrate against — the
450-query expansion grew that count to 39, and retention at S*=0.08 dropped
to 90.1% (exactly on the constraint boundary, zero margin). **The frozen
operating point for this change is S*=0.02**, which trades 1.8 points of
zero-injection accuracy (88.6%→86.8%) for 8.2 points of recall retention
(90.1%→98.3%) versus the in-sample-optimal S*=0.08 — the right side of that
trade given goal #2 above.

Model artifact (frozen, quantized int8, the standard HuggingFace dynamic-INT8
export — same family of artifact as the embedder's own quantization
options):

- File: `model_quantized.onnx`
- Size: 23,143,499 bytes (22.07 MB)
- SHA-256: `e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe`

The fp32 reference variant (`model.onnx`, 90,992,115 bytes / 86.78 MB, SHA-256
`c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870`) was
measured bit-for-bit quality-identical to the quantized variant on both gold
sets and materially heavier on RAM (161–211 MB vs 48–103 MB incremental,
depending on measurement convention) for zero quality benefit — ruled out.

*Alternative considered*: shipping Gate A (distribution-shape) as a cheap
first-pass filter ahead of Gate B — rejected: Gate A's out-of-sample failure
(recall retention below its own promised floor, F0.5 *worse* than doing
nothing) means it would need its own re-validation and threshold governance
for no measured benefit once Gate B is in place; not worth the added
moving part.

### D3. Provisioning: a manifest *entry kind*, not a parallel allowlist

The relevance model is provisioned through the exact same pinned-allowlist
mechanism `EmbeddingModelProvisioner` already implements for embedding
models — the allowlist gains a `RelevanceModelManifestEntry` alongside the
existing `EmbeddingModelManifestEntry`: `ModelId`, `ModelUrl`, `ModelSha256`,
`ModelByteSize`, and — the one field embedding manifests don't need —
`CalibratedThreshold` (S*=0.02). This is memory-core-redesign's
**manifest-carried operating point** pattern (the same zero-config mechanism
that let `MinCosineSimilarity` ship without requiring every operator to
calibrate their own floor): the threshold travels with the model id it was
measured against, so a future model swap cannot silently reuse a threshold
calibrated for a different model's score distribution. Download, atomic
temp+rename, and SHA-256 verification reuse the provisioner's existing code
path unchanged — this is a new manifest row and entry type, not new
download/verify logic.

*Alternative considered*: a fully separate `RelevanceModelProvisioner`
class — rejected: the download/verify/reject-unknown-id logic has zero
model-kind-specific behavior; duplicating it would just be two copies of the
same supply-chain boundary to keep in sync.

### D4. Warmup and holder: extend the existing warmup service; a third holder, not a forced merge

`EmbeddingWarmupHostedService` gains a second provisioning step: when
`Memory.Embeddings.Enabled`, it provisions and warms the relevance model the
same way it does the embedding model (provision-or-degrade, one warm-up
inference call, gap-repair is not applicable here since there's no per-item
derived state to repair). The scorer is exposed through a new
`RelevanceScorerHolder`, following `MemoryEmbedderHolder`'s exact shape
(mutable holder, always non-null, initial value an `UnavailableRelevanceScorer`
stub, replaced once by the warmup service, read fresh on every use — never
cached by a consumer).

Keeping three holders (`MemoryEmbedderHolder`, `MemoryVectorIndexHolder`,
`RelevanceScorerHolder`) rather than merging them keeps each concern
independently swappable and testable, consistent with what already exists.
**Consolidating the two model-runtime holders (embedder + relevance scorer)
into a single combined "embedding runtime holder"** is noted here as an
optional future simplification — both models are provisioned by the same
warmup step and share the same availability semantics, so a combined holder
would remove one moving part — but it is not required for this change and is
left as a follow-up decision rather than blocking this slice on a refactor
of already-shipped code.

### D5. Recall wiring: a post-floor scoring stage under its own sub-budget

In `SQLiteMemoryRecallCoordinator`, the gate applies strictly after the
existing hybrid-recall floor stage, and only in `hybrid` mode (a query
vector was available): the floor already reduced the candidate set to
`aboveFloor` (≤`AutoRecallMaxItems` = 3, per the shoot-out's exact
candidate-generation protocol — the gate never sees a candidate the floor
would not already have admitted). Each survivor is paired with the query and
scored via `RelevanceScorerHolder.Current.ScoreAsync`, under a CE sub-budget
(~60 ms) nested inside the overall `RecallTimeoutMs` via a linked
`CancellationTokenSource` — the same pattern the query-embedding sub-budget
already uses (measured p95 35 ms for 3 pairs leaves roughly 1.7x headroom
before the sub-budget itself is hit). Candidates scoring below the
manifest/config threshold are dropped; **zero survivors after the gate is a
"nothing injected" outcome**, identical in kind to zero survivors at the
floor — the `[memory-recall]` block continues to be omitted entirely, not
emitted empty.

When the gate is unavailable, over its sub-budget, or recall is running in
`lexical` (degraded, no query vector) mode, the gate step is skipped
entirely and the floor's own output proceeds to injection unfiltered — this
is the same floor-only behavior that shipped in Slice 4, now reachable via
two independent degradation paths (embedder degraded → lexical mode already
skips the floor's cosine gate too; relevance-scorer degraded → floor's
cosine gate still applies, but no CE gate on top).

*Alternative considered*: applying the gate to the full vector top-k (10
candidates, before the floor) instead of just the ≤3 floor survivors —
rejected: this is exactly what the shoot-out measured and what the
out-of-sample validation certifies (candidates = floor-passing top-3);
scoring a wider candidate pool the gate was never validated against would
invalidate the calibrated threshold and roughly 3x the per-turn CE cost for
no measured benefit.

### D6. Activation: one mental switch, explicit override only

`Memory.Recall.RelevanceGate { Enabled, Threshold }`, both nullable:

- `Enabled = null` (default) → follows `Memory.Embeddings.Enabled`. An
  operator who turned on embeddings gets the gate; there is no second switch
  to discover or forget to flip.
- `Enabled = true/false` → explicit override, independent of the embeddings
  switch (e.g. an operator who wants embeddings for dedup/hybrid-recall but
  not the extra CE latency per turn).
- `Threshold = null` (default) → follows the manifest's calibrated S*
  (0.02) for whichever relevance model id is active.
- `Threshold = <value>` → explicit override, for an operator who re-runs the
  shoot-out's threshold sweep against their own corpus and wants a different
  operating point.

This mirrors `MinCosineSimilarity`'s existing "config default, manifest
provides the calibrated number" relationship — no new configuration
philosophy, just one more nullable pair.

### D7. Logging and eval coverage

`memory_retrieval_final` gains two fields: `gateScores` (the CE score per
surviving-then-gated candidate, for post-hoc threshold analysis without
needing a fresh eval run) and `droppedByGate` (count, mirroring the existing
`filteredByFloor` field's shape). A new eval case seeds a corpus with
unrelated memories, asks an off-topic question, and asserts both that no
`[memory-recall]` block appears in the assembled prompt and that a gate
marker appears in the logs — the automated analogue of the shoot-out's
"zero-injection accuracy" metric, pinned as a regression gate rather than
left as a one-time measurement.

### D8. Degradation semantics

Model unavailable (not provisioned, hash failure, runtime load error) or CE
sub-budget exceeded ⇒ floor-only behavior (identical to pre-this-change
Slice 4 output) plus a rate-limited `memory_recall_gate_degraded` log
(matching the existing `memory_recall_vector_degraded` cooldown pattern —
loud on the first occurrence of a reason, not spammy on every subsequent
turn) and doctor visibility (extending the existing embedding doctor check
or adding a sibling relevance-gate doctor check — implementation detail for
tasks, not a design fork). The system never silently changes recall
selectivity without one of these signals firing.

## Risks / Trade-offs

- [MS MARCO domain mismatch under-scores procedural/command-style memories]
  → measured, not hypothetical: of the 39 floor-surviving true positives in
  the expanded gold set, 6 scored below S*=0.08 (2 below the frozen S*=0.02),
  concentrated in release-workflow/procedural-context memories that are
  useful-as-context but don't read as "the answer" to a cross-encoder trained
  on MS MARCO's answer-passage judgments. At the frozen S*=0.02 this costs
  ~1.7% of retained recall. Mitigation: recorded as a residual, not silently
  absorbed; future work is domain calibration or a class-conditional
  threshold for procedural/tool-lesson-adjacent memory classes.
- [Judge-agreement caveat on the validation set] → the 450-query expansion's
  inter-rater agreement (κ=0.435, pooling 11 candidates/query, mostly
  sub-floor and deliberately ambiguous) is materially below the original
  July gold set's agreement (κ=0.754, judging only the 3 actually-injected
  items/query — an easier, less skewed task). Mitigated by harsher-wins
  aggregation (a doc counts as `relevant` only if both judging passes agreed)
  which biases the expanded gold set conservative — the right bias for
  validating a precision-oriented gate, but it means per-query labels in the
  expansion are noisier than July's and the aggregate tables should be
  trusted over any single query's label.
- [Threshold is model-conditional, like every other threshold in this
  system] → S*=0.02 is calibrated specifically against
  `Xenova/ms-marco-MiniLM-L-6-v2`'s score distribution; swapping the
  relevance model without re-running the threshold sweep would silently
  invalidate it. Mitigated the same way `MinCosineSimilarity` is: the
  threshold travels in the manifest keyed to the model id (D3), not as a
  bare config default disconnected from which model produced it.
- [Combined resource envelope is real but not free] → quantized CE adds
  ~103 MB incremental RSS and ~11 ms p50 / ~35 ms p95 for 3 pairs on the
  reference CPU; combined with int8 embeddings (263 MB) and daemon peak
  (397 MB), the operator's measured total is ≈763 MB against a 1 GB K8s pod
  limit — inside budget, but the margin (≈260 MB) is not so large that a
  future addition to the memory runtime gets it for free. Mitigated by
  measuring rather than assuming, and by keeping the CE sub-budget (~60 ms)
  small relative to the overall 300 ms recall timeout so a degraded gate
  never risks the turn itself.
- [Nested sub-budgets: query-embedding (~150 ms) + gate (~60 ms) inside one
  300 ms `RecallTimeoutMs`] → worst case both sub-budgets fully elapse
  (210 ms) before any lexical/ranking work runs, leaving less slack than
  Slice 4 alone had. Not yet measured end-to-end under production
  contention. Flagged as an open question (below), not silently assumed
  safe.
- [Two-holders-become-three] → `MemoryEmbedderHolder` +
  `MemoryVectorIndexHolder` + the new `RelevanceScorerHolder` is more moving
  parts than a consolidated holder would be. Accepted for this change (D4)
  as consistent with the existing pattern; flagged as an optional future
  consolidation rather than deferred silently.

## Migration Plan

1. Ships as an independent slice on top of `feature/memory-embeddings`'s
   already-landed hybrid-recall stage (memory-core-redesign Slice 4). No
   slice ordering dependency on any *other* part of memory-core-redesign
   beyond what Slice 4 already requires.
2. Config-gated end to end: `Memory.Embeddings.Enabled = false` (the current
   `dev` default) means the gate's provisioning step never runs and the
   coordinator never attempts to resolve a `RelevanceScorerHolder` — zero
   behavior change for any operator who hasn't already opted into
   embeddings. `Memory.Recall.RelevanceGate.Enabled = false` is a second,
   independent escape hatch for an operator who wants embeddings without the
   gate's added per-turn latency.
3. Rollback: disabling either switch returns to exactly the prior Slice-4
   floor-only behavior; the relevance model artifact is derived/cacheable
   data like the embedding model, safe to delete.
4. Schema: new `Memory.Recall.RelevanceGate` node added to
   `netclaw-config.v1.schema.json`, all-nullable, migration-friendly per the
   constitution's schema rules — no existing config document needs edits to
   remain valid.
5. Calibration-verification harness: because the threshold is
   model-conditional (Risk above), tasks include a short operator-facing note
   (alongside the runbook, not a new production code path) describing how to
   re-run the shoot-out's threshold-sweep protocol against a different
   relevance model or a different corpus, so re-calibration is a documented
   procedure rather than tribal knowledge trapped in a local research
   directory.

## Open Questions

- Combined worst-case latency of the query-embedding sub-budget (~150 ms)
  plus the new CE sub-budget (~60 ms) inside the single 300 ms
  `RecallTimeoutMs`, measured end-to-end under realistic contention rather
  than each sub-budget's own isolated measurement — gates this change's
  sub-budget sizing the same way Slice 4 gated its own latency assumption
  before shipping.
- Whether the deferred R2-mirroring decision for the embedding model artifact
  (memory-core-redesign, post-PoC) should extend to this second (relevance)
  model artifact once that decision is made.
- Whether to consolidate `MemoryEmbedderHolder` and `RelevanceScorerHolder`
  into one combined embedding-runtime holder (D4) — left open rather than
  decided, since both shapes are viable and the choice has no behavioral
  consequence.
