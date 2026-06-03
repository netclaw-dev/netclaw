## MODIFIED Requirements

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout (default: 60 seconds). The tool
SHALL drain stdout and stderr in bounded memory (each to the capture ceiling
`ToolConfig.MaxOutputChars`) and return the combined output bounded to the
ceiling — it does NOT itself window, redact, or spill (the central
`bounded-tool-output` mechanism does, after redaction). `shell_execute` SHALL
declare a small verbose inline budget (`InlineOutputBudgetChars`) so its skimmable
output is bounded aggressively. Before execution, the shell tool SHALL check the
hard deny list via `ShellCommandPolicy`; hard-denied commands SHALL be rejected
before `ToolPathPolicy` path checks.

#### Scenario: Execute command and return output

- **GIVEN** the `shell` grant is available for the session
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with `netclaw daemon stop`
- **WHEN** `ShellCommandPolicy` evaluates the command
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
- **THEN** the returned combined output is bounded by `MaxOutputChars` (one shared
  ceiling, not a per-stream cap)
- **AND** the dispatcher applies the inline budget + spill + steer on top
  (per `bounded-tool-output`)

#### Scenario: Stdin closed prevents interactive commands

- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path

- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path

## ADDED Requirements

### Requirement: File read tool bounds its read for memory safety

The `file_read` tool's default (no `offset`/`limit`) path SHALL read a bounded
head of the file (up to `ToolConfig.MaxOutputChars`) and stop — it SHALL NOT read
the entire file into memory before truncating. The existing line-range
(`offset`/`limit`) path SHALL remain bounded. `file_read` SHALL NOT redact its
result itself; the central `DispatchingToolExecutor` redaction covers it. The
inline bound + spill (if any) is applied centrally per `bounded-tool-output`;
`file_read` is a content tool and uses the session content budget.

#### Scenario: Large file is read in bounded memory

- **WHEN** the agent reads a file larger than the capture ceiling with no
  `offset`/`limit`
- **THEN** the tool reads only a bounded head and does not materialize the whole
  file in memory
- **AND** it appends a steer to read a specific range (`offset`/`limit`) or `grep`

#### Scenario: Secrets in a read file are redacted by the dispatcher

- **GIVEN** a file contains a secret-bearing value (e.g. an API key)
- **WHEN** the agent reads the file
- **THEN** the result returned to the model has the secret redacted (by the
  central dispatcher redaction)
