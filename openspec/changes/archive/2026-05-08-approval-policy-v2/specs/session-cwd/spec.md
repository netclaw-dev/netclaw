## ADDED Requirements

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

`ShellTool` SHALL include a one-line hint in the tool result returned
to the model when a call is denied because its cwd is outside both
`session_dir` and `project_dir`. The hint SHALL suggest
`set_working_directory <path>` with the path that triggered the denial,
in a format recognizable to the agent so it can self-correct without a
roundtrip through the user.

The hint SHALL only be emitted when the denial reason is "cwd outside
safe spaces" and `set_working_directory` is in the audience's tool
exposure list. The hint SHALL NOT be emitted for hard-deny-list refusals
or for `ToolPathPolicy` denials (those have different remediation paths).

#### Scenario: Denial in foreign tree includes set_working_directory hint

- **GIVEN** a Personal session with `project_dir` not set
- **WHEN** the agent invokes `shell_execute` with cwd `~/repos/bar/`
- **AND** the user denies the resulting prompt
- **THEN** the tool result includes a hint pointing at
  `set_working_directory ~/repos/bar/`

#### Scenario: Hint is not emitted for hard-deny refusals

- **GIVEN** a hard-deny-list block on the command
- **WHEN** `shell_execute` returns the deny error
- **THEN** the result does NOT include a `set_working_directory` hint

#### Scenario: Hint is not emitted when set_working_directory is unavailable

- **GIVEN** a Public session where `set_working_directory` is not in
  the tool exposure list
- **WHEN** a shell call is denied for cwd-outside-safe-space
- **THEN** the result does NOT include a `set_working_directory` hint

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

## MODIFIED Requirements

### Requirement: set_working_directory tool

The system SHALL provide a `set_working_directory` tool that sets the
session's project directory to a specified path AND expands the
approval gate's safe-space root set for Personal and Team audiences.
The tool SHALL validate that the target path is a real directory,
resolve it to an absolute path, and validate it against the audience
trust profile's read-allowed roots. The tool SHALL be profile-managed
so that audiences without directory navigation privileges (Public,
Team by default) cannot use it.

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
- **AND** the approval safe-space root for shell invocations switches
  to the new project directory
