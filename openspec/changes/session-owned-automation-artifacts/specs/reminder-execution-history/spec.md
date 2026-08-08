## MODIFIED Requirements

### Requirement: Execution record written per run

On completion of each reminder execution, the system SHALL append one structured record to the history path for that reminder owner.

A `CurrentSession` reminder SHALL use this path:

`~/.netclaw/sessions/{session-key}/reminders/{reminderId}.history.jsonl`

A `Channel` or `None` reminder SHALL use this path:

`~/.netclaw/schedules/reminders/{reminderId}.history.jsonl`

The record SHALL contain `firedAt`, `success`, `durationMs`, `sessionId`, and `errorMessage`. A write failure SHALL not change the execution result.

#### Scenario: Successful execution recorded

- **WHEN** a reminder execution completes successfully
- **THEN** the store appends a record with `success: true`, elapsed `durationMs`, and the execution session ID
- **AND** the manager receives the successful execution result

#### Scenario: Failed execution recorded

- **WHEN** a reminder execution fails or reaches its timeout
- **THEN** the store appends a record with `success: false` and a non-null `errorMessage`

#### Scenario: History follows current-session ownership

- **GIVEN** a reminder has `Delivery.Kind = CurrentSession`
- **WHEN** the store appends its execution record
- **THEN** the history file is beside the definition in the source session reminder directory

#### Scenario: History follows daemon ownership

- **GIVEN** a reminder has `Delivery.Kind = Channel` or `Delivery.Kind = None`
- **WHEN** the store appends its execution record
- **THEN** the history file is beside the definition in the daemon reminder directory

#### Scenario: History write failure does not block manager

- **WHEN** a history file write fails
- **THEN** the system logs a warning with the reminder ID and error detail
- **AND** the manager still receives the completion or failure result
- **AND** the history failure does not change the reminder failure counter

### Requirement: History file lifecycle

The history file SHALL be created beside the definition on the first reminder execution. It SHALL use the same daemon or session ownership scope.

When a reminder is deleted, the system SHALL delete its definition and history from that scope. An absent history file SHALL produce an empty result.

#### Scenario: History file absent before first execution

- **GIVEN** a reminder has never run
- **WHEN** a history read is requested
- **THEN** the store returns an empty list without an error

#### Scenario: Current-session history deleted with reminder

- **GIVEN** a `CurrentSession` reminder has a history file
- **WHEN** the reminder is deleted
- **THEN** the definition and history are removed from the source session reminder directory

#### Scenario: Daemon-scoped history deleted with reminder

- **GIVEN** a `Channel` or `None` reminder has a history file
- **WHEN** the reminder is deleted
- **THEN** the definition and history are removed from the daemon reminder directory
