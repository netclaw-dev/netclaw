## MODIFIED Requirements

### Requirement: Conversation compaction

The system SHALL compact long session history using summary reduction. Before
compaction runs, the system SHALL trigger a pre-compaction memory flush that
persists durable memories (key facts, decisions, action items) to local memory
files so they survive context reset.

#### Scenario: Compaction threshold reached

- **GIVEN** session history exceeds configured threshold
- **WHEN** compaction runs
- **THEN** a compaction event is persisted
- **AND** compacted state remains usable for future turns

#### Scenario: Pre-compaction memory flush

- **GIVEN** session history exceeds configured compaction threshold
- **WHEN** compaction is about to run
- **THEN** the system SHALL execute a silent agentic turn that extracts durable
  memories from the conversation
- **AND** persists them to local memory files before context is reset

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit a turn broadcast via
pub/sub. Broadcast delivery SHALL use a publish-subscribe pattern so that
multiple adapters (Slack, future web UI, timer adapter) can independently
consume turn events without the session actor knowing which adapters are
subscribed.

#### Scenario: Persist and broadcast assistant reply

- **WHEN** the assistant produces a response
- **THEN** a turn event is persisted
- **AND** a broadcast is published to the session topic for all subscribers

#### Scenario: Multi-adapter broadcast delivery

- **GIVEN** multiple adapters are subscribed to session broadcasts
- **WHEN** a turn broadcast is published
- **THEN** each subscribed adapter receives the broadcast independently
- **AND** the session actor does not reference specific adapter types

## ADDED Requirements

### Requirement: Tool context in session state

The system SHALL load available tools into session state based on the active
policy grants at session initialization. Tool definitions SHALL be refreshed
from the tool registry each time a session actor starts or recovers.

#### Scenario: Session loads granted tools at initialization

- **GIVEN** the ACL grants `shell`, `web_search`, and `mcp:memorizer` to the
  current channel and sender
- **WHEN** a session actor initializes
- **THEN** session state includes tool definitions for only the granted tool
  categories

#### Scenario: Denied tools excluded from session

- **GIVEN** the ACL does not grant `github` for the current channel
- **WHEN** a session actor initializes
- **THEN** GitHub tool definitions are not loaded into session state
