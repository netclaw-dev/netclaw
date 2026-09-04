## Purpose

Define how a session tracks its project directory and how the agent
declares it via `set_working_directory`. A valid declaration adds the project
directory to the trusted roots that reviewed-safe policy uses.
## Requirements

### Requirement: Relative first-party filesystem paths use session-owned bases

First-party filesystem tools SHALL resolve a relative path against the declared
project directory when one exists. Otherwise, they SHALL use the immutable
session directory. If neither base exists, they SHALL return an
`invalid_context` correction. They SHALL NOT use the daemon process current
directory. The system SHALL request a path access decision for the canonical
path and requested file operation. The decision includes protected-path checks.

#### Scenario: Relative read uses declared project

- **GIVEN** a project directory `/workspace/project` and session `/session/current`
- **WHEN** `file_read` receives `src/App.cs`
- **THEN** it authorizes and reads `/workspace/project/src/App.cs`
- **AND** it does not use the daemon current directory

#### Scenario: Relative write falls back to the session directory

- **GIVEN** no declared project and session directory `/session/current`
- **WHEN** `file_write` receives `notes/result.md`
- **THEN** it resolves `/session/current/notes/result.md`
- **AND** the existing session write policy decides authorization

#### Scenario: Traversal receives no implicit authority

- **GIVEN** project directory `/workspace/project`
- **WHEN** a file tool receives `../../outside.txt`
- **THEN** it canonicalizes the result before policy evaluation
- **AND** it denies the call when the path is outside trusted roots

#### Scenario: Missing base returns correction

- **GIVEN** a tool context has no project or session directory
- **WHEN** a first-party filesystem tool receives a relative path
- **THEN** it returns `invalid_context`
- **AND** it performs no filesystem access

### Requirement: Failed filesystem operations do not change project context

A denied or failed `set_working_directory` or filesystem call SHALL NOT change
the project directory or recent-file context. Only a validated successful
project declaration SHALL replace the project and reload its instructions.

#### Scenario: Denied declaration leaves prior project intact

- **GIVEN** a session declares `/workspace/old`
- **WHEN** `set_working_directory` is denied for `/workspace/new`
- **THEN** the project directory remains `/workspace/old`
- **AND** project instructions are not loaded from the denied path
### Requirement: Session-scoped project directory

