# Design: memory-core-redesign

## Context

The July 2026 audit (`docs/research/memory-audit-2026-07.md`) measured the
memory system end-to-end against the live 1,216-document corpus: 46% of
auto-injected memories are pollution (relevance judged, κ=0.754); the lexical
composite score carries no relevance signal (precision flat across every
floor); the LLM curation tier had **zero** successful decisions in its
production lifetime; consolidation fired twice ever while redundancy reached
14% and doubled in five weeks; the checkpoint worker drops 95% of its intake;
`Searchable` recall mode secretly participates in automatic recall; the Trace
class and expiry mechanics are fully vestigial (0 traces ever, no deletion,
204 expired records mummified on disk); `memory_edges` has 0 rows and the
facet planner knows 4 demo facets against ~900 real ones.

The quick-win slice (PR #1568) revived the LLM tier, adopted the balanced
curation prompt, re-tuned lexical scoring, and added an injection budget.
This change is the structural remainder: add the missing **judgment**
(embeddings), add the missing **metabolism** (lifecycle: lossless merge,
expiry, consolidation), and **subtract** the dead structure that three
redesign cycles accreted. Prior art constraints come from the May 2026
autoresearch (`docs/research/memory-recall-findings-2026-05.md`): nominate-by-cosine/decide-by-LLM is
ratified (no cosine threshold separates duplicates from siblings — siblings
live at 0.905–0.941 inside the duplicate band), and the nominator model is
snowflake-arctic-embed 137M (33M-class models measured inadequate for
doc-to-doc dedup).

Actor/persistence context: memory lives in the daemon's single SQLite file
(`NetclawPaths.MemorySqliteDbPath == SqliteDbPath`), whose memory tables are
owned by `SQLiteMemoryStore.InitializeAsync` (idempotent DDL), NOT by the
daemon's `SchemaMigrator`. Two write pipelines exist today: the inline
per-session path (`SessionMemoryObserverActor` → `MemoryProposalGate` →
`MemoryCurationActor`, a per-session child of `LlmSessionActor`) and the
daemon checkpoint worker (`MemoryCurationWorkerService` →
`MemoryCurationEngine`). Recall runs on the session actor's turn path under a
hard latency budget (`Memory.RecallTimeoutMs`, default 300 ms).

## Goals / Non-Goals

**Goals**

1. Fewer, more comprehensive memories: near-duplicates are detected
   semantically at write time and merged losslessly.
2. Fewer, more accurate injections: recall is gated by an absolute semantic
   relevance floor; most turns inject nothing (measured correct outcome for
   65% of real queries).
3. A real lifecycle: expired rows are deleted, redundant clusters are
   consolidated under operator control, short-lived (≈72 h) memories exist
   and work.
4. Tool-use lessons are captured and surface exactly when the relevant tool
   is used.
5. Less machinery: taxonomy and pipelines shrink to the behaviors that
   actually exist; every metadata field written is consumed by a reader.

**Non-Goals**

- Multilingual embeddings (model swap later; vectors keyed by `model_id`).
- ANN indexes (brute force is sub-ms at this corpus scale; revisit ≥50k).
- Automatic (code-level) detection of tool-use corrections.
- Applying consolidation to any corpus as part of implementation (tooling
  ships; each apply run is an operator decision).
- Multi-node/cluster memory; this remains single-process MVP.

## Decisions

### D1. Embedding runtime: in-process ONNX, new `src/Netclaw.Embeddings` project

`Microsoft.ML.OnnxRuntime` (CPU EP; linux-x64 + linux-arm64 ship in 1.25+) +
`FastBertTokenizer` (pure managed WordPiece — the chosen model is BERT-class)
+ `System.Numerics.Tensors` for SIMD cosine. The consumer-defined seam
`IMemoryEmbedder` lives in `Netclaw.Actors/Memory` so actor code never
references OnnxRuntime; `Netclaw.Embeddings` is referenced by Daemon and CLI
only. A singleton `OnnxMemoryEmbedder` holds one `InferenceSession`
(`IntraOpNumThreads` bounded, concurrency semaphore ≤2); an
`UnavailableMemoryEmbedder` stub carries `IsAvailable=false` for degraded
mode.

*Alternative considered*: Ollama sidecar — rejected: violates the
single-process constitution, adds a network hop inside the recall budget, and
creates a second silent-failure surface. *Alternative*: embedding via the
existing chat-provider plugins — rejected: recall must work when no provider
is reachable, and provider embedding APIs are not uniformly available.

### D2. Model provisioning: pinned allowlist, download at initialization, never embedded

`Memory.Embeddings.ModelId` selects from an **in-code allowlist manifest**
(model id → URL + byte size + SHA-256); arbitrary URLs are rejected
(supply-chain boundary). An `EmbeddingWarmupHostedService` provisions at
daemon start when `AutoDownload=true` (atomic temp+rename download, hash
verify, then one warm-up inference), or the operator runs
`netclaw memory backfill-embeddings`. The ~90–140 MB artifact is never an
embedded resource (would bloat every RID publish). Default model:
snowflake-arctic-embed-m (~110M params, fp32 ONNX, pinned by hash — int8 is a
future optimization, not what Stage A shipped; May-ratified), mxbai-embed-large
335M is the allowlisted fallback. Post-PoC decision deferred: mirroring
artifacts into the existing R2 feeds channel vs pinned upstream URLs.

### D3. Vector storage: separate `memory_embeddings` table, owned by the store

`memory_embeddings(item_id, item_kind, model_id, content_hash, dims, vector
BLOB, created_at, PRIMARY KEY(item_id, model_id))`, created in
`SQLiteMemoryStore.InitializeAsync` alongside the other memory DDL — not a
daemon migration, preserving the store's standalone-initialization contract
(doctor and tests construct it without the migrator). Content hash =
SHA-256 of normalized title+body; re-embed is skip-if-hash-match, so backfill
re-runs are free. Model change = new `model_id` rows + `--force` backfill; no
rewrite of the 224 MB documents table. Backfill state is **derived**
(LEFT JOIN on current model + hash), never a progress table. kNN executes as
a brute-force scan over an in-memory `MemoryVectorIndex` (flat float[] per
model, ~1.8 MB at current scale, invalidated by a store version counter). No
sqlite-vec/native extensions (ARM64 + deployment liability for zero benefit
at this scale).

*Failure/recovery*: a crash between document commit and embedding upsert
leaves a missing-embedding row; the warmup service's gap-repair sweep and the
embedding doctor check both surface and heal it. Vectors are derived data —
loss is always recoverable by re-embedding.

### D4. Write-side: one evaluator; kNN nominates, LLM decides; no cosine auto-merge

The duplicated evaluation logic in `MemoryCurationActor.EvaluateSingleAsync`
and `MemoryCurationEngine` collapses into one shared `MemoryCurationEvaluator`
used by both the inline actor and the daemon worker (today's guards diverge —
`GuardDestructiveUpdate` exists on one path only). Evaluation order:

1. Exact-anchor + near-identical body → deterministic SKIP (cheap fast path).
2. Embedding kNN nomination at `NominatorSimilarityThreshold` (default 0.86)
   / `NominatorK` (default 5). **Any nominee forces the LLM tier** — the May
   measurement stands: no cosine threshold separates duplicates from siblings,
   so cosine never auto-merges and never auto-skips.
3. No nominee and no anchor match → CREATE without an LLM call (the common
   case stays cheap; median nominee count on a random write is 0).
4. Embedder unavailable → the current lexical candidate search runs as the
   explicitly-logged degraded path.

*Alternative considered*: cosine auto-merge tier above 0.95 — rejected: the
measured sample shows ~3 pairs there, not worth a data-loss risk surface.

### D5. Lossless merge: LLM-synthesized body + deterministic MergeGuard + append fallback

CONSOLIDATE/UPDATE decisions now carry a merged body
(`CurationDecision.MergedBody`) synthesized by the curation LLM from
full-content previews. A deterministic `MergeGuard` validates it: load-bearing
tokens (URLs, numbers, versions, dates, code identifiers) from every source
body must survive (≥95%), and length must not collapse. On failure the write
degrades to a **structural append** (existing body + dated separator +
proposal — finally producing the `AppendDocument` semantics that have existed
unused since the enums were written). The raw
`markdown_body = excluded.markdown_body` overwrite becomes unreachable from
curation decisions. Records remain immutable and curation-bypassing.

*Why not prompt-only*: the May decider eval measured the balanced prompt at
~27% wrong-merge on hard near-duplicates. The guard turns a wrong merge from
silent data loss into recoverable over-consolidation.

### D6. Read-side: hybrid recall with an absolute cosine floor

Per turn: embed the query once (sub-budget inside `RecallTimeoutMs`; on
timeout or unavailable → lexical-only + rate-limited
`memory_recall_vector_degraded` log). Candidates = FTS5 top-k ∪ vector top-k,
deduplicated, **all candidates passing the identical policy gates**
(audience/boundary/sensitivity/recall-mode) regardless of source — a
correctness requirement with its own scenario. Scoring = weighted fusion
(`VectorWeight` 0.7 × cosine + `LexicalWeight` 0.3 × squashed selector score
+ dampened class prior), then an **absolute floor**: `MinCosineSimilarity`
(default 0.55, calibrated against the real-traffic gold set
`gold-prod-2026-07`). Nothing above the floor → inject nothing, and the
volatile `[memory-recall]` block is omitted entirely (zero tokens). Recency
decay (`RecencyHalfLifeDays`, floor-bounded multiplier) breaks ties toward
fresh knowledge. The quick-win char budget and `AutoRecallMaxItems` remain
the outer bounds.

*Alternative considered*: RRF fusion — rejected: rank-only fusion always
admits the top item even when nothing is relevant; the zero-injection
behavior requires an absolute score. *Latency measured, not assumed*: Ollama
measurements ran far above the 10–50 ms/query assumption, and the in-process
ONNX fp32 measurement (Slice 2 task 2.13; full numbers in Open Questions)
shows the same problem persists — p95 ≈ 315 ms on the i9-9900K reference box,
~2× over the 150 ms sub-budget, because the embedder pads every input to a
fixed 512 tokens regardless of actual length.

**Mitigation, measured (`tools/embed-latency-bench` dynamic-length
extension)**: the ONNX graph's sequence axis is symbolic
(`input_ids`/`attention_mask`/`token_type_ids` all declare
`[batch_size, sequence_length]`, no fixed shape), so padding to the actual
tokenized length (rounded up to a multiple of 8) instead of a fixed 512 is a
drop-in change — no re-export needed. On the same reference box: short-query
p50 **19.0 ms**, p95 **20.9 ms** (was p50 281.9 ms / p95 310.5 ms fixed-512 —
~15× faster, ~7× under the 150 ms sub-budget); medium (~178 tok) p50
**84.1 ms** (was 281.7 ms); doc-length (~442 tok) p50 **235.5 ms** (was
280.3 ms — smaller gain because 442 tokens is already close to 512).
Correctness parity across 10 fixed sentences (short queries + longer bank
sentences), fixed-512 vs dynamic-length, cosine similarity: **1.000000 on
every sentence** (min = mean = 1.000000) — the attention mask fully absorbs
the padding difference, so this is a pure performance change with no
retrieval-quality risk. **Decision: Slice 4 adopts dynamic sequence length
(bucket-of-8 rounding) as the query-embedding mitigation**, not int8
quantization and not a relaxed budget — the 150 ms sub-budget holds with
large headroom once padding is length-aware.

### D7. Taxonomy rebalance: recall modes mean what they say

- **BREAKING (semantic fix)**: `Searchable` leaves the automatic recall pool
  (`SearchByPlanAsync` admits `auto` only). `Searchable` = find_memories
  surface; `Manual` = explicit-id access; `Never` = policy-hidden. The 22
  legacy compaction rows were already repaired in the quick-win slice; a
  startup data-repair re-asserts invariants idempotently.
- Formation: the observer sidecar proposes a recall mode; the policy gate
  honors it for durable facts with **default `searchable`** — `auto` is
  reserved for standing facts that should color every conversation (identity,
  durable preferences, environment). This breaks the measured 97%-auto
  monoculture at the source. The distillation prompt is rewritten for fewer,
  more comprehensive proposals (consolidate related observations into one
  document; propose fewer atomic fragments).
- **Trace revival**: the sidecar may propose `trace` (short-lived operational
  state, TTL 72 h) — the class becomes reachable, recallable while fresh
  (recall mode `auto` with its TTL as the guard, weighted below durable
  facts), and actually deleted by the expiry sweep (D8).
- **Tool lessons**: new `MemoryClass.ToolLesson` → Document/MergeDocument/
  Searchable, anchored `anchor_type="tool"`, `canonical_name=<tool>`.
  Captured explicitly (`store_memory` accepts the class; the `netclaw-memory`
  skill instructs saving a lesson when the user corrects tool usage) and by
  the sidecar distillation prompt (correction-hunting instruction). Recall is
  **per-tool context injection**: on a tool's first use in a session, the
  tool-execution pipeline appends a compact `[tool-lessons:<name>]` block
  (top 2 by `updated_at`, bounded chars) to the tool result — an exact
  anchor-id lookup, no embedding, outside the pre-turn recall budget, reset
  on compaction. The dead `verified-tool-finding` +25 recall bonus is
  removed; `store_memory` with the class becomes the first real producer of
  the `VerifiedToolFinding` checkpoint flag.

*Alternative considered*: overloading Evidence for lessons — rejected:
Evidence is policy-forced to immutable Record + searchable, so lessons could
never be refined by curation and would never surface unprompted.

### D8. Metabolism: expiry sweep + operator-gated consolidation

- **Expiry sweep**: a daemon maintenance step (piggybacking the checkpoint
  worker's idle loop) DELETEs rows whose `expires_at` has passed beyond a
  grace window — they are already invisible to every read path, so deletion
  is behavior-neutral by construction; each sweep logs counts. (Audit: 204 of
  384 evidence records currently mummified.)
- **Consolidation**: `netclaw memory consolidate --dry-run` builds the kNN
  cluster graph, runs the merge-synthesis prompt per cluster, and writes a
  human-editable `plan.jsonl` + report — no mutation. `--apply --plan <path>`
  executes a reviewed plan verbatim: refuses a live daemon by default, takes
  a `VACUUM INTO` backup first, applies in batched transactions, re-embeds
  merged bodies, rebuilds FTS rows, and records a `memory_maintenance_runs`
  ledger row. `netclaw memory status` reports class/recall-mode/embedding
  coverage. CLI-owned rather than a daemon job because the ratification gate
  is inherently interactive.

### D9. Subtraction

Removed with evidence they carry no load (audit): `memory_edges` table and
its DDL/spec requirement (0 rows ever; anchors remain as flat grouping keys);
the facet/soft-scope *inference* in `DeterministicRetrievalPlanning` (4
hardcoded demo facets; stopword-hygiene and lexical-term extraction remain);
the checkpoint worker's unconditional turn-complete enqueue (gated at enqueue
by the same project-fact precondition the extractor applies — eliminating
~95% wasted enqueue/lease/deserialize cycles; the freed lane is where the
expiry sweep lives). Wire enums keep their values for serialization
compatibility; only dead *behavior* is deleted.

## Risks / Trade-offs

- [Model download unavailable offline at first run] → loud degraded mode:
  doctor Error, daemon status `embeddings: degraded`, rate-limited logs;
  lexical recall keeps serving. Never silent.
- [Query-embedding latency blows the 300 ms recall budget on CPU] →
  **confirmed with fixed-512 padding, then resolved by measurement** (Slice 2
  task 2.13: p95 ≈ 315 ms, ~2× over the 150 ms sub-budget on the reference
  box). The dynamic-sequence-length experiment (see D6 and Open Questions)
  confirmed the ONNX graph's sequence axis is symbolic (not a fixed shape)
  and measured short-query p95 at 20.9 ms once padding matches actual token
  length — ~7× under budget, with 1.000000 cosine parity against fixed-512
  across 10 test sentences. Slice 4 ships dynamic-length padding
  (bucket-of-8) as the mitigation; warmup inference at start and
  `RecallTimeoutMs` remain in place as defense-in-depth, not as the primary
  fix.
- [LLM merge synthesis loses information] → MergeGuard token-retention check
  + structural-append fallback; consolidation applies only via human-ratified
  plan files with a backup taken first.
- [Cosine floor calibrated on one corpus generalizes poorly] → floor lives in
  config next to `ModelId`; gold-set eval (real traffic) pins the calibration;
  doctor warns on mixed-model embedding rows.
- [Searchable-out-of-auto surprises users who relied on incidental recall] →
  BREAKING is called out; `find_memories` covers the tail; formation default
  changes only affect NEW memories; consolidation plans may propose
  recall-mode changes but only under ratification.
- [ARM64 native OnnxRuntime regression] → CI publish smoke leg on linux-arm64;
  FastBertTokenizer is pure managed.
- [Two write paths drift again during the transition] → shared
  `MemoryCurationEvaluator` lands as its own slice before any nominator work;
  divergence becomes structurally impossible rather than reviewed-for.

## Migration Plan

1. Slices are independently shippable, in order: (1) shared evaluator
   extraction (behavior-neutral refactor), (2) embedding foundation (writes
   vectors, nothing reads them — zero behavior risk), (3) write-side
   nominate→decide + lossless merge, (4) read-side hybrid + cosine floor,
   (5) taxonomy rebalance + trace revival + tool lessons, (6) maintenance
   CLI + expiry sweep + subtraction.
2. Existing corpora: `backfill-embeddings` (measured: minutes) is required
   before slices 3–4 activate their vector paths; both paths degrade loudly
   to lexical when coverage is incomplete rather than misbehaving.
3. Rollback: each slice is config-gated (`Memory.Embeddings.Enabled`,
   nominator/recall thresholds) — disabling returns to the quick-win
   behavior. Vectors are derived data; dropping `memory_embeddings` is safe.
4. Schema: new tables via idempotent `InitializeAsync` DDL; config surface
   added to `netclaw-config.v1.schema.json` with defaults (migration-friendly
   per the constitution's schema rules); `netclaw-memory` system skill updated
   in the same PR as each behavior slice.

## Open Questions

- ~~ONNX int8 query-embedding latency on reference hardware (measure in
  Slice 2; gates Slice 4's sub-budget design)~~ **MEASURED (Slice 2 task
  2.13, `tools/embed-latency-bench`, batch=1, 200 timed iterations/corpus
  after 20 warmups)**. Production path is fp32, not int8 (int8 remains a
  deferred D2 optimization). Reference box: i9-9900K, 8 logical cores,
  contended condition (load avg 2.0–3.6, ~11/15 GiB RAM in use, live daemon
  running):

  | corpus                      | tokens (mean) | p50     | p95    |
  |------------------------------|---------------|---------|--------|
  | short query                  | 13.8          | 281 ms  | 315 ms |
  | medium (~180 tok)             | 178.2         | 274 ms  | 298 ms |
  | doc-length (~440 tok)         | 442.1         | 275 ms  | 294 ms |
  | short, concurrency=2          | 13.8          | 274 ms  | 291 ms |
  | cold load (model load + 1st embed) | —        | 1069 ms | —      |

  All three corpora cost nearly the same regardless of length, because
  `OnnxMemoryEmbedder` always runs a fixed 512-token forward pass (no
  length-based truncation) — the fp32 matmul, not tokenization, dominates.
  Concurrency=2 gave no throughput benefit on this contended box (two
  parallel 100-call loops took as long in aggregate as one sequential
  200-call stream). **Verdict: the 150 ms query-embedding sub-budget does
  not hold on this hardware — p95 is ~2.1× over budget (margin ≈ −165 ms)**;
  the highest-leverage unexplored mitigation is a query-specific max-length
  (e.g. 64 tokens, not int8 quantization) before Slice 4 ships.
- ~~Does dynamic (query-specific) sequence length actually work on this ONNX
  graph, and is it a drop-in change?~~ **MEASURED AND RESOLVED** (same
  `tools/embed-latency-bench`, dynamic-length extension, same box, same
  batch=1/200-iteration/20-warmup protocol). Step 1: `InferenceSession
  .InputMetadata` shows all three inputs (`input_ids`, `attention_mask`,
  `token_type_ids`) declare shape `[batch_size, sequence_length]` — both
  dimensions symbolic, not fixed — so the graph accepts any sequence length;
  no re-export required. Step 2: padding each input to its actual tokenized
  length (rounded up to a multiple of 8) instead of fixed 512:

  | corpus                | tokens (mean) | fixed-512 p50 | fixed-512 p95 | dynamic-len p50 | dynamic-len p95 |
  |------------------------|---------------|---------------|---------------|------------------|------------------|
  | short query            | 13.8          | 281.9 ms      | 310.5 ms      | **19.0 ms**      | **20.9 ms**      |
  | medium (~178 tok)      | 178.2         | 281.7 ms      | 312.2 ms      | **84.1 ms**      | **93.3 ms**      |
  | doc-length (~442 tok)  | 442.1         | 280.3 ms      | 304.6 ms      | **235.5 ms**     | **250.1 ms**     |

  Step 3, correctness (not just speed): 10 fixed sentences (5 short queries +
  5 longer bank sentences), embedded both ways, cosine similarity fixed-512
  vs dynamic-length — **1.000000 on all 10 (min = mean = 1.000000)**: the
  attention mask fully accounts for the padding difference, so this is a
  correctness-neutral, pure-performance change. Contention context: load
  average 1.40/1.44/2.36 before the ~6-minute run, 4.76/3.63/3.08 after (the
  run's own CPU load, not external contention). **Verdict: dynamic sequence
  length is adopted as the Slice 4 mitigation** — short-query p95 lands at
  ~14% of the 150 ms sub-budget (huge margin), medium and doc-length both
  drop meaningfully too. Int8 quantization and relaxing the sub-budget are no
  longer necessary; both remain available as future levers if traffic shifts
  toward longer queries.
- Final `MinCosineSimilarity` default (calibrate against `gold-prod-2026-07`
  during Slice 4; 0.55 is the working hypothesis).
- Whether the R2 feeds channel should mirror model artifacts (post-PoC
  operational decision; allowlist design is unaffected).
- Trace auto-recall weighting while fresh (small prior vs durable-fact parity)
  — decide with eval cases in Slice 5.
