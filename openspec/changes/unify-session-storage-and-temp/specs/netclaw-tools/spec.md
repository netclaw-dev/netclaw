This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Spawned child references are machine-actionable

A successful `spawn_agent` result SHALL return the child run identifier, an
exact child log path, and the exact child artifact directory. The current
parent SHALL receive read authority for the child log and read and attach
authority for the artifact directory through child ownership. A failed spawn
SHALL NOT return locations that appear usable.

The system SHALL resolve and create the child log target before it returns a
successful result. The log can be empty. An immediate authorized `file_read`
SHALL NOT fail because the log path is not ready.

The result shape SHALL be equivalent to:

```text
run_id: "run-7"
log_path: "/srv/netclaw/sessions/s-42/subagents/run-7/logs/session.log"
artifact_dir: "/srv/netclaw/sessions/s-42/subagents/run-7/artifacts"
```

#### Scenario: Example - successful spawn returns child references

- **WHEN** a parent successfully starts a child run
- **THEN** the tool result contains the child run identifier
- **AND** it contains the exact child log path and artifact directory
- **AND** both paths belong to that parent session

#### Scenario: Example - parent reads a child artifact with an existing tool

- **GIVEN** a successful spawn returned the child artifact directory
- **WHEN** the owning parent calls `file_read` or `attach_file` for a file below
  that directory
- **THEN** child ownership can satisfy the artifact-area authorization check
- **AND** no new artifact-reference reader is required

#### Scenario: Example - parent reads child logs with existing tools

- **GIVEN** a successful spawn returned the exact child log path
- **WHEN** the owning parent uses `file_read`, `file_search`, or `file_list`
- **THEN** the existing tool performs its normal bounded operation
- **AND** no special child-log tool is required

#### Scenario: Counterexample - log read scope does not grant writes

- **GIVEN** the parent can read a same-session child log
- **WHEN** it calls `file_write` or `file_edit` for that log
- **THEN** the log-read scope does not authorize the mutation
- **AND** normal write policy decides the call

#### Scenario: Counterexample - failed spawn has no usable child references

- **WHEN** the child run is not created
- **THEN** the tool result reports failure
- **AND** it contains no child log path or artifact directory

#### Scenario: Example - successful child log path is ready

- **WHEN** `spawn_agent` returns a successful child result
- **THEN** the returned log path identifies an existing file
- **AND** an authorized `file_read` can open it immediately

### Requirement: Existing file tools can inspect same-session logs

The existing `file_read`, `file_search`, and `file_list` tools SHALL accept log
paths from the current session envelope when their audience policy permits the
tool. Each tool SHALL keep its existing output bounds, pagination, and query
contract. This capability SHALL NOT add a new tool or a log-specific query
language.

The same-session log read scope SHALL include the main session log and its
child logs for every parent and child run. It SHALL also cover resolved legacy
main and child log paths. It SHALL NOT include another session. It SHALL NOT
grant write, edit, attach, or shell authority.

The existing file tools SHALL return their normal file content. The system
SHALL NOT add a log-specific redaction or projection layer. Existing file-tool
output bounds and audience policy SHALL still apply.

`file_read` and `file_search` SHALL support an active session-log writer on
POSIX and Windows. Their read handles SHALL NOT block the writer or fail only
because the writer keeps its append handle open.

#### Scenario: Example - parent reads the next child log page

- **GIVEN** a parent owns the child log path from `spawn_agent`
- **WHEN** it calls `file_read` with `StartLine=1` and a bounded `Limit`
- **THEN** the tool returns that normal line range
- **AND** the parent can request the next range with a later `StartLine`

#### Scenario: Example - parent searches child logs with an existing tool

- **GIVEN** a parent owns a child log path
- **WHEN** it calls `file_search` on that path's directory in content mode
- **THEN** the tool returns its normal bounded matches
- **AND** the parent does not need a shell search

#### Scenario: Example - agent lists its session logs

- **GIVEN** an agent has same-session log read scope
- **WHEN** it calls `file_list` for its session log area
- **THEN** the tool lists only paths that its current session owns
- **AND** it applies its normal result limit

#### Scenario: Example - active Windows log remains readable

- **GIVEN** the session-log writer holds its normal append handle open
- **WHEN** `file_read` or `file_search` opens that log on Windows
- **THEN** the read succeeds with the normal file-tool result
- **AND** the writer can append and flush another line

#### Scenario: Counterexample - same-session log gets no special projection

- **GIVEN** a same-session log contains normal session diagnostic content
- **WHEN** an authorized agent reads it with `file_read`
- **THEN** the tool returns its normal bounded file content
- **AND** Netclaw does not replace it with a log-specific activity view

#### Scenario: Counterexample - foreign session log is denied

- **GIVEN** a log path belongs to another session
- **WHEN** the current agent passes that path to an existing file tool
- **THEN** the tool denies the request
- **AND** it does not reveal whether the foreign file exists

#### Scenario: Example - log path survives parent recovery

- **GIVEN** a parent received a child log path before an actor restart
- **WHEN** the recovered parent reads that path
- **THEN** current session ownership authorizes the same child lineage
- **AND** the existing file tool applies its current output limits

### Requirement: Worktree creation uses a managed destination

The deferred `worktree_create` tool SHALL create a Git worktree for an
authorized source repository or the current project. It SHALL accept the
branch selection but SHALL NOT accept an arbitrary destination path. It SHALL
allocate a collision-safe destination below the current session's worktree
area and SHALL return the exact created path. The tool SHALL NOT delete an
existing directory or worktree. A successful call SHALL record the owning
session and run until a later cleanup capability removes that record.

#### Scenario: Example - current project gets a managed worktree

- **GIVEN** the current project is an authorized Git repository
- **WHEN** the agent calls `worktree_create` for a valid branch
- **THEN** the tool creates a worktree below the session worktree area
- **AND** it returns the exact path and successful file activity

#### Scenario: Counterexample - caller cannot choose an external destination

- **WHEN** an agent attempts to supply a destination outside the managed
  worktree area
- **THEN** the schema or input validation rejects the request
- **AND** no Git process starts

#### Scenario: Counterexample - unauthorized source repository is denied

- **GIVEN** a requested source repository is outside current authority
- **WHEN** the agent calls `worktree_create`
- **THEN** authorization denies the operation
- **AND** no destination is allocated

#### Scenario: Example - successful worktree can become project scope

- **GIVEN** `worktree_create` succeeds
- **WHEN** the session applies the tool outcome
- **THEN** it applies the returned typed project-scope effect
- **AND** it loads project instructions through the existing project-scope
  contract

#### Scenario: Counterexample - failed worktree does not change project scope

- **WHEN** worktree creation fails or is denied
- **THEN** the project scope remains unchanged
- **AND** the tool does not remove a pre-existing directory

#### Scenario: Example - worktree ownership survives actor recovery

- **GIVEN** a successful worktree belongs to one session and child run
- **WHEN** the parent actor recovers
- **THEN** the ownership record still identifies that session and run
- **AND** recovery does not delete the worktree
