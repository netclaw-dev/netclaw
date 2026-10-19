## MODIFIED Requirements

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

## REMOVED Requirements

### Requirement: Soft deletion retains reminder history

**Reason**: No one-shot stays as a disabled definition after this change. Every ended one-shot keeps its history through its tombstone. The requirement returns as "Ended one-shot history stays readable through its tombstone".
**Migration**: The startup reconciliation converts each old disabled one-shot to a tombstone (see `netclaw-scheduling`, "Reconciliation ends leftover one-shots").

## ADDED Requirements

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
