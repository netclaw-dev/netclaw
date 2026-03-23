## Context

Memory curation currently runs as a `BackgroundService` polling loop
(`MemoryCurationWorkerService`) that leases checkpoints from SQLite every
250ms and writes them to the store via `ApplyCurationBatchAsync`. There are
two checkpoint paths:

1. **`MemoryCheckpointPayload`** (turn-complete, explicit requests) — goes
   through `MemoryCurationEngine.CurateAsync()` which runs fingerprint dedup
   against existing memories before extracting operations.
2. **`ObservedMemoryCheckpointPayload`** (sidecar proposals) — returns
   operations directly with zero evaluation. This is the primary source of
   duplicates and fragmented anchors.

The observation sidecar LLM generates proposals without visibility into
existing stored memories. It picks canonical names independently each time,
producing `akka-net-release`, `akka-net-latest-release`, and
`akka.net-release-1.5.62` as separate anchors for the same concept.

The anchor-based dedup added in the current PR catches same-anchor
duplicates at the SQL layer, but cannot detect cross-anchor semantic
duplicates, update stale content, or consolidate fragmented anchors.

## Goals / Non-Goals

**Goals:**

- All memory writes flow through an evaluate-before-write step that queries
  existing memories before deciding what to persist
- Same-anchor duplicates are collapsed (skip or update in place)
- Near-duplicate anchors are detected via fuzzy name matching and consolidated
- Stale content is updated rather than duplicated alongside the old version
- The evaluation logic is encapsulated in a single actor with a clear system
  prompt, making it easy to upgrade from rules-based to LLM-assisted evaluation
- Failed evaluations retry naturally via actor scheduling
- No schema changes to the SQLite memory tables

**Non-Goals:**

- Embedding-based semantic similarity (future upgrade, not v1)
- LLM-assisted evaluation in v1 (the actor carries a prompt but v1 uses
  deterministic rules; the prompt is the upgrade point)
- Changing the observation sidecar's proposal format or behavior
- Changing the explicit memory tools (`find_memories`, `store_memory`, etc.)
- Cross-session memory consolidation (actor operates within a single domain)

## Decisions

### 1. ReceiveActor with state machine behaviors, not a persistent actor

The curation actor does not need event sourcing or snapshots. It processes
each proposal to completion (write or skip), then moves to the next. There
is no state worth recovering — the checkpoint queue in SQLite is the durable
log. If the actor crashes, the `MemoryCurationWorkerService` re-leases
pending checkpoints on restart.

**Alternative considered:** `ReceivePersistentActor` with persisted events.
Rejected because the checkpoint table already provides exactly-once
delivery semantics. Adding persistence would double-write (persist event +
write to memory tables) without benefit.

### 2. Per-session child actor, not a global singleton

The `MemoryCurationActor` is a child of `LlmSessionActor`, created on
demand when the first memory proposal arrives. This gives it:

- **Per-session context for free** — the parent passes domain, sensitivity
  defaults, and recently-used anchor names. The child can build a working
  set of "anchors I've seen this session" and reuse them, preventing the
  sidecar from reinventing names each turn.
- **Automatic lifecycle** — created in `PreStart`, dies when parent
  passivates. No lazy creation, no null checks, no orphan cleanup.
- **No contention** — each session evaluates its own proposals independently.

Cross-domain visibility comes from the shared SQLite database, not from
actor topology. The child queries the same `memory_anchors` and
`memory_documents` tables that a global actor would. Concurrent writes from
different sessions are safe because the `ON CONFLICT` upsert in SQLite
handles contention at the transaction level.

**Alternative considered:** Global singleton actor (like `ReminderManagerActor`).
Rejected because it adds a routing bottleneck without benefit — the database
already provides cross-session visibility, and per-session context (recent
anchors, domain) would need to be serialized into every message instead of
being available from the parent.

The `MemoryCurationWorkerService` continues to handle checkpoint queue
drain for crash recovery — any checkpoints not processed inline by a
session's child actor get picked up by the worker on restart.

### 3. Three-phase evaluation pipeline

Each proposal goes through:

