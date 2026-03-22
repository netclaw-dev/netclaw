## Why

The memory curation pipeline writes proposals to the database blindly. The
observation sidecar generates proposals without visibility into what's already
stored, and the `ObservedMemoryCheckpointPayload` path bypasses the fingerprint
dedup that exists on the `MemoryCheckpointPayload` path. This produces
duplicate documents under the same anchor (5 copies of "favorite color is blue")
and fragmented anchors for the same concept (11 different anchor names for
"Akka.NET release info"). Anchor-based dedup at the SQL layer catches
same-anchor duplicates but cannot detect cross-anchor semantic duplicates or
update stale content.

The fix is an evaluate-first actor that sits between proposal generation and
database writes. Before saving, it queries existing memories and makes one of
four decisions:

1. **Skip** — proposal is redundant; an existing memory already says this
2. **Update** — existing memory is stale or incomplete; replace/merge with
   newer content in place
3. **Consolidate** — multiple existing memories cover the same concept under
   different anchors; merge them into one canonical anchor and tombstone the
   others
4. **Create** — genuinely novel; no existing match found

This eliminates the need for after-the-fact dedup and keeps the database clean
from the start. The actor carries a system prompt that guides its evaluation
decisions — initially rules-based (anchor lookup + content comparison), with an
upgrade path to LLM-assisted evaluation for harder cases like staleness
detection and semantic consolidation. Eventually consistent processing is
acceptable since memory writes don't need to complete within the user's turn.

## What Changes

- Introduce a `MemoryCurationActor` (persistent actor with state machine
  behaviors) that receives curation operations from session actors and the
  existing checkpoint pipeline
- The actor queries existing documents/anchors before writing, resolving
  duplicates and stale content at evaluation time instead of write time
- Remove the current `ApplyCurationBatchAsync` direct-write path for
  observed memory proposals — all writes flow through the curation actor
- The `MemoryCurationWorkerService` background loop feeds leased checkpoints
  to the curation actor instead of calling the store directly
- Fuzzy anchor matching (normalized name comparison) catches near-duplicate
  anchors like `akka-net-release` vs `akka-net-latest-release`
- Fresher-wins policy: when a proposal conflicts with an existing document,
  the more recent content replaces the older content in place
- Consolidation: when multiple anchors cover the same concept, merge their
  documents into the canonical anchor and tombstone the redundant ones
- The actor carries a system prompt defining evaluation criteria: what makes
  a memory worth keeping, when existing content is stale, and how to choose
  the canonical anchor during consolidation. This prompt is the upgrade
  point for future LLM-assisted evaluation

## Capabilities

### New Capabilities

(none — this is a pipeline redesign, not a new user-facing capability)

### Modified Capabilities

- `netclaw-agent-memory`: Requirements affected:
  - "Rules-first candidate extraction" — the curation actor becomes the
    evaluation point, replacing blind batch writes with evaluate-then-write
  - "Documents versus records semantics" — merge-document dedup and
    consolidation move from the SQL layer into the actor's evaluation step;
    stale documents are updated in place rather than duplicated
  - "Pre-compaction memory flush" — checkpoint consumption changes from
    direct store writes to actor-mediated evaluation
  - "Hierarchical anchor graph memory model" — consolidation may merge
    fragmented anchors, changing the graph topology to reduce redundancy

## Impact

- **Code**: `MemoryCurationWorkerService`, `MemoryCurationEngine`,
  `SQLiteMemoryStore.ApplyCurationBatchAsync`, `MemoryProposalGate`
- **Actor system**: New persistent actor registered via Akka.Hosting DI,
  child of session manager or standalone top-level actor
- **Database**: No schema changes — same tables, same upsert SQL. The
  anchor-based dedup logic in `ApplyCurationBatchAsync` moves into the actor
- **Dependencies**: None new
- **Operational**: `netclaw stats` memory section unchanged. Curation actor
  health observable via existing Akka logging. Failed evaluations retry via
  actor scheduling (replaces checkpoint retry_count mechanism)
