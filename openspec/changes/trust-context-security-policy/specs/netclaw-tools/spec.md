## MODIFIED Requirements

### Requirement: Tool registration with MEAI

All first-party tools SHALL be registered as `Microsoft.Extensions.AI` tool definitions at startup. Tool metadata (name, description, parameters) SHALL be defined at registration. Available tools presented to the LLM SHALL be filtered per turn based on grants, posture policy, the resolved audience profile, effective trust context, and capability classification.

#### Scenario: Tools registered at startup

- **WHEN** the Netclaw process starts
- **THEN** all configured first-party tools are registered as MEAI tool definitions
- **AND** each tool definition includes name, description, and parameter schema

#### Scenario: Session receives filtered tool set

- **GIVEN** a session has ACL grants for `web_search` and `web_fetch` but not `shell`
- **WHEN** the session starts and tools are provided to the LLM
- **THEN** only `web_search` and `web_fetch` tool definitions are included
- **AND** `shell` is not offered to the LLM

#### Scenario: Tool results returned as tool response messages

- **GIVEN** the LLM issues a tool call during a turn
- **WHEN** the tool executes and produces a result
- **THEN** the result is returned to the LLM as an MEAI tool response message
- **AND** the session continues the turn loop with the tool result in context

#### Scenario: Public-facing turn does not see privileged tool

- **GIVEN** a deployment has `shell` enabled for owner-only personal turns
- **WHEN** a public or team-scoped turn starts
- **THEN** `shell` is omitted from the tool set presented to the model

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool with explicit policy modes `off`, `sandbox-only`, and `host-allowed`. Until sandbox infrastructure exists, `sandbox-only` SHALL remain a reserved policy mode that is not executable. `host-allowed` SHALL only be invocable when both grants and active trust context permit it. Stdin SHALL be closed (no interactive commands). Execution SHALL enforce a configurable timeout (default: 60 seconds). Output SHALL be truncated to a configurable limit.

#### Scenario: Execute command and return output

- **GIVEN** the `shell` grant is available for the session
- **AND** shell mode is `host-allowed`
- **WHEN** the agent invokes the shell tool with a command during an allowed trust context
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

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

#### Scenario: Public-tainted working context blocks shell despite grant

- **GIVEN** shell mode is `host-allowed`
- **AND** a matching shell grant exists
- **WHEN** the active trust context is downgraded by public-tainted or sensitive-read content
- **THEN** the shell tool is denied
- **AND** the denial reason indicates the context is below the shell audience threshold

### Requirement: Policy-gated tool invocation

The system SHALL check grants, posture policy, the resolved audience profile, effective trust context, and capability classification before every tool execution. Tool invocations SHALL be logged with audit records including tool name, invoking session, timestamp, and allow/deny result.

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

#### Scenario: Exfiltration-capable tool denied for downgraded audience

- **GIVEN** a tool is classified as publish-external or exfiltration-capable
- **WHEN** the active trust context is `public` or a downgraded sensitive-read subtask without approval
- **THEN** the runtime denies invocation even if the tool is configured and registered

### Requirement: Audience profiles define tool and resource scope

The system SHALL support operator-configurable audience profiles for `public`, `team`, and `personal`. Each profile SHALL resolve to explicit tool visibility and resource scopes rather than relying on runtime path guessing.

#### Scenario: Public profile limits file access to session directory

- **GIVEN** the active trust context is `public`
- **AND** the resolved `public` audience profile grants local file access
- **WHEN** the model requests `file_read` or `file_write`
- **THEN** the runtime applies the `public` profile's configured roots
- **AND** the recommended default roots are limited to `{session_dir}`

#### Scenario: Personal profile may explicitly allow all tools and directories

- **GIVEN** the operator configures the `personal` audience profile with `tool mode = all`
- **AND** the filesystem mode for that profile is set to `all`
- **WHEN** a `personal` turn runs without downgrade
- **THEN** the runtime may expose all granted tools and unrestricted local filesystem access for that profile
- **AND** doctor output warns that the personal profile is effectively unrestricted

#### Scenario: Broader profile does not leak into narrower audience

- **GIVEN** the `personal` audience profile allows all tools and directories
- **AND** the `public` audience profile allows only session-scoped file access
- **WHEN** a turn is evaluated as `public`
- **THEN** the runtime uses only the resolved `public` profile
- **AND** the broader personal settings do not apply through inheritance or fallback

#### Scenario: Public-context file reads are limited to session artifacts

- **GIVEN** the active trust context is `public`
- **AND** the model requests `file_read`
- **WHEN** the requested path is outside the current session directory
- **THEN** the runtime denies the read
- **AND** the denial explains that public-context file access is limited to the session directory

#### Scenario: Public-context file writes are limited to session artifacts

- **GIVEN** the active trust context is `public`
- **AND** the model requests `file_write`
- **WHEN** the requested path is outside the current session directory
- **THEN** the runtime denies the write
- **AND** the denial explains that public-context file access is limited to the session directory