Each session SHALL maintain a mutable `ProjectDirectory` in `WorkingContext`
that tracks which project the session is working on. This is the root
directory of the project (where `AGENTS.md` or `.netclaw/AGENTS.md` lives).
The project directory SHALL be independent of the immutable session directory.
The session directory provides the default relative-path base. The project
directory SHALL persist in `SessionSnapshot` through `WorkingContext`. It SHALL
survive compaction, actor recovery, and daemon restart.

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
session's project directory to a specified path. A successful declaration adds
that directory to the trusted roots for Personal and Team audiences.
The tool SHALL validate that the target path is a real directory,
resolve it to a canonical path, and request a path access decision for the read
file operation.
The tool SHALL be profile-managed
so that audiences without directory navigation privileges (Public,
Team by default) cannot use it. The working-directory declaration is
deliberately NOT granted interactive Personal shell-equivalent reach
(netclaw-dev/netclaw#1724). Every audience and mode SHALL limit declarations
to the session directory, project directory, and configured global read roots.
A declaration changes the roots that reviewed-safe policy uses. It also
loads project identity files into the system prompt.

The model-visible tool description SHALL tell the agent to declare its project
root. The declaration adds the project directory as a trusted root for
reviewed-safe shell policy. The description SHALL NOT present the tool as a
`cd`-style cwd change. The declaration tells Netclaw which project the agent
uses. This declaration can reduce approval prompts for project-scoped work.

#### Scenario: set_working_directory updates project directory

- **GIVEN** a session with no project directory set
- **AND** the audience trust profile allows reads under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/workspaces/akadonic`
- **THEN** the session project directory is set to
  `/home/user/workspaces/akadonic`
- **AND** the project's identity file is loaded on the next LLM call
- **AND** subsequent shell calls with cwd inside that directory may receive
  reviewed-safe coverage

#### Scenario: set_working_directory rejected outside trusted roots

- **GIVEN** a session with audience profile allowing reads only under
  `/home/user`
- **WHEN** the agent invokes `set_working_directory` with path `/etc/nginx`
- **THEN** the project directory remains unchanged
- **AND** the tool returns an error indicating the path is outside trusted
  roots

#### Scenario: set_working_directory rejected for nonexistent directory

- **GIVEN** a session with audience profile allowing reads under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/nonexistent`
- **THEN** the project directory remains unchanged
- **AND** the tool returns an error indicating the directory does not exist

#### Scenario: Personal audience limits project declaration to trusted roots

- **GIVEN** a session with personal audience (`ToolFilesystemMode.All`)
- **AND** the target directory is outside the trusted roots for project
  declaration
  (session directory, project directory, and configured global read roots)
- **WHEN** the agent invokes `set_working_directory` with that valid directory
- **THEN** the project directory is NOT updated
- **AND** the tool returns an error indicating the target is outside the
  session, project, or configured trusted roots
- **AND** `file_read` / `file_list` / `attach_file` on the same path still
  resolve (interactive Personal shell-equivalent reach, netclaw-dev/netclaw#1724)

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
- **AND** the project trusted root for shell invocations switches
  to the new project directory

### Requirement: Shell tool cwd selection is explicit and deterministic

`ShellTool` SHALL resolve the working directory for every invocation
in this priority order: explicit `WorkingDirectory` argument when
provided, else `WorkingContext.ProjectDirectory` when set, else
`session_dir` (`<session-envelope>/workspace` for a version-2 session).
`ShellTool` SHALL NOT fall
through to `ProcessStartInfo`'s default behavior of inheriting the
daemon process's cwd.

This rule gives each shell invocation a known cwd below a declared trusted
root, unless the call supplies an explicit override. The approval policy uses
the applicable path access decision for that cwd.

#### Scenario: Cwd defaults to project_dir when set

- **GIVEN** a session with `WorkingContext.ProjectDirectory` set to
  `~/repos/foo/`
- **WHEN** the agent invokes `shell_execute` with command `pwd` and
  no `WorkingDirectory`
- **THEN** the command runs with cwd `~/repos/foo/`

#### Scenario: Cwd defaults to session_dir when project_dir is null

- **GIVEN** a session with `WorkingContext.ProjectDirectory` null
- **WHEN** the agent invokes `shell_execute` with command `pwd` and
  no `WorkingDirectory`
- **THEN** the command runs with cwd `<session-envelope>/workspace`

#### Scenario: Explicit WorkingDirectory overrides default

- **GIVEN** a session with `WorkingContext.ProjectDirectory` set to
  `~/repos/foo/`
- **WHEN** the agent invokes `shell_execute` with command `pwd` and
  `WorkingDirectory` `/tmp/`
- **THEN** the command runs with cwd `/tmp/`
- **AND** the approval gate requests a path access decision for `/tmp/`

#### Scenario: Cwd never inherits daemon process cwd

- **GIVEN** the daemon process was launched with cwd `/var/lib/netclawd/`
- **AND** a session has neither `project_dir` set nor an explicit
  `WorkingDirectory` argument
- **WHEN** the agent invokes `shell_execute`
- **THEN** the command does NOT run with cwd `/var/lib/netclawd/`
- **AND** the resolved cwd is `session_dir`

### Requirement: Shell tool failure-path hint for cwd outside trusted roots

`ShellTool` SHALL include a one-line remediation hint in the model result when
a call is denied because its cwd is outside trusted roots. The remediation
presenter SHALL add the hint only when an applicable remediation is available.

For a non-temp cwd, the hint SHALL suggest `set_working_directory <path>` only
when that tool is exposed. The same path access decision used by that tool
SHALL accept the exact path without substitution. For a Personal cwd at the
captured platform temporary root, the hint SHALL identify the managed
temporary directory. It SHALL NOT suggest a project declaration for the
platform temporary root. Team and Public calls retain their earlier denial
boundary. Public results retain existing path redaction.

The hint SHALL NOT appear for hard-deny-list refusals or protected-path
denials. It SHALL NOT appear when the managed temporary directory is
unavailable. It SHALL NOT appear when `set_working_directory` would reject a
non-temp cwd.

#### Scenario: Denial in declarable foreign tree includes set_working_directory hint

- **GIVEN** a Personal session with `project_dir` not set
- **AND** the shared directory policy accepts `~/repos/bar/`
- **WHEN** the agent invokes `shell_execute` with cwd `~/repos/bar/`
- **AND** the user denies the resulting prompt
- **THEN** the tool result includes a hint pointing at `set_working_directory ~/repos/bar/`

#### Scenario: Denied platform-temp retry retains managed-temp recommendation

- **GIVEN** an agent received the managed-temp remediation for the platform
  temporary root
- **AND** it repeated the original call unchanged to request ordinary approval
- **WHEN** the user denies that approval
- **THEN** the remediation presenter identifies the exact managed temporary
  directory
- **AND** it does not suggest `set_working_directory` for the platform temporary root

#### Scenario: Undeclarable foreign tree has no project declaration hint

- **GIVEN** a non-temp cwd is outside the roots accepted by `set_working_directory`
- **WHEN** the user denies the resulting shell prompt
- **THEN** the tool result does not suggest declaring that cwd

#### Scenario: Hint is not emitted for hard-deny refusals

- **GIVEN** a hard-deny-list block on the command
- **WHEN** `shell_execute` returns the deny error
- **THEN** the result does NOT include a working-directory remediation hint

#### Scenario: Hint is not emitted when remediation tools are unavailable

- **GIVEN** a Public session where `set_working_directory` is not exposed
- **AND** no private managed-temp remediation is available
- **WHEN** a shell call is denied because its cwd is outside trusted roots
- **THEN** the result does NOT include a working-directory remediation hint

### Requirement: set_working_directory adds a project trusted root

Setting `WorkingContext.ProjectDirectory` SHALL add that directory to the
trusted roots for Personal and Team audiences. A later shell invocation below
that root can receive reviewed-safe coverage. The reviewed-safe
catalog and link checks still apply. Public audiences SHALL NOT receive
`set_working_directory`, and their trusted roots SHALL remain unchanged.

This requirement defines the dependency between `session-cwd` and
`tool-approval-gates`. A project declaration supplies a trusted root. The path
access decision remains the authority owner.

#### Scenario: Setting project_dir relaxes future approval prompts

- **GIVEN** a Personal session with `project_dir` initially null
- **AND** the agent has previously been denied `grep` calls in
  `~/repos/foo/`
- **WHEN** the agent calls `set_working_directory ~/repos/foo/`
- **AND** the agent retries `grep -r "x" .` with cwd `~/repos/foo/`
- **THEN** reviewed-safe policy covers the call below the trusted root
- **AND** no prompt is rendered

#### Scenario: Public audience does not get a project trusted root

- **GIVEN** a Public session
- **WHEN** the tool exposure list is computed
- **THEN** `set_working_directory` is not included
- **AND** its trusted roots remain unchanged

### Requirement: Working context block includes project directory

The system SHALL include the current project directory in the
`[working-context]` block emitted by `WorkingContext.ToContextBlock()`
when the project directory is set.

#### Scenario: Project directory included in working context block

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic` and recent files
- **WHEN** `ToContextBlock()` is called
- **THEN** the output includes `project_dir: /home/user/workspaces/akadonic`
  alongside the recent files listing

### Requirement: Working context includes derived Git worktree state
For Team and Personal turns whose `WorkingContext.ProjectDirectory` is declared and Git identifies it as a worktree, the system SHALL asynchronously derive a fresh Git snapshot at turn start and render it as a nested section of `[working-context]`. The snapshot SHALL include worktree root, common repository directory, branch or detached state, HEAD, upstream and ahead/behind when configured, and staged, modified, and untracked counts. Derived Git state SHALL NOT be persisted in session state. Git inspection SHALL return explicit available, not-repository, executable-not-found, or unavailable outcomes.

#### Scenario: Linked worktree is distinguished from common repository
- **GIVEN** a Team or Personal session project directory inside a linked Git worktree
- **WHEN** the next turn-start working-context snapshot is built
- **THEN** the model-visible context identifies the linked worktree path and common repository directory
- **AND** reports the linked worktree's branch and HEAD

#### Scenario: Git state refreshes on the next turn
- **GIVEN** a tool changes branch, HEAD, or dirty state during one turn
- **WHEN** the session begins its next turn
- **THEN** the new volatile working-context nudge contains the updated Git snapshot
- **AND** earlier history messages are not rewritten

#### Scenario: Non-Git project has no Git section
- **GIVEN** a declared project directory that Git identifies as not a repository
- **WHEN** working context is assembled
- **THEN** normal project and recent-file context remains available
- **AND** no Git section is rendered

#### Scenario: Git inspection failure is visible
- **GIVEN** an eligible project directory whose Git state cannot be inspected because Git is missing, times out, or the repository is invalid
- **WHEN** working context is assembled
- **THEN** Git status is reported as unavailable with a sanitized reason
- **AND** the failure is not represented as a clean or non-Git worktree

#### Scenario: Stale Git result is discarded
- **GIVEN** asynchronous Git inspection began for an earlier turn generation
- **WHEN** its result arrives after the session has advanced to another turn
- **THEN** the actor discards the stale result
- **AND** it is not rendered into the active turn

#### Scenario: Git remote credentials are never rendered
- **GIVEN** a repository with a credential-bearing remote URL
- **WHEN** Git working context is rendered
- **THEN** no remote credentials or complete remote URL appears in model-visible context or logs

### Requirement: Project-directory declarations reject control characters

The `set_working_directory` tool SHALL reject a path that contains NUL, CR, or
LF before filesystem resolution. The tool SHALL return a bounded error without
echoing the authored path.

#### Scenario: Controlled path cannot become project scope

- **GIVEN** a path contains NUL, CR, or LF
- **WHEN** an agent calls `set_working_directory` with that path
- **THEN** the tool returns an error without the authored path
- **AND** the project scope remains unchanged
- **AND** project instructions are not loaded from that path

### Requirement: Existing session context announces managed paths

The system SHALL preserve the existing `[session]` context block and its
`session_dir` entry. It SHALL extend that block with the applicable `temp_dir`,
`artifact_dir`, `worktree_dir`, and `log_path` entries. This rule applies to
Personal and Team parent and child runs. The system SHALL NOT add a second
context block or repeat these paths in per-turn guidance.

The system SHALL state the distinct purpose of each path. Public context SHALL
retain its existing private-path policy. The guidance SHALL preserve an
explicitly required platform temporary path.

The context SHALL derive from the existing parent or child run scope. It SHALL
NOT add a public protocol field or persist a path as agent identity. It SHALL
NOT change shell authorization.

For a parent with a version-2 storage binding, `session_dir` SHALL mean
`<session-envelope>/workspace`. For a child, `temp_dir` and `artifact_dir`
SHALL be siblings below `<session-envelope>/subagents/<run-id>`. The context
SHALL NOT use `session_dir` as a synonym for the complete storage envelope.
It SHALL describe `session_dir` as the working directory and relative-path
fallback. It SHALL describe `temp_dir` as disposable run-local storage.

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
- **AND** it does not describe the session directory as disposable storage

#### Scenario: Counterexample - disposable guidance cannot point to session_dir

- **GIVEN** the model needs a location for disposable run-local output
- **WHEN** Netclaw renders managed-path guidance
- **THEN** the guidance points to `temp_dir`
- **AND** it does not tell the model to use `session_dir` for disposable output

### Requirement: Managed temporary directory is the private temporary location

The system SHALL provide a separate managed temporary directory for each
parent and child run. A version-2 parent SHALL use
`<session-envelope>/tmp/parent`. A child SHALL use
`<session-envelope>/subagents/<run-id>/tmp`. The system SHALL identify this
directory as the preferred location for disposable files. It SHALL use the
session directory as the shell working-directory fallback when no other cwd
exists. It SHALL NOT use the complete envelope as that fallback.

Personal and Team working context and correction text SHALL provide the
absolute managed temporary path when the agent needs an alternative to the
platform temporary root. Public context SHALL retain existing private-path
redaction. The system SHALL NOT silently substitute a path. It SHALL NOT imply
that this behavior deletes temporary files.

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
session context. It SHALL NOT place that directory below a run's managed
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
