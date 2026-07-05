# Tasks: memory-core-redesign

Slices are independently shippable in order; each slice's final tasks are its
constitution gates (tests, evals where mapped, schema/skill sync, slopwatch).

## 1. Shared curation evaluator (behavior-neutral refactor)

- [x] 1.1 Extract `MemoryCurationEvaluator` from `MemoryCurationActor.EvaluateSingleAsync` and wire the actor through it
- [x] 1.2 Route `MemoryCurationEngine` (daemon worker) through the same evaluator, including `GuardDestructiveUpdate`, deleting the divergent inline logic
- [x] 1.3 Characterization tests proving inline and daemon paths produce identical decisions for the same inputs
- [x] 1.4 Run full memory test suites + slopwatch; no behavior change expected (decision-mix log fields unchanged)

## 2. Embedding foundation

- [x] 2.1 Create `src/Netclaw.Embeddings` project (Microsoft.ML.OnnxRuntime CPU, FastBertTokenizer, System.Numerics.Tensors) and `IMemoryEmbedder` seam in `Netclaw.Actors/Memory`
- [x] 2.2 Implement `OnnxMemoryEmbedder` (single InferenceSession, bounded intra-op threads, concurrency semaphore) + `UnavailableMemoryEmbedder`
- [x] 2.3 Implement `EmbeddingModelProvisioner`: pinned allowlist (id → URL, size, SHA-256), atomic download, hash verification, rejection of unknown ids
- [x] 2.4 Add `memory_embeddings` table + `UpsertEmbeddingAsync`/`FindNearestByEmbeddingAsync`/coverage queries to `SQLiteMemoryStore.InitializeAsync` (idempotent DDL)
- [x] 2.5 Implement `MemoryContentHasher` (normalized title+body SHA-256) and hash-skip on re-embed
- [x] 2.6 Implement `MemoryVectorIndex` (per-model flat float[] brute-force cosine, store-version invalidation)
- [x] 2.7 `EmbeddingWarmupHostedService`: provision-or-degrade at startup, warm-up inference, gap-repair sweep; register `IMemoryEmbedder` in daemon DI
- [x] 2.8 Embed-on-write after both curation batch commit paths
- [x] 2.9 `netclaw memory backfill-embeddings [--force]` CLI command
- [x] 2.10 `MemoryEmbeddingDoctorCheck` (model presence/hash, coverage, mixed-model warning) + daemon status `embeddings: degraded` surface + rate-limited degradation logs
- [x] 2.11 Config: `Memory.Embeddings { Enabled, ModelId, AutoDownload }` + schema sync with defaults
- [x] 2.12 Tests: provisioner hash-rejection/unknown-id, hash-skip, gap repair, vector index invalidation, degraded stub; CI uses a tiny fixture ONNX model (no downloads in tests)
- [x] 2.13 **Measure ONNX int8 short-query embedding latency on reference hardware; record the number in design.md and gate Slice 4's sub-budget on it**
- [x] 2.14 ARM64 publish smoke leg exercising OnnxRuntime load
- [x] 2.15 Update `netclaw-memory` + `netclaw-operations` skills (backfill command, degraded mode); eval suite run

## 3. Write-side nominate→decide + lossless merge

- [x] 3.1 Nominator in the shared evaluator: kNN shortlist at `Memory.Curation.NominatorSimilarityThreshold`/`NominatorK`; any nominee forces the LLM tier; no-nominee-no-anchor creates without LLM; lexical candidate search becomes the logged degraded path
- [x] 3.2 Extend `CurationPromptBuilder` response protocol: CONSOLIDATE/UPDATE emit a merged body; `CurationDecision.MergedBody`; full-content previews for nominated candidates
- [x] 3.3 Implement `MergeGuard` (load-bearing-token retention ≥95%, length collapse check) with structural-append fallback producing `AppendDocument` semantics
- [x] 3.4 Route all curation UPDATE/CONSOLIDATE writes through guard-validated merged bodies; make raw whole-body overwrite unreachable from curation decisions
- [x] 3.5 Config: `Memory.Curation { NominatorSimilarityThreshold, NominatorK, LlmMaxOutputTokens, LlmTimeoutSeconds }` (replacing hardcoded constants) + schema sync
- [x] 3.6 Tests: paraphrase-dupe nomination (fixture pairs from the audit corpus shape), sibling pairs never auto-merge, MergeGuard property tests, append fallback, both-pipelines parity
- [x] 3.7 Eval suite (memory category) + skill sync; update decision-mix expectations (consolidate share should rise from ~0.1%)

## 4. Read-side hybrid recall + absolute floor

