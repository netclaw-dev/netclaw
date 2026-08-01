## MODIFIED Requirements

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the Netclaw process user context through the canonical platform shell: `/bin/bash` on Unix-like hosts and PowerShell 7 (`pwsh`) on Windows. Stdin SHALL be closed (no interactive commands). Execution SHALL enforce a configurable timeout (default: 60 seconds). The tool SHALL drain stdout and stderr in bounded memory (each to the capture ceiling `ToolConfig.MaxOutputChars`) and return the combined output bounded to the ceiling — it does NOT itself window, redact, or spill (the central `bounded-tool-output` mechanism does, after redaction). `shell_execute` SHALL declare a small verbose inline budget (`InlineOutputBudgetChars`) so its skimmable output is bounded aggressively. Before execution, the shell tool SHALL structurally parse the command with the canonical grammar and check every executable clause against `ShellCommandPolicy`; hard-denied commands SHALL be rejected before `ToolPathPolicy` path checks. A missing canonical shell SHALL fail visibly without fallback.

#### Scenario: Execute command and return output

- **GIVEN** the `shell` grant is available for the session
- **WHEN** the agent invokes the shell tool with a command in the canonical grammar
- **THEN** the command is executed as the Netclaw process user through the canonical shell
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with a pipeline containing `netclaw daemon stop`
- **WHEN** `ShellCommandPolicy` evaluates every executable clause
- **THEN** the command is rejected with "Command blocked by hard deny policy"
- **AND** the shell process is never started

#### Scenario: Execution timeout enforced

- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 60 seconds)
- **THEN** the process is terminated
- **AND** the tool returns a timeout error message to the LLM

#### Scenario: Combined output bounded by the capture ceiling

- **GIVEN** a shell command writes large output to both stdout and stderr
- **WHEN** the output is captured
- **THEN** the returned combined output is bounded by `MaxOutputChars` (one shared ceiling, not a per-stream cap)
- **AND** the dispatcher applies the inline budget + spill + steer on top (per `bounded-tool-output`)

#### Scenario: Stdin closed prevents interactive commands

- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path

- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path

#### Scenario: Missing PowerShell fails without cmd fallback

- **GIVEN** Netclaw runs on Windows and `pwsh` is unavailable
- **WHEN** the agent invokes `shell_execute`
- **THEN** the tool returns an actionable PowerShell 7 unavailable error
- **AND** neither `cmd.exe` nor `powershell.exe` is started

## ADDED Requirements

### Requirement: Shell approvals cover every executable clause

Approval candidate extraction and persisted approval matching SHALL use the canonical grammar and SHALL include every executable pipeline clause in order. Persisted approval candidates SHALL retain platform-correct path and case comparison behavior.

#### Scenario: Approved head does not authorize tail

- **GIVEN** a persisted approval matches the head of a pipeline
- **AND** the pipeline tail has a different executable candidate
- **WHEN** approval policy evaluates the command
- **THEN** the tail remains unapproved
- **AND** execution requires a new approval or is denied according to policy

#### Scenario: Windows scoped approval round trip

- **GIVEN** a directory-scoped PowerShell approval is persisted on Windows
- **WHEN** it is loaded and compared with a later command in the approved directory
- **THEN** the stored canonical representation matches using Windows path and case semantics
- **AND** a command outside the approved directory does not match
