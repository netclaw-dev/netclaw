This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## RENAMED Requirements

- FROM: `Subagent context announces private session scratch`
- TO: `Existing session context announces managed paths`
- FROM: `Session directory is the private shell scratch location`
- TO: `Managed temporary directory is the private temporary location`

## MODIFIED Requirements

### Requirement: Existing session context announces managed paths

The system SHALL preserve the existing `[session]` context block and its
`session_dir` entry. It SHALL extend that same block with the applicable
`temp_dir`, `artifact_dir`, `worktree_dir`, and `log_path` entries for Personal
and Team parent and child runs. It SHALL NOT add a second context block or
repeat these paths in per-turn guidance.

The system SHALL state the distinct purpose of each path. Public context SHALL
retain its existing private-path policy. The guidance SHALL preserve an
explicitly required platform temporary path.

The context SHALL derive from the existing parent or child run scope. It SHALL
NOT add a public protocol field, persist a path as agent identity, or change
shell authorization.

For a parent with a version-2 storage binding, `session_dir` SHALL mean
`<session-envelope>/workspace`. For a child, `temp_dir` and `artifact_dir`
SHALL be siblings below `<session-envelope>/subagents/<run-id>`. The context
SHALL NOT use `session_dir` as a synonym for the complete storage envelope.
It SHALL describe `session_dir` as the working directory and relative-path
fallback. It SHALL describe `temp_dir` as disposable run-local storage. Current
runtime prompts and tool schemas SHALL NOT call either path “session scratch.”

#### Scenario: Example - Personal child receives distinct managed paths

- **GIVEN** a Personal child has a bound session and run scope
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context contains its exact session, temporary, and artifact
  directories
- **AND** it contains the session's exact worktree directory
- **AND** it contains the exact log path for that child run
- **AND** it describes `temp_dir` as disposable working storage
- **AND** it describes `artifact_dir` as the location for outputs that the
  parent or user must keep
- **AND** it does not imply that path knowledge grants shell authority

#### Scenario: Example - Personal parent extends the existing session block

- **GIVEN** a Personal parent already receives `[session]` with `session_dir`
- **WHEN** Netclaw assembles its first model context for the new layout
- **THEN** the same block also contains `temp_dir`, `artifact_dir`,
  `worktree_dir`, and `log_path`
- **AND** no second session block contains duplicate path guidance

#### Scenario: Team child receives distinct managed paths

- **GIVEN** a Team child has a valid bound run scope
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context contains that run's exact managed paths
- **AND** it identifies the current run's `log_path`
- **AND** existing Team tool and shell policy remains unchanged

#### Scenario: Counterexample - Public child cannot receive private paths

- **GIVEN** a Public child has internal managed paths
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context does not contain those paths
- **AND** no replacement guidance discloses another private filesystem path

#### Scenario: Counterexample - implementation cannot replace context assembly

- **GIVEN** the current parent and child context assemblers already emit
  `session_dir`
- **WHEN** the implementation adds the new path entries
- **THEN** it extends those existing assembly seams
- **AND** it does not create another prompt provider or context protocol

#### Scenario: Counterexample - explicit platform temporary intent is preserved

- **GIVEN** a Personal or Team child receives managed-path guidance
- **WHEN** its task explicitly requires the platform temporary directory
- **THEN** the guidance tells the child to preserve that requirement
- **AND** Netclaw does not rewrite the path or grant authority to it

#### Scenario: Project declaration does not replace managed paths

- **GIVEN** a child has received its initial managed-path context
- **WHEN** it later calls `set_working_directory` successfully
- **THEN** its project scope and project instructions update through the
  existing contract
- **AND** its bound session, temporary, and artifact paths remain unchanged

#### Scenario: Example - file schemas name the relative-path fallback correctly

- **GIVEN** a workspace file tool accepts a relative path
- **WHEN** its model-visible schema describes path resolution
- **THEN** it says that the current project is tried before the session
  directory
- **AND** it does not describe the session directory as disposable scratch

#### Scenario: Counterexample - disposable guidance cannot point to session_dir

- **GIVEN** the model needs a location for disposable run-local output
- **WHEN** Netclaw renders managed-path guidance
- **THEN** the guidance points to `temp_dir`
- **AND** it does not tell the model to use `session_dir` as scratch