- [ ] 4.1 Query embedding per turn with a vector sub-budget inside `RecallTimeoutMs`; lexical-only fallback + `memory_recall_vector_degraded` log on miss
- [ ] 4.2 Candidate union (FTS5 ∪ vector top-k) with policy-gate parity for vector-sourced hits
- [ ] 4.3 Weighted fusion scoring + `MinCosineSimilarity` absolute floor; omit the `[memory-recall]` block entirely on zero injections
- [ ] 4.4 Recency half-life decay (floor-bounded multiplier) on composite scores
- [ ] 4.5 Config: `Memory.Recall { VectorWeight, LexicalWeight, MinCosineSimilarity, RecencyHalfLifeDays }` + schema sync
- [ ] 4.6 Calibrate the floor against `gold-prod-2026-07` (local gold set); record calibration numbers in design.md
- [ ] 4.7 Gold-set recall regression suite (fixture corpus + labeled queries asserting injected/withheld ids, MRR/precision floors, zero-injection cases)
- [ ] 4.8 Flip scenario P09 (paraphrase-gap) back to expected-recall; policy-parity scenario test; latency budget test with warm embedder
- [ ] 4.9 Eval suite + `netclaw-memory` skill update (hybrid recall, zero-injection normality)

## 5. Taxonomy rebalance, trace revival, tool lessons

- [ ] 5.1 **BREAKING**: restrict automatic recall to `recall_mode='auto'` in `SearchByPlanAsync` (searchable leaves the auto pool); update `MemoryIndexContextLayer` guidance
- [ ] 5.2 Formation: policy gate honors sidecar-proposed recall mode for durable facts, defaulting to `searchable`; observer distillation prompt rewritten for fewer, more comprehensive proposals with an explicit auto-mode whitelist (identity/preferences/environment)
- [ ] 5.3 Trace revival: reachable producer (sidecar may propose `trace` with 72 h TTL), fresh-trace auto-recall eligibility weighted below durable facts, removal of the unreachable turn-complete Trace dead code
- [ ] 5.4 `MemoryClass.ToolLesson` (`tool_lesson`) → Document/MergeDocument/Searchable with per-tool anchors; `store_memory` accepts the class and sets the `VerifiedToolFinding` checkpoint flag
- [ ] 5.5 Sidecar distillation prompt: correction-hunting instruction producing tool-lesson proposals
- [ ] 5.6 Per-tool context injection in the tool-execution pipeline: `[tool-lessons:<name>]` block on first use per session (bounded, once per tool, reset on compaction); remove the dead `verified-tool-finding` +25 recall bonus
- [ ] 5.7 Tests: searchable-out-of-auto regression, formation default, trace TTL round-trip, lesson capture→injection end-to-end, once-per-session + compaction reset
- [ ] 5.8 Eval cases: tool-lesson store→new-session→first-tool-use surfaces lesson; must-auto-recall identity facts still auto-recall after rebalance
- [ ] 5.9 Skill sync (`netclaw-memory`: classes table, lessons guidance) + schema sync for any new wire values

## 6. Maintenance CLI, expiry sweep, subtraction

- [ ] 6.1 `memory_maintenance_runs` ledger table (store DDL)
- [ ] 6.2 `netclaw memory consolidate --dry-run`: kNN cluster graph → merge synthesis → `plan.jsonl` + report, zero mutation (byte-identical DB test)
- [ ] 6.3 `netclaw memory consolidate --apply --plan <path>`: live-daemon refusal (override flag), `VACUUM INTO` backup, batched apply, re-embed + FTS rebuild, ledger row
- [ ] 6.4 Expiry sweep in the daemon maintenance loop (grace window, per-class deletion logging)
- [ ] 6.5 `netclaw memory status` (composition, coverage, pending checkpoints, expired-awaiting-sweep, recent ledger)
- [ ] 6.6 Checkpoint enqueue gating: turn-complete lane gated by the extractor's precondition at enqueue time
- [ ] 6.7 Subtraction: drop `memory_edges` DDL, remove facet/soft-scope inference from `DeterministicRetrievalPlanning` (keep stopword hygiene + lexical terms), delete dead Trace path remnants
- [ ] 6.8 Integration tests on a seeded corpus: backfill→dry-run→edited-plan apply→status round-trip; sweep deletes only past-grace rows
- [ ] 6.9 Runbook update (`docs/runbooks/memory-health-and-evals.md`): embedding, consolidation, sweep operations
- [ ] 6.10 Final gates: full test suites, eval suite, slopwatch, headers, schema doctor round-trip on a real config
