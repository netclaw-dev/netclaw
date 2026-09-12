This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Session storage binding is durable and versioned

Before a new-layout session creates a session-owned file, the system SHALL
persist an immutable storage binding with the layout version and absolute
session storage envelope root. The system SHALL use that binding when it
resumes the session. A configuration change, environment override, or binary
upgrade SHALL NOT reinterpret the envelope root. The binding SHALL NOT contain
a second log root. Two distinct raw session identifiers SHALL NOT resolve to
the same physical envelope, even when their human-readable sanitized forms are
equal.

Channel ingress, the parent actor, child-run creation, and the log dispatcher
SHALL resolve storage through one shared get-or-bind operation. That operation
SHALL be atomic for concurrent first consumers. A filesystem helper SHALL NOT
independently choose a new-layout path from only the session identifier and
current configuration.

The system SHALL use exactly one SQLite database at
`NetclawPaths.SqliteDbPath`. This Netclaw database SHALL be the source of truth
for actor journal and snapshot data, durable reminders, the session catalog,
daily statistics, memory, and session storage bindings. Production
configuration SHALL NOT expose another database path or an in-memory
persistence provider. A supplied `Persistence` section SHALL be rejected by
configuration validation and daemon startup.

#### Scenario: Example - new session binds one envelope before use

- **GIVEN** a new session has no storage binding
- **WHEN** it first needs session-owned filesystem storage
- **THEN** the system persists the version-2 binding before it creates a file
- **AND** every parent and child path derives from the persisted envelope

#### Scenario: Counterexample - configuration cannot relocate a bound session

- **GIVEN** a session has a persisted storage binding for
  `/srv/netclaw-a/sessions/s-42`
- **WHEN** configuration changes the sessions base to
  `/srv/netclaw-b/sessions`
- **THEN** the session continues to use the persisted envelope under
  `/srv/netclaw-a`
- **AND** the system does not move, copy, or partly reinterpret the session

#### Scenario: Counterexample - binding failure prevents an untracked write

- **GIVEN** the system cannot persist the storage binding
- **WHEN** a filesystem operation needs the new layout
- **THEN** the operation fails before it writes session-owned data
- **AND** the system does not derive a fallback root from current configuration

#### Scenario: Example - ingress binds before it stages an attachment

- **GIVEN** the first message for a new session contains an attachment
- **WHEN** channel ingress downloads the attachment before actor processing
- **THEN** it resolves and persists the storage binding first
- **AND** it stages the untrusted bytes below
  `<session-envelope>/attachment-staging`
- **AND** it moves an accepted file below `<session-envelope>/workspace/inbox`

#### Scenario: Counterexample - storage location does not bypass admission

- **GIVEN** an attachment exists below the session envelope staging directory
- **WHEN** its content scan rejects the attachment
- **THEN** the pipeline does not move it into `workspace/inbox`
- **AND** the session does not create agent-visible media from it

#### Scenario: Example - concurrent first messages share one binding

- **GIVEN** two ingress requests race to create one new session
- **WHEN** both call the shared storage resolver
- **THEN** one atomic binding wins
- **AND** both requests receive the same persisted envelope root

#### Scenario: Counterexample - sanitized identifiers cannot collide

- **GIVEN** raw session identifiers `channel/a_b` and `channel/a/b`
- **AND** their display-safe forms would otherwise be equal
- **WHEN** the resolver binds storage for both sessions
- **THEN** it persists two different envelope roots
- **AND** later recovery maps each raw identifier to its original root

#### Scenario: Counterexample - helper cannot bypass layout selection

- **GIVEN** a new session has no binding yet
- **WHEN** an ingress or logging helper needs a path
- **THEN** the helper does not compute a writable path from only the session ID
  and configured base
- **AND** no file is created before the shared resolver selects the layout

#### Scenario: Example - live deployment uses one database

- **GIVEN** a live Netclaw daemon
- **WHEN** it persists actor, reminder, catalog, statistics, memory, or storage data
- **THEN** all SQLite records use `NetclawPaths.SqliteDbPath`
- **AND** no second SQLite database is created

#### Scenario: Counterexample - test persistence is not operator configuration

- **GIVEN** a test harness uses an in-memory actor journal
- **WHEN** a live deployment supplies a `Persistence` configuration section
- **THEN** configuration validation and daemon startup reject it
- **AND** no independent setting can redirect the Netclaw database

### Requirement: Existing sessions resume without migration

The system SHALL leave the storage binding absent for a session that predates
the new layout. It SHALL use the current legacy session-directory and
session-log path resolvers for that session. An upgrade SHALL NOT move, copy,
rename, or delete its data. If an operator changes a legacy root, the system
SHALL NOT claim that it relocates or rediscovers files below the old root.

#### Scenario: Example - legacy session resumes after upgrade

- **GIVEN** a persisted session predates the storage binding
- **WHEN** a current binary resumes it
- **THEN** the storage binding remains absent
- **AND** the system uses the existing session and log path resolvers
- **AND** no migration changes the existing files

#### Scenario: Counterexample - legacy session cannot become a hybrid

