---
name: netclaw-memory
description: "REQUIRED when the user asks what you remember, recall, or know from past conversations, previous sessions, cross-session memory, memory classes, or memory types. Also before using memory tools: find_memories, get_memories, store_memory, update_memory."
metadata:
  author: netclaw
  version: "1.14.0"
---

# Netclaw Memory

Read this before using any memory tool. It defines how memory works and
when to use each tool.

## Audience and Feature Gating

Memory is subject to two independent gates:

- **Audience gate:** Public sessions have no access to memory tools, automatic
  recall, or memory extraction. Memory is fully inert for Public — no reads,
  writes, or recall. Historical memories authored by Public sessions are also
  excluded from recall and search for all audiences.
- **Deployment gate:** `Memory.Enabled` in `netclaw.json` (default `true`).
  When `false`, memory is disabled for ALL audiences — recall returns empty,
  memory tools are hidden from discovery, and the observation sidecar skips
  extraction.

Both gates must pass for memory to function.

## How Memory Works

- **Automatic recall** runs before each user turn and injects relevant
  `durable_fact` (and occasionally `evidence`) memories into the conversation.
- Recall is **selective by design**: candidates must clear a relevance floor
  and a per-turn character budget, so **many turns inject nothing at all**.
  An absent `[memory-recall]` block means nothing relevant cleared the bar —
  this is the normal, healthy outcome for most turns, not a malfunction and
  not evidence that memory is broken. Never tell the user "my memory isn't
  working" just because a turn had no `[memory-recall]` block. Use
  `find_memories` when you believe relevant memories exist that automatic
  recall did not surface.
- Recall is **policy-aware**: `audience` and `boundary` still govern what
  can be surfaced for the current turn.
- Recall resolves once at turn start and the same bundle is reused during
  tool-loop follow-ups.
- Recalled memories may persist into session history for ongoing context, so
  per-turn policy is **first-contact gating**, not a way to retroactively
  scrub information already surfaced earlier in the session.
- **Explicit tools** are a manual-control layer on top of automatic recall.
- Memory is SQLite-backed and cross-session only within the active
  domain/boundary policy envelope.
- **Duplicate detection is semantic when embeddings are enabled**
  (`Memory.Embeddings.Enabled`): a near-duplicate proposal is nominated by
  embedding similarity and adjudicated by the curator LLM (skip, update,
  consolidate, or create) — similarity alone never merges or skips anything.
  Merges are lossless-or-append: the curator writes a merged body that keeps
  every source fact, and a deterministic guard falls back to appending the
  proposal instead of overwriting when that check fails.
- Memory IDs shown by automatic recall, `find_memories`, and `get_memories`
  (e.g. `doc-…` / `rec-…`) are stable, opaque handles. Copy them **verbatim**
  into `get_memories` or `update_memory` — do not rewrite or reformat them.

### Hybrid Recall (semantic + lexical)

When `Memory.Embeddings.Enabled` is `true`, automatic recall is **hybrid**:
candidates come from the union of full-text search (FTS5) and vector
nearest-neighbor search, then a single fused ranking decides what (if
anything) gets injected. When embeddings are disabled, recall is
lexical-only — same candidate pool, no vector term or cosine floor.

