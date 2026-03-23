## ADDED Requirements

### Requirement: Per-session memory curation actor

Each `LlmSessionActor` SHALL create a `MemoryCurationActor` child during
`PreStart`. The curation actor starts in an `Idle` behavior and processes
proposals as they arrive. The session actor SHALL send observed-memory
proposals to its curation child via `Tell` instead of enqueuing them as
checkpoints. The curation actor SHALL query the shared SQLite memory store
for existing anchors and documents during evaluation — cross-session and
cross-domain visibility comes from the database, not from actor topology.

#### Scenario: Curation actor created at session start

- **GIVEN** a new `LlmSessionActor` is starting
- **WHEN** `PreStart` executes
- **THEN** the session creates a `MemoryCurationActor` child via
  `Context.ActorOf`
- **AND** the child starts in `Idle` behavior

#### Scenario: Curation actor dies with parent session

- **GIVEN** a session has an active curation child actor
- **WHEN** the session passivates due to idle timeout
- **THEN** the curation child is stopped automatically
- **AND** no orphan actors remain in the actor system

### Requirement: Four-decision evaluation pipeline

The curation actor SHALL evaluate each proposed document operation by
querying existing memories and making exactly one of four decisions:

1. **Skip** — the proposal is redundant; an existing memory already captures
   this information with equal or greater detail.
2. **Update** — an existing memory covers this topic but is stale or
   incomplete; the actor SHALL replace the existing document's content with
   the proposal's content, preserving the existing document ID.
3. **Consolidate** — multiple existing memories describe the same concept
   under different anchor names; the actor SHALL merge documents into a
   single canonical anchor and tombstone the redundant anchors.
4. **Create** — the proposal is genuinely novel; no existing memory matches.

Immutable records (`MemoryKind.Record`) SHALL bypass the evaluation pipeline
and always create (append-only semantics preserved).

#### Scenario: Redundant proposal is skipped

- **GIVEN** an existing document under `anchor:aaron` contains "favorite
  color is blue"
- **WHEN** a new proposal arrives with anchor name "aaron" and content
  "favorite color is blue"
- **THEN** the curation actor decides **Skip**
- **AND** no new document is created

#### Scenario: Stale document is updated in place

- **GIVEN** an existing document under `anchor:akka-net-release` contains
  "latest version is 1.5.60" with freshness timestamp T1
- **WHEN** a new proposal arrives with anchor name "akka-net-release" and
  content "latest version is 1.5.62" with freshness timestamp T2 > T1
- **THEN** the curation actor decides **Update**
- **AND** the existing document's content is replaced with the proposal's
  content
- **AND** the existing document ID is preserved

#### Scenario: Fragmented anchors are consolidated

- **GIVEN** existing documents exist under `anchor:akka-net-release` and
  `anchor:akka-net-latest-release` with overlapping content
- **WHEN** a new proposal arrives with anchor name "akka-net-version-info"
  covering the same topic
- **THEN** the curation actor decides **Consolidate**
- **AND** documents are migrated to the canonical anchor (most documents or
  highest confidence)
- **AND** redundant anchors are tombstoned, not deleted

#### Scenario: Novel proposal creates new memory

- **GIVEN** no existing anchor or document matches the proposal's topic
- **WHEN** the proposal is evaluated
- **THEN** the curation actor decides **Create**
- **AND** a new anchor and document are persisted

#### Scenario: Immutable records bypass evaluation

- **GIVEN** a memory operation has `MemoryKind.Record`
- **WHEN** the curation actor receives it
- **THEN** it creates the record without querying existing memories
- **AND** append-only semantics are preserved

### Requirement: Two-tier evaluation strategy

The curation actor SHALL apply deterministic rules first and invoke the LLM
evaluation prompt only when the rules produce an ambiguous result. The rules
tier SHALL run at zero inference cost. The LLM tier SHALL use the compaction
model with reasoning tokens disabled and a 10-second timeout.

#### Scenario: Rules tier resolves exact anchor match

- **GIVEN** a proposal's normalized anchor name exactly matches an existing
  anchor
- **AND** content overlap exceeds 80%
- **WHEN** the rules tier evaluates the proposal
- **THEN** it decides **Skip** without invoking the LLM

#### Scenario: Rules tier resolves fresher content

- **GIVEN** a proposal's normalized anchor name exactly matches an existing
  anchor
- **AND** the proposal has a newer freshness timestamp and different content
- **WHEN** the rules tier evaluates the proposal
- **THEN** it decides **Update** without invoking the LLM

#### Scenario: Ambiguous result falls through to LLM tier

- **GIVEN** fuzzy anchor matching finds a candidate
- **AND** content similarity is between 40% and 80%
- **WHEN** the rules tier cannot make a confident decision
- **THEN** the curation actor invokes the LLM evaluation prompt with the
  proposal and existing candidates
- **AND** the LLM returns one of: SKIP, UPDATE, CONSOLIDATE, or CREATE

#### Scenario: LLM timeout falls back to rules decision

- **GIVEN** the LLM evaluation call exceeds the 10-second timeout
- **WHEN** the timeout fires
- **THEN** the curation actor falls back to the rules tier's best-effort
  decision (Create if unsure)
- **AND** the proposal is not lost or blocked

### Requirement: Fuzzy anchor name matching

The curation actor SHALL detect near-duplicate anchor names by tokenizing
on `-` and comparing token sets. Two anchors SHALL be considered fuzzy
matches if the shorter name's tokens are a subset of the longer name's
tokens, OR if they differ by at most one token and share at least 60%
Jaccard similarity.

#### Scenario: Subset anchor names match