- **GIVEN** an existing unbound session has separate data and log directories
- **WHEN** a current binary resumes it without storage reconfiguration
- **THEN** both existing path resolvers remain in use
- **AND** the system does not route new logs into a new-layout envelope

#### Scenario: Counterexample - legacy root change does not migrate files

- **GIVEN** an unbound session has files below a configured legacy root
- **WHEN** the operator changes that root
- **THEN** the old files remain in their original location
- **AND** this capability does not promise discovery below the old root

#### Scenario: Counterexample - old binary support for new sessions is out of scope

- **GIVEN** an older binary does not understand the storage binding
- **WHEN** release documentation describes compatibility
- **THEN** it promises that current binaries preserve existing unbound sessions
- **AND** it does not promise that a pre-feature binary can resume a newly
  bound session

#### Scenario: Example - journal-only legacy session remains discoverable

- **GIVEN** an existing session has journal records but no snapshot and no
  storage binding
- **WHEN** the current resolver checks whether the session predates the new
  layout
- **THEN** it recognizes the shipped journal schema and table
- **AND** it resumes the existing path behavior without creating a new binding

### Requirement: Version 2 uses one physical session envelope

For a session with a version-2 binding, the system SHALL place the parent
session directory, attachment staging, artifacts, temporary files, worktrees,
raw log, and all child-run directories below the persisted session storage envelope. Each child
run SHALL place its artifacts, temporary files, and raw log below
`<session-envelope>/subagents/<run-id>`. Daemon-global logs SHALL remain outside
the session envelope.

The parent session directory SHALL be `<session-envelope>/workspace`. Raw logs
SHALL use `<session-envelope>/logs/session.log` for the parent and
`<session-envelope>/subagents/<run-id>/logs/session.log` for a child.

#### Scenario: Example - one envelope contains parent and child data

- **GIVEN** a version-2 parent at `/srv/netclaw/sessions/s-42`
- **AND** the parent creates child run `run-7`
- **WHEN** the parent and child resolve their storage paths
- **THEN** the parent cwd is `/srv/netclaw/sessions/s-42/workspace`
- **AND** the parent raw log is
  `/srv/netclaw/sessions/s-42/logs/session.log`
- **AND** the child artifacts, temporary files, and raw log are below
  `/srv/netclaw/sessions/s-42/subagents/run-7`

#### Scenario: Example - sibling child runs do not share storage

- **GIVEN** child runs `run-7` and `run-8` belong to one parent
- **WHEN** the system derives their paths
- **THEN** each child path contains its own opaque run identifier
- **AND** neither child's artifact, temporary, or log path is below the other
  child's directory

#### Scenario: Counterexample - new raw logs cannot use a second root

- **GIVEN** a version-2 session
- **WHEN** the log dispatcher resolves a parent or child target
- **THEN** the target is below the persisted session envelope
- **AND** it is not below `NetclawPaths.SessionLogsDirectory`

#### Scenario: Counterexample - daemon logs do not enter session storage

- **GIVEN** the daemon emits a process-wide diagnostic
- **WHEN** the diagnostic is written
- **THEN** it uses the daemon-global log location
- **AND** it is not written to a session envelope

### Requirement: Session storage supplies the shared sessions root

The system SHALL store every new session envelope below the trusted Netclaw
sessions root. Parent and child runs SHALL receive this root as filesystem
authorization input. The session capability SHALL supply storage paths and
SHALL NOT decide file-operation authority.

Existing unbound sessions SHALL keep their established data paths. The system
SHALL also supply the legacy session-log root while those sessions remain
supported. It SHALL NOT move or copy legacy files.

The `netclaw-tools` capability SHALL own the path access decision. Session
identity SHALL NOT add another allow list, deny list, or ownership check.

This shared root intentionally lets one session analyze another session's
logs. Audience policy and file-operation permissions still decide each
request.

#### Scenario: Example - all new sessions share one trusted root

- **GIVEN** sessions `s-1` and `s-2` use version 2
- **WHEN** the system creates their envelopes
- **THEN** both envelopes are descendants of the Netclaw sessions root
- **AND** parent and child runs receive that common root

#### Scenario: Example - one session analyzes another session's log

- **GIVEN** a run can use `file_read` under the Netclaw sessions root
- **WHEN** it requests the canonical log path for another session
- **THEN** `netclaw-tools` evaluates one `Read` path access decision
- **AND** session identity adds no separate restriction

#### Scenario: Counterexample - storage location does not grant an operation

- **GIVEN** an audience cannot use `file_write`
- **WHEN** it requests a write below the sessions root
- **THEN** the path relationship does not grant the write
- **AND** `netclaw-tools` denies the operation

#### Scenario: Counterexample - the sessions root is not a shell grant

- **GIVEN** a shell path is below the sessions root
- **WHEN** the agent submits a shell command
- **THEN** normal shell syntax and approval policy still apply
- **AND** storage membership alone does not authorize execution

#### Scenario: Counterexample - this layout is not a process sandbox

- **GIVEN** a process already runs as the Netclaw operating-system identity
- **WHEN** it learns a session path
- **THEN** the storage layout does not claim to block an operating-system file open
- **AND** a separate containment capability must define that stronger boundary
