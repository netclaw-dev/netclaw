## MODIFIED Requirements

### Requirement: Schedule persistence

The system SHALL persist each reminder definition as a JSON file. It SHALL preserve the definition and its schedule across process restarts.

A `CurrentSession` definition SHALL use this path:

`~/.netclaw/sessions/{session-key}/reminders/{reminderId}.json`

A `Channel` or `None` definition SHALL use this path:

`~/.netclaw/schedules/reminders/{reminderId}.json`

The reminder store SHALL keep reminder IDs unique across both ownership scopes. It SHALL map each ID to one validated canonical path.

The Akka.Reminders payload SHALL remain an ID-only pointer. The manager SHALL resolve that ID through the reminder store before execution.

On startup, the system SHALL load all valid definitions and reconcile all active schedules. A paused definition SHALL remain paused.

#### Scenario: Reminders survive process restart

- **GIVEN** valid reminder definitions exist in daemon and session reminder directories
- **WHEN** the Netclaw process restarts
- **THEN** the reminder store rebuilds its ID-to-path index
- **AND** the manager reconciles all active schedules
- **AND** paused reminders remain paused

#### Scenario: New current-session reminder is durable before confirmation

- **GIVEN** a session creates a `CurrentSession` reminder
- **WHEN** the manager confirms the new reminder
- **THEN** the definition already exists under the source session directory
- **AND** its stored `Delivery.SessionId` matches that directory

#### Scenario: New daemon-scoped reminder is durable before confirmation

- **GIVEN** a session creates a `Channel` or `None` reminder
- **WHEN** the manager confirms the new reminder
- **THEN** the definition already exists under the daemon reminder directory

#### Scenario: Scheduler payload remains stable after a definition move

- **GIVEN** an Akka.Reminders occurrence contains a `ReminderPayload` with one reminder ID
- **AND** the definition moved from the legacy directory to its session directory
- **WHEN** the occurrence fires
- **THEN** the manager resolves the same ID through the rebuilt store index
- **AND** no scheduler payload migration is necessary

#### Scenario: Duplicate reminder ID fails loud

- **GIVEN** two definition files claim the same reminder ID
- **WHEN** the reminder store builds its index
- **THEN** the store rejects the ambiguous duplicate
- **AND** it logs both paths
- **AND** the manager does not schedule the duplicate

#### Scenario: Corrupt reminder definition does not run

- **GIVEN** a reminder definition contains invalid JSON or an invalid schema
- **WHEN** the reminder store loads the file
- **THEN** the store excludes the definition from its index
- **AND** the manager does not schedule it
- **AND** the current invalid-definition policy reports or removes the file

## ADDED Requirements

### Requirement: Legacy current-session reminder migration

On startup, the reminder store SHALL inspect legacy definitions in the daemon reminder directory. It SHALL move each valid `CurrentSession` definition to its canonical session directory.

The store SHALL move a matching history file before it moves the definition. The definition move SHALL act as the migration commit point.

The store SHALL NOT overwrite a destination file. A conflict or invalid `Delivery.SessionId` SHALL produce an error and preserve the source files.

The store SHALL continue to resolve a valid source definition after a migration error. This compatibility path SHALL produce an operator-visible log entry.

#### Scenario: Legacy current-session reminder moves to its session

- **GIVEN** a valid legacy `CurrentSession` definition exists in the daemon reminder directory
- **AND** no destination artifact exists
- **WHEN** startup migration runs
- **THEN** the history file moves to the source session reminder directory when present
- **AND** the definition moves to the same directory
- **AND** the reminder keeps its current ID and schedule

#### Scenario: Daemon-scoped reminder does not move

- **GIVEN** a valid legacy definition has `Delivery.Kind = Channel` or `Delivery.Kind = None`
- **WHEN** startup migration runs
- **THEN** the definition and history remain in the daemon reminder directory

#### Scenario: Migration conflict preserves source data

- **GIVEN** a legacy definition and its destination path both exist
- **WHEN** startup migration runs
- **THEN** the migration does not overwrite either file
- **AND** the store logs the conflict
- **AND** the source definition remains available through the explicit compatibility path

#### Scenario: Restart resumes an interrupted migration

- **GIVEN** a prior migration moved the history but did not move the definition
- **WHEN** the daemon restarts
- **THEN** the migration accepts the matching destination history
- **AND** it completes the definition move without duplicate history records
