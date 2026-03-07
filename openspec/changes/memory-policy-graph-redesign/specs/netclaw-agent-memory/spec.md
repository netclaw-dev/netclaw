## MODIFIED Requirements

### Requirement: Pre-compaction memory flush

The system SHALL replace the current single-step pre-compaction memory flush with checkpoint-driven background memory curation. The session SHALL emit durable memory checkpoints on eligible events including turn completion, explicit memory requests, compaction boundaries, verified tool findings, and accepted subagent findings. Compaction-related checkpoints SHALL be high priority, but the user-facing turn SHALL wait only for durable checkpoint enqueue acknowledgment, not for curator completion.

#### Scenario: Compaction boundary creates a high-priority checkpoint
- **GIVEN** a session is approaching or crossing the compaction threshold
- **WHEN** the session prepares to compact history
- **THEN** the system enqueues a high-priority memory checkpoint containing the relevant summary inputs
- **AND** compaction continues after checkpoint enqueue succeeds

#### Scenario: Checkpoint curation retries after failure
- **GIVEN** a checkpoint was enqueued successfully
- **WHEN** background curation fails or times out
- **THEN** the checkpoint remains pending with retry metadata
- **AND** durable memory is not partially committed

### Requirement: Standard configuration directory

The system SHALL use `~/.netclaw/` as the standard configuration directory with `memory/` as the durable memory home. The memory subsystem SHALL store its SQLite database, schema metadata, and migration artifacts under `~/.netclaw/memory/`. The legacy `~/.netclaw/memories/` directory SHALL be preserved as a read-only migration source during transition and SHALL NOT remain the primary write path after this redesign.

#### Scenario: Memory directory and database created on startup
- **GIVEN** `~/.netclaw/memory/` does not exist
- **WHEN** the Netclaw process starts with the redesigned memory subsystem enabled
- **THEN** the system creates the directory and initializes the SQLite database schema
- **AND** the daemon reports memory status as healthy when initialization succeeds

#### Scenario: Legacy file memory directory preserved during migration
- **GIVEN** `~/.netclaw/memories/` contains legacy markdown memories
- **WHEN** the redesigned memory subsystem starts
- **THEN** the system leaves the legacy files untouched
- **AND** treats them as import input rather than the active write store

### Requirement: Pluggable memory backend with 4-tool surface

The system SHALL use a local SQLite-backed structured memory substrate as Netclaw's default and normative durable memory implementation. The frontline model SHALL continue to see the explicit compatibility tools `find_memories`, `get_memories`, `store_memory`, and `update_memory`, but those tools SHALL operate over the SQLite memory graph and shared policy pipeline rather than selecting between file-backed and Memorizer-backed primary providers. Legacy provider settings MAY be read only for migration and compatibility messaging.

#### Scenario: SQLite memory is the active default substrate
- **GIVEN** Netclaw starts with the redesigned memory system
- **WHEN** no legacy migration override is required
- **THEN** the daemon uses the local SQLite memory database as the primary durable memory store
- **AND** explicit memory tools route to that store

#### Scenario: Legacy provider config triggers compatibility migration
- **GIVEN** configuration still contains a legacy `Memory.Provider` value such as `files` or `memorizer`
- **WHEN** the daemon starts after this redesign
- **THEN** the daemon reports that the legacy provider mode is deprecated
- **AND** begins migration or degraded compatibility behavior instead of treating the legacy provider as the normative architecture

### Requirement: Two-phase memory retrieval

Memory retrieval SHALL run in two modes: automatic pre-turn recall and explicit two-phase retrieval. Automatic recall SHALL happen before each user-facing model turn and SHALL inject a bounded recall bundle derived from the structured memory graph. Explicit retrieval SHALL continue to use `find_memories` for lightweight search and `get_memories` for full hydration when manual follow-up is needed.

#### Scenario: Automatic recall runs before a user-facing turn
- **GIVEN** a user sends a new message into an existing or new session
- **WHEN** the session prepares the next model call
- **THEN** the system runs a policy-aware automatic recall query against durable memory
- **AND** injects a bounded recall bundle before the model sees the turn

