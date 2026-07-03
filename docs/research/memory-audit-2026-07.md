# Netclaw Memory System Audit — July 2026

> Tooling referenced in this report (miners, judges, dedup harnesses) lives in
> the operator-local research store (`~/recall-research-local/`), not in the
> repo — it operates on real (PII) corpus data.

Follow-up to the May 2026 autoresearch investigation (`memory-recall-findings-2026-05.md`).
Measured against the live local instance: 1,216-document corpus (cloned
2026-07-03 via `VACUUM INTO`), all daemon logs since 2026-04-13, and the last
14 days of session logs (172 files, 112 recall events judged exhaustively).
All examples below are redacted/generalized; raw data stays in
`$HOME/recall-research-local/2026-07/` and is never committed.

Code references are to `dev @ cd5099c82`.

## Executive summary

1. **Automatic recall is measurably polluting context.** Of 251 judged injected
   memories: **46% pollution, 27% marginal, 26% relevant** (Cohen's κ = 0.754
   on a 20% double-judged sample — substantial agreement). Only **29% of recall
   events helped** the turn; **19% were actively harmful** (misleading content
   injected). On **65% of real queries (60/93), zero relevant memories were
   injected** — the correct behavior on those turns is injecting *nothing*,
   which the always-take-3 design cannot do.
2. **The lexical composite score carries no relevance signal on real traffic.**
   A floor sweep over 155 score-joined items shows relevant-precision flat at
   ~0.34 for every floor from 10 to 60. Raising the floor sheds relevant and
   polluting items in equal proportion — the May "floor 10→20" tuning delivers
   *fewer* but not *more accurate*. Only a semantically informed score
   (embeddings) can deliver both.
3. **The LLM curation tier has never worked.** Lifetime record across all
   daemon logs: **6 invocations, 0 successes** — 3 timeouts (May 4/12/21), then
   3 empty responses (`responseLength=0`; Jun 10/18/26) after the timeout was
   presumably outrun. Cause: `ModelRole.Compaction` falls back to the Main
   model (Qwen3.6-27B, a reasoning model) whose hidden thinking exhausts the
   512-token output cap. Every ambiguous dedup decision has fallen through to
   deterministic fallbacks.
4. **The write-side dedup tier is structurally inert.** Decision mix over the
   whole log history: create 1,352 / skip 657 / update 436 / **consolidate 2**
   (0.08%). Only 4 of 1,387 anchors hold more than one document — anchor-level
   grouping effectively never happens; the corpus accretes near-duplicates
   at ~13.4 new documents/day.
5. **The daemon checkpoint pipeline is 95% waste.** 3,895 checkpoints enqueued,
   3,704 dropped before curation — 3,678 of them `TurnCompleteNoProjectFact`
   (the rules-first extractor finds nothing to extract in ordinary turns).
   Memory formation is in practice carried by the inline sidecar observer, not
   the checkpoint worker. Every turn pays enqueue+lease+deserialize for a 95%
   no-op.
6. **`Searchable` recall mode is part of automatic recall.**
   `SQLiteMemoryStore.SearchByPlanAsync` (`:862`, records `:890`; also `:563`)
   admits `recall_mode IN ('auto','searchable')`. Only `Manual`/`Never` are
   excluded. Consequences: (a) the 22 pre-#1225 compaction-boundary summaries
   (recall_mode=searchable, Apr–May) are still auto-injectable and appear among
   the top measured polluters (~19 injections in 14 days, mostly judged
   pollution); (b) any future "demote to searchable" rebalancing does NOT
   remove documents from automatic recall — a design-critical semantic trap.
7. **The corpus is a monoculture.** 1,190/1,216 documents (97.9%) are
   `durable_fact`; 1,177 (96.8%) sit in auto-injectable recall modes. 384
   records are all evidence/searchable. `memory_edges` has 0 rows — the anchor
   graph in the spec is entirely unused. Answer to "are we saving too many
   documents?": yes — one class, one recall mode, no structure.

## 1. Corpus statistics (clone of 2026-07-03)

