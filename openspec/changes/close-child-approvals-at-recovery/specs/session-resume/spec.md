## ADDED Requirements

### Requirement: Recovery logs an approval request only for an approval that stays open

Journal replay SHALL NOT write a `Tool approval requested` log entry. When recovery completes, the session SHALL write one `Tool approval requested` entry with `recovered=True` for each approval that is still open. An approval is open when the session holds it as pending, and a live turn or a live child owner can still use it. The session SHALL NOT write the entry for an approval that a later journal record answers, cancels, expires, or closes. A live request SHALL write its entry with `recovered=False`.

The `LlmSessionActor` owns this decision. The approval table is actor-local state, and replay rebuilds it. The journal is the durable record.

A line with `recovered=True` does not mean that the session sent the prompt again. Recovery never sends a prompt.

#### Scenario: Only the open call logs at recovery

- **GIVEN** a journal holds four approval requests: an answered parent call, a parent call that is still open, a call of a child run that the parent cancelled, and a call of a child run that was live at the restart
- **WHEN** the session recovers
- **THEN** the session writes exactly one `Tool approval requested` entry
- **AND** the entry names the open parent call and carries `recovered=True`

#### Scenario: A live request logs once

- **GIVEN** a session that is not recovering
- **WHEN** the session records a new approval request
- **THEN** the session writes one `Tool approval requested` entry with `recovered=False`

#### Scenario: A negative example

- **GIVEN** a journal holds one answered parent call and no other approval
- **WHEN** the session recovers
- **THEN** the session writes no `Tool approval requested` entry

### Requirement: A cancel record or a terminal record closes the child approvals of its run

A `CancellationRequested` record or a `TerminalRecorded` record of a child run SHALL close every approval entry of that run, pending or resolved. The session SHALL apply this one rule when it persists the record and when journal replay reads the record. A terminal record that recovery wrote for a lost run (`Lost: true`) is a terminal record. A checkpoint record and a prepared-result record SHALL NOT close an entry. A record for a run with no entry SHALL NOT cause an error.

The `LlmSessionActor` owns the decision. The approval table in `SessionToolWorkflow` is actor-local. The child run records are the durable input. This rule adds no persisted type, because the records already exist.

#### Scenario: Replay of a cancel record

- **GIVEN** a journal holds a child approval request and then the cancel record of its run
- **WHEN** the session recovers
- **THEN** the session writes no `Tool approval requested` entry for that call
- **AND** an entry of another run, or of a parent call, is not closed by this record

#### Scenario: Replay of a terminal record, also a lost one

- **GIVEN** a journal holds a child approval request and then a terminal record of its run, cancelled or lost
- **WHEN** the session recovers
- **THEN** the session writes no `Tool approval requested` entry for that call

#### Scenario: A negative example

- **GIVEN** a live child run has an open approval and records a checkpoint
- **WHEN** the session applies the checkpoint
- **THEN** the approval entry stays open

### Requirement: Recovery closes child approvals before a stashed command runs

A child owner never survives a restart. In `RecoveryCompleted`, before the session runs a stashed command, the session SHALL close every child approval that remains after replay. A response to a closed child approval SHALL find no entry. It SHALL get the `prompt_expired` nack, and it SHALL run no tool. The approval channel SHALL NOT be the first gate for such a response.

Recovery then records each run that was live at the restart as lost. That terminal record closes the entries at the next replay.

#### Scenario: A late answer reaches a recovering session

- **GIVEN** a journal holds a child approval whose child run was live at the restart
- **AND** an approval answer for that call wakes the session and waits in the stash
- **WHEN** recovery completes
- **THEN** the session finds no entry for the call and answers with the `prompt_expired` nack
- **AND** no tool runs

#### Scenario: A late answer to a parent call that was answered

- **GIVEN** a journal holds a parent call that the person answered
- **WHEN** a second answer for that call arrives after recovery
- **THEN** the session answers with a nack
- **AND** no tool runs

### Requirement: A closed child approval does not block a snapshot

A snapshot SHALL be allowed when the approval table holds no entry and no parked tool batch is open. A child approval that the session closed SHALL NOT keep the table non-empty. An open parent approval SHALL block the snapshot.

#### Scenario: An old journal recovers and the session snapshots again

- **GIVEN** an old journal with an answered call, a cancelled child run, and a lost child run, and no open parent call
- **WHEN** the session recovers
- **THEN** the session saves a snapshot that covers the whole journal

#### Scenario: An open approval blocks a snapshot

- **GIVEN** a parent approval is still open
- **WHEN** the session persists an event
- **THEN** the session saves no snapshot and logs that an approval batch is unresolved
