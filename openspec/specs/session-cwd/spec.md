## ADDED Requirements

### Requirement: Session-scoped project directory

Each session SHALL maintain a mutable `ProjectDirectory` in `WorkingContext`
that tracks which project the session is working on. This is the root
directory of the project (where `AGENTS.md` or `.netclaw/AGENTS.md` lives).
The project directory SHALL be independent of the immutable session directory
(`~/.netclaw/sessions/{id}/`) used for state isolation. The project directory
SHALL be persisted in `SessionSnapshot` via `WorkingContext` and survive
compaction, actor recovery, and daemon restart.

#### Scenario: New session has no project directory

- **GIVEN** a session is created with no prior persisted state
- **WHEN** the session actor initializes
- **THEN** `WorkingContext.ProjectDirectory` is null
- **AND** no `[project-instructions]` block is injected

#### Scenario: Project directory survives daemon restart

- **GIVEN** a session has project directory set to `/home/user/workspaces/akadonic`
- **WHEN** the daemon crashes and restarts
- **THEN** the recovered session has project directory equal to
  `/home/user/workspaces/akadonic`
- **AND** the project's identity file is loaded on the first LLM call

#### Scenario: Project directory survives compaction

- **GIVEN** a session has project directory set to `/home/user/workspaces/akadonic`
- **WHEN** context compaction occurs
- **THEN** `WorkingContext.ProjectDirectory` is preserved in the compacted state

#### Scenario: Backward compat for sessions without project directory

- **GIVEN** a session was created before project directory tracking was
  implemented
- **WHEN** the session actor recovers from a snapshot without a project
  directory field
- **THEN** `ProjectDirectory` is null
- **AND** the session functions normally with no `[project-instructions]` block

### Requirement: set_working_directory tool

The system SHALL provide a `set_working_directory` tool that sets the
session's project directory to a specified path. The tool SHALL validate
that the target path is a real directory, resolve it to an absolute path,
and validate it against the audience trust profile's read-allowed roots.
The tool SHALL be profile-managed so that audiences without directory
navigation privileges (Public, Team by default) cannot use it.

#### Scenario: set_working_directory updates project directory

- **GIVEN** a session with no project directory set
- **AND** the audience trust profile allows reads under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/workspaces/akadonic`
- **THEN** the session project directory is set to
  `/home/user/workspaces/akadonic`
- **AND** the project's identity file is loaded on the next LLM call

#### Scenario: set_working_directory rejected outside allowed roots

- **GIVEN** a session with audience profile allowing reads only under
  `/home/user`
- **WHEN** the agent invokes `set_working_directory` with path `/etc/nginx`
- **THEN** the project directory remains unchanged
- **AND** the tool returns an error indicating the path is outside allowed
  roots

#### Scenario: set_working_directory rejected for nonexistent directory

- **GIVEN** a session with audience profile allowing reads under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/nonexistent`
- **THEN** the project directory remains unchanged
- **AND** the tool returns an error indicating the directory does not exist

#### Scenario: Personal audience allows any valid directory

- **GIVEN** a session with personal audience (`ToolFilesystemMode.All`)
- **WHEN** the agent invokes `set_working_directory` with any valid directory
- **THEN** the project directory is updated

#### Scenario: set_working_directory not exposed to public audience

- **GIVEN** a session with public audience
- **WHEN** the tool exposure list is computed
- **THEN** `set_working_directory` is not included

#### Scenario: Switching projects replaces context

- **GIVEN** a session with project directory `/home/user/workspaces/akadonic`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/workspaces/other-project`
- **THEN** the project directory changes to `/home/user/workspaces/other-project`
- **AND** the next LLM call loads identity files from the new project
- **AND** the old project's identity files are no longer injected

### Requirement: Working context block includes project directory

The `[working-context]` block emitted by `WorkingContext.ToContextBlock()`
SHALL include the current project directory when set.

#### Scenario: Project directory included in working context block

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic` and recent files
- **WHEN** `ToContextBlock()` is called
- **THEN** the output includes `project_dir: /home/user/workspaces/akadonic`
  alongside the recent files listing