**Phase 1 — Anchor resolution:**
Normalize the proposed anchor name (lowercase, trim, dash-for-spaces).
Query `memory_anchors` for exact match. If no exact match, query for fuzzy
matches using prefix/suffix overlap or Levenshtein-like heuristic on the
normalized name (e.g., `akka-net-release` matches `akka-net-latest-release`
if they share a long common substring and differ by ≤2 tokens).

**Phase 2 — Content comparison:**
If an existing anchor is found, fetch its most recent document. Compare
content:
- If content is substantially similar (e.g., >80% token overlap after
  normalization) → **Skip** (redundant)
- If content differs and proposal is newer (by `freshness_at`) → **Update**
  (replace document body, preserve document ID)
- If content differs and proposal is older → **Skip** (stale proposal)

**Phase 3 — Consolidation check:**
If fuzzy matching found multiple existing anchors covering the same concept,
pick the canonical anchor (most documents, highest confidence, or oldest
creation date as tiebreaker). Migrate documents from redundant anchors to
the canonical one, tombstone the redundant anchors.

**Immutable records** (`MemoryKind.Record`) skip Phase 2-3 and always create
(append-only semantics preserved).

### 4. Two ingestion paths: inline from session + worker for recovery

**Primary path (inline):** `LlmSessionActor` sends proposals directly to
its curation child via `Tell` after the observation sidecar completes. This
skips checkpoint serialization/deserialization entirely for the hot path.
The child evaluates and writes to the store.

**Recovery path (worker):** `MemoryCurationWorkerService` keeps its polling
loop for checkpoints that weren't processed inline — crash recovery,
explicit memory requests, compaction boundary checkpoints. The worker calls
`MemoryCurationEngine.CurateAsync()` + `store.ApplyCurationBatchAsync()`
as before (no actor involvement for recovery writes).

This means observed-memory proposals (the main source of duplicates) go
through the actor's evaluate-before-write pipeline, while the existing
checkpoint queue handles durability and crash recovery without change.

### 5. Fuzzy anchor matching via normalized token sets

Anchor names are tokenized by splitting on `-`, then compared as sets.
Two anchors are considered "fuzzy matches" if:
- They share ≥60% of their tokens (Jaccard similarity)
- AND the shorter name is a subset of the longer name's tokens, OR they
  differ by at most 1 token

Examples:
- `akka-net-release` vs `akka-net-latest-release` → tokens `{akka,net,release}`
  vs `{akka,net,latest,release}` → subset match → fuzzy match
- `akka-net-release` vs `user-preferred-color` → 0% overlap → no match
- `akka-net-release-1.5.62` vs `akka-net-release` → subset → fuzzy match

This is cheap (string operations only, no LLM) and catches the observed
fragmentation patterns without false positives on unrelated anchors.

### 6. Actor behaviors: Idle → Evaluating → Writing

```
Idle:
  receive CurateCheckpoint → extract operations → for each operation:
    query existing anchors/documents → Evaluating

Evaluating:
  apply skip/update/consolidate/create decision
  if more operations in batch → continue evaluating
  when batch complete → execute writes → Writing

Writing:
  execute batch write to SQLite
  reply CurationCompleted to sender
  → Idle
```

The actor processes one checkpoint at a time (batch of operations). This is
intentionally sequential — memory writes are not latency-sensitive and
sequential processing avoids concurrent modification of the same anchors.

### 7. Curation evaluation prompt

The actor uses a two-tier evaluation strategy. v1 applies deterministic
rules first and only falls through to the LLM prompt when the rules produce
an ambiguous result (e.g., fuzzy anchor match found but content similarity
is in the 40-80% gray zone).

**Rules tier (always runs, no LLM cost):**
- Exact anchor match + >80% content overlap → Skip
- Exact anchor match + different content + newer timestamp → Update
- Fuzzy anchor match + subset tokens → candidate for consolidation
- No match found → Create

**LLM tier (runs only when rules are ambiguous):**

