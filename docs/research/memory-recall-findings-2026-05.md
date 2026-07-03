# Recall autoresearch — findings (first pass)

> Tooling referenced in this report (miners, judges, dedup harnesses) lives in
> the operator-local research store (`~/recall-research-local/`), not in the
> repo — it operates on real (PII) corpus data.

Offline calibration of netclaw's **read-side** memory recall, Karpathy-autoresearch
style. Optimized against a local snapshot of a real ~693-memory store with an
80-query human-labeled gold set (corpus + gold never committed — PII). Objective:
**F0.5 over the load decision** (reward loading relevant + rejecting junk; penalize
missing relevant + loading junk; β=0.5 favors precision — "fewer, more pertinent").

## Result (validated)

Coordinate descent over `RecallScoringParams` (91 experiments) on a 56-query train
split, validated on a 24-query held-out test split:

| set | baseline F0.5 | optimized F0.5 | precision | recall |
|-----|--------------|----------------|-----------|--------|
| train (56) | 0.513 | 0.586 | 0.50 → 0.59 | 0.75 → 0.68 |
| **held-out test (24)** | 0.612 | **0.679** (+11%) | **0.60 → 0.70** | 0.86 → 0.76 |

Train and test gains are consistent → the improvement generalizes (not overfitting).
It is almost entirely a **precision** gain: it loads materially less junk.

## Discovered config + why it works

`{ MinimumCompositeScore: 20, DurableFactBonus: 480, AnchorMatchWeight: 2, SoftScopeWeight: 0 }`

- **Composite floor 10 → 20** — the production floor was too permissive; many marginal
  matches that barely cleared 10 were junk.
- **Durable-fact bonus 120 → 480** — strongly prefer atomic `durable_fact` memories
  over `evidence`/`trace`. (Evidence-class blobs — see below — were a major polluter.)
- **Anchor weight 8 → 2 and soft-scope weight 3.5 → 0** — the key insight. Loose
  anchor/soft-scope matching was the **junk-injection vector**: large, topically broad
  memories match many anchors/scopes and score highly without being relevant. Damping
  these signals lets precise lexical matches dominate, so atomic relevant memories win
  and broad blobs collapse below the floor.

## The `compaction-boundary` pollution (a write-side issue)

The single worst recall polluter is a cluster of **10 memories titled
`compaction-boundary`**. These are **session compaction summaries** — when a session
fills its context window, netclaw summarizes the conversation; that 4–7 KB summary is
being persisted as a **`searchable` `evidence` memory** (facet `session-summary`,
anchor `compaction`).

Because each is a multi-thousand-character kitchen-sink summary, it lexically matches
*almost any* query in that session's topic area — so it ranked **#1 for unrelated
queries** (e.g. scored 41 and outranked every real doc for a hardware-planning query).
This is fundamentally a **write-side / formation problem**: whole-session summaries
should not be auto-recallable atomic memories. The read-side optimization above
*suppresses* them (via the anchor/scope/floor changes), but the durable fix is to stop
persisting compaction summaries as `searchable` recall documents (or give them a
non-recall class).

## Residual ceiling (next lever)

A human debrief of before/after loads showed the metric win is real but not uniform:
- The aggressive floor (20) sometimes drops a genuinely-relevant doc along with junk
  (a recall cost on a minority of queries).
- Some ranking failures **persist** and are *not* fixable by numeric weights — e.g. an
  unrelated "TUI library" doc outranking the on-topic doc. These are rooted **upstream
  in query planning** (anchor/facet/scope inference in `DeterministicRetrievalPlanning.cs`)
  and in the FTS candidate fetch (4 gold docs were never retrieved at all). That planner
  is the highest-leverage surface for the next pass.

## Follow-up: quantifying the compaction fix + hardening the optimizer

**Compaction fix in isolation is modest.** Declassing the 10 `compaction-boundary`
docs to a non-auto-recall mode (simulating the write-side fix, netclaw-dev/netclaw#1224)
moved the full-set baseline only +0.008 (F0.5 0.543 → 0.551) and was flat on the test
split. The blobs are egregious on the *few* queries they hijack, but a small slice of
the total junk. The read-side config remains the larger, durable lever — and it's
*additive*: applied on the compaction-fixed corpus it scores **0.678–0.691** on test
(vs 0.679 on the polluted corpus), i.e. slightly better once the blobs are gone.

**Coordinate descent is path-dependent — use multi-start.** A single greedy run from
defaults on the compaction-fixed corpus got trapped at `floor≈16` (held-out test 0.610,
*below* the 0.612 default — it would have shipped a regression). Multi-start
(defaults + a known-good seed + random restarts, fixed RNG seed) escapes the trap: the
seeded start reached `floor22/df480/anchor2/ss0` → held-out test **0.678**. The random
restarts mostly fell back into the weak basin, so seeding from prior winners matters.
`optimize.py` now does multi-start by default.

## Query planning: vocabulary-aware inference is a measured dead end

The `plan` command exposed why recall is coarse: on real (lowercase, technical)
queries the planner emits **zero facets, zero scopes, and often zero anchors** —
its facet/anchor inference is hardcoded to a handful of demo domains and its anchor
regex only catches capitalized tokens. So recall runs on **lexical/BM25 matching**
almost alone; the `+6` facet signal is dead (fired on 6 of 80 queries, and only
`project_fact` of its 4 hardcoded facets even exists on real docs), and soft-scopes
just re-emit the anchors (which is why the optimizer drove `SoftScopeWeight→0`).

Hypothesis: make the planner **vocabulary-aware** — match query tokens against the
store's real anchor + facet vocabulary. Built it (anchor/facet vocab from the store,
document-frequency rarity gates, one-directional prefix matching) behind a flag and
measured it. **It consistently underperformed plain lexical baseline:**