### Requirement: set_working_directory tool

The system SHALL provide a `set_working_directory` tool that declares the
session's project directory. The tool SHALL validate that the target is a real
directory, resolve its canonical path, and request the shared
`DeclareProjectScope` path access decision. That operation SHALL use read-file
authority while remaining distinct from an ordinary read.

The audience profile `AllowedTools` SHALL control whether the tool is exposed.
Every audience and mode SHALL limit project declarations to the session
directory, current project directory, and configured read roots. User approval
and the default interactive Personal `All` file profile SHALL NOT widen those
declaration roots.

A successful declaration SHALL update project scope, add the directory to the
trusted roots used by reviewed-safe shell policy, and load project identity
files into the system prompt. The model-visible description SHALL present the
tool as project declaration, not as a shell `cd` command.

#### Scenario: set_working_directory updates project directory

- **GIVEN** a session with no project directory set
- **AND** the audience trust profile allows declarations under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/workspaces/akadonic`
- **THEN** the session project directory is set to
  `/home/user/workspaces/akadonic`
- **AND** the project's identity file is loaded on the next LLM call
- **AND** subsequent shell calls inside that directory may receive reviewed-safe
  coverage

#### Scenario: set_working_directory rejected outside trusted roots

- **GIVEN** a session whose project declarations are limited to `/home/user`
- **WHEN** the agent invokes `set_working_directory` with path `/etc/nginx`
- **THEN** the project directory remains unchanged
- **AND** the tool reports that the path is outside trusted roots

#### Scenario: set_working_directory rejected for nonexistent directory

- **GIVEN** a session with read authority under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/nonexistent`
- **THEN** the project directory remains unchanged
- **AND** the tool reports that the directory does not exist

#### Scenario: Personal audience limits project declaration to trusted roots

- **GIVEN** a default Personal file profile with broad interactive read access
- **AND** a valid target outside the session, current project, and configured
  read roots
- **WHEN** the agent invokes `set_working_directory` with that target
- **THEN** the project directory is not updated
- **AND** the declaration is denied even though an ordinary interactive
  Personal read of the same path may be allowed

#### Scenario: set_working_directory not exposed to public audience

- **GIVEN** a Public session
- **WHEN** the tool exposure list is computed
- **THEN** `set_working_directory` is not included

#### Scenario: Switching projects replaces context

