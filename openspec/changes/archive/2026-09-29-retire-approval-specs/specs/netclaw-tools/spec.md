## MODIFIED Requirements

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout: `Session.ToolExecutionTimeoutSeconds`
(default: 90 seconds), or the agent's per-call timeout hint when present. The tool
SHALL drain stdout and stderr in bounded memory (each to the capture ceiling
`ToolConfig.MaxOutputChars`) and return the combined output bounded to the
ceiling — it does NOT itself window, redact, or spill (the central
`bounded-tool-output` mechanism does, after redaction). `shell_execute` SHALL
declare a small verbose inline budget (`InlineOutputBudgetChars`) so its skimmable
output is bounded aggressively. Authorization, including hard deny and the
launch re-check, SHALL complete before the process starts (`tool-authorization`
TA-5 and TA-14).

#### Scenario: Execute command and return output

- **GIVEN** the audience and shell mode admit `shell_execute` for the session
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with `netclaw daemon stop`
- **WHEN** authorization evaluates the command
- **THEN** the tool result is `Tool access denied: hard_deny_self_destructive`
- **AND** the shell process is never started

#### Scenario: Execution timeout enforced

- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 90 seconds)
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

## REMOVED Requirements

### Requirement: Policy-gated tool invocation

**Reason**: `tool-authorization` restates the gate order and the four outcomes in TA-3, TA-4, TA-5, TA-6, TA-9, and TA-10, and the log lines in TA-15. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-3 to TA-10 and TA-15.

### Requirement: Tool execution context carries a parsed audience

**Reason**: `tool-authorization` restates this rule in TA-1. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-1.

### Requirement: Session file authority

**Reason**: `tool-authorization` restates this rule in TA-6. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-6.

### Requirement: File protection is an inner tool-policy layer

**Reason**: `tool-authorization` restates this rule in TA-6. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-6.

### Requirement: Ordinary configuration is readable without exposing secrets

**Reason**: `tool-authorization` restates this rule in TA-6. That capability is the single owner of tool authorization rules. It states that ordinary `netclaw.json` is readable while secret stores stay read-denied.

**Migration**: Use `tool-authorization` TA-6.

### Requirement: Working directory declaration stays scoped

**Reason**: `session-cwd` Requirement "set_working_directory tool" owns the declaration contract, and `tool-authorization` TA-6 owns the `DeclareProjectScope` path access decision.

**Migration**: Use `session-cwd` "set_working_directory tool" and `tool-authorization` TA-6.