| variant | gold-all F0.5 | held-out test (tuned params) |
|---------|--------------|------------------------------|
| baseline (no vocab) | **0.551** | **0.691** |
| vocab-aware (best of 3 tightenings) | 0.521–0.543 | 0.607 |

Why: the clean signal (rare entities like `vllm`, `rdma`) is small and largely
already covered by the capitalized-acronym regex, while **common query words map to
many facets** (`configured`→every `*_config`, `monitoring`/`search`/`provider`
likewise), each adding `+6` to loosely-related docs — noise ≥ signal. No deterministic
prefix rule fixes `work`↔`workflow` / `configured`↔`configuration`: mapping free-text
queries to a facet taxonomy is **fundamentally a semantic problem**.

**Reverted** (kept only the `plan` diagnostic command). The conclusion: deterministic
planning has hit its ceiling; the semantic/facet recall lever is **embeddings** (the
ONNX/vector path), not more keyword rules. A vocabulary snapshot would also need
ongoing refresh (TTL / invalidate-on-write / incremental) — maintenance cost we'd
only take on for a feature that actually helps, which this doesn't.

## Embedding (semantic) recall — POC

Deterministic planning was a dead end because free-text -> memory matching is
semantic. POC: embed every recallable memory and each query with a local
sentence-embedding model (ollama `all-minilm`, 384-dim, ~33M params), rank by
cosine over the WHOLE corpus (no BM25 fetch ceiling), score on the SAME gold +
objective. `embed_poc.py`.

| metric | lexical (after full tuning loop) | embedding (all-minilm, one threshold) |
|--------|----------------------------------|---------------------------------------|
| gold-all F0.5 | 0.551 default / ~0.61 tuned | **0.695** |
| held-out test F0.5 | **0.691** | **0.699** |
| MRR | 0.836 | **0.878** |

With a *single* knob (similarity threshold τ=0.54) and no weights/planner/floor
machinery, embedding recall **matches the fully-tuned deterministic system on
held-out test** and far exceeds untuned lexical. It also fixes the specific
failures tuning could not: for a hardware-spec query it ranks the specifications
doc above the dated price-watch docs; for a subsystem-mechanics query it returns
the five on-topic docs instead of an unrelated UI doc that shared a keyword.

Crucial caveat — **this understates embeddings.** The gold was pooled from *lexical*
candidates, so relevant docs only embeddings surface (e.g. the doc that ranked #1
for the subsystem-mechanics query, plus several inference-stack docs) are
unlabeled and counted as false positives. A fair number needs re-pooling (label the
union of lexical + embedding top-k).

Caveats / next: 256-token cap truncates long memories (needs chunking); production
requires the ONNX/vector path (embed-on-write, vector store, ANN search). But the
signal is clear: **semantic embeddings are the lever for the residual recall problems
deterministic methods can't reach.**

### Model comparison + re-pooled (fair) gold

Benchmarked three local ollama embedders. `mxbai-embed-large` (335M) is the best
ranker; `all-minilm` (33M) is nearly identical on this corpus; `nomic-embed-text`
underperforms badly via ollama (nDCG 0.45 — a known ollama quirk, not the model).

Then **re-pooled the gold**: added the 42 embedding-surfaced, lexical-missed docs
across 25 queries (e.g. "Akka Reminders Local Repository Path", "vLLM DGX Spark Issue
Monitor Script" — both FTS recall-ceiling misses). Fair comparison:

| metric | lexical (tuned) | mxbai-large | all-minilm |
|--------|-----------------|-------------|------------|
| MRR | ~0.84 | **0.931** | **0.931** |
| gold-all best F0.5 | ~0.61 | 0.717 | **0.727** |
| held-out test F0.5 | 0.663 | 0.638¹ | **0.723** |

¹ threshold-transfer noise on the 24-q test; mxbai's gold-all best-τ (0.717) is the
reliable read. On a fair gold, embeddings beat the fully-tuned deterministic system —
MRR 0.93 vs 0.84 — and lexical recall drops (0.79->0.69) as its fetch-ceiling misses
become visible. The 33M model suffices at this corpus size; a bigger model's edge
would show at scale. (Re-pool additions are a title-based draft pending ratification.)

## Reproduce

See `program.md`. Harness: `eval` / `dump-candidates` / `dedup` / `plan`. Loop:
`optimize.py` (multi-start). Embedding POC: `embed_poc.py` (local ollama `all-minilm`;
`--show <queryIds>` to inspect top hits).
The tuned config is **not** promoted to `RecallScoringParams.Default` — it is tuned on
one corpus and shows per-query recall regressions; promotion needs cross-corpus
validation first.