| Metric | Value |
|---|---|
| memory_documents | 1,216 (durable_fact 1,190 / evidence 26) |
| recall_mode | auto 1,177 / searchable 22 / manual 17 |
| memory_records | 384 (all evidence, all searchable) |
| memory_anchors | 1,387; **only 4 hold >1 document** |
| memory_edges | 0 rows (spec'd graph unused) |
| Creation rate (last 30 d) | 13.4 docs/day |
| Injected tokens per 3-item recall (mean) | ~349 tokens |
| Corpus ever injected (all logs) | 120 recall events; Gini 0.268 |

Growth: 693 docs (May 29) → 1,216 (Jul 3), ≈ +15/day sustained. At this rate
the corpus doubles roughly every 3 months with no consolidation pressure.

## 2. Recall pollution (14-day exhaustive judgment)

Method: every `turn_memory_recall` event in the frozen 14-day log snapshot
(112 events, 281 injected items; 101 distinct event ids) was reconstructed with
its triggering user message (`mine_recall_events.py`) and judged by 12 parallel
Haiku agents against `prompts/pollution_judge.md`; a 20% sample was
independently double-judged (`pollution_report.py`).

| Measure | Value |
|---|---|
| Item verdicts | relevant 66 (26%) / marginal 69 (27%) / **pollution 116 (46%)** |
| Event verdicts | helpful 29% / neutral 52% / **harmful 19%** |
| Inter-rater agreement | κ = 0.754; 84% exact (55 paired items) |
| Queries with zero relevant injections | **60 of 93 (65%)** |
| Most-injected offender | a since-deleted document — 11 injections, 11× pollution (operator deleted it during the window: user-visible pollution pain) |
| Compaction-boundary docs (pre-#1225) | ~19 injections across 3 documents, predominantly pollution |

Redacted examples (harmful events; queries paraphrased, no verbatim session
content or real memory titles):

- A recurring check-for-new-benchmark-results task was injected with a
  weeks-old *diagnostic memory about a resolved inference-server incident*
  (pollution — steers the agent into an unrelated investigation), alongside
  two genuinely relevant benchmark memories.
- A user note that a pull request had merge conflicts was injected with an
  *unrelated chat-platform administration architecture memory* (pollution)
  alongside one relevant PR-workflow memory.

### Floor sweep (155 score-joined items) — the key negative result

| Floor | Items kept | Relevant kept | Pollution suppressed | Precision(relevant) |
|---|---|---|---|---|
| 10 (current) | 155 | 100% | 0% | 0.34 |
| 15 | 131 | 88% | 17% | 0.35 |
| 20 (May proposal) | 85 | 54% | 46% | 0.33 |
| 30 | 50 | 33% | 68% | 0.34 |
| 60 | 18 | 10% | 86% | 0.28 |

**Precision is flat across the entire range** — the composite score does not
distinguish relevant from polluting memories on real queries. Score-floor
tuning alone cannot fix pollution; it can only trade volume. (Caveat: sweep
uses current scoring params; the May-tuned weights reshape scores and were
validated on synthetic gold — re-validate against `gold-prod-2026-07` before
adopting. The structural conclusion — lexical score ≈ no relevance signal —
matches the May finding that embeddings beat tuned lexical MRR 0.93 vs 0.84.)

A real-traffic gold set was ratified from these judgments
(`gold_from_judgments.py` → `gold-prod-2026-07.jsonl`, local): 93 queries
(7 dropped for double-judge disagreement), 33 with ≥1 relevant doc. May gold
sets remain usable: 96–99% of their doc-ids survive in the July corpus.

## 3. Curation-tier post-mortem (all daemon logs, Apr 13 – Jul 3)

| Marker | Count |
|---|---|
| memory_checkpoint_enqueued | 3,895 |
| memory_checkpoint_dropped_before_curation | 3,704 (**95.1%**) |
| — DropReason=TurnCompleteNoProjectFact | 3,678 |
| — DropReason=FingerprintDuplicate | 18 |
| — DropReason=SecretLikeContent | 3 |
| curation_actor_evaluating (inline sidecar path) | 1,925 |
| Decisions: create / skip / update / consolidate | 1,352 / 657 / 436 / **2** |
| curation_llm_decision (success) | **0 — lifetime** |
| curation_llm_timeout | 3 (May 4, 12, 21) |
| curation_llm_no_decision (empty response) | 3 (Jun 10, 18, 26) |
| curation_ambiguous_create_fallback | 11 |

DB reconciliation (clone `memory_checkpoints`, 4,359 rows): 4,196 turn-complete
/ 106 explicit / 57 compaction; 4,358 completed, 1 failed (retry 5).

Readings:

- **The LLM tier is dead, and it barely matters at current thresholds**: the
  ambiguous Jaccard band (0.40–0.80) escalated only ~14 of 1,925 inline
  evaluations (~0.7%). Fixing the LLM tier without widening what reaches it
  (embedding nomination) would leave it >99% idle.
- **Consolidation is effectively nonexistent** (2 events lifetime), consistent
  with the 4-multi-doc-anchor corpus and the May finding that word-Jaccard is
  blind to paraphrase.
- **The checkpoint worker is a no-op treadmill**: 95% of its intake is
  discarded by design (`MemoryCurationEngine.CurateAsync` →
  `LogCheckpointDropped`, `MemoryCurationPipeline.cs:514`). The inline
  sidecar observer (`SessionMemoryObserverActor`) is the real producer.

## 4. Retro-embedding overhead (measured — answers "what's the CPU cost?")

Method: `embed_bench.py` — all 1,216 documents (title+body, ≤900 chars) through
`snowflake-arctic-embed:137m` via Ollama on the live box (i9-9900K, 8 cores,
daemon running, RAM-capped `MemoryMax=4G` scope), cold cache, sequential
(matches embed-on-write) then concurrency-4 (backfill projection).

| Measure | snowflake-arctic-embed 137M |
|---|---|
| **Full 1,216-doc backfill, sequential** | **8.3 min** (2.5 docs/s) |
| **Full backfill, concurrency-4** | **4.5 min** (4.6 docs/s) |
| Per-doc latency | p50 210 ms / mean 407 ms / p95 1.42 s |
| Peak Ollama RSS | 424–430 MB |
| Model cold-load (first call) | 5.7 s |
| Failures | 0 |

Conclusions:

- **Retro-embedding the whole corpus is a non-event**: minutes of wall clock,
  <0.5 GB RAM, runnable live. Steady-state embed-on-write (~13 docs/day) is
  ~5 s/day of compute.
- **Design correction for Phase C**: per-doc latency via Ollama-on-CPU is
  ~4–30× the 10–50 ms the hybrid-recall design assumed. Documents are long
  (≤900 chars) and the box was contended, and in-process ONNX int8 removes the
  HTTP hop — but the **150 ms query-embedding sub-budget inside the 300 ms
  recall budget must be re-measured on the actual ONNX path** (queries are
  ~10–30 tokens, far cheaper than docs) before the hybrid design is committed.
  Mitigations if it doesn't fit: raise `RecallTimeoutMs` (now configurable),
  cache query embeddings per turn (already single-flight), or embed on a
  pre-warmed dedicated thread.

## 5. Dedup refresh on the 1,216-doc corpus (clone-only)

Method: salvaged May pipeline (`dedup_audit.py` → `dedup_review.py` →
`consolidation_worklist.py`) against the July clone, arctic-137M embeddings at
the ratified nominator bar τ=0.86 (equivalent to mxbai@0.90 per the May
re-pool calibration).

**Leak table (arctic oracle; production Jaccard gate evaluated on every near pair):**

| cos ≥ | near-dup pairs | caught (Jac≥.80) | LLM gray (.40–.80) | silent-create (<.40) | escape rate |
|---|---|---|---|---|---|
| 0.80 | 1,513 | 37 | 114 | 1,362 | 98% |
| **0.86** | **412** | **37** | **108** | **267** | **91%** |
| 0.90 | 201 | 37 | 96 | 68 | 82% |

- `caught` is **pinned at 37 across every bar** — the identical structural
  signature May measured (then 33): word-Jaccard cannot see paraphrase, and
  corpus growth hasn't changed that by a single percentage point.
- **267 silent-create pairs → 239 distinct memories in 69 clusters; raw
  eliminable upper bound 170 documents = 14% of the corpus** (May: 65 memories
  / 24 clusters / 41 raw ≈ 6% of 693). The redundant share has roughly
  **doubled in five weeks** — consistent with a curation tier whose
  consolidate rate is 2-per-lifetime. Applying May's ratification yield
  (~63–90% of raw upper bound are true dupes), the honest expectation is
  **~107–153 removable documents (9–13%)**, pending the human ratification
  pass on the worksheet.
- Largest cluster: size 25 (a reminders-topic chain, cos 0.860–0.940) —
  transitive chaining at τ0.86 links related-but-distinct topics; ratification
  must split mega-clusters, and several members are junk-grade
  `"Project Fact: <sentence fragment>"` memories emitted by the rules
  extractor (a formation-quality finding in its own right).
- **Apples-to-apples with May (mxbai oracle, same bars):**

  | cos ≥ | May pairs (693 docs) | Jul pairs (1,216 docs) | May silent-create | Jul silent-create | May escape | Jul escape |
  |---|---|---|---|---|---|---|
  | 0.90 | 169 | 248 | 52 | 116 | 80% | 85% |
  | 0.92 | 145 | 178 | 30 | 49 | 77% | 79% |
  | 0.95 | 103 | 113 | 3 | 6 | 68% | 68% |

  Silent-create pairs at 0.90 **more than doubled (52 → 116)** while the corpus
  grew 1.75× — duplication is accelerating super-linearly, as expected when
  every near-dupe becomes a fresh attractor for the next paraphrase. `caught`
  moved 33 → 37 (the gate found four more literal restatements in five weeks).

Artifacts (all local, never committed): `dedup-missed-2026-07.jsonl` (pairs),
`dedup-clusters-2026-07.md` (ratification worksheet, 69 clusters),
`consolidation-worklist.jsonl` (machine-readable input for the future
`netclaw memory consolidate` tooling). **No apply tooling was built and the
live DB was not touched** (per scope decision).

## 6. Consolidated defect list (dev @ cd5099c82)

| # | Defect | Where | Status |
|---|---|---|---|
| D1 | Destructive merge: `markdown_body=excluded.markdown_body` — "merge" is full-body overwrite | `SQLiteMemoryStore.cs:1437` (inline), `:1595` (daemon) | Confirmed May, still shipping |
| D2 | Baseline curation prompt; UPDATE guidance ("date/price/status changed") is the measured time-series-clobber vector; 200-char previews (`ContentPreviewMaxChars`) starve the decider | `CurationPromptBuilder.cs:18,20-58` | Confirmed May, still shipping |
| D3 | LLM curation tier dead: reasoning-model thinking exhausts 512-token cap; 10 s timeout also hit | `MemoryCurationActor.cs` (~`:320` token cap, `:57` timeout); model routing `ChatClientRouter` Compaction→Main | **Newly measured: 0-for-6 lifetime** |
| D4 | Jaccard gate blind to paraphrase; ambiguous band escalates ~0.7% | `CurationRulesEvaluator.cs:58-64`, `AnchorNameMatcher.cs:153` | Confirmed May; consolidate share now measured at 0.08% |
| D5 | `Searchable` recall mode included in automatic recall — semantic trap; pre-#1225 compaction docs still polluting | `SQLiteMemoryStore.cs:563,862,890`; data: 22 rows Apr–May | **New** |
| D6 | #1225 fix not retroactive: existing compaction-boundary rows left auto-injectable (via D5) | data defect in live DB | **New** |
| D7 | Dead config knobs: `MemoryConfig.RecallTimeoutMs`/`AutoRecallMaxItems` never read; values hardcoded | `MemoryConfig.cs:23,28`; `SessionRecallManager.cs:84,68` | Confirmed |
| D8 | Composite floor/bonuses hardcoded; floor carries no relevance signal on real traffic | `SQLiteMemoryRecallCoordinator.cs:34,134-161` | **Newly measured (floor sweep)** |
| D9 | Daemon checkpoint path 95% dropped (`TurnCompleteNoProjectFact`) — wasted enqueue/lease cycles every turn | `MemoryCurationPipeline.cs:514`, `MemoryCurationWorkerService` | **New** |
| D10 | Facet planner inert: 4 hardcoded demo facets | `DeterministicRetrievalPlanning.cs` | Confirmed May |
| D11 | Doc/code mismatch: context layer tells the model recall is durable_fact-only; planner also admits Evidence (and D5 adds searchable) | `MemoryIndexContextLayer.cs:55` | Confirmed |
| D12 | `VerifiedToolFinding` category modeled + score-boosted but no producer ever sets it | `SessionMemoryCheckpointFactory.cs:28,60,86`, `SqliteStoreMemoryTool.cs:54`; dead +25 bonus `SQLiteMemoryRecallCoordinator.cs:154` | Confirmed |
| D13 | `memory_edges` empty; `AppendDocument` semantics never produced; anchor graph unused | schema + `MemoryDomainEnums.cs` | Confirmed |
| D14 | `GuardDestructiveUpdate` covers only the inline path; containment-based (drops new detail rather than merging) | `CurationRulesEvaluator.cs:235-266` | Confirmed |
| D15 | No token budget on recalled content; no recency decay; MaxItems fixed 3 | `SessionRecallManager.cs:68`, coordinator | Confirmed |
| D16 | Legacy zero-byte `~/.netclaw/memory.db` (unused; confusing) | local instance | New (housekeeping) |
| D17 | Recall of since-deleted documents observed (top offender deleted mid-window; 23 injected items unresolvable in clone) | operational observation | New (monitor) |
| D18 | Trace class (72 h TTL — the "short-lived memory" concept) fully vestigial: **0 ever formed**; the only producing path early-returns before its classification code (unreachable at `MemoryCurationPipeline.cs:122-137`); maps to `RecallMode.Never` so even a formed trace would be write-only | `MemoryCurationPipeline.cs:176-189,243`, `MemoryExpiryDefaults.cs` | **New** |
| D19 | No expiry sweep exists — expiry is read-time filtering only; **204 of 384 evidence records are past their 30-day expiry and still on disk**, invisible but accumulating forever; documents never get an expiry at all | `SQLiteMemoryStore.cs:284,867` (filters); no DELETE/purge job anywhere | **New** |

## 7. Implications

**For the quick-win slice (Phase B):**
- D3 fix (token cap 512→1500 + reasoning suppression + no-decision doctor
  visibility) is necessary but NOT sufficient — pair with D2 (balanced prompt,
  700-char previews). Expect low LLM traffic until embedding nomination widens
  the ambiguous band (Phase C).
- Floor: do NOT blindly adopt floor=20. Real-traffic sweep shows proportional
  loss. Re-validate the full May-tuned weight set against `gold-prod-2026-07`;
  adopt the weights if precision improves, keep floor moderate otherwise.
- Immediate cheap pollution wins available *now*: retire the 22 pre-#1225
  compaction rows from the auto pool (data migration to `manual`, or fix D5's
  predicate), and the injection char budget (D15).

**For the build-out (Phase C):**
- The 65%-zero-relevant result makes the absolute cosine floor (inject nothing
  when nothing clears it) the centerpiece requirement of hybrid recall.
- D5 forces a decision: either `Searchable` is removed from the automatic pool
  (breaking change to recall semantics; makes "demote to searchable" a real
  rebalancing lever) or the type rebalance must use `Manual`. Recommend the
  former, with the doc/code mismatch (D11) fixed in the same change.
- D9 suggests the checkpoint worker's turn-complete lane should be gated at
  enqueue time (don't enqueue what the extractor will certainly drop), or the
  lane repurposed for embedding backfill work.
- D18/D19 make short-lived memory a real Slice-5 design input: the "useful for
  ~3 days, worthless after" category (deploy states, in-flight incident
  context) is exactly what Trace was designed for and never delivered. Making
  it real needs a reachable producer (sidecar may propose `trace` + TTL), a
  recall mode that surfaces fresh traces (not `Never`), and an actual expiry
  sweep that deletes — not just hides — expired rows.