#### Scenario: Explicit two-phase retrieval remains available
- **GIVEN** the automatic recall bundle was insufficient or the user explicitly asks what Netclaw remembers
- **WHEN** the frontline model calls `find_memories`
- **THEN** it receives lightweight results suitable for selection
- **AND** can call `get_memories` to fetch full memory bodies only for the selected items

### Requirement: Memory context layer per backend

The memory context layer SHALL explain that durable recall is automatic by default and that explicit memory tools are reserved for manual search, save, and correction workflows. The layer SHALL surface degraded memory status when automatic recall or durable persistence is unavailable. It SHALL no longer teach the model that backend selection is part of normal memory usage.

#### Scenario: Context layer teaches automatic recall first
- **GIVEN** the redesigned memory subsystem is healthy
- **WHEN** a session prompt is assembled
- **THEN** the memory context layer explains that Netclaw automatically recalls durable memory before each turn
- **AND** reserves explicit memory tools for deliberate memory operations

#### Scenario: Context layer reports degraded memory state
- **GIVEN** the memory database is unavailable or recall has been disabled due to an operational fault
- **WHEN** a session prompt is assembled
- **THEN** the memory context layer reports degraded memory status
- **AND** does not claim that durable recall is functioning normally

## REMOVED Requirements

### Requirement: Memorizer-backed store_memory delegation
**Reason**: Durable memory ownership now belongs to Netclaw's first-party SQLite substrate and checkpoint pipeline rather than a Memorizer-backed primary provider.
**Migration**: Route explicit `store_memory` calls through the compatibility facade and background curation pipeline; keep Memorizer only as a deferred optional integration/export path.

### Requirement: Memorizer-backed find/get/update as MCP pass-throughs
**Reason**: Netclaw no longer treats Memorizer MCP tools as the primary durable memory implementation.
**Migration**: Keep `find_memories`, `get_memories`, and `update_memory` as SQLite-backed compatibility tools; any future Memorizer bridge must sit behind the first-party memory service instead of replacing it.

### Requirement: File-based memory store
**Reason**: The markdown file store is replaced by structured SQLite memory so Netclaw can support hierarchy, policy metadata, graph edges, and automatic recall.
**Migration**: Import legacy markdown memories into SQLite and preserve source files as read-only migration artifacts.

### Requirement: Memorizer discovery guidance
**Reason**: Core memory usage should no longer depend on discovering an optional external tool server.
**Migration**: Update system skills and prompt guidance to teach automatic recall plus explicit compatibility tools, with optional Memorizer integration documented separately if reintroduced later.

## ADDED Requirements

### Requirement: Hierarchical anchor graph memory model

The system SHALL model durable memory around anchors/entities with optional parent-child hierarchy and typed graph edges. Anchors SHALL support containment (`project` -> `repo` -> `service`) and non-hierarchical relationships (`related_to`, `depends_on`, `owned_by`) so recall can expand around the relevant entity without flattening all memory into note blobs.

#### Scenario: Recall traverses anchor hierarchy
- **GIVEN** a project anchor contains repository and service child anchors
- **WHEN** a user asks about the project at the parent level
- **THEN** the recall pipeline MAY retrieve child-scoped memory through the hierarchy
- **AND** only items allowed by policy are injected into the recall bundle

### Requirement: Durable memory policy envelope

Every durable anchor, document, record, and edge SHALL carry policy metadata including `domain`, `sensitivity`, `recallMode`, `confidence`, `freshness`, and `updateSemantics`. The write path SHALL assign or reject these values before persistence, and the recall path SHALL filter by them before prompt injection.

#### Scenario: Sensitive memory is blocked from auto recall
- **GIVEN** a stored memory item is marked `domain=business`, `sensitivity=secret`, and `recallMode=manual`
- **WHEN** a personal-domain session runs automatic pre-turn recall
- **THEN** the item is excluded from the automatic recall bundle
- **AND** it remains available only to explicit authorized workflows if policy allows

### Requirement: Documents versus records semantics

The system SHALL distinguish mutable `documents` from immutable `records`. Documents SHALL represent living, mergeable knowledge that can be updated in place with version history. Records SHALL represent time-bound observations that are immutable once written and can only be superseded, expired, or tombstoned by subsequent operations.

