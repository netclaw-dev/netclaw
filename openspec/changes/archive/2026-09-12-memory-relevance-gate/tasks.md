# Tasks: memory-relevance-gate

Implementation targets `feature/memory-embeddings` (memory-core-redesign
Slices 2–4 are this change's starting point, not `dev`). Slices are
independently shippable in order.

## 1. Scorer, provisioning, manifest/config

- [x] 1.1 `IRelevanceScorer` seam in `Netclaw.Actors/Memory` (`ModelId`,
      `IsAvailable`, order-preserving batch `ScoreAsync`) +
      `UnavailableRelevanceScorer` stub, matching `IMemoryEmbedder`'s
      throw-on-call-while-unavailable contract
- [x] 1.2 `OnnxCrossEncoderScorer` in `Netclaw.Embeddings`: pair encoding
      (`[CLS] query [SEP] candidate [SEP]`, correct `token_type_ids`,
      `only_second` truncation so the query is never truncated), dynamic
      sequence length bucketed to multiples of 8, sigmoid applied host-side
      over the single-logit output
- [x] 1.3 `RelevanceModelManifestEntry` (`ModelId`, `ModelUrl`,
      `ModelSha256`, `ModelByteSize`, `CalibratedThreshold`) added to
      `EmbeddingModelProvisioner`'s allowlist alongside the existing
      embedding-model entries; pin `Xenova/ms-marco-MiniLM-L-6-v2`
      `model_quantized.onnx` (22.07 MB,
      SHA-256 `e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe`,
      `CalibratedThreshold = 0.02`)
- [x] 1.4 `RelevanceScorerHolder` (mirrors `MemoryEmbedderHolder`: mutable,
      always non-null, initial `UnavailableRelevanceScorer`, replaced once by
      the warmup service); `EmbeddingWarmupHostedService` gains a second
      provision-or-degrade step (provision, hash-verify, one warm-up
      inference) for the relevance model when `Memory.Embeddings.Enabled`
- [x] 1.5 Config: `Memory.Recall.RelevanceGate { Enabled (nullable, follows
      Embeddings.Enabled), Threshold (nullable, follows manifest
      `CalibratedThreshold`) }` + `netclaw-config.v1.schema.json` sync with
      defaults (additive, nullable, non-breaking)

## 2. Coordinator wiring, degradation, tests, eval

- [x] 2.1 `SQLiteMemoryRecallCoordinator`: post-floor gate stage — score each
      of the ≤`AutoRecallMaxItems` floor survivors under a ~60 ms CE
      sub-budget (linked CTS nested inside `RecallTimeoutMs`, same pattern as
      the existing query-embedding sub-budget); drop candidates below the
      active threshold; zero survivors after the gate ⇒ inject nothing
      (reuse the existing zero-injection path, don't fork it)
- [x] 2.2 Degradation: relevance model unavailable, sub-budget exceeded, or
      recall running in lexical (non-hybrid) mode ⇒ skip the gate entirely
      and inject the floor's own result unfiltered; rate-limited
      `memory_recall_gate_degraded` log (same cooldown pattern as
      `memory_recall_vector_degraded`)
- [x] 2.3 Doctor visibility for the relevance model (extend the existing
      embedding doctor check or add a sibling check): model presence/hash,
      provisioning failure, degraded-mode reason
- [x] 2.4 Logging: `memory_retrieval_final` gains `gateScores` (per-candidate
      score for every gated candidate) and `droppedByGate` (count)
- [x] 2.5 Tests: pair-encoding correctness (token_type_ids, truncation-only-
      second, dynamic length bucketing) against fixture pairs; threshold
      admit/reject boundary; degraded-scorer fallback to floor-only;
      sub-budget-timeout fallback; zero-survivors-after-gate produces the
      same result shape as zero-survivors-at-the-floor; config
      nullable-follows-manifest resolution (both `Enabled` and `Threshold`)
- [x] 2.6 Eval case: seed a corpus with unrelated memories, ask an off-topic
      question, assert no `[memory-recall]` block in the assembled prompt
      and a gate marker present in the logs for that turn (the zero-
      injection regression the gate exists to enforce)

## 3. Docs, skill sync, scorecard, calibration note

- [x] 3.1 Update `netclaw-memory` skill: relevance gate exists, follows
      `Memory.Embeddings.Enabled`, explicit override knobs, degraded-mode
      behavior (floor-only fallback)
- [x] 3.2 Runbook (`docs/runbooks/memory-health-and-evals.md`): relevance
      gate section — doctor check, degradation log line, how to read
      `gateScores`/`droppedByGate` in `memory_retrieval_final`
- [x] 3.3 Record a scorecard in `design.md` (already drafted from the
      shoot-out; keep in sync if any number changes before merge) and add a
      short calibration-verification harness note (how to re-run the
      threshold sweep against a different relevance model or corpus, so
      re-calibration is a documented procedure, not tribal knowledge in a
      local research directory) alongside the runbook
