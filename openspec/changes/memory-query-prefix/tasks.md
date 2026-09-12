# Tasks: memory-query-prefix

Implementation targets `feature/memory-embeddings` (on top of
memory-core-redesign Slices 2–4 and memory-relevance-gate). The prefix and
the floor recalibration ship in the same slice — they are not independently
safe.

## 1. Seam, manifest, and embedder

- [x] 1.1 `EmbeddingPurpose` enum (`Passage`, `RetrievalQuery`) and purpose
      parameter on `IMemoryEmbedder.EmbedAsync`/`EmbedBatchAsync`; update ALL
      call sites explicitly (embed-on-write, backfill CLI, gap repair, dedup
      nominator → `Passage`; recall coordinator → `RetrievalQuery`); no
      optional parameter. `UnavailableMemoryEmbedder` + all test fakes updated.
- [x] 1.2 `EmbeddingModelManifestEntry` gains `QueryPrefix` (string) and
      `CalibratedMinCosineSimilarity` (double?); arctic fp32 entry pins the
      model-card prefix verbatim and `CalibratedMinCosineSimilarity = 0.24`;
      mxbai fallback entry carries its own documented prefix and a null
      calibration (uncalibrated → recall treats as hybrid-unavailable)
- [x] 1.3 `OnnxMemoryEmbedder` prepends the manifest `QueryPrefix` for
      `RetrievalQuery` purpose before tokenization; passage path
      byte-identical to today (regression-guarded by an exact-vector test
      against the fixture model)
- [x] 1.4 Holder plumbing: active entry's `QueryPrefix`/
      `CalibratedMinCosineSimilarity` reachable at the recall seam (same
      pattern as `RelevanceScorerHolder.CalibratedThreshold`)

## 2. Floor resolution, coordinator, config

- [x] 2.1 `MemoryRecallConfig.MinCosineSimilarity` becomes nullable; null →
      manifest calibration, explicit value → override; schema sync
      (nullable, description warns the value is model+encoding specific) +
      defaults tests updated
- [x] 2.2 `SQLiteMemoryRecallCoordinator`: effective floor resolved
      config-or-manifest per turn; missing calibration + no override ⇒
      lexical-only + rate-limited degraded log (distinct reason, same
      cooldown pattern); `memory_retrieval_final` logs effective floor and
      its source
- [x] 2.3 Doctor: embedding check reports active model's prefix presence and
      effective floor source
- [x] 2.4 Tests: prefix applied only to `RetrievalQuery` (fixture-model
      vector inequality query-vs-passage for same text); passage-path
      byte-compat regression; floor resolution matrix (manifest / override /
      missing-calibration degrade); coordinator lexical-only on
      missing-calibration; scenario suite (P01–P21 incl. P09) green with the
      new seam signature

## 3. Calibration record, evals, docs

- [x] 3.1 Record the prefixed fp32 calibration as the calibration of record:
      design.md of THIS change already carries the table; add a superseding
      note to memory-core-redesign design.md D6 (0.68 remains the no-prefix
      historical record) — keep both changes' docs consistent
- [x] 3.2 Eval suite run (memory category) on the final behavior
- [x] 3.3 `netclaw-memory` skill sync: prefix is automatic, floor now
      follows the model manifest by default, override knob semantics; bump
      metadata.version
- [x] 3.4 Full gates: build, all affected test suites, slopwatch, headers