- **GIVEN** existing anchor `akka-net-release` (tokens: `{akka,net,release}`)
- **WHEN** a proposal arrives with anchor name `akka-net-latest-release`
  (tokens: `{akka,net,latest,release}`)
- **THEN** the fuzzy matcher identifies them as a match
- **AND** the existing anchor is returned as a consolidation candidate

#### Scenario: Unrelated anchor names do not match

- **GIVEN** existing anchor `akka-net-release` (tokens: `{akka,net,release}`)
- **WHEN** a proposal arrives with anchor name `user-preferred-color`
  (tokens: `{user,preferred,color}`)
- **THEN** the fuzzy matcher does not identify them as a match

#### Scenario: Version-suffixed anchor matches base

- **GIVEN** existing anchor `akka-net-release` (tokens: `{akka,net,release}`)
- **WHEN** a proposal arrives with anchor name `akka-net-release-1.5.62`
  (tokens: `{akka,net,release,1.5.62}`)
- **THEN** the fuzzy matcher identifies them as a match

## MODIFIED Requirements

### Requirement: Pre-compaction memory flush

The system SHALL replace the current single-step pre-compaction memory flush
with checkpoint-driven background memory curation. The session SHALL emit
durable memory checkpoints on eligible events including turn completion,
explicit memory requests, compaction boundaries, and accepted subagent
findings. Observed-memory proposals from the sidecar SHALL be sent directly
to the session's curation child actor instead of being enqueued as
checkpoints. Compaction-related checkpoints SHALL be high priority, but the
user-facing turn SHALL wait only for durable checkpoint enqueue
acknowledgment, not for curator completion.

#### Scenario: Compaction boundary creates a high-priority checkpoint

- **GIVEN** a session is approaching or crossing the compaction threshold
- **WHEN** the session prepares to compact history
- **THEN** the system enqueues a high-priority memory checkpoint containing the
  relevant summary inputs
- **AND** compaction continues after checkpoint enqueue succeeds

#### Scenario: Checkpoint curation retries after failure

- **GIVEN** a checkpoint was enqueued successfully
- **WHEN** background curation fails or times out
- **THEN** the checkpoint remains pending with retry metadata
- **AND** durable memory is not partially committed

#### Scenario: Observed proposals bypass checkpoint queue

- **GIVEN** the observation sidecar has completed and produced proposals
- **WHEN** the `MemoryObservationCompleted` handler runs
- **THEN** accepted proposals are sent to the session's curation child actor
  via `Tell`
- **AND** they are NOT enqueued as `ObservedMemoryCheckpointPayload`
  checkpoints

### Requirement: Rules-first candidate extraction

The system SHALL run deterministic rules before any curator LLM call when
converting checkpoints into durable memory. These rules SHALL reject
ephemeral chatter, duplicates, policy-violating content, and low-confidence
candidates before invoking the curator. For observed-memory proposals
processed by the curation actor, the rules tier SHALL additionally evaluate
proposals against existing stored memories to detect redundancy, staleness,
and anchor fragmentation before writing.

#### Scenario: Trivial chatter is filtered before curation

- **GIVEN** a checkpoint contains both stable project facts and casual
  acknowledgments
- **WHEN** rules-first extraction runs
- **THEN** the stable facts survive as candidates
- **AND** the casual acknowledgments are dropped without calling the curator
  for them

#### Scenario: Existing-memory lookup runs before write

- **GIVEN** the curation actor receives a merge-document proposal
- **WHEN** the rules tier runs
- **THEN** it queries existing documents by anchor name (exact and fuzzy)
- **AND** compares content to determine skip, update, consolidate, or create

### Requirement: Documents versus records semantics

The system SHALL distinguish mutable `documents` from immutable `records`.
Documents SHALL represent living, mergeable knowledge that can be updated in
place with version history. Records SHALL represent time-bound observations
that are immutable once written and can only be superseded, expired, or
tombstoned by subsequent operations. The curation actor SHALL enforce
document mutability by updating existing documents in place when a newer
proposal covers the same anchor, rather than creating duplicate documents.

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

#### Scenario: Duplicate document proposal updates existing

- **GIVEN** a document exists under `anchor:user-preferred-color` with
  content "blue"
- **WHEN** a new merge-document proposal arrives for the same anchor with
  content "green"
- **THEN** the curation actor updates the existing document in place
- **AND** does not create a second document under the same anchor

### Requirement: Hierarchical anchor graph memory model

The system SHALL model durable memory around anchors/entities with optional
parent-child hierarchy and typed graph edges. Anchors SHALL support
containment (`project` -> `repo` -> `service`) and non-hierarchical
relationships (`related_to`, `depends_on`, `owned_by`) so recall can expand
around the relevant entity without flattening all memory into note blobs.
The curation actor MAY consolidate fragmented anchors that represent the same
concept, merging their documents into a canonical anchor and tombstoning the
redundant anchors to maintain graph integrity.

#### Scenario: Recall traverses anchor hierarchy

- **GIVEN** a project anchor contains repository and service child anchors
- **WHEN** a user asks about the project at the parent level
- **THEN** the recall pipeline MAY retrieve child-scoped memory through the
  hierarchy
- **AND** only items allowed by policy are injected into the recall bundle

#### Scenario: Fragmented anchors consolidated into canonical

- **GIVEN** anchors `akka-net-release`, `akka-net-latest-release`, and
  `akka-net-version-info` exist with overlapping content
- **WHEN** the curation actor detects them as fuzzy matches during evaluation
- **THEN** it picks the canonical anchor (most documents, highest confidence)
- **AND** migrates documents from redundant anchors to the canonical one
- **AND** tombstones the redundant anchors rather than deleting them