```
You are a memory curator. You decide whether a proposed memory should be
saved, and if so, how it relates to existing memories.

You will receive:
- A PROPOSED memory (title, anchor name, content, timestamp)
- Zero or more EXISTING memories that may be related (title, anchor name,
  content, timestamp, memory ID)

Make exactly ONE decision:

SKIP — The proposed memory is redundant. An existing memory already
captures this information with equal or greater detail. Do not save.

UPDATE <memory_id> — The proposed memory contains newer or more accurate
information than the identified existing memory. Replace the existing
memory's content with the proposed content. Use this when:
- A version number, date, price, or status has changed
- The proposal adds meaningful detail to an existing fact
- The existing memory is stale (older timestamp, outdated information)

CONSOLIDATE <memory_id> [<memory_id>...] — The proposed memory and one or
more existing memories describe the same concept under different names.
Merge them into a single memory under the best anchor name. Use this when:
- Anchor names are variations of the same thing
  (e.g., "akka-net-release" and "akka-net-latest-version")
- Content overlaps substantially but is spread across multiple entries

CREATE — The proposed memory is genuinely new. No existing memory covers
this topic. Save it as a new entry.

Respond with ONLY the decision keyword and any required IDs. No explanation.

Examples:
  SKIP
  UPDATE doc-abc123
  CONSOLIDATE doc-abc123 doc-def456
  CREATE
```

**User message format (per evaluation):**

```
PROPOSED:
  anchor: {proposed_anchor_name}
  title: {proposed_title}
  content: {proposed_content}
  timestamp: {proposed_freshness_at}

EXISTING CANDIDATES:
[1] id={memory_id} anchor={anchor_name} title={title}
    content: {content_preview}
    timestamp: {freshness_at}

[2] id={memory_id} anchor={anchor_name} title={title}
    content: {content_preview}
    timestamp: {freshness_at}
```

**Design notes:**

- The prompt asks for a single keyword response to minimize token cost and
  parsing complexity. No chain-of-thought needed for this decision.
- Content previews are truncated to ~200 chars to keep the prompt small.
  The full content comparison happens in the rules tier.
- The LLM call uses the compaction model (`ModelRole.Compaction`) with
  reasoning/thinking tokens disabled, same as the existing keyword
  enrichment sidecar pattern. Expected latency: <2s on local Ollama models.
- Timeout: 10s with fallback to the rules-tier decision. If the LLM is
  unavailable, the actor never blocks — it falls back to deterministic
  behavior (create if unsure).
- The prompt is stored as a static method on the actor (like
  `MemorySidecarPromptBuilder`) so it can be iterated without changing the
  actor's structure.

## Risks / Trade-offs

**[Risk] Fuzzy matching false positives** → Mitigation: Conservative
thresholds (subset match OR single-token difference). When in doubt, create
a new anchor — false negatives (extra anchors) are cheaper than false
positives (merging unrelated concepts). Log all consolidation decisions for
operator review.

**[Risk] Consolidation data loss** → Mitigation: Tombstone redundant anchors
rather than delete. Documents are moved (re-anchored), not deleted. All
operations logged. Tombstoned anchors can be restored if a consolidation
was wrong.

**[Risk] Actor throughput under burst load** → Mitigation: The checkpoint
queue absorbs bursts. The actor drains at its own pace. Under extreme load
(100+ checkpoints queued), the worker's 250ms poll interval naturally
throttles delivery. Monitor queue depth via `netclaw stats`.

**[Risk] Stale-wins on clock skew** → Mitigation: All timestamps use the
daemon's `TimeProvider`, so clock skew only matters across daemon restarts.
Within a daemon lifetime, `freshness_at` ordering is reliable.

**[Trade-off]** v1 uses deterministic rules only. This means truly semantic
duplicates with different wording (e.g., "user likes blue" vs "favorite
color is blue") won't be caught without embeddings or LLM evaluation. This
is acceptable for v1 — the rules catch the high-frequency cases (same anchor,
near-duplicate anchor names) that account for the majority of observed duplication.

## Open Questions

1. Should consolidation run eagerly (on every proposal) or as a periodic
   background sweep? Eager catches fragmentation as it happens but adds
   latency per proposal. A periodic sweep could batch consolidation work.

2. When fuzzy matching finds a candidate, should the actor always auto-merge
   or should it log a "consolidation candidate" and wait for operator
   confirmation? v1 leans toward auto-merge with logging.