#### Scenario: Preference update modifies a document
- **GIVEN** an operator preference is stored as a document on a `person` anchor
- **WHEN** the operator corrects that preference later
- **THEN** the system updates the document according to its merge semantics
- **AND** preserves version lineage for auditability

#### Scenario: Historical event becomes a superseded record
- **GIVEN** a host IP change is stored as a record on a `host` anchor
- **WHEN** a newer verified IP change is persisted
- **THEN** the new fact is stored as a new record
- **AND** the older record is marked as superseded rather than overwritten

### Requirement: Rules-first candidate extraction

The system SHALL run deterministic rules before any curator LLM call when converting checkpoints into durable memory. These rules SHALL reject ephemeral chatter, duplicates, policy-violating content, and low-confidence candidates before invoking the curator.

#### Scenario: Trivial chatter is filtered before curation
- **GIVEN** a checkpoint contains both stable project facts and casual acknowledgments
- **WHEN** rules-first extraction runs
- **THEN** the stable facts survive as candidates
- **AND** the casual acknowledgments are dropped without calling the curator for them

### Requirement: Automatic pre-turn recall

The system SHALL execute automatic recall before each user-facing model turn using the latest user message, recent session context, active anchors, and policy scope. Automatic recall SHALL be bounded by a latency budget and SHALL degrade safely when the memory substrate is unavailable.

#### Scenario: Recall completes within budget
- **GIVEN** the memory substrate is healthy
- **WHEN** a new turn begins
- **THEN** the session retrieves and injects a bounded recall bundle before the model call
- **AND** the recall operation completes within the configured time budget or degrades safely

#### Scenario: Recall failure degrades without blocking the turn
- **GIVEN** the memory database is temporarily unavailable
- **WHEN** the session starts automatic recall for a turn
- **THEN** the user-facing turn continues without durable recall injection
- **AND** the session records degraded memory status for diagnostics

### Requirement: Main session owns durable memory persistence

The main user-facing session SHALL be the default owner of durable memory writes. Subagents and other helper workflows SHALL return findings to the owning session, and the owning session SHALL decide whether those findings become checkpoints and durable writes.

#### Scenario: Subagent findings flow through the parent session
- **GIVEN** a subagent returns structured findings from research work
- **WHEN** the parent session accepts those findings
- **THEN** the parent session turns them into a checkpoint for durable memory review
- **AND** the subagent does not write durable memory directly

### Requirement: Memory evaluation and operational criteria

The redesigned memory subsystem SHALL ship with an eval suite and operational SLOs covering recall quality, noise suppression, privacy behavior, and latency. The implementation SHALL NOT be considered complete until the seeded eval suite demonstrates the configured thresholds.

#### Scenario: Seeded memory eval suite passes
- **GIVEN** the seeded recall/privacy fixture suite is executed against the redesigned subsystem
- **WHEN** the results are reported
- **THEN** relevant recall coverage, noise suppression, privacy leakage, and latency metrics meet the thresholds defined by the change design
- **AND** a failing metric blocks rollout from being treated as complete

#### Scenario: Local Ollama eval profile is the primary gate
- **GIVEN** the seeded memory eval suite supports multiple model profiles
- **WHEN** Netclaw validates the redesigned memory subsystem before rollout
- **THEN** it runs the default gate against smaller local Ollama-hosted models
- **AND** passing larger hosted models does not waive a failing local Ollama eval result

### Requirement: Legacy memory migration and compatibility stance

The system SHALL provide a migration path from the legacy markdown memory directory and legacy provider-oriented configuration into the SQLite structured memory substrate. During the transition window, explicit memory tool names SHALL remain stable so existing prompts and skills continue to function.

#### Scenario: Legacy markdown memories import into SQLite
- **GIVEN** legacy markdown memories exist under `~/.netclaw/memories/`
- **WHEN** the operator runs or accepts migration into the redesigned subsystem
- **THEN** those memories are imported into anchors, documents, and records in SQLite
- **AND** the original markdown files are preserved as source artifacts

#### Scenario: Compatibility tool names remain stable
- **GIVEN** an existing prompt or skill instructs the model to use `find_memories` and `store_memory`
- **WHEN** the redesigned memory subsystem is active
- **THEN** those tool names continue to function
- **AND** they execute against the SQLite memory service and policy pipeline
