This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Session storage binding is durable and versioned

Before a new-layout session creates a session-owned file, the system SHALL
persist an immutable storage binding with the layout version and absolute
session storage envelope root. The system SHALL use that binding when it
resumes the session. A configuration change, environment override, or binary
upgrade SHALL NOT reinterpret the envelope root. The binding SHALL NOT contain
a second log root.

Channel ingress, the parent actor, child-run creation, and the log dispatcher
SHALL resolve storage through one shared get-or-bind operation. That operation
SHALL be atomic for concurrent first consumers. A filesystem helper SHALL NOT
independently choose a new-layout path from only the session identifier and
current configuration.

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

#### Scenario: Example - ingress binds before it writes media

- **GIVEN** the first message for a new session contains an attachment
- **WHEN** channel ingress prepares the media file before actor processing
- **THEN** it resolves and persists the storage binding first
- **AND** it writes the file below `<session-envelope>/workspace/media`

#### Scenario: Example - concurrent first messages share one binding

- **GIVEN** two ingress requests race to create one new session
- **WHEN** both call the shared storage resolver
- **THEN** one atomic binding wins
- **AND** both requests receive the same persisted envelope root

#### Scenario: Counterexample - helper cannot bypass layout selection

- **GIVEN** a new session has no binding yet
- **WHEN** an ingress or logging helper needs a path
- **THEN** the helper does not compute a writable path from only the session ID
  and configured base
- **AND** no file is created before the shared resolver selects the layout

### Requirement: Existing sessions resume without migration

The system SHALL leave the storage binding absent for a session that predates
the new layout. It SHALL continue to use the existing session-directory and
session-log path resolvers for that session. An upgrade SHALL NOT move, copy,
rename, or delete its data.

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

#### Scenario: Counterexample - old binary support for new sessions is out of scope

- **GIVEN** an older binary does not understand the storage binding
- **WHEN** release documentation describes compatibility
- **THEN** it promises that current binaries preserve existing unbound sessions
- **AND** it does not promise that a pre-feature binary can resume a newly
  bound session

### Requirement: Version 2 uses one physical session envelope

For a session with a version-2 binding, the system SHALL place the parent
session directory, artifacts, temporary files, worktrees, raw log, and all
child-run directories below the persisted session storage envelope. Each child
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

### Requirement: Same-session logs use existing file-tool read authority

The system SHALL give existing file-read, file-list, and file-search operations
read access to logs in the current session envelope. The scope SHALL include
the main session log and every child log. Every parent and child run in that
session SHALL receive the same log-read scope. The scope SHALL NOT include
another session.

For an existing unbound session, the system SHALL build the same read scope
from the unchanged legacy main-log resolver and durable child lineage. It SHALL
NOT move or copy a legacy log to make it readable.

This read scope SHALL NOT authorize file writes, file edits, attachments, or
shell execution. The default no-project working directory SHALL remain the
`workspace/` child. The system SHALL NOT add the complete envelope as a shell
safe root.

The implementation SHALL NOT redefine `{session_dir}` as the session envelope.
It SHALL NOT add the complete envelope or `subagents/` directory as a read
root. It SHALL authorize only normalized main-log and child-log path shapes.
Existing link, reparse-point, and protected-path checks SHALL still apply.

This requirement defines Netclaw application authorization. It SHALL NOT be
documented or tested as OS-level containment of an arbitrary process that has
already received execution authority under the Netclaw identity.

#### Scenario: Example - default recursive search stays in workspace

- **GIVEN** a version-2 session has no project scope
- **WHEN** a shell starts without an explicit working directory
- **THEN** its cwd is `<session-envelope>/workspace`
- **AND** a recursive search of `.` does not include the sibling `logs/` or
  `subagents/` areas by directory containment

#### Scenario: Example - agent reads its own session log

- **GIVEN** an agent uses a version-2 session envelope
- **WHEN** it calls `file_read` for its main session log
- **THEN** same-session log scope authorizes the read
- **AND** `file_read` applies its normal output bounds

#### Scenario: Example - parent reads an owned child log

- **GIVEN** a parent owns child run `run-7`
- **WHEN** it calls `file_search` on the returned log path's directory
- **THEN** same-session log scope authorizes the search
- **AND** no special log tool is required

#### Scenario: Example - child reads another log in the same session

- **GIVEN** child runs `run-7` and `run-8` belong to one session
- **WHEN** `run-7` reads the main log or the log for `run-8`
- **THEN** same-session log scope authorizes the read
- **AND** the request remains subject to normal file-tool limits

#### Scenario: Example - legacy session keeps readable log paths

- **GIVEN** an existing unbound session uses separate data and log roots
- **WHEN** its parent or child calls an existing file tool for a resolved
  same-session log path
- **THEN** same-session log scope authorizes the operation
- **AND** no file moves into a new envelope

#### Scenario: Counterexample - foreign session log is denied

- **GIVEN** a log path belongs to another session envelope
- **WHEN** the current agent calls `file_read`, `file_list`, or `file_search`
- **THEN** same-session log scope does not authorize the operation
- **AND** the result does not reveal whether the foreign file exists

#### Scenario: Counterexample - log read does not grant mutation

- **GIVEN** an agent can read its same-session logs
- **WHEN** it calls `file_write` or `file_edit` for a log path
- **THEN** same-session log scope does not authorize that operation
- **AND** normal write policy decides the call

#### Scenario: Counterexample - broad child root is not authorized

- **GIVEN** a version-2 session has child logs, artifacts, and temporary files
- **WHEN** policy constructs the same-session log read scope
- **THEN** it does not add `<session-envelope>/subagents` as a broad root
- **AND** log-read scope cannot read a child artifact or temporary file

#### Scenario: Counterexample - linked path cannot escape log scope

- **GIVEN** a same-session log directory contains a filesystem link to another
  session
- **WHEN** an agent reads, lists, or searches through that link
- **THEN** existing path safety policy denies the operation
- **AND** same-session ownership does not bypass that denial

#### Scenario: Counterexample - envelope is not the shell root

- **GIVEN** an agent can read logs in its session envelope
- **WHEN** policy selects a default shell cwd or shell safe root
- **THEN** it uses the session directory or existing project scope
- **AND** it does not use the complete envelope because log reads are allowed

#### Scenario: Counterexample - this layout is not a process sandbox

- **GIVEN** an arbitrary process has already received execution authority as
  the Netclaw OS identity
- **WHEN** it learns a same-session log path
- **THEN** this storage layout alone does not claim to stop the OS file open
- **AND** a future containment capability must define that stronger boundary
