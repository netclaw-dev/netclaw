## Purpose

Define how a session tracks its project directory and how the agent
declares it via `set_working_directory`. The project directory is the
load-bearing input to the approval gate's safe-space root set: declaring
it expands the trust boundary for shell invocations under that tree.
## Requirements

### Requirement: Relative first-party filesystem paths use session-owned bases

First-party filesystem tools SHALL resolve a relative path against the declared
project directory when one exists. Otherwise, they SHALL use the immutable
session directory. If neither base exists, they SHALL return an
`invalid_context` correction. They SHALL NOT use the daemon process current
directory. The canonical path SHALL pass existing scope and protected-path
policies.

#### Scenario: Relative read uses declared project

- **GIVEN** a project directory `/workspace/project` and session `/session/current`
- **WHEN** `file_read` receives `src/App.cs`
- **THEN** it authorizes and reads `/workspace/project/src/App.cs`
- **AND** it does not use the daemon current directory

#### Scenario: Relative write falls back to session scratch

- **GIVEN** no declared project and session directory `/session/current`
- **WHEN** `file_write` receives `notes/result.md`
- **THEN** it resolves `/session/current/notes/result.md`
- **AND** the existing session write policy decides authorization

#### Scenario: Traversal receives no implicit authority

- **GIVEN** project directory `/workspace/project`
- **WHEN** a file tool receives `../../outside.txt`
- **THEN** it canonicalizes the result before policy evaluation
- **AND** it denies the call when the path is outside authorized roots

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
session's project directory to a specified path AND expands the
approval gate's safe-space root set for Personal and Team audiences.
The tool SHALL validate that the target path is a real directory,
resolve it to an absolute path, and validate it against the audience
trust profile's read-allowed roots. The tool SHALL be profile-managed
so that audiences without directory navigation privileges (Public,
Team by default) cannot use it. The working-directory declaration is
deliberately NOT granted interactive Personal shell-equivalent reach
(netclaw-dev/netclaw#1724): it SHALL be clamped to the autonomous zone
(session directory, project directory, and configured global read
roots) in every audience and mode, because declaring a working
directory widens the shell safe-verb auto-approve zone and loads
project identity files into the system prompt.

The tool description visible to the model SHALL frame the tool as
"declare your project root and expand your trusted scope so shell
commands inside that tree run without per-command approval" rather
than as a `cd`-style cwd change. Calling this tool is the load-bearing
gesture by which the agent signals what it is working on; the agent's
approval friction depends on doing so when the work is project-scoped.

#### Scenario: set_working_directory updates project directory

- **GIVEN** a session with no project directory set
- **AND** the audience trust profile allows reads under `/home/user`
- **WHEN** the agent invokes `set_working_directory` with
  path `/home/user/workspaces/akadonic`
- **THEN** the session project directory is set to
  `/home/user/workspaces/akadonic`
- **AND** the project's identity file is loaded on the next LLM call
- **AND** subsequent shell calls with cwd inside that directory may
  participate in the safe-verb auto-allow short-circuit

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

#### Scenario: Personal audience clamps to the autonomous zone

- **GIVEN** a session with personal audience (`ToolFilesystemMode.All`)
- **AND** the target directory is outside the autonomous zone
  (session directory, project directory, and configured global read roots)
- **WHEN** the agent invokes `set_working_directory` with that valid directory
- **THEN** the project directory is NOT updated
- **AND** the tool returns an error indicating the target is outside the
  session, project, or configured autonomous roots
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
- **AND** the approval safe-space root for shell invocations switches
  to the new project directory

### Requirement: Shell tool cwd defaults to declared safe spaces

`ShellTool` SHALL resolve the working directory for every invocation
in this priority order: explicit `WorkingDirectory` argument when
provided, else `WorkingContext.ProjectDirectory` when set, else
`session_dir` (the per-session directory under
`~/.netclaw/sessions/<session-id>/`). `ShellTool` SHALL NOT fall
through to `ProcessStartInfo`'s default behavior of inheriting the
daemon process's cwd.

This guarantees every shell invocation has a known cwd parented under a
declared safe space (or an explicit override), which is the precondition
the approval policy depends on.

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
- **THEN** the command runs with cwd `~/.netclaw/sessions/<session-id>/`

#### Scenario: Explicit WorkingDirectory overrides default

- **GIVEN** a session with `WorkingContext.ProjectDirectory` set to
  `~/repos/foo/`
- **WHEN** the agent invokes `shell_execute` with command `pwd` and
  `WorkingDirectory` `/tmp/`
- **THEN** the command runs with cwd `/tmp/`
- **AND** the approval gate evaluates safe-space membership against `/tmp/`

#### Scenario: Cwd never inherits daemon process cwd

- **GIVEN** the daemon process was launched with cwd `/var/lib/netclawd/`
- **AND** a session has neither `project_dir` set nor an explicit
  `WorkingDirectory` argument
- **WHEN** the agent invokes `shell_execute`
- **THEN** the command does NOT run with cwd `/var/lib/netclawd/`
- **AND** the resolved cwd is `session_dir`

### Requirement: Shell tool failure-path hint for cwd outside safe spaces

`ShellTool` SHALL include a one-line remediation hint in the tool result returned to the model when a call is denied because its cwd is outside both `session_dir` and `project_dir` and a safe correction is available.

For a non-temp cwd, the hint SHALL suggest `set_working_directory <path>` only when that tool is exposed and the same filesystem policy used by `set_working_directory` accepts the exact path without substitution. For a Personal cwd equal to the captured platform temporary root, the hint SHALL instead identify the exact session directory as private scratch and SHALL NOT suggest declaring the platform temporary root. Team and Public shell calls retain their existing earlier denial boundary, and Public results SHALL retain existing path redaction.

The hint SHALL NOT be emitted for hard-deny-list refusals, `ToolPathPolicy` denials, an unavailable session scratch path, or a foreign non-temp cwd that `set_working_directory` would reject.

#### Scenario: Denial in declarable foreign tree includes set_working_directory hint

- **GIVEN** a Personal session with `project_dir` not set
- **AND** the shared directory policy accepts `~/repos/bar/`
- **WHEN** the agent invokes `shell_execute` with cwd `~/repos/bar/`
- **AND** the user denies the resulting prompt
- **THEN** the tool result includes a hint pointing at `set_working_directory ~/repos/bar/`

#### Scenario: Denied platform-temp retry retains scratch recommendation

- **GIVEN** an agent received the session-scratch correction for the platform temporary root
- **AND** it repeated the original call unchanged to request ordinary approval
- **WHEN** the user denies that approval
- **THEN** the tool result identifies the exact session directory as private scratch
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
- **AND** no private session-scratch correction is available
- **WHEN** a shell call is denied for cwd-outside-safe-space
- **THEN** the result does NOT include a working-directory remediation hint

### Requirement: set_working_directory expands the approval safe space

Setting `WorkingContext.ProjectDirectory` SHALL expand the approval gate's
safe-space root set for Personal and Team audiences: subsequent shell
invocations whose cwd resolves under the new project directory SHALL
participate in the safe-verb auto-allow short-circuit (subject to the
safe-verbs list and symlink-segment guard). For Public audience,
`set_working_directory` SHALL NOT be available and the safe space SHALL
remain `session_dir` only.

This requirement formalizes the dependency between session_cwd and
tool-approval-gates: the act of declaring the project root is the act
of opening the approval trust boundary.

#### Scenario: Setting project_dir relaxes future approval prompts

- **GIVEN** a Personal session with `project_dir` initially null
- **AND** the agent has previously been denied `grep` calls in
  `~/repos/foo/`
- **WHEN** the agent calls `set_working_directory ~/repos/foo/`
- **AND** the agent retries `grep -r "x" .` with cwd `~/repos/foo/`
- **THEN** the approval gate short-circuits (safe verb in safe space)
- **AND** no prompt is rendered

#### Scenario: Public audience does not get safe-space expansion

- **GIVEN** a Public session
- **WHEN** the tool exposure list is computed
- **THEN** `set_working_directory` is not included
- **AND** the only safe space remains `session_dir`

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

### Requirement: Subagent context announces private session scratch

Before the first model call, the system SHALL include the exact bound `session_dir` in Personal and Team subagent working context and SHALL identify it as private scratch for disposable artifacts. The guidance SHALL preserve an explicitly required platform temporary path. Public subagent context SHALL NOT include the private session path.

The context SHALL be derived from the child run's existing bound session scope. It SHALL NOT add a public protocol field, persist the path as agent identity, create a second scratch directory, or change shell authorization.

#### Scenario: Personal child receives exact scratch path

- **GIVEN** a Personal subagent has bound session directory `/home/user/.netclaw/sessions/example`
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context contains `session_dir: /home/user/.netclaw/sessions/example`
- **AND** it identifies `session_dir` as the location for disposable artifacts
- **AND** it does not imply that the directory grants shell authority

#### Scenario: Team child receives exact scratch path

- **GIVEN** a Team subagent has a valid bound session directory
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context contains that exact directory as private scratch
- **AND** existing Team tool and shell policy remains unchanged

#### Scenario: Public child retains path redaction

- **GIVEN** a Public subagent has an internal bound session directory
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context does not contain that directory
- **AND** no scratch guidance discloses another private filesystem path

#### Scenario: Explicit platform temporary requirement is preserved

- **GIVEN** a Personal or Team subagent receives scratch guidance
- **WHEN** its task explicitly requires `/tmp` or the native Windows temporary directory
- **THEN** the guidance tells the child to preserve that requirement
- **AND** Netclaw does not rewrite the path or grant authority to it

#### Scenario: Project declaration does not replace session scratch

- **GIVEN** a child has received its initial session scratch context
- **WHEN** it later calls `set_working_directory` successfully
- **THEN** its project scope and project instructions update through the existing contract
- **AND** its bound `session_dir` remains unchanged

### Requirement: Session directory is the private shell scratch location

The system SHALL identify the existing per-session directory as the private scratch location for disposable shell artifacts. Personal and Team model-visible working-context and correction text SHALL provide its absolute path when the agent needs an alternative to the platform temporary root. Public contexts SHALL retain existing path redaction and SHALL NOT receive the private absolute session path. The system SHALL NOT create a second scratch directory, silently substitute the path, or imply that session-directory cleanup occurs as part of this behavior.

#### Scenario: Shell without project scope defaults to session scratch

- **GIVEN** a session has no declared project directory
- **AND** its session directory is `/home/user/.netclaw/sessions/example`
- **WHEN** the agent invokes `shell_execute` without an explicit working directory
- **THEN** the shell working directory is `/home/user/.netclaw/sessions/example`
- **AND** the working context identifies that directory as session scratch

#### Scenario: Scratch recommendation uses the existing session directory

- **GIVEN** a correction recommends private scratch
- **WHEN** the correction is rendered for the agent
- **THEN** it names the exact existing session directory
- **AND** it does not name a newly created `scratch` child directory

#### Scenario: Public context does not receive private scratch path

- **GIVEN** a Public parent agent or subagent
- **WHEN** it evaluates a platform-temp shell call
- **THEN** it does not receive the private session-directory path
- **AND** existing Public path-redaction behavior remains

#### Scenario: Personal and Team headless context nudges scratch use

- **GIVEN** a Personal or Team headless session
- **WHEN** its working context is assembled
- **THEN** the context identifies the exact session directory as private scratch for disposable artifacts
- **AND** it states that an explicitly required platform-temp path must be preserved
- **AND** it does not imply that approval prompts or automatic cleanup exist

#### Scenario: No cleanup is implied

- **GIVEN** an agent writes a disposable artifact under the session directory
- **WHEN** the current session ends
- **THEN** this capability does not delete or schedule deletion of that artifact
- **AND** retention remains unchanged until a separate cleanup capability is specified
