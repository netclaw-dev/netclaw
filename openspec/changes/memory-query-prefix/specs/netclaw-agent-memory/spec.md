# Delta: netclaw-agent-memory (memory-query-prefix)

Base: this delta applies on top of memory-core-redesign's pending delta for
the same requirement (hybrid recall + absolute floor), which is the current
authoritative text pre-archive.

## MODIFIED Requirements

### Requirement: Automatic pre-turn recall

The system SHALL execute automatic recall before each user-facing model turn
using the latest user message, recent session context, active anchors, and
policy scope. Recall SHALL be hybrid: lexical (FTS5) and semantic (embedding
cosine) candidates are merged, and every candidate SHALL pass the identical
audience/boundary/sensitivity/recall-mode policy gates regardless of which
retriever surfaced it. The turn query SHALL be embedded in the active
embedding model's documented retrieval mode (including any model-documented
query prefix); document-side embeddings SHALL remain in the model's document
mode. Injection SHALL be gated by an absolute relevance floor whose value
SHALL resolve from the active model's manifest-carried retrieval calibration
unless explicitly overridden in configuration; a floor calibrated for one
model or encoding mode SHALL NOT be silently applied to another — when the
active model carries no retrieval calibration and no explicit override is
configured, recall SHALL run lexical-only with a structured degradation log.
When no candidate clears the effective floor, the turn SHALL inject nothing
and the recall context block SHALL be omitted entirely. Automatic recall
SHALL be bounded by a latency budget and SHALL degrade safely — to
lexical-only scoring with a structured degradation log when the embedder is
unavailable or over its sub-budget, and to no injection when the memory
substrate is unavailable.

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

#### Scenario: Floor follows the active model's calibration

- **GIVEN** `Memory.Recall.MinCosineSimilarity` is not explicitly configured
- **WHEN** recall runs with an embedding model whose manifest carries a
  calibrated retrieval floor
- **THEN** that manifest value is the effective floor for the turn
- **AND** the retrieval log records the effective floor and its source
  (manifest or config override)

#### Scenario: Missing calibration degrades to lexical-only

- **GIVEN** the active embedding model's manifest carries no retrieval
  calibration and no explicit floor override is configured
- **WHEN** a turn begins with the embedder healthy
- **THEN** recall runs lexical-only for the turn
- **AND** a rate-limited structured degradation log records the missing
  calibration as the reason