- **GIVEN** a session with project directory `/home/user/workspaces/akadonic`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/workspaces/other-project`
- **THEN** the project directory changes to `/home/user/workspaces/other-project`
- **AND** the next LLM call loads identity files from the new project
- **AND** the old project's identity files are no longer injected
- **AND** the reviewed-safe trusted root switches to the new project directory

### Requirement: Managed temporary directory is the private temporary location

The system SHALL provide a separate managed temporary directory for each
parent and child run. A version-2 parent SHALL use
`<session-envelope>/tmp/parent`. A child SHALL use
`<session-envelope>/subagents/<run-id>/tmp`. The system SHALL identify this
directory as the preferred location for disposable files. It SHALL use the
session directory, not the complete envelope, as the shell working-directory
fallback when no project or explicit working directory exists.

Personal and Team working context and correction text SHALL provide the
absolute managed temporary path when the agent needs an alternative to the
platform temporary root. Public context SHALL retain existing private-path
redaction. The system SHALL NOT silently substitute a path or imply that
temporary-directory cleanup occurs as part of this behavior.

#### Scenario: Example - no-project shell separates cwd and temp

- **GIVEN** a session has no declared project directory
- **WHEN** the agent invokes `shell_execute` without an explicit working
  directory
- **THEN** the shell working directory is the bound session directory at
  `<session-envelope>/workspace`
- **AND** temporary APIs in the process resolve to the managed temporary
  directory

#### Scenario: Counterexample - complete envelope is not the fallback cwd

- **GIVEN** a version-2 session has raw logs and child runs in its envelope
- **WHEN** the agent invokes `shell_execute` without a project or explicit cwd
- **THEN** the shell does not start at `<session-envelope>`
- **AND** a recursive search of `.` does not include those sibling areas

#### Scenario: Parent and child temporary directories do not collide

- **GIVEN** a parent and two child runs belong to one session
- **WHEN** Netclaw resolves their managed temporary directories
- **THEN** each run receives a different directory
- **AND** every directory remains below the same session envelope

#### Scenario: Example - correction names the managed temporary directory

- **GIVEN** a correction recommends private temporary storage
- **WHEN** the correction is rendered for the agent
- **THEN** it names the exact managed temporary directory for that run
- **AND** it does not name the complete session envelope as disposable storage

#### Scenario: Counterexample - Public context cannot receive managed paths

- **GIVEN** a Public parent or child agent
- **WHEN** it evaluates a platform-temp operation
- **THEN** it does not receive a private managed path
- **AND** existing Public path-redaction behavior remains

#### Scenario: Counterexample - session end does not imply cleanup

- **GIVEN** an agent writes a disposable file under its managed temporary
  directory
- **WHEN** the current session ends
- **THEN** this capability does not delete or schedule deletion of that file
- **AND** retention remains unchanged until a separate cleanup capability is
  specified

## ADDED Requirements

### Requirement: Every run receives the standard temporary environment

Before the system starts a shell or another child process, it SHALL create and
validate the run's managed temporary directory. It SHALL set `TMPDIR`, `TMP`,
and `TEMP` to that exact directory in the child process environment. It SHALL
set all three variables on POSIX and Windows. It SHALL NOT change the daemon's
global process environment.

The system SHALL capture the host platform temporary root before it injects
the managed values. Policy that identifies an explicitly authored unmanaged
temporary path SHALL use that captured host value. It SHALL NOT assume a fixed
Windows temporary path.

#### Scenario: Example - POSIX child process uses managed temp

- **GIVEN** a POSIX parent or child run has a managed temporary directory
- **WHEN** Netclaw starts a shell process
- **THEN** `TMPDIR`, `TMP`, and `TEMP` all equal that directory
- **AND** a standard temporary-path API returns a path below it

#### Scenario: Example - Windows child process uses managed temp

- **GIVEN** a Windows parent or child run has a managed temporary directory
- **WHEN** Netclaw starts a shell process
- **THEN** `TMPDIR`, `TMP`, and `TEMP` all equal that directory
- **AND** the native or .NET temporary-path API returns a path below it

#### Scenario: Sibling runs keep isolated environments

- **GIVEN** two child runs execute concurrently
- **WHEN** each reads its temporary environment
- **THEN** each sees only its own managed temporary path
- **AND** the daemon environment remains unchanged

#### Scenario: Counterexample - failed preparation cannot use host temp

- **GIVEN** the managed temporary directory cannot be created or validated
- **WHEN** Netclaw prepares a shell or child process
- **THEN** process creation fails before user code runs
- **AND** Netclaw does not fall back to the host platform temporary root

#### Scenario: Example - old background job record remains readable

- **GIVEN** a persisted background job definition predates the
  `ManagedTemporaryDirectory` property
- **WHEN** the current daemon loads that JSON after restart
- **THEN** it preserves the job's terminal history
- **AND** a pending or running job follows the existing restart contract and
  becomes `Lost`
- **AND** the owning session receives the existing lost-job notification
- **AND** the daemon does not resume the job with the host temporary root

### Requirement: Worktrees have a separate session-owned area

The system SHALL distinguish managed worktrees from ordinary temporary files.
It SHALL expose `<session-envelope>/worktrees` as `worktree_dir` in the existing
session context and SHALL NOT place that directory below a run's managed
temporary directory. Agents SHALL use existing shell and project-scope tools
to create and adopt worktrees. This capability SHALL NOT define automatic
worktree cleanup or a worktree-specific tool.

#### Scenario: Example - worktree stays outside run temp

- **GIVEN** an agent needs a Git worktree
- **WHEN** it chooses a destination below the announced `worktree_dir`
- **THEN** it is below the session worktree area
- **AND** it is not below the parent or child temporary directory

#### Scenario: Counterexample - session end does not delete worktree

- **GIVEN** a managed worktree contains source changes
- **WHEN** the session ends
- **THEN** this capability does not delete the worktree
- **AND** later cleanup requires a separate policy

#### Scenario: Counterexample - worktree directory is not a temporary API root

- **GIVEN** a child process calls a standard temporary-path API
- **WHEN** Netclaw has injected the run environment
- **THEN** the API resolves below `temp_dir`
- **AND** it does not resolve below `worktree_dir`
