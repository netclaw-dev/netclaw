## MODIFIED Requirements

### Requirement: Execution record written per run

On completion of each reminder execution (success or failure), the system SHALL append one structured record to
`~/.netclaw/reminders/{reminderId}.history.jsonl`. The record SHALL contain:
`firedAt` (ISO 8601 UTC), `success` (bool), `durationMs` (int),
`sessionId` (string, format `reminder/{id}/{firedAtMs}` for isolated sessions),
`errorMessage` (string or null), and `source` (`scheduled` or `manual`). A write
failure SHALL be logged as a warning and SHALL NOT affect the execution result
reported to the reminder manager.

History readers SHALL treat records without a `source` field as `scheduled` for
backward compatibility with history files written before manual execution
support.

#### Scenario: Successful scheduled execution recorded

- **WHEN** a scheduled reminder execution completes successfully
- **THEN** a record with `success: true`, `source: "scheduled"`, elapsed `durationMs`, and the
  session ID is appended to `{reminderId}.history.jsonl`
- **AND** the execution result reported to the manager is unaffected by the
  write

#### Scenario: Failed scheduled execution recorded

- **WHEN** a scheduled reminder execution fails or times out
- **THEN** a record with `success: false`, `source: "scheduled"`, and a non-null `errorMessage` is
  appended to `{reminderId}.history.jsonl`

#### Scenario: Manual execution recorded

- **WHEN** an operator-triggered manual reminder execution completes successfully or fails
- **THEN** a record with `source: "manual"` is appended to `{reminderId}.history.jsonl`
- **AND** the record includes the same `firedAt`, `success`, `durationMs`, `sessionId`, and `errorMessage` fields as scheduled executions

#### Scenario: Legacy history record defaults to scheduled source

- **GIVEN** an existing history file contains a record without a `source` field
- **WHEN** reminder history is read
- **THEN** the record is returned with source `scheduled`
- **AND** the missing field does not make the whole history read fail

#### Scenario: History write failure does not block manager

- **WHEN** the history file write fails (e.g., disk full, permission error)
- **THEN** a warning is logged with the reminder ID and error detail
- **AND** the completion or failure message is still sent to the manager
- **AND** the reminder's failure counter is not incremented due to the
  history write failure
