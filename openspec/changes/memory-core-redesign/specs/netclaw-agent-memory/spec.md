# Delta: netclaw-agent-memory (memory-core-redesign)

## MODIFIED Requirements

### Requirement: Automatic pre-turn recall

The system SHALL execute automatic recall before each user-facing model turn
using the latest user message, recent session context, active anchors, and
policy scope. Recall SHALL be hybrid: lexical (FTS5) and semantic (embedding
cosine) candidates are merged, and every candidate SHALL pass the identical
audience/boundary/sensitivity/recall-mode policy gates regardless of which
retriever surfaced it. Injection SHALL be gated by an absolute relevance
floor: when no candidate clears the configured minimum semantic similarity,
the turn SHALL inject nothing and the recall context block SHALL be omitted
entirely. Automatic recall SHALL be bounded by a latency budget and SHALL
degrade safely — to lexical-only scoring with a structured degradation log
when the embedder is unavailable or over its sub-budget, and to no injection
when the memory substrate is unavailable.

#### Scenario: Recall completes within budget

- **GIVEN** the memory substrate is healthy
- **WHEN** a new turn begins
- **THEN** the session retrieves and injects a bounded recall bundle before the
  model call
- **AND** the recall operation completes within the configured time budget or
  degrades safely

#### Scenario: Nothing relevant means nothing injected

- **GIVEN** the memory store contains no memory semantically related to the
  user's message
- **WHEN** automatic recall runs for the turn
- **THEN** no memory items are injected
- **AND** no recall context block is added to the prompt
- **AND** the retrieval log records zero injected items with the applied floor

#### Scenario: Vector-sourced candidates obey policy gates

- **GIVEN** a memory item excluded by the session's audience or sensitivity
  policy
- **WHEN** the semantic retriever surfaces that item as a top cosine candidate
- **THEN** the item is filtered before scoring exactly as a lexical candidate
  would be

#### Scenario: Embedder degradation is loud, not silent

- **GIVEN** the embedding runtime is unavailable or exceeds its per-turn
  sub-budget
- **WHEN** automatic recall runs
- **THEN** recall proceeds lexical-only within the same latency budget
- **AND** a structured vector-degradation event is logged for diagnostics

#### Scenario: Recall failure degrades without blocking the turn

- **GIVEN** the memory database is temporarily unavailable
- **WHEN** the session starts automatic recall for a turn
- **THEN** the user-facing turn continues without durable recall injection
- **AND** the session records degraded memory status for diagnostics

### Requirement: Rules-first candidate extraction

The system SHALL run deterministic rules before any curator LLM call when
converting checkpoints into durable memory. These rules SHALL reject ephemeral
chatter, policy-violating content, and low-confidence candidates before
invoking the curator. Duplicate detection SHALL be semantic: an embedding
nearest-neighbor nomination step SHALL shortlist existing memories above a
configured similarity threshold, and any nomination SHALL force the curator
LLM to decide the relationship (skip, update, consolidate, or create).
Similarity SHALL only nominate — the system SHALL NOT auto-merge or auto-skip
on a similarity threshold alone. Both write pipelines (inline session curation
and daemon checkpoint curation) SHALL evaluate candidates through one shared
evaluator with identical guards. When the embedding runtime is unavailable,
extraction SHALL fall back to lexical candidate search and SHALL log the
degradation.

#### Scenario: Trivial chatter is filtered before curation

- **GIVEN** a checkpoint contains both stable project facts and casual
  acknowledgments
- **WHEN** rules-first extraction runs
- **THEN** the stable facts survive as candidates
- **AND** the casual acknowledgments are dropped without calling the curator for
  them

#### Scenario: Paraphrased duplicate is nominated and adjudicated

- **GIVEN** an existing memory states a fact and a new proposal states the
  same fact in different words with low word overlap
- **WHEN** extraction runs with a healthy embedding runtime
- **THEN** the existing memory is nominated by embedding similarity
- **AND** the curator LLM decides the relationship
- **AND** no automatic merge occurs from the similarity score alone

#### Scenario: Novel proposal skips the curator

- **GIVEN** a proposal with no embedding nomination above the threshold and no
  matching anchor
- **WHEN** extraction runs
- **THEN** the proposal is stored as a new memory without a curator LLM call

### Requirement: Documents versus records semantics

The system SHALL distinguish mutable `documents` from immutable `records`.
Documents SHALL represent living, mergeable knowledge. A curator merge
decision (consolidate or update) SHALL produce a merged body that preserves
the information of every source document; a deterministic merge guard SHALL
verify load-bearing content (identifiers, numbers, dates, URLs) survives, and
on guard failure the system SHALL fall back to a lossless structural append
with provenance rather than overwriting. Destructive whole-body replacement
SHALL NOT be reachable from curation decisions. Records SHALL represent
time-bound observations that are immutable once written and can only be
superseded, expired, or tombstoned by subsequent operations; dated
observations SHALL be stored as new entries, never overwritten by newer
readings.

#### Scenario: Preference update modifies a document

- **GIVEN** an operator preference is stored as a document on a `person` anchor
- **WHEN** the operator corrects that preference later
- **THEN** the system updates the document according to its merge semantics
- **AND** preserves version lineage for auditability

#### Scenario: Lossy merge output falls back to append

- **GIVEN** the curator produces a merged body missing load-bearing content
  from a source document
- **WHEN** the merge guard validates the merge
- **THEN** the merge is rejected
- **AND** the proposal is appended to the existing document with a dated
  separator instead

