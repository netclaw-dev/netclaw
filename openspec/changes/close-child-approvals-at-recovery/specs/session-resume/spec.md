## ADDED Requirements

### Requirement: Recovery logs an approval request only for an approval that stays open

Journal replay SHALL NOT write a `Tool approval requested` log entry. When recovery completes, the session SHALL write one `Tool approval requested` entry with `recovered=True` for each approval that is still open. An approval is open when the session holds it as pending, and a live turn or a live child owner can still use it. The session SHALL NOT write the entry again for an approval that a later journal event answers, cancels, expires, or closes. A live request SHALL write its entry with `recovered=False`.

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

### Requirement: Recovery closes child approvals before a stashed command runs

A child owner never survives a restart. In `RecoveryCompleted`, before the session runs a stashed command, the session SHALL close every child approval that replay rebuilt. A response to a closed child approval SHALL get the `prompt_expired` or `already_answered` nack. It SHALL run no tool.

#### Scenario: A late answer reaches a recovering session

- **GIVEN** a journal holds a child approval whose child run was live at the restart
- **AND** an approval answer for that call wakes the session and waits in the stash
- **WHEN** recovery completes
- **THEN** the session answers the response with a nack
- **AND** no tool runs

### Requirement: A durable event closes child approvals

When the session closes one or more child approvals of a run, it SHALL persist one `ChildApprovalsClosed` event for that run before it relies on the close. The event names the session, the run, a reason, and the close time. The reason is one of `RunCancelled`, `RunTerminal`, or `OwnerLost`. The session SHALL persist the event when a cancel request closes approvals, when a terminal result closes approvals, and when recovery closes the approvals of a lost owner. The session SHALL NOT persist the event for a run that has no open approval entry. Replay of the event SHALL close the entries of that run without a log entry, and an entry that is already closed SHALL NOT cause an error.

The `LlmSessionActor` owns the close decision. The `SessionToolWorkflow` approval table is actor-local. The event is durable. It is a framework-owned type with its own serializer manifest, `cac-v1`.

#### Scenario: Recovery of a stranded child approval

- **GIVEN** a journal holds an accepted child run and its approval request
- **AND** the journal holds no terminal event and no close event
- **WHEN** the session recovers
- **THEN** the session persists one `ChildApprovalsClosed` event with the reason `OwnerLost` for the run
- **AND** the session saves a snapshot after recovery

#### Scenario: A cancel closes the approval once

- **GIVEN** a live child run with an open approval
- **WHEN** the parent cancels the child run
- **THEN** the journal gets exactly one `ChildApprovalsClosed` event with the reason `RunCancelled`
- **AND** the later terminal event of the run adds no second close event

#### Scenario: Replay applies a close event

- **GIVEN** a journal holds a child approval request and then a `ChildApprovalsClosed` event for its run
- **WHEN** replay reaches the close event
- **THEN** the approval table holds no entry for that run
- **AND** an entry of another run, or of a parent call, stays

#### Scenario: A negative example

- **GIVEN** a run has no entry in the approval table
- **WHEN** the session cancels the run
- **THEN** the session persists no `ChildApprovalsClosed` event

### Requirement: A closed child approval does not block a snapshot

A snapshot SHALL be allowed when the approval table holds no entry and no parked tool batch is open. A child approval that the session closed, durably or at recovery, SHALL NOT keep the table non-empty.

#### Scenario: The session snapshots after a stranded approval closes

- **GIVEN** recovery closed the only child approval
- **WHEN** the session persists its next event
- **THEN** the session saves a snapshot

#### Scenario: An open approval blocks a snapshot

- **GIVEN** a parent approval is still open
- **WHEN** the session persists an event
- **THEN** the session saves no snapshot and logs that an approval batch is unresolved

### Requirement: An old journal without close events still recovers

A journal that was written before `ChildApprovalsClosed` existed SHALL recover. The recovery step in `RecoveryCompleted` SHALL close the child approvals that replay rebuilt, and it SHALL persist the close events for them. The session SHALL NOT fail, and it SHALL NOT keep a stale child entry.

#### Scenario: Old journal of a cancelled and a lost child

- **GIVEN** a journal with a cancelled child run and a lost child run, each with an approval request and no close event
- **WHEN** the session recovers
- **THEN** the approval table holds no child entry
- **AND** the session writes no `Tool approval requested` entry for either call
