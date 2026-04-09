## MODIFIED Requirements

### Requirement: Policy-gated tool invocation

The system SHALL check ACL grants and approval policy before every tool
execution. Tool invocations SHALL be logged with audit records including tool
name, invoking session, timestamp, allow/deny/approval result, and approval
decision details when applicable. The `ToolAccessDecision` SHALL support three
outcomes: `Allow`, `Deny(reason)`, and `RequiresApproval(context)`.

When `RequiresApproval` is returned, the tool execution pipeline SHALL pause
the individual tool task and emit a `ToolInteractionRequest` to session
subscribers. The pipeline SHALL NOT block other tool calls in the same batch.

#### Scenario: Granted tool executes successfully

- **GIVEN** the session has an ACL grant for `web_search`
- **AND** `web_search` is in Auto approval mode
- **WHEN** the LLM requests a web search tool call
- **THEN** the ACL check passes
- **AND** the tool executes
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  `allow` result

#### Scenario: Ungrantable tool denied at invocation

- **GIVEN** the session does not have an ACL grant for `shell`
- **WHEN** the LLM requests a shell tool call
- **THEN** the ACL check fails
- **AND** the tool is not executed
- **AND** a policy denial with reason code is returned to the LLM
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  `deny` result

#### Scenario: Tool requires approval and is approved

- **GIVEN** the session has an ACL grant for `shell`
- **AND** `shell_execute` is in Approval mode for the session's audience
- **AND** the command pattern is not already approved in `IToolApprovalService`
- **WHEN** the LLM requests a shell tool call
- **THEN** `ToolAccessPolicy` returns `RequiresApproval`
- **AND** `DispatchingToolExecutor` consults `IToolApprovalService`
- **AND** the pipeline emits a `ToolInteractionRequest` and pauses the task
- **AND** when the user approves, the tool executes
- **AND** an audit record is logged with `approved` result

#### Scenario: Tool requires approval and is denied by user

- **GIVEN** the pipeline has emitted an approval prompt
- **WHEN** the user denies
- **THEN** the tool result is "Command denied by user"
- **AND** an audit record is logged with `denied_by_user` result

#### Scenario: Audit records available in diagnostics

- **GIVEN** tool invocations have occurred
- **WHEN** the operator views diagnostics
- **THEN** audit records show tool name, invoking session, timestamp, and
  allow/deny/approval result for each invocation

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout (default: 60 seconds). Output
SHALL be truncated to a configurable limit. Before execution, the shell tool
SHALL check the hard deny list via `ShellCommandPolicy`. Hard-denied commands
SHALL be rejected before `ToolPathPolicy` path checks.

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

#### Scenario: Output truncated to limit

- **GIVEN** a shell command produces output exceeding the configured limit
- **WHEN** the output is captured
- **THEN** the output is truncated to the configured character limit
- **AND** a truncation indicator is appended

#### Scenario: Stdin closed prevents interactive commands

- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path

- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path
