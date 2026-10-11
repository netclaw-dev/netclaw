## ADDED Requirements

The terms in these requirements use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).
The later `repair-agent-tool-boundaries` change adds complete base-chain link
validation and exact fallback states.

### Requirement: Relative first-party filesystem paths use session-owned bases

`session-cwd` SHALL select the declared project directory for a relative path
when that base is available. Otherwise, it SHALL select the immutable session
directory. If neither base is available, it SHALL return an `invalid_context`
correction. It SHALL NOT use the daemon process current directory.

The capability SHALL pass the selected base and authored path to the filesystem
authorization contract that `netclaw-tools` owns. It SHALL NOT make a separate
path access decision.

#### Scenario: Relative read uses declared project

- **GIVEN** a session with project directory `/workspace/project` and session
  directory `/session/current`
- **WHEN** `file_read` receives `src/App.cs`
- **THEN** it authorizes and reads `/workspace/project/src/App.cs`
- **AND** it does not resolve the path from the daemon current directory

#### Scenario: Relative write uses the session directory

- **GIVEN** a session without a declared project and with session directory
  `/session/current`
- **WHEN** `file_write` receives `notes/result.md`
- **THEN** it resolves `/session/current/notes/result.md`
- **AND** the existing session-directory write policy decides authorization

#### Scenario: Traversal receives no implicit authority

- **GIVEN** project directory `/workspace/project`
- **WHEN** a file tool receives `../../outside.txt`
- **THEN** `session-cwd` passes the selected base and authored path to `netclaw-tools`
- **AND** the path access decision denies a path outside the trusted roots

#### Scenario: Missing base returns correction

- **GIVEN** a sessionless tool context with no project or session directory
- **WHEN** a first-party filesystem tool receives a relative path
- **THEN** it returns `invalid_context`
- **AND** it performs no filesystem access

### Requirement: Failed filesystem operations do not change project context

A denied or failed `set_working_directory` or filesystem tool call SHALL NOT
change the declared project directory or recent-file context. Only a validated
successful project declaration SHALL replace the project directory and reload
project instructions.

#### Scenario: Denied declaration leaves prior project intact

- **GIVEN** a session already declares `/workspace/old`
- **WHEN** `set_working_directory` is denied for `/workspace/new`
- **THEN** the project directory remains `/workspace/old`
- **AND** project instructions are not reloaded from the denied path
