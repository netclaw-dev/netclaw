## ADDED Requirements

### Requirement: Session directory owns session automation artifacts

The system SHALL use the canonical session directory as the lifecycle boundary for session-owned automation files.

The boundary SHALL include `CurrentSession` reminder definitions, their history files, background job definitions, and background job output logs.

The boundary SHALL NOT include `Channel` or `None` reminder files. Those reminders have daemon scope because they create new sessions.

The system SHALL derive each artifact path from the typed `SessionId`. It SHALL NOT accept an arbitrary file path as ownership authority.

#### Scenario: New current-session reminder uses the session boundary

- **GIVEN** a new reminder has `Delivery.Kind = CurrentSession`
- **AND** its delivery contains a valid `SessionId`
- **WHEN** the reminder store saves the definition
- **THEN** the definition is stored under that session directory
- **AND** its history uses the same reminder subdirectory

#### Scenario: New background job uses the session boundary

- **GIVEN** a session starts a new background job
- **WHEN** the job store saves its definition and output
- **THEN** both artifacts are stored under that session directory

#### Scenario: New daemon-scoped reminder stays outside the session boundary

- **GIVEN** a new reminder has `Delivery.Kind = Channel` or `Delivery.Kind = None`
- **WHEN** the reminder store saves the definition
- **THEN** the definition and history remain in the daemon reminder directory

#### Scenario: Trusted root does not replace exact owner validation

- **GIVEN** a candidate artifact path is under a trusted project or global root
- **AND** the path is outside the exact source session directory
- **WHEN** the store validates a session-owned artifact path
- **THEN** the store rejects the path

#### Scenario: Session artifact path contains a symbolic link

- **GIVEN** a session reminder or job path contains a symbolic link or reparse point
- **WHEN** a store reads, writes, deletes, or enumerates that path
- **THEN** the store rejects the path
- **AND** it does not access the link target

#### Scenario: Generic file write targets a definition

- **GIVEN** an agent can write other files in its session directory
- **WHEN** it uses a generic file or safe shell path to write a direct `reminders/*.json` or `jobs/*.json` definition
- **THEN** the file access policy rejects the write
- **AND** reminder history and job output paths remain writable
