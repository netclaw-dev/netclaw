## ADDED Requirements

### Requirement: Slack thread session identity

The system SHALL key each session by `{channelId}/{threadTs}`.

#### Scenario: Route repeated thread messages to same actor

- **GIVEN** a thread session key already exists
- **WHEN** a new message arrives in the same thread
- **THEN** the same session actor handles the turn

### Requirement: Session recovery across restart

The system SHALL recover session state from journal and snapshots.

#### Scenario: Recover context after process restart

- **GIVEN** prior persisted turns exist
- **WHEN** the process restarts
- **THEN** the session recovers prior context before processing new input
