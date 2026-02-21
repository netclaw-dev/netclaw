# netclaw-session Specification

## Purpose

Define session identity, turn lifecycle, persistence recovery, and compaction
behavior.

## Requirements

### Requirement: Slack thread session identity

The system SHALL key each session by `{channelId}/{threadTs}`.

#### Scenario: Route repeated thread messages to same actor

- **GIVEN** a thread session key already exists
- **WHEN** a new message arrives in the same thread
- **THEN** the same session actor handles the turn

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit a turn broadcast.

#### Scenario: Persist and broadcast assistant reply

- **WHEN** the assistant produces a response
- **THEN** a turn event is persisted
- **AND** a broadcast is emitted for subscribers

### Requirement: Session recovery across restart

The system SHALL recover session state from journal and snapshots.

#### Scenario: Recover context after process restart

- **GIVEN** prior persisted turns exist
- **WHEN** the process restarts
- **THEN** the session recovers prior context before processing new input

### Requirement: Conversation compaction

The system SHALL compact long session history using summary reduction.

#### Scenario: Compaction threshold reached

- **GIVEN** session history exceeds configured threshold
- **WHEN** compaction runs
- **THEN** a compaction event is persisted
- **AND** compacted state remains usable for future turns
