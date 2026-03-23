## MODIFIED Requirements

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool with explicit policy modes `off`, `sandbox-only`, and `host-allowed`. `sandbox-only` SHALL execute commands through a configured isolated runner and SHALL remain unavailable until the backend validates successfully. `host-allowed` SHALL execute commands as the Netclaw process user context. Stdin SHALL be closed (no interactive commands). Execution SHALL enforce a configurable timeout (default: 60 seconds). Output SHALL be truncated to a configurable limit.

#### Scenario: Execute command and return output in host mode
- **GIVEN** the `shell` grant is available for the session
- **AND** shell mode resolves to `host-allowed`
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Execute command and return output in sandbox mode
- **GIVEN** the `shell` grant is available for the session
- **AND** shell mode resolves to `sandbox-only`
- **AND** the sandbox backend is healthy
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command executes inside the configured isolated runner
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Sandbox backend unavailable blocks execution
- **GIVEN** shell mode resolves to `sandbox-only`
- **WHEN** the sandbox backend is unavailable or misconfigured
- **THEN** the shell tool returns a sandbox backend error
- **AND** the command is not executed on the host

#### Scenario: Execution timeout enforced
- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 60 seconds)
- **THEN** the process or sandbox invocation is terminated
- **AND** the tool returns a timeout error message to the LLM

#### Scenario: Output truncated to limit
- **GIVEN** a shell command produces output exceeding the configured limit
- **WHEN** the output is captured
- **THEN** the output is truncated to the configured character limit
- **AND** a truncation indicator is appended

#### Scenario: Stdin closed prevents interactive commands
- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process or sandbox invocation is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path
- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path or its mounted sandbox equivalent

### Requirement: Policy-gated tool invocation

The system SHALL check ACL grants before every tool execution. Tool invocations SHALL be logged with audit records including tool name, invoking session, timestamp, and allow/deny result.

#### Scenario: Granted tool executes successfully
- **GIVEN** the session has an ACL grant for `web_search`
- **WHEN** the LLM requests a web search tool call
- **THEN** the ACL check passes
- **AND** the tool executes
- **AND** an audit record is logged with tool name, session ID, timestamp, and `allow` result

#### Scenario: Ungrantable tool denied at invocation
- **GIVEN** the session does not have an ACL grant for `shell`
- **WHEN** the LLM requests a shell tool call
- **THEN** the ACL check fails
- **AND** the tool is not executed
- **AND** a policy denial with reason code is returned to the LLM
- **AND** an audit record is logged with tool name, session ID, timestamp, and `deny` result

#### Scenario: Audit records available in diagnostics
- **GIVEN** tool invocations have occurred
- **WHEN** the operator views diagnostics
- **THEN** audit records show tool name, invoking session, timestamp, and allow/deny result for each invocation

#### Scenario: Sandbox failure does not widen to host execution
- **GIVEN** the session is authorized for shell execution only through `sandbox-only`
- **WHEN** sandbox launch fails after the tool invocation begins
- **THEN** the tool returns the sandbox failure details to the session
- **AND** Netclaw does not retry the command via `host-allowed`
