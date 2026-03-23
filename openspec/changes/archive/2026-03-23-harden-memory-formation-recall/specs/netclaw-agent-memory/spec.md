## ADDED Requirements

### Requirement: Deterministic memory classes and recall eligibility
The system SHALL classify persisted memory candidates into deterministic classes based on source path and structured metadata: `durable_explicit`, `durable_inferred`, and `conversation_trace`. Automatic recall SHALL only consider classes marked auto-eligible, with `conversation_trace` excluded by default. Phase A SHALL implement classing and eligibility rules without introducing a candidate/promotion ledger.

#### Scenario: Explicit save is classed as durable explicit
- **GIVEN** a user issues an explicit remember/save request
- **WHEN** the system processes the write through the explicit memory path
- **THEN** the candidate is classed as `durable_explicit`
- **AND** the item is auto-recall eligible if policy envelope checks pass

#### Scenario: Turn completion chatter is classed as conversation trace
- **GIVEN** a normal turn produces acknowledgement chatter and execution trace text
- **WHEN** automatic memory classing runs for checkpoint candidates
- **THEN** those candidates are classed as `conversation_trace`
- **AND** they are excluded from automatic recall eligibility by default

### Requirement: Truthful explicit save acknowledgements
User-visible explicit save acknowledgements SHALL reflect actual durable write outcomes. The system SHALL emit a success acknowledgement only after durable persistence confirmation and SHALL emit failure/degraded acknowledgements when persistence fails or times out.

#### Scenario: Success acknowledgement follows durable commit
- **GIVEN** an explicit save request passes validation and policy checks
- **WHEN** the persistence actor confirms durable write completion
- **THEN** the session emits a success acknowledgement
- **AND** the acknowledgement references the committed durable write, not a queued intent

#### Scenario: Failed write does not emit success acknowledgement
- **GIVEN** an explicit save request is accepted for processing
- **WHEN** durable persistence fails or times out before commit confirmation
- **THEN** the session does not emit a success acknowledgement
- **AND** the user receives a failure/degraded acknowledgement with retry guidance

## MODIFIED Requirements

### Requirement: Rules-first candidate extraction

The system SHALL run deterministic rules before any curator LLM call when
converting checkpoints into durable memory. These rules SHALL reject ephemeral
chatter, duplicates, policy-violating content, and low-confidence candidates
before invoking the curator. Phase A extraction SHALL be structure-oriented and
metadata-driven; brittle literal phrase matching SHALL NOT be the primary
admission mechanism.

#### Scenario: Trivial chatter is filtered before curation

- **GIVEN** a checkpoint contains both stable project facts and casual
  acknowledgments
- **WHEN** rules-first extraction runs
- **THEN** the stable facts survive as candidates
- **AND** the casual acknowledgments are dropped without calling the curator for
  them

#### Scenario: Deterministic admission rejects low-value turn snapshots

- **GIVEN** a turn-completion checkpoint candidate has conversational-trace class
  and no explicit save intent
- **WHEN** deterministic admission gates evaluate class, confidence, and novelty
- **THEN** the candidate is rejected from durable auto-save
- **AND** no durable write attempt is made for that candidate

### Requirement: Automatic pre-turn recall

The system SHALL execute automatic recall before each user-facing model turn
using the latest user message, recent session context, active anchors, and
policy scope. Automatic recall SHALL be bounded by a latency budget and SHALL
degrade safely when the memory substrate is unavailable. The recall pipeline
SHALL apply deterministic hard filters before ranking, including default
exclusion of `conversation_trace` items from automatic injection.

#### Scenario: Recall completes within budget

- **GIVEN** the memory substrate is healthy
- **WHEN** a new turn begins
- **THEN** the session retrieves and injects a bounded recall bundle before the
  model call
- **AND** the recall operation completes within the configured time budget or
  degrades safely

#### Scenario: Recall failure degrades without blocking the turn

- **GIVEN** the memory database is temporarily unavailable
- **WHEN** the session starts automatic recall for a turn
- **THEN** the user-facing turn continues without durable recall injection
- **AND** the session records degraded memory status for diagnostics

#### Scenario: Conversation trace is filtered from automatic recall

- **GIVEN** matching memories include both durable facts and conversation-trace
  entries
- **WHEN** automatic pre-turn recall builds the injection bundle
- **THEN** conversation-trace entries are excluded by deterministic eligibility
  filters
- **AND** ranking is applied only to remaining auto-eligible durable items

### Requirement: Explicit memory control paths

The system SHALL treat `store_memory` and `update_memory` as deliberate
manual-control paths layered on top of automatic recall and background curation.
The frontline agent SHALL invoke `store_memory` only for explicit
remember/save requests, deliberate high-value pinning, or operator-directed
structured note capture. The frontline agent SHALL invoke `update_memory` only
for correction, supersede, tombstone, or metadata changes to an existing
durable memory item. In Phase A, explicit save success responses SHALL be tied
to durable write confirmation rather than optimistic pre-commit acknowledgements.

#### Scenario: Frontline agent uses store_memory for an explicit save request

- **GIVEN** the user explicitly asks Netclaw to remember a fact or preference
- **WHEN** the frontline agent chooses how to persist that information
- **THEN** it uses `store_memory` as the deliberate explicit write path
- **AND** the request still flows through checkpoint and policy handling rather
  than direct uncontrolled persistence

#### Scenario: Frontline agent uses update_memory for correction

- **GIVEN** an existing durable memory item must be corrected or superseded
- **WHEN** the frontline agent applies the user's correction
- **THEN** it uses `update_memory`
- **AND** it does not use `store_memory` to create an untracked duplicate for
  the same correction

#### Scenario: Explicit save acknowledgement waits for write confirmation

- **GIVEN** the frontline agent has issued `store_memory` for an explicit save
- **WHEN** the durable write confirmation has not yet been received
- **THEN** the session does not emit a completed-save success acknowledgement
- **AND** success is emitted only after confirmed persistence

### Requirement: Memory evaluation and operational criteria

The redesigned memory subsystem SHALL ship with an eval suite and operational
SLOs covering recall quality, noise suppression, privacy behavior, and latency.
The implementation SHALL NOT be considered complete until the seeded eval suite
demonstrates the configured thresholds. Phase A rollout SHALL require separate
smoke and realistic suites, both using synthetic/sanitized fixtures only, and
stability gates across repeated runs.

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

#### Scenario: Stability gate requires consecutive passing runs

- **GIVEN** smoke and realistic suites both report passing metrics in one run
- **WHEN** rollout gating is evaluated for enabling Phase A defaults
- **THEN** thresholds must pass for the required consecutive run count
- **AND** a failing run resets the stability gate until passing streak is re-established
