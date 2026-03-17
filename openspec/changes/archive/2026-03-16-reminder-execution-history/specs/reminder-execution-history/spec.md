# reminder-execution-history Specification

## Purpose

Define the per-reminder execution history store: how execution records are
written, retained, trimmed, and exposed to operators and the agent.

## Requirements

### Requirement: Execution record written per run

On completion of each reminder execution (success or failure), the system
SHALL append one structured record to
`~/.netclaw/reminders/{reminderId}.history.jsonl`. The record SHALL contain:
`firedAt` (ISO 8601 UTC), `success` (bool), `durationMs` (int),
`sessionId` (string, format `reminder/{id}/{firedAtMs}`), and `errorMessage`
(string or null). A write failure SHALL be logged as a warning and SHALL NOT
affect the execution result reported to the reminder manager.

#### Scenario: Successful execution recorded

- **WHEN** a reminder execution completes successfully
- **THEN** a record with `success: true`, elapsed `durationMs`, and the
  session ID is appended to `{reminderId}.history.jsonl`
- **AND** the execution result reported to the manager is unaffected by the
  write

#### Scenario: Failed execution recorded

- **WHEN** a reminder execution fails or times out
- **THEN** a record with `success: false` and a non-null `errorMessage` is
  appended to `{reminderId}.history.jsonl`

#### Scenario: History write failure does not block manager

- **WHEN** the history file write fails (e.g., disk full, permission error)
- **THEN** a warning is logged with the reminder ID and error detail
- **AND** the completion or failure message is still sent to the manager
- **AND** the reminder's failure counter is not incremented due to the
  history write failure

### Requirement: History retention cap

The history store SHALL enforce a maximum record count per reminder, configured
by `ReminderConfig.HistoryMaxRecords` (default: 500). When appending a new
record would exceed the cap, the store SHALL remove the oldest records to
bring the total back to `HistoryMaxRecords`. The trim operation SHALL use an
atomic write (write to a `.tmp` file, then rename) to prevent partial state
on crash.

#### Scenario: Oldest record trimmed at cap

- **GIVEN** a reminder has exactly `HistoryMaxRecords` records in its history
- **WHEN** a new execution completes
- **THEN** the oldest record is removed
- **AND** the new record is appended
- **AND** the total record count remains at `HistoryMaxRecords`

#### Scenario: Partial write on crash leaves valid state

- **GIVEN** a trim-and-rewrite is in progress
- **WHEN** the process crashes during the rewrite
- **THEN** either the old `.history.jsonl` or the new `.tmp` file is present
- **AND** the store recovers by preferring the renamed file if it exists, or
  the original if the rename did not complete

### Requirement: History file lifecycle

The history file SHALL be created on the first execution of a reminder. When
a reminder is deleted (via CLI or agent tool), the corresponding
`.history.jsonl` file SHALL be deleted. If no history file exists for a
reminder ID, read operations SHALL return an empty result rather than an error.

#### Scenario: History file absent before first execution

- **GIVEN** a reminder has been created but never executed
- **WHEN** a history read is requested for that reminder
- **THEN** an empty list is returned with no error

#### Scenario: History file deleted with reminder

- **GIVEN** a reminder with a non-empty history file exists
- **WHEN** the reminder is deleted
- **THEN** both the `.json` definition file and the `.history.jsonl` file
  are removed from `~/.netclaw/reminders/`
