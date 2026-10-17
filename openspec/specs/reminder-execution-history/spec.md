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
a reminder is deleted with the permanent delete command (CLI or HTTP API), the
corresponding `.history.jsonl` file SHALL be deleted. A permanent delete of an
ended one-shot SHALL delete its history file and then its tombstone. When a
one-shot ends, its history file SHALL stay until its tombstone is purged. If no
history file exists for a reminder ID that has a definition or a tombstone,
read operations SHALL return an empty result rather than an error.

#### Scenario: History file absent before first execution

- **GIVEN** a reminder has been created but never executed
- **WHEN** a history read is requested for that reminder
- **THEN** an empty list is returned with no error

#### Scenario: History file deleted with reminder

- **GIVEN** a reminder with a non-empty history file exists
- **WHEN** the reminder is permanently deleted
- **THEN** both the `.json` definition file and the `.history.jsonl` file
  are removed from `~/.netclaw/schedules/reminders/`

#### Scenario: Permanent delete of an ended one-shot

- **GIVEN** a one-shot ended and has a tombstone and a history file
- **WHEN** the operator runs `netclaw reminder delete <id>`
- **THEN** the history file and the tombstone no longer exist
- **AND** the command reports that the reminder was permanently deleted

#### Scenario: The end of a one-shot keeps the history file

- **GIVEN** a one-shot with three history records
- **WHEN** the one-shot ends
- **THEN** the definition file no longer exists
- **AND** the history file still holds the three records

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

### Requirement: Ended one-shot history stays readable through its tombstone

Netclaw SHALL keep the execution history of an ended one-shot for 12 days after its end time, whatever its outcome. The tombstone SHALL supply the audience for every history and status read of an ended one-shot. Netclaw SHALL apply the same audience rule as for a live reminder. A read without a definition and without a tombstone SHALL reply "not found". A tombstone that Netclaw cannot read SHALL count as absent for reads only, and Netclaw SHALL log an error and raise an operational alert. It does not count as absent when a fire decides whether a one-shot runs (see `netclaw-scheduling`). Netclaw SHALL NOT expose history without an audience to check. The `Failed` tombstone also holds the reminder prompt (see `netclaw-scheduling`), and the same audience rule gates its read.

Recurring reminders keep their history until an explicit delete. The history of an id that never existed is reported as not found.

#### Scenario: Completed one-shot keeps its history for the retention period

- **GIVEN** a one-shot has a successful execution record
- **WHEN** Netclaw ends it as `Completed`
- **THEN** reminder history for the id returns the record
- **AND** Netclaw deletes the history file and then the tombstone after the retention period

#### Scenario: Failed one-shot keeps its history

- **GIVEN** a one-shot reaches its poison threshold
- **WHEN** Netclaw ends it as `Failed`
- **THEN** all failure records stay available through reminder history for 12 days

#### Scenario: History of an ended one-shot inside the audience

- **GIVEN** a Team one-shot ended
- **WHEN** a Team caller calls `get_reminder_history` for its id
- **THEN** the tool returns the history records

#### Scenario: History of an ended one-shot outside the audience

- **GIVEN** a Personal one-shot ended
- **WHEN** a Team caller calls `get_reminder_history` for its id
- **THEN** the tool replies as for an id that never existed

#### Scenario: An unreadable tombstone hides the history

- **GIVEN** an ended one-shot has a history file and a tombstone file that Netclaw cannot read
- **WHEN** a caller reads the history or the status of its id
- **THEN** the reply is "not found" and shows no history
- **AND** Netclaw logs an error and emits the `reminder.tombstone.invalid` alert
- **AND** the tombstone file stays on disk

#### Scenario: A history file without a definition or a tombstone

- **GIVEN** a history file exists for `orphan-id`
- **AND** no definition and no tombstone exist for `orphan-id`
- **WHEN** an Operator runs `netclaw reminder history orphan-id`
- **THEN** the command exits with a non-zero code and says that the reminder was not found

#### Scenario: Execution actor stops before it reports an outcome

- **GIVEN** a reminder execution actor stops before manager acceptance
- **WHEN** DeathWatch reports the stop
- **THEN** the manager appends a failed execution record
- **AND** the failure record identifies the unexpected stop