#### Scenario: Historical event becomes a superseded record

- **GIVEN** a host IP change is stored as a record on a `host` anchor
- **WHEN** a newer verified IP change is persisted
- **THEN** the new fact is stored as a new record
- **AND** the older record is marked as superseded rather than overwritten

### Requirement: Durable memory policy envelope

Every durable anchor, document, and record SHALL carry policy metadata
including `audience`, `sensitivity`, `recallMode`, `confidence`, `freshness`,
and `updateSemantics`. The write path SHALL assign or reject these values
before persistence, and the recall path SHALL filter by them before prompt
injection. Recall modes SHALL mean what they name: `auto` items are eligible
for automatic pre-turn recall; `searchable` items surface only through
explicit search tools; `manual` items are reachable only by explicit id;
`never` items are hidden from all recall surfaces. Formation SHALL default
newly distilled durable facts to `searchable`, reserving `auto` for standing
facts intended to color every conversation (identity, durable preferences,
environment).

#### Scenario: Sensitive memory is blocked from auto recall

- **GIVEN** a stored memory item is marked `audience=personal`,
  `sensitivity=secret`, and `recallMode=manual`
- **WHEN** a session whose audience does not include `personal` runs automatic
  pre-turn recall
- **THEN** the item is excluded from the automatic recall bundle
- **AND** it remains available only to explicit authorized workflows if policy
  allows

#### Scenario: Searchable items stay out of automatic recall

- **GIVEN** a memory item with `recallMode=searchable`
- **WHEN** automatic pre-turn recall runs on a strongly matching query
- **THEN** the item is not injected automatically
- **AND** an explicit `find_memories` search for the same terms returns it

#### Scenario: Topical distillate defaults to searchable

- **GIVEN** the observation sidecar distills a topical project fact without an
  explicit recall-mode proposal
- **WHEN** the proposal passes the policy gate
- **THEN** it persists with `recallMode=searchable`

## REMOVED Requirements

### Requirement: Hierarchical anchor graph memory model

**Reason**: Measured vestigial — the `memory_edges` table has held zero rows
across the system's production lifetime, no recall path traverses hierarchy or
typed edges, and semantic (embedding) retrieval supersedes graph expansion as
the mechanism for surfacing related memories. Maintaining the graph schema and
its write-side metadata is carrying cost without a reader.

**Migration**: Anchors remain as flat grouping keys (including the per-tool
anchors used by tool lessons). The `memory_edges` table and its DDL are
dropped; no data migration is required because no data exists. Related-memory
discovery is served by embedding similarity (`memory-embeddings` capability).

## ADDED Requirements

### Requirement: Short-lived trace memories

The system SHALL support a short-lived memory class for operational state
that is useful for roughly its TTL (default 72 hours) and worthless after —
deploy states, in-flight incident context, temporary environment quirks. The
observation sidecar SHALL be able to propose this class; fresh (unexpired)
trace memories SHALL be eligible for automatic recall weighted below durable
facts; expired trace memories SHALL be deleted by the maintenance sweep, not
merely hidden.

#### Scenario: Fresh trace surfaces, expired trace is gone

- **GIVEN** a trace memory recording an in-flight deployment state, created
  one hour ago
- **WHEN** the user asks about the deployment
- **THEN** the trace is eligible for the automatic recall bundle
- **WHEN** the trace's TTL elapses and the maintenance sweep runs
- **THEN** the row is deleted from the store

### Requirement: Tool-use lesson memories

The system SHALL support a tool-lesson memory class capturing durable lessons
about correct tool usage (conventions, flags, pitfalls), stored as mergeable
documents anchored to the tool they concern. Lessons SHALL be capturable
explicitly through the memory tools and proposable by the observation sidecar
when a transcript shows the user correcting the agent's tool usage. Lessons
SHALL surface through per-tool context injection: on a tool's first use in a
session, a bounded lessons block for that tool SHALL be appended to the tool
result, outside the pre-turn recall budget, at most once per tool per session
(reset on compaction). Lessons for one tool SHALL deduplicate through the
standard curation pipeline so they consolidate into few comprehensive
documents per tool.

#### Scenario: Correction becomes a lesson and surfaces on next use

- **GIVEN** the user corrects the agent's usage of a tool (e.g., a release
  tag must not carry a `v` prefix)
- **WHEN** the lesson is stored as a tool-lesson memory anchored to that tool
- **AND** a later session uses that tool for the first time
- **THEN** the tool result carries a bounded lessons block containing the
  lesson
- **AND** subsequent uses of the same tool in that session do not repeat the
  block

#### Scenario: Repeated lessons consolidate per tool

- **GIVEN** an existing lesson document for a tool
- **WHEN** a semantically overlapping lesson for the same tool is proposed
- **THEN** the curation pipeline nominates the existing lesson and the curator
  merges losslessly rather than accumulating near-duplicates

### Requirement: Checkpoint enqueue gating

The daemon checkpoint pipeline SHALL NOT enqueue work it will deterministically
discard: turn-complete checkpoints SHALL be gated at enqueue time by the same
precondition the extractor applies, so the worker's intake consists of
checkpoints that can produce memory operations.

#### Scenario: Unextractable turn produces no checkpoint

- **GIVEN** a completed turn whose content contains no extractable memory
  candidate
- **WHEN** the session evaluates checkpoint enqueue
- **THEN** no turn-complete checkpoint is enqueued
- **AND** explicit memory requests and compaction boundaries are unaffected