- **Fusion**: each candidate's score is `VectorWeight × cosine similarity +
  LexicalWeight × squashed lexical score`, class-prior adjusted, then
  **recency-decayed** (a half-life multiplier that favors fresher memories
  among otherwise similar candidates but never zeroes out an old one on age
  alone).
- **Query prefix is automatic, per-model**: the turn query is embedded using
  whatever retrieval-query encoding the active embedding model documents —
  for the shipped default `snowflake-arctic-embed-m-int8` (and the
  allowlisted fp32 `snowflake-arctic-embed-m` it's quantized from), that
  means a fixed instruction string is prepended before the query text. This
  is a property of the model, not something you configure; document-side
  embeddings (stored memories) are never prefixed, so this never requires
  re-embedding existing content.
- **Absolute floor**: independent of the fused score, any candidate whose raw
  cosine similarity falls below the effective `MinCosineSimilarity` is
  dropped before ranking. If nothing clears the floor, nothing is injected —
  this is a correct, healthy outcome, not degraded recall. See the
  zero-injection note above: don't editorialize about memory being broken
  when this happens.
- **The floor follows the active model's manifest by default.**
  `Memory.Recall.MinCosineSimilarity` (nullable) is `null` unless an operator
  explicitly overrides it — when `null`, the effective floor is whichever
  calibration is pinned to the currently active embedding model (0.24 for
  the shipped default `snowflake-arctic-embed-m-int8` prefixed encoding;
  also 0.24 for the allowlisted fp32 `snowflake-arctic-embed-m` prefixed
  encoding, calibrated independently — int8 measured as a strict
  retrieval-quality improvement over fp32 on the same gold sets, not a
  size/latency tradeoff). **The numeric
  meaning of this value is model- and encoding-specific**: cosine
  distributions shift materially between models, and even for the same
  model between a prefixed and unprefixed encoding — an old value copied
  from a different model/encoding combination can silently break recall
  (measured: F0.5 = 0.0 when the pre-prefix 0.68 floor was applied to
  prefixed queries). Only set an explicit override after re-running the
  calibration-verification procedure against the model and encoding actually
  active. `Memory.Recall.VectorWeight` defaults 0.7, `LexicalWeight` 0.3,
  `RecencyHalfLifeDays` 30.
- **Degradation is explicit and logged, not silent**: a turn whose
  query-embedding step misses its latency sub-budget (or has no embedder
  available) falls back to lexical-only scoring for that turn and logs
  `memory_recall_vector_degraded`. A model with no manifest-carried
  retrieval calibration and no explicit `MinCosineSimilarity` override
  degrades the same way, with reason `missing_calibration` — this is
  expected for a newly-added or not-yet-calibrated model variant, not a
  bug. A candidate with no embedding row for the current model degrades to
  lexical-only scoring for that candidate alone (rather than being excluded)
  and logs `memory_recall_coverage_gap`. All of these are self-healing or
  intentional, not persistent failures — see Diagnostics below.
- **Backfilling an existing corpus**: enabling `Memory.Embeddings.Enabled`
  on a deployment that already has memories does not retroactively embed
  them. Until they're embedded, recall for those documents degrades to
  lexical scoring and `memory_recall_coverage_gap` keeps firing. Operators
  should run `netclaw memory backfill-embeddings` right after turning
  embeddings on so the gap closes immediately instead of waiting for
  embed-on-write to catch up opportunistically.
- **Upgrading onto a new default model id (e.g. the fp32→int8 default
  flip)**: an existing install with vectors stored under the previous
  `Memory.Embeddings.ModelId` self-heals automatically — vector coverage,
  the curation nominator, and hybrid recall are all scoped to the *current*
  model id, so the daemon's startup gap-repair sweep sees every document as
  missing a current-model embedding and re-embeds the whole corpus under the
  new id with no operator action required. The old model's vectors are never
  deleted, just no longer read. Until gap repair finishes, recall degrades
  to lexical-only (same self-healing, logged degradation as any other
  coverage gap above), and `netclaw doctor` surfaces the interim
  mixed-model state as a warning recommending `netclaw memory
  backfill-embeddings --force` to force it immediately instead of waiting.

### Relevance Gate (cross-encoder)

The cosine floor above answers "is this candidate on-topic?" — it does not
answer "does this candidate actually help answer the question?" A second
stage, the **relevance gate**, runs after the floor for exactly this reason:
a tiny cross-encoder (`ms-marco-minilm-l-6-v2`) jointly scores `(query,
candidate)` for each of the (≤3) floor survivors and drops anything below
its calibrated threshold.

- **Activation follows `Memory.Embeddings.Enabled`** — one mental switch, no
  second thing to discover. `Memory.Recall.RelevanceGate.Enabled` (nullable)
  is an explicit override for an operator who wants embeddings for
  dedup/hybrid-recall but not the extra per-turn cross-encoder latency;
  `Memory.Recall.RelevanceGate.Threshold` (nullable) is an explicit override
  of the manifest's calibrated operating point. Leave both `null` unless you
  have a specific reason to diverge — the manifest-carried default is what
  was validated out-of-sample.
- **Only ever runs in hybrid mode**, on the floor's own survivors — it never
  sees a wider candidate pool and never runs when recall has already
  degraded to lexical-only.
- **Zero survivors after the gate is a healthy outcome**, identical in kind
  to zero survivors at the floor: the `[memory-recall]` block is omitted
  entirely, not emitted empty. Do not treat an absent recall block as
  evidence the gate (or memory generally) is broken — see the zero-injection
  note above.
- **Degradation is explicit and logged, not silent**: when the relevance
  model is unavailable, its sub-budget is exceeded, or recall is running in
  lexical mode, the gate step is skipped and the floor's own result is
  injected unfiltered — the exact pre-gate behavior. This fires
  `memory_recall_gate_degraded` (rate-limited, same cooldown pattern as
  `memory_recall_vector_degraded`). A degraded gate never silently changes
  what gets injected without this marker.

## When to Use Explicit Tools

### `find_memories` + `get_memories`

Use when:
- The user explicitly asks what you remember
- Automatic recall seems insufficient for the question
- You need targeted retrieval beyond the injected bundle

Pattern: `find_memories("query")` -> scan results -> `get_memories("id1,id2")`

Normal `find_memories` behavior:
- searches `durable_fact` plus current `evidence`
- excludes `trace`
- hides expired evidence by default
- respects the current turn's effective `audience` and `boundary`

### `store_memory`

Use only for deliberate save requests:
- User explicitly says "remember this" or "save this for later"
- Pinning a high-value fact, decision, or preference

Do NOT call `store_memory` reflexively on routine turns - the observation
sidecar handles background memory formation automatically.

Policy rules for explicit writes:
- explicit writes still inherit the current turn's `audience` and `boundary`
- explicit writes may narrow policy scope, but must never widen it
- raw secrets, credentials, tokens, and private keys are never durable memory

Automatic observation note:
- a non-empty adopted thread window still counts as adopted context for audit
  and approval provenance
- automatic memory suppression only kicks in when the adopted window includes a
  sender other than the current authorized author
- self-only adopted history does not suppress automatic memory formation by
  itself

### `update_memory`

Use only to correct or supersede an existing memory.

Use the memory ID exactly as shown by automatic recall, `find_memories`, or
`get_memories`. For documents, prefer `new_content` when replacing a full
hydrated memory. Use `old_text` + `new_text` only when making a precise
find-and-replace edit. To delete a memory, pass `delete: true`.

## Memory Classes

| Class | Recall | Expiry |
|-------|--------|--------|
| `durable_fact` | Auto-recall when it clears the relevance floor | Never expires |
| `evidence` | Search (`find_memories`); auto-recall only on very strong matches | Expires after 30 days |
| `trace` | Not searchable | Expires after 72 hours |

## Policy Envelope

Every durable memory item should be understood as carrying:

- `memory_class`
- `audience`
- `boundary`
- `domain`
- `sensitivity`
- `recall_mode`

Write-time and read-time policy both matter. Correct classification alone is
not enough - recall and intentional search must also honor the active trust
context.

## Identity vs Memory

Identity files (`SOUL.md`, `AGENTS.md`, `TOOLING.md`) define **the agent** —
persona, tone, operating rules, and the foundational user grounding set at init
(name, timezone). Do **not** put project facts, research, tool findings, or
**durable facts and preferences about the user** (favorites, family, history,
working preferences) in identity files — those go through the **memory pipeline**
(`store_memory`) and are recalled when relevant. A user asking you to "remember" a
preference is a memory write, not a `SOUL.md` edit.

If unsure, load `netclaw-operations` for the identity-vs-memory triage guide.

## Diagnostics

When memory behavior looks wrong:

1. `netclaw status`
2. `netclaw doctor`
3. load `netclaw-operations`
4. read `docs/runbooks/memory-health-and-evals.md`

Useful log events:

**Recall pipeline** (grep for `memory_retrieval` / `memory_recall`):
- `memory_retrieval_request_plan` — query tokenization, facets, soft scopes, anchor hints
- `memory_retrieval_candidate_selection` — all candidates with selector scores
- `memory_retrieval_final` — floor filtering results, final injected items; carries
  `appliedFloor` and `floorSource` (`manifest` or `override`) so a floor mismatch is
  diagnosable without reading config; also carries `gateScores` (the cross-encoder score for
  every candidate the relevance gate scored) and `droppedByGate` (count the gate dropped) when
  the gate ran
- `turn_memory_recall` — summary event with item count and duration
- `memory_recall_vector_degraded` — turn fell back to lexical-only recall (embedder
  unavailable, no vector index, or the query-embedding sub-budget was exceeded)
- `memory_recall_coverage_gap` — one or more candidates had no embedding row for the
  current model; they degrade to lexical scoring rather than being excluded, and the
  gap self-heals via embed-on-write plus `netclaw memory backfill-embeddings`
- `memory_recall_gate_degraded` — the relevance gate was skipped for this turn (model
  unavailable, sub-budget exceeded, or recall in lexical mode); the floor's own result was
  injected unfiltered

**Formation pipeline** (grep for `memory_observation`):
- `memory_observation_sidecar_completed`
- `memory_observation_gate_result`

### Embeddings

Embeddings are provisioned at daemon start when `Memory.Embeddings.Enabled` is
`true` (default `false` for now). When unavailable:
- Log: `memory_embedding_unavailable` (embedder) or `memory_relevance_gate_unavailable`
  (relevance/cross-encoder model)
- Daemon status shows: `embeddings: degraded`
- Lexical recall continues to work normally
- An operator alert (`memory.embedding_model.unavailable` /
  `memory.relevance_model.unavailable`, pushed via the same notification sink as
  `provider.unreachable`/`reminder.execution.failed`) fires once per model per
  daemon run, naming the model, the failure reason, and the consequence (lexical-only
  recall/dedup, or an unfiltered relevance gate) — this is the push-based signal;
  `netclaw doctor`/`netclaw status` remain the pull-based ones

`netclaw doctor`'s Memory Embeddings check reports whether the active model
has a query prefix (`queryPrefix=True/False`) and the effective retrieval
floor plus its source (`floor=0.240 (source=manifest)`, or `floor=none ...`
when the active model carries no retrieval calibration and no override is
configured) — check this first when recall quality looks off after a model
or config change.

To repopulate existing memory vectors after enabling embeddings:
```
netclaw memory backfill-embeddings [--force]
```

## Eval Gate

Before rollout, run the redesigned provider-independent eval suites first,
then optional live smoke checks with local Ollama models.
