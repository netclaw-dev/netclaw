This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Spawned child references are machine-actionable

A successful `spawn_agent` result SHALL return the child run identifier, an
opaque reference for the child's activity log, and the exact child artifact
directory. The current parent SHALL receive read and attach authority for that
artifact directory through child ownership. The authority SHALL NOT include a
sibling raw-log or temporary area. The result SHALL NOT disclose the raw log
path. A failed spawn SHALL NOT return locations that appear usable.

The result shape SHALL be equivalent to:

```text
run_id: "run-7"
activity_log_ref: "child-log:opaque-value"
artifact_dir: "/srv/netclaw/sessions/s-42/subagents/run-7/artifacts"
```

#### Scenario: Example - successful spawn returns child references

- **WHEN** a parent successfully starts a child run
- **THEN** the tool result contains the child run identifier
- **AND** it contains an opaque activity-log reference and exact artifact
  directory
- **AND** it does not contain a raw-log path

#### Scenario: Example - parent reads a child artifact with an existing tool

- **GIVEN** a successful spawn returned the child artifact directory
- **WHEN** the owning parent calls `file_read` or `attach_file` for a file below
  that directory
- **THEN** child ownership can satisfy the artifact-area authorization check
- **AND** no new artifact-reference reader is required

#### Scenario: Counterexample - artifact directory does not expose siblings

- **GIVEN** the parent knows a child artifact directory
- **WHEN** it requests a sibling file below the child's `logs/` or `tmp/` area
- **THEN** the artifact-area authority does not authorize that request
- **AND** normal policy decides the call

#### Scenario: Counterexample - failed spawn has no usable child references

- **WHEN** the child run is not created
- **THEN** the tool result reports failure
- **AND** it contains no active log reference or artifact directory

### Requirement: Parent can read bounded child activity without shell

The deferred `subagent_log_read` tool SHALL accept only a child activity-log
reference owned by the current parent. It SHALL return a bounded, redacted
activity projection with a continuation cursor. The request schema SHALL
include an optional literal-query field. The tool SHALL NOT return system
prompts, credentials, secrets, raw
approval payloads, or unredacted tool arguments and results.
The reference SHALL remain valid after parent actor recovery while the child
raw-log lineage exists.

#### Scenario: Example - parent reads the next child activity page

- **GIVEN** a parent owns a child activity-log reference
- **WHEN** it calls `subagent_log_read` with that reference and no cursor
- **THEN** the tool returns the first bounded page of redacted activity
- **AND** it returns a continuation cursor when more activity exists

#### Scenario: Example - parent filters child activity with a literal query

- **GIVEN** a parent owns a child activity-log reference
- **WHEN** it supplies a literal query
- **THEN** the result contains only bounded matching activity records
- **AND** the query is not interpreted as an executable expression

#### Scenario: Counterexample - foreign child reference is denied

- **GIVEN** a child activity-log reference belongs to another parent session
- **WHEN** the current parent calls `subagent_log_read`
- **THEN** the tool denies the request
- **AND** it does not reveal whether matching raw files exist

#### Scenario: Counterexample - projection cannot expose sensitive records

- **GIVEN** a child raw log contains prompts, approval payloads, credentials,
  or unredacted tool data
- **WHEN** the parent reads the child activity projection
- **THEN** those records are omitted or centrally redacted
- **AND** the output remains within its configured byte and record limits

#### Scenario: Example - child reference survives parent recovery

- **GIVEN** a parent received a child activity-log reference before an actor
  restart
- **WHEN** the recovered parent uses that reference
- **THEN** the tool resolves the same parent-owned child lineage
- **AND** it applies the current redaction and output limits

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
