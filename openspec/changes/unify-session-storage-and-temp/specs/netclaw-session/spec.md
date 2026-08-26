This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Session storage binding is durable and versioned

Before a new-layout session creates a session-owned file, the system SHALL
persist an immutable storage descriptor that contains the layout version, the
absolute agent-data root, and the absolute protected audit root. The system
SHALL use the persisted descriptor when it resumes the session. A configuration
change, environment override, or binary upgrade SHALL NOT reinterpret either
root of an existing session.

#### Scenario: Example - new session binds its roots before use

- **GIVEN** a session has no storage descriptor
- **WHEN** it first needs session-owned filesystem storage
- **THEN** the system persists a version-2 descriptor before it creates a file
- **AND** every later path for that session derives from the persisted roots

#### Scenario: Counterexample - configuration cannot relocate a bound session

- **GIVEN** a session has a persisted version-2 storage descriptor
- **WHEN** the configured Netclaw home or sessions path changes
- **THEN** the session continues to use both persisted roots
- **AND** the system does not move or copy the session implicitly

#### Scenario: Counterexample - binding failure prevents a split session

- **GIVEN** the system cannot persist the storage descriptor
- **WHEN** a filesystem operation needs the new layout
- **THEN** the operation fails before it writes session-owned data
- **AND** the system does not derive a temporary fallback root

### Requirement: Legacy sessions resume without migration

The system SHALL recognize a session that predates the version-2 storage
descriptor and SHALL continue to use its legacy session and log paths. An
upgrade SHALL NOT move, copy, rename, or delete legacy session data. A current
binary SHALL read all recorded log lineages for a session when an earlier
binary wrote a legacy log after rollback.

#### Scenario: Example - legacy session resumes after upgrade

- **GIVEN** a persisted session has no version-2 storage descriptor
- **WHEN** a current binary resumes it
- **THEN** the system uses the legacy session and log paths
- **AND** no migration changes the existing files

#### Scenario: Counterexample - rollback does not hide a second log lineage

- **GIVEN** a version-2 session was resumed by an earlier binary
- **AND** the earlier binary wrote a legacy session log
- **WHEN** a current binary resumes the session again
- **THEN** the supported session inspector reads both log lineages
- **AND** it presents their records in time order

### Requirement: Version 2 binds agent data and audit data to one session

For a version-2 session, the system SHALL place artifacts, inbound files,
media, bounded tool outputs, temporary files, worktrees, and child artifacts
below the persisted agent-data root. It SHALL place raw parent and child logs
below the persisted audit root. Each child run SHALL have an opaque run
identifier and SHALL use paths below its parent's corresponding root.
Daemon-global logs SHALL remain outside both session roots.

#### Scenario: Example - parent agent storage uses named areas

- **GIVEN** a version-2 parent session
- **WHEN** the system creates its storage areas
- **THEN** the agent-data root contains separate artifact, inbound, media,
  tool-output, temporary, worktree, and child-run areas
- **AND** no area is placed below the operating system temporary root unless
  the persisted agent-data root itself was configured there

#### Scenario: Counterexample - raw logs do not enter agent storage

- **GIVEN** a version-2 parent session
- **WHEN** the system resolves its raw log target
- **THEN** the target is below the persisted audit root
- **AND** the target is not below the agent-data root

#### Scenario: Example - child storage stays below its parent

- **GIVEN** a parent session creates a child run
- **WHEN** the child writes its log, artifacts, or temporary files
- **THEN** artifacts and temporary files are below the parent agent-data root
- **AND** raw logs are below the parent audit root
- **AND** each child path contains the child's opaque run identifier

#### Scenario: Counterexample - daemon logs do not enter session storage

- **GIVEN** the daemon emits a process-wide diagnostic
- **WHEN** the diagnostic is written
- **THEN** it uses the existing daemon-global log location
- **AND** it is not written to a session's audit root

### Requirement: Raw audit data stays outside agent filesystem authority

The system SHALL NOT add the version-2 audit root to workspace-file or shell
safe spaces through normal session context. Knowledge of the agent-data root,
session ownership, or an opaque child reference SHALL NOT grant raw audit-file
access. The parent SHALL use a bounded, redacted child-activity interface
instead of raw file or shell access.

#### Scenario: Counterexample - workspace read cannot open a raw log

- **GIVEN** an agent can read normal files below its agent-data root
- **WHEN** it requests a raw parent or child log below the audit root
- **THEN** the system denies the read
- **AND** the denial does not disclose raw log content

#### Scenario: Counterexample - default shell cwd cannot contain the audit root

- **GIVEN** a shell starts in the agent-data root or a declared project
- **WHEN** policy computes authority from the default session cwd
- **THEN** that authority does not include the separate audit root
- **AND** a recursive command in the agent-data root does not reach raw logs by
  directory containment

#### Scenario: Example - parent can inspect bounded child activity

- **GIVEN** a parent owns a child run
- **WHEN** it requests activity through the supported child-log interface
- **THEN** the system returns a bounded and redacted projection
- **AND** the parent does not need the raw log path
