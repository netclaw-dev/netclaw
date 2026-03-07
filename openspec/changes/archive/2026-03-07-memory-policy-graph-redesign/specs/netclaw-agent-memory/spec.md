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

The system SHALL use `~/.netclaw/` as the standard configuration directory with `memory/` as the durable memory home. The memory subsystem SHALL store its SQLite database, schema metadata, and health/queue state under `~/.netclaw/memory/`. The redesigned MVP SHALL NOT require any legacy memory directory or import step in order to start cleanly.

#### Scenario: Memory directory and database created on startup
- **GIVEN** `~/.netclaw/memory/` does not exist
- **WHEN** the Netclaw process starts with the redesigned memory subsystem enabled
- **THEN** the system creates the directory and initializes the SQLite database schema
- **AND** the daemon reports memory status as healthy when initialization succeeds

#### Scenario: Greenfield startup requires no legacy memory store
- **GIVEN** `~/.netclaw/memory/` is empty and `~/.netclaw/memories/` does not exist
- **WHEN** the redesigned memory subsystem starts for the first time
- **THEN** the system initializes successfully without any import step
- **AND** uses the SQLite memory store as the only required durable memory substrate

### Requirement: Pluggable memory backend with 4-tool surface

The system SHALL use a local SQLite-backed structured memory substrate as Netclaw's default and normative durable memory implementation. The frontline model SHALL continue to see the explicit tools `find_memories`, `get_memories`, `store_memory`, and `update_memory`, but those tools SHALL operate over the SQLite memory graph and shared policy pipeline rather than selecting between file-backed and Memorizer-backed primary providers. Legacy provider modes SHALL NOT be required for MVP delivery.

#### Scenario: SQLite memory is the active default substrate
- **GIVEN** Netclaw starts with the redesigned memory system
- **WHEN** the daemon initializes the memory subsystem
- **THEN** the daemon uses the local SQLite memory database as the primary durable memory store
- **AND** explicit memory tools route to that store

#### Scenario: MVP does not depend on legacy provider compatibility
- **GIVEN** the redesigned memory subsystem is being delivered for greenfield MVP use
- **WHEN** implementation scope is evaluated
- **THEN** SQLite-backed memory and the explicit tool surface are sufficient for completion
- **AND** legacy provider-mode bridging may be omitted or deferred to a future change

### Requirement: Two-phase memory retrieval

Memory retrieval SHALL run in two modes: automatic pre-turn recall and explicit two-phase retrieval. Automatic recall SHALL happen before each user-facing model turn and SHALL inject a bounded recall bundle derived from the structured memory graph. Explicit retrieval SHALL continue to use `find_memories` for lightweight search and `get_memories` for full hydration when manual follow-up is needed. Automatic recall is the primary retrieval path; explicit retrieval is a deliberate manual-control path.

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

#### Scenario: Routine turn relies on automatic recall first
- **GIVEN** a normal user-facing turn begins
- **WHEN** the automatic recall bundle already provides the relevant durable context
- **THEN** the frontline model does not need to call explicit retrieval tools by default
- **AND** proceeds using the system-managed recall bundle

### Requirement: Memory context layer per backend

The memory context layer SHALL explain that durable recall is automatic by default and that explicit memory tools are reserved for deliberate manual search, save, and correction workflows. The layer SHALL surface degraded memory status when automatic recall or durable persistence is unavailable. It SHALL no longer teach the model that backend selection is part of normal memory usage, and it SHALL explicitly tell the frontline model not to call write tools reflexively on every turn.

#### Scenario: Context layer teaches automatic recall first
- **GIVEN** the redesigned memory subsystem is healthy
- **WHEN** a session prompt is assembled
- **THEN** the memory context layer explains that Netclaw automatically recalls durable memory before each turn
- **AND** reserves explicit memory tools for deliberate memory operations

