# reminder-execution-history Specification

## Purpose

Define the per-reminder execution history store: how execution records are
written, retained, trimmed, and exposed to operators and the agent.

## Requirements

### Requirement: Execution record written per run

On completion of each reminder execution (success or failure), the system SHALL append one structured record to
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

### Requirement: Persisted reminder definitions carry required trust fields

A persisted `ReminderDefinition` SHALL declare its audience and boundary fields
as `required` and non-optional, so that every in-process construction is
enforced by the compiler. A legacy reminder JSON document that lacks these
fields SHALL be rejected at load — the reminder store SHALL log an error naming
the document and the missing fields, SHALL exclude the reminder from `Get` and
`List` (so it is never scheduled), and SHALL preserve the file on disk. The
system SHALL NOT substitute an audience or boundary for a reminder with no
persisted trust context.

#### Scenario: Legacy reminder document is rejected at load

- **GIVEN** a persisted `ReminderDefinition` JSON document that predates this
  change and lacks an audience or boundary field
- **WHEN** the reminder store reads it
- **THEN** the reminder is excluded — `Get` returns nothing and `List` omits it
- **AND** an error naming the document and the missing fields is logged
- **AND** the file is preserved on disk for the operator to repair or remove
- **AND** no audience or boundary is substituted, so the reminder is not scheduled

#### Scenario: Current reminder documents round-trip unchanged

- **GIVEN** a `ReminderDefinition` written after this change with explicit
  audience and boundary
- **WHEN** the reminder store deserializes it
- **THEN** the audience and boundary are read verbatim with no error logged

### Requirement: Soft deletion retains reminder history

Netclaw SHALL retain execution history when it soft-deletes a one-shot that reached its poison threshold. Only an explicit delete command SHALL remove that history file.

A successful one-shot is not soft-deleted: Netclaw removes its definition and its history file together, so no orphaned history remains.

#### Scenario: Completed one-shot removes its history with its definition

- **GIVEN** a one-shot has a successful execution record
- **WHEN** Netclaw settles it as complete
- **THEN** Netclaw deletes the definition and the history file together

#### Scenario: Failed one-shot retains history

- **GIVEN** a one-shot reaches its poison threshold
- **WHEN** Netclaw disables it with outcome `Failed`
- **THEN** all failure records remain available through reminder history

#### Scenario: Execution actor stops before it reports an outcome

- **GIVEN** a reminder execution actor stops before manager acceptance
- **WHEN** DeathWatch reports the stop
- **THEN** the manager appends a failed execution record
- **AND** the failure record identifies the unexpected stop
