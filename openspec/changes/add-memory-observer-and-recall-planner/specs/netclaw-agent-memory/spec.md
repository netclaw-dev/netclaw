## ADDED Requirements

### Requirement: Memory classes and expiry semantics

The system SHALL classify persisted memory items into `durable_fact`, `evidence`, and `trace` independent of whether the underlying row is stored as a document or record. `evidence` and `trace` SHALL carry expiry metadata, while `durable_fact` SHALL remain non-expiring by default unless a more specific lifecycle rule applies.

#### Scenario: Evidence record receives expiry metadata

- **GIVEN** a research passage or tool-result excerpt is accepted for persistence
- **WHEN** the system stores it as `evidence`
- **THEN** the stored item includes an expiry timestamp or derived expiry window
- **AND** automatic recall treats the item as ineligible after expiry

#### Scenario: Trace remains short-lived and not auto recalled

- **GIVEN** a diagnostic execution breadcrumb is accepted for persistence as `trace`
- **WHEN** the system stores the item
- **THEN** the item receives short-lived expiry metadata
- **AND** the item is excluded from automatic recall by default

### Requirement: SOUL identity boundary

The system SHALL treat `SOUL.md` as a narrow identity/profile surface only. Automatic memory observation and evidence capture SHALL NOT promote project facts, tool findings, research passages, or execution trace into `SOUL.md`; those items SHALL remain in SQLite durable memory or be rejected.

#### Scenario: Identity preference is eligible for identity workflow

- **GIVEN** an observed change concerns the agent's name, tone, or standing communication preference
- **WHEN** deterministic gating evaluates the proposal
- **THEN** the proposal MAY route to the identity/profile workflow
- **AND** it does not become a general durable-memory auto-write unless that workflow accepts it

#### Scenario: Research finding is blocked from SOUL promotion

- **GIVEN** an observed proposal contains a project fact or research passage
- **WHEN** the proposal is evaluated against the `SOUL.md` boundary
- **THEN** the proposal is rejected from the identity/profile path
- **AND** the item remains in SQLite memory or is dropped according to policy

## MODIFIED Requirements

### Requirement: Two-phase memory retrieval

Memory retrieval SHALL run in two modes: automatic pre-turn recall and explicit
two-phase retrieval. Automatic recall SHALL happen before each user-facing
model turn and SHALL inject a bounded recall bundle derived from the structured
memory graph. Explicit retrieval SHALL continue to use `find_memories` for
lightweight search and `get_memories` for full hydration when manual follow-up
is needed. Automatic recall is the primary retrieval path; explicit retrieval
is a deliberate manual-control path. Automatic recall SHALL be limited to
`durable_fact` items, while intentional search SHALL search `durable_fact` plus
`evidence` by default.

#### Scenario: Automatic recall runs before a user-facing turn

- **GIVEN** a user sends a new message into an existing or new session
- **WHEN** the session prepares the next model call
- **THEN** the system runs a policy-aware automatic recall query against durable
  memory
- **AND** injects a bounded recall bundle before the model sees the turn

#### Scenario: Explicit two-phase retrieval remains available

- **GIVEN** the automatic recall bundle was insufficient or the user explicitly
  asks what Netclaw remembers
- **WHEN** the frontline model calls `find_memories`
- **THEN** it receives lightweight results suitable for selection
- **AND** can call `get_memories` to fetch full memory bodies only for the
  selected items

#### Scenario: Routine turn relies on automatic recall first

- **GIVEN** a normal user-facing turn begins
- **WHEN** the automatic recall bundle already provides the relevant durable
  context
- **THEN** the frontline model does not need to call explicit retrieval tools by
  default
- **AND** proceeds using the system-managed recall bundle

#### Scenario: Intentional search returns evidence while automatic recall does not

- **GIVEN** matching memory contains both durable facts and supporting evidence
- **WHEN** an automatic recall bundle is prepared
- **THEN** only `durable_fact` items are considered for injection
- **AND** the supporting `evidence` remains available only through explicit search and hydration

### Requirement: Memory context layer per backend

The memory context layer SHALL explain that durable recall is automatic by
default and that explicit memory tools are reserved for deliberate manual
search, save, and correction workflows. The layer SHALL surface degraded memory
status when automatic recall or durable persistence is unavailable. It SHALL no
longer teach the model that backend selection is part of normal memory usage,
and it SHALL explicitly tell the frontline model not to call write tools
reflexively on every turn. The guidance SHALL distinguish automatic recall from
intentional search by stating that automatic recall is `durable_fact` only,
while explicit search can retrieve `evidence`.

#### Scenario: Context layer teaches automatic recall first

