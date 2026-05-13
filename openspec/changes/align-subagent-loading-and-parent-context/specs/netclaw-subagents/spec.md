## ADDED Requirements

### Requirement: File-defined subagent registry reloads without daemon restart

The system SHALL resolve file-defined subagent definitions from a reloadable
registry backed by `~/.netclaw/agents/*.md`. Before resolving a user-facing
subagent for `spawn_agent` or routed skill execution, the runtime SHALL detect
whether the definitions directory changed since the last successful snapshot and
SHALL reload the registry when needed.

Reloaded snapshots SHALL apply add, update, and delete changes to subsequent
subagent executions without daemon restart.

#### Scenario: Added subagent becomes available on next activation

- **GIVEN** the active registry snapshot does not include `ops-helper`
- **AND** the operator adds a valid `ops-helper.md` definition under
  `~/.netclaw/agents`
- **WHEN** the next `spawn_agent` or routed-skill lookup occurs
- **THEN** the runtime reloads the registry before lookup
- **AND** `ops-helper` is available for that activation

#### Scenario: Edited subagent definition takes effect on next activation

- **GIVEN** `ops-helper` is already loaded from disk
- **AND** the operator edits its prompt or metadata on disk to a new valid state
- **WHEN** the next subagent lookup occurs
- **THEN** the runtime reloads the registry before lookup
- **AND** the next spawned `ops-helper` run uses the updated definition

#### Scenario: Deleted subagent disappears on next activation

- **GIVEN** `ops-helper` is present in the active registry snapshot
- **WHEN** its source file is deleted from `~/.netclaw/agents`
- **AND** the next subagent lookup occurs
- **THEN** the reloaded registry no longer contains `ops-helper`
- **AND** later attempts to resolve it fail deterministically

### Requirement: Invalid reload changes fail closed

The runtime SHALL exclude reloaded subagent definitions that no longer pass
loader validation from the active registry snapshot and SHALL emit
deterministic diagnostics. The system SHALL NOT continue serving the prior
version of an invalidated definition.

#### Scenario: Invalid edit removes previously valid definition

- **GIVEN** `ops-helper` was valid in the previous registry snapshot
- **AND** the operator edits `ops-helper.md` so it becomes invalid
- **WHEN** the next subagent lookup triggers reload
- **THEN** `ops-helper` is absent from the new active snapshot
- **AND** the runtime emits diagnostics identifying the file and rejection reason
- **AND** resolving `ops-helper` fails instead of using the stale prior version

### Requirement: Subagent runs use immutable definition snapshots

Once a subagent run starts, it SHALL keep the resolved definition snapshot for
the duration of that run. Later registry reloads SHALL affect only future
subagent executions.

#### Scenario: Running subagent ignores mid-run definition edit

- **GIVEN** a subagent run has already started from a valid definition snapshot
- **WHEN** the source definition file changes on disk before that run completes
- **THEN** the in-flight subagent keeps its original definition snapshot
- **AND** only later activations use the reloaded definition

### Requirement: Subagent executions inherit parent context snapshot

When a session launches a subagent, the runtime SHALL capture an immutable
parent-context snapshot for that run. The snapshot SHALL include the parent
session identifier, parent `session_dir`, and the parent's current
`WorkingContext.ProjectDirectory` when set.

The inherited snapshot provides execution grounding for the child and SHALL NOT
broaden the child beyond the parent session's existing audience, tool, or file
access posture.

#### Scenario: Spawned subagent inherits parent session and project directories

- **GIVEN** a parent session has `session_dir` `/tmp/netclaw/sessions/abc`
- **AND** `WorkingContext.ProjectDirectory` is `/home/user/workspaces/netclaw`
- **WHEN** the session spawns a subagent
- **THEN** the child run receives both directory values in its execution snapshot

#### Scenario: Parent project switch affects only later subagents

- **GIVEN** a parent session spawns subagent A with project directory
  `/home/user/workspaces/project-a`
- **AND** the parent later switches to `/home/user/workspaces/project-b`
- **WHEN** subagent A is still running
- **THEN** subagent A keeps the project-a snapshot
- **AND** only subagents spawned after the switch inherit project-b
