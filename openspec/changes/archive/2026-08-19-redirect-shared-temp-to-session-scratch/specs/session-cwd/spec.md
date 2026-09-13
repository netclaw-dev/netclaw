## ADDED Requirements

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

## MODIFIED Requirements

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
