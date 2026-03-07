## Why

Source PRDs: `PRD-007` (primary), `PRD-001`, `PRD-002`.

Netclaw's current memory model is too flat, too manual, and too backend-shaped: durable recall depends on the model explicitly calling memory tools, the file-backed store cannot represent entities or relationships, and the optional Memorizer path pushes core memory behavior into an external integration. PRD-007 needs a first-party memory architecture that is local, policy-aware, and reliable enough to drive automatic recall and durable cross-session knowledge without leaking sensitive facts across domains.

## What Changes

- **BREAKING** Replace the current file-backed memory store plus optional Memorizer-backed unified provider as Netclaw's primary memory architecture with a local SQLite-backed structured memory substrate.
- Introduce anchor/entity-oriented memory with hierarchical containment, typed graph edges, and first-class `document` vs `record` semantics.
- Add a policy envelope on durable memory covering domain separation, sensitivity, recall mode, confidence, freshness, and update semantics.
- Move memory recall and most persistence decisions into system-managed flows: automatic pre-turn recall, checkpoint detection, background curation, and rules-first candidate filtering before any curator LLM call.
- Make the main user-facing session the default owner of durable memory writes; subagents return findings to the parent session instead of writing durable memory directly.
- Preserve a minimal explicit memory tool surface for operator-directed save/search/correct flows, but treat it as a compatibility layer over the new substrate rather than the primary recall path.
- Define migration from `~/.netclaw/memories/` and legacy `Memory.Provider` settings, along with a compatibility stance for existing prompts, skills, and optional Memorizer integrations.
- Add measurable success and eval criteria for recall quality, noise suppression, privacy behavior, and latency, with local Ollama-model evaluation as the primary pre-rollout gate.
- Keep scope explicit: SQLite structured memory, policy-aware recall, checkpoint curation, compatibility shims, and migration are MVP-now; embeddings, external sync, and richer graph tooling remain deferred.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `netclaw-agent-memory`: replaces backend-centric memory with SQLite structured memory, automatic recall, policy-aware persistence, checkpoint curation, compatibility tools, and migration behavior.
- `netclaw-session`: adds pre-turn automatic recall, checkpoint scheduling, and degraded recovery behavior around memory reads/writes.
- `netclaw-subagents`: changes durable memory ownership so subagents report findings back to the main session instead of persisting by default.

## Impact

- **Code/runtime**: new SQLite memory schema, repositories/query layer, checkpoint queue, recall planner, background curator worker, compatibility tool facades, and session/subagent wiring changes.
- **Persistence**: durable memory moves out of markdown files and away from optional MCP-provider coupling into a dedicated first-party SQLite store with explicit migration from legacy data.
- **Security/privacy**: domain and sensitivity policy become part of every durable memory object; automatic recall must fail closed on policy mismatches and degrade safely on storage errors.
- **Operations**: the daemon must surface memory health, pending checkpoints, and migration state; existing file memories remain importable during transition.
- **Compatibility**: existing `find_memories`/`get_memories`/`store_memory`/`update_memory` usage remains supported initially through shims, while legacy file and Memorizer provider modes are deprecated as primary backends.
