# Delta: netclaw-agent-memory (memory-relevance-gate)

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
entirely. Floor-surviving candidates SHALL additionally pass a relevance
gate — a cross-encoder scoring of each candidate jointly with the query —
before injection; when the gate is active and every floor-surviving
candidate scores below the active threshold, the turn SHALL inject nothing,
identical in kind to the floor's own zero-survivors outcome. Automatic
recall SHALL be bounded by a latency budget and SHALL degrade safely — to
lexical-only scoring with a structured degradation log when the embedder is
unavailable or over its sub-budget, to floor-only scoring with a structured
degradation log when the relevance gate is unavailable or over its
sub-budget, and to no injection when the memory substrate is unavailable.

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

#### Scenario: Floor-surviving candidate that is not useful is gated out

- **GIVEN** a candidate clears the absolute cosine floor but does not help
  answer the user's message
- **WHEN** the relevance gate scores that candidate
- **THEN** the candidate is dropped before injection
- **AND** no recall context block is added for that candidate alone if it
  was the only floor survivor

#### Scenario: Relevance gate degradation is loud, not silent

- **GIVEN** the relevance gate is unavailable or exceeds its per-turn
  sub-budget
- **WHEN** automatic recall runs with candidates that survived the absolute
  cosine floor
- **THEN** those candidates are injected unfiltered by the gate, within the
  same latency budget
- **AND** a structured gate-degradation event is logged for diagnostics
