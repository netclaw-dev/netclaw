## MODIFIED Requirements

### Requirement: Schedule persistence

The system SHALL persist each reminder definition as a JSON file. It SHALL preserve the definition and its schedule across process restarts.

A new `CurrentSession` definition SHALL use this path:

`~/.netclaw/sessions/{session-key}/reminders/{reminderId}.json`

A new `Channel` or `None` definition SHALL use this path:

`~/.netclaw/schedules/reminders/{reminderId}.json`

The store SHALL read definitions from the daemon directory and each fixed session reminder directory.

The store SHALL preserve the current path for each existing definition. It SHALL NOT move a definition during startup or update.

The reminder store SHALL keep reminder IDs unique across both ownership scopes. It SHALL reject an ID that exists in more than one directory.

The Akka.Reminders payload SHALL remain an ID-only pointer. The manager SHALL resolve that ID through the reminder store before execution.

On startup, the system SHALL load all valid definitions and reconcile all active schedules. A paused definition SHALL remain paused.

#### Scenario: Reminders survive process restart

- **GIVEN** valid reminder definitions exist in daemon and session reminder directories
- **WHEN** the Netclaw process restarts
- **THEN** the reminder store finds the definitions through fixed-directory scans
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

#### Scenario: Existing current-session reminder stays at its current path

- **GIVEN** a valid `CurrentSession` definition exists in the daemon reminder directory
- **WHEN** the store starts, reads, or updates that reminder
- **THEN** the definition remains in the daemon reminder directory
- **AND** the manager resolves its existing ID-only scheduler payload
- **AND** the current schedule remains active

#### Scenario: Existing daemon-scoped reminder stays at its current path

- **GIVEN** a valid `Channel` or `None` definition exists in the daemon reminder directory
- **WHEN** the store starts, reads, or updates that reminder
- **THEN** the definition remains in the daemon reminder directory
- **AND** no session-owned copy is created

#### Scenario: Duplicate reminder ID fails loud

- **GIVEN** two definition files claim the same reminder ID
- **WHEN** the reminder store resolves that ID
- **THEN** the store rejects the ambiguous duplicate
- **AND** it logs both paths
- **AND** the manager does not schedule the duplicate

#### Scenario: Invalid candidate does not shadow a valid reminder

- **GIVEN** one valid reminder claims an ID
- **AND** a corrupt or owner-mismatched file uses the same encoded file name in another scope
- **WHEN** the store resolves the reminder ID
- **THEN** the store returns the valid reminder
- **AND** it applies the current invalid-file policy to the invalid candidate

#### Scenario: Invalid owner transition keeps the current schedule

- **GIVEN** an enabled reminder definition is stored in a session directory
- **WHEN** an update changes its session owner or daemon ownership scope
- **THEN** the manager rejects the update before a scheduler mutation
- **AND** the current definition and schedule remain active

#### Scenario: Session owner mismatch fails loud

- **GIVEN** a session reminder file names a different `Delivery.SessionId`
- **WHEN** the reminder store reads the file
- **THEN** the store rejects the definition
- **AND** it logs the file path and owner mismatch

#### Scenario: Corrupt reminder definition does not run

- **GIVEN** a reminder definition contains invalid JSON or an invalid schema
- **WHEN** the reminder store loads the file
- **THEN** the store excludes the definition
- **AND** the manager does not schedule it
- **AND** the current invalid-definition policy reports or removes the file