- **GIVEN** the redesigned memory subsystem is healthy
- **WHEN** a session prompt is assembled
- **THEN** the memory context layer explains that Netclaw automatically recalls
  durable memory before each turn
- **AND** reserves explicit memory tools for deliberate memory operations

#### Scenario: Context layer distinguishes store and update usage

- **GIVEN** the redesigned memory subsystem is healthy
- **WHEN** memory guidance is injected into the session prompt
- **THEN** the guidance says `store_memory` is for deliberate save/remember
  actions
- **AND** the guidance says `update_memory` is for correction, supersede,
  tombstone, or metadata changes to existing memory

#### Scenario: Context layer reports degraded memory state

- **GIVEN** the memory database is unavailable or recall has been disabled due
  to an operational fault
- **WHEN** a session prompt is assembled
- **THEN** the memory context layer reports degraded memory status
- **AND** does not claim that durable recall is functioning normally

#### Scenario: Context layer explains evidence search boundary

- **GIVEN** the redesigned memory subsystem is healthy
- **WHEN** memory guidance is injected into the session prompt
- **THEN** the guidance states that automatic recall does not inject `evidence`
- **AND** the guidance states that deliberate `find_memories` searches may return `evidence` results

### Requirement: Rules-first candidate extraction

The system SHALL run deterministic rules before any curator LLM call when
converting checkpoints into durable memory. These rules SHALL reject ephemeral
chatter, duplicates, policy-violating content, and low-confidence candidates
before invoking the curator. Rules-first extraction SHALL evaluate structured
`MemoryProposal` results from `MemoryObservationSidecar`, but sidecar output
SHALL remain advisory until deterministic policy, schema, dedupe, class, and
expiry gates accept it.

#### Scenario: Trivial chatter is filtered before curation

- **GIVEN** a checkpoint contains both stable project facts and casual
  acknowledgments
- **WHEN** rules-first extraction runs
- **THEN** the stable facts survive as candidates
- **AND** the casual acknowledgments are dropped without calling the curator for
  them

#### Scenario: Invalid sidecar proposal is rejected before checkpoint enqueue

- **GIVEN** `MemoryObservationSidecar` returns a proposal with an unknown class,
  invalid schema, or denied policy envelope
- **WHEN** deterministic proposal gating evaluates the proposal
- **THEN** the proposal is rejected
- **AND** no durable write checkpoint is created from that proposal

#### Scenario: Accepted sidecar proposal remains system-owned

- **GIVEN** `MemoryObservationSidecar` returns a valid `durable_fact` proposal
- **WHEN** deterministic proposal gating accepts the proposal
- **THEN** the proposal is converted into a checkpoint operation for background curation
- **AND** the sidecar does not write SQLite memory directly

### Requirement: Memory evaluation and operational criteria

The redesigned memory subsystem SHALL ship with an eval suite and operational
SLOs covering recall quality, noise suppression, privacy behavior, and latency.
The implementation SHALL NOT be considered complete until the seeded eval suite
demonstrates the configured thresholds. The eval program SHALL include
formation-then-recall flows, evidence-vs-durable separation checks, and
deterministic gate-correctness checks rather than only pre-seeded-memory recall
fixtures.

#### Scenario: Seeded memory eval suite passes

- **GIVEN** the seeded recall/privacy fixture suite is executed against the
  redesigned subsystem
- **WHEN** the results are reported
- **THEN** relevant recall coverage, noise suppression, privacy leakage, and
  latency metrics meet the thresholds defined by the change design
- **AND** a failing metric blocks rollout from being treated as complete

#### Scenario: Local Ollama eval profile is the primary gate

- **GIVEN** the seeded memory eval suite supports multiple model profiles
- **WHEN** Netclaw validates the redesigned memory subsystem before rollout
- **THEN** it runs the default gate against smaller local Ollama-hosted models
- **AND** passing larger hosted models does not waive a failing local Ollama
  eval result

#### Scenario: Formation then recall suite validates stored durable facts

- **GIVEN** a sanitized conversation fixture contains a strong user assertion and later follow-up question
- **WHEN** the eval first runs observation and durable write flow, then runs automatic recall
- **THEN** the assertion is formed as `durable_fact`
- **AND** the later automatic recall turn retrieves it without needing a pre-seeded row

#### Scenario: Evidence separation suite blocks evidence from auto recall

- **GIVEN** a sanitized fixture stores both a durable fact and supporting `evidence`
- **WHEN** automatic recall and intentional search are evaluated separately
- **THEN** automatic recall excludes the `evidence`
- **AND** intentional search can still retrieve the `evidence` when asked