#### Scenario: Context layer distinguishes store and update usage
- **GIVEN** the redesigned memory subsystem is healthy
- **WHEN** memory guidance is injected into the session prompt
- **THEN** the guidance says `store_memory` is for deliberate save/remember actions
- **AND** the guidance says `update_memory` is for correction, supersede, tombstone, or metadata changes to existing memory

#### Scenario: Context layer reports degraded memory state
- **GIVEN** the memory database is unavailable or recall has been disabled due to an operational fault
- **WHEN** a session prompt is assembled
- **THEN** the memory context layer reports degraded memory status
- **AND** does not claim that durable recall is functioning normally

## REMOVED Requirements

### Requirement: Memorizer-backed store_memory delegation
**Reason**: Durable memory ownership now belongs to Netclaw's first-party SQLite substrate and checkpoint pipeline rather than a Memorizer-backed primary provider.
**Migration**: Route explicit `store_memory` calls through the SQLite-backed explicit tool facade and background curation pipeline; keep Memorizer only as a deferred optional integration/export path if it is reintroduced later.

### Requirement: Memorizer-backed find/get/update as MCP pass-throughs
**Reason**: Netclaw no longer treats Memorizer MCP tools as the primary durable memory implementation.
**Migration**: Keep `find_memories`, `get_memories`, and `update_memory` as SQLite-backed explicit tools; any future Memorizer bridge must sit behind the first-party memory service instead of replacing it.

### Requirement: File-based memory store
**Reason**: The markdown file store is replaced by structured SQLite memory so Netclaw can support hierarchy, policy metadata, graph edges, and automatic recall.
**Migration**: No import path is required for MVP; if legacy markdown compatibility is needed later, it should be introduced through a follow-up change.

### Requirement: Memorizer discovery guidance
**Reason**: Core memory usage should no longer depend on discovering an optional external tool server.
**Migration**: Update system skills and prompt guidance to teach automatic recall plus explicit manual tools, with optional Memorizer integration documented separately if reintroduced later.

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

### Requirement: Explicit memory control paths

The system SHALL treat `store_memory` and `update_memory` as deliberate manual-control paths layered on top of automatic recall and background curation. The frontline agent SHALL invoke `store_memory` only for explicit remember/save requests, deliberate high-value pinning, or operator-directed structured note capture. The frontline agent SHALL invoke `update_memory` only for correction, supersede, tombstone, or metadata changes to an existing durable memory item.

#### Scenario: Frontline agent uses store_memory for an explicit save request
- **GIVEN** the user explicitly asks Netclaw to remember a fact or preference
- **WHEN** the frontline agent chooses how to persist that information
- **THEN** it uses `store_memory` as the deliberate explicit write path
- **AND** the request still flows through checkpoint and policy handling rather than direct uncontrolled persistence

#### Scenario: Frontline agent uses update_memory for correction
- **GIVEN** an existing durable memory item must be corrected or superseded
- **WHEN** the frontline agent applies the user's correction
- **THEN** it uses `update_memory`
- **AND** it does not use `store_memory` to create an untracked duplicate for the same correction

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

### Requirement: Greenfield SQLite-first delivery stance

The redesigned memory subsystem SHALL be implementation-ready as a greenfield MVP without requiring legacy markdown import or legacy provider-mode compatibility. Explicit memory tool names SHALL remain stable within the redesigned subsystem so prompt and skill guidance can target a consistent manual-control surface.

#### Scenario: Greenfield MVP completes without import work
- **GIVEN** Netclaw has no production-deployed public memory data that must be preserved
- **WHEN** the redesigned memory subsystem is implemented for MVP
- **THEN** no markdown import or provider-mode migration is required for completeness
- **AND** deferred legacy compatibility does not block delivery

#### Scenario: Explicit tool names remain stable
- **GIVEN** a prompt or skill instructs the model to use `find_memories` and `store_memory`
- **WHEN** the redesigned memory subsystem is active
- **THEN** those tool names continue to function
- **AND** they execute against the SQLite memory service and policy pipeline
