## ADDED Requirements

### Requirement: Self-configuration safety (SEC-008)

The system SHALL validate configuration changes before writing them to disk.
ACL and security policy files MUST NOT be self-modifiable by the agent. The
agent SHALL be permitted to modify personality files, project registry,
environment inventory, and schedule definitions through conversation.

#### Scenario: Agent modifies permitted configuration

- **GIVEN** the agent has `config_write` grant
- **WHEN** the agent writes to a personality file or project registry
- **THEN** the change is validated against schema before write
- **AND** the write succeeds if validation passes

#### Scenario: Agent blocked from modifying security files

- **WHEN** the agent attempts to modify ACL, security policy, or gateway
  configuration files
- **THEN** the write is rejected regardless of grants
- **AND** an audit record is created for the denied attempt

#### Scenario: Invalid config change rejected

- **GIVEN** the agent has `config_write` grant
- **WHEN** the agent writes configuration that fails schema validation
- **THEN** the write is rejected
- **AND** the agent receives validation error details

### Requirement: Shell execution boundaries (SEC-009)

The system SHALL enforce safety boundaries on shell command execution. Shell
commands SHALL run as the process user with no privilege escalation. A
configurable timeout (default 60 seconds) SHALL terminate long-running
commands. Output SHALL be truncated at a configurable limit. Interactive
commands (those requiring stdin) SHALL be rejected. Working directory SHALL be
restricted to configured allowed paths.

#### Scenario: Shell command completes within timeout

- **GIVEN** shell execution timeout is configured to 60 seconds
- **WHEN** a shell command completes in 10 seconds
- **THEN** the output is returned to the session

#### Scenario: Shell command exceeds timeout

- **GIVEN** shell execution timeout is configured to 60 seconds
- **WHEN** a shell command runs for more than 60 seconds
- **THEN** the process is terminated
- **AND** the session receives a timeout error

#### Scenario: Interactive command rejected

- **WHEN** a shell command requires interactive stdin input
- **THEN** execution is rejected before launch
- **AND** the session receives a rejection reason

#### Scenario: Output truncation

- **GIVEN** output truncation limit is configured
- **WHEN** shell command output exceeds the configured limit
- **THEN** output is truncated with an indicator that content was omitted

#### Scenario: Working directory restriction

- **WHEN** a shell command targets a directory outside configured allowed paths
- **THEN** execution is denied with a policy reason code

### Requirement: Tool invocation audit

The system SHALL create audit records for all tool invocations. Each audit
record SHALL include: tool name, session ID, timestamp, and allow/deny result.

#### Scenario: Allowed tool invocation is audited

- **WHEN** a tool invocation is allowed by policy
- **THEN** an audit record is created with tool name, session ID, timestamp, and
  result `allow`

#### Scenario: Denied tool invocation is audited

- **WHEN** a tool invocation is denied by policy
- **THEN** an audit record is created with tool name, session ID, timestamp, and
  result `deny` with reason code

#### Scenario: Audit records visible in diagnostics

- **WHEN** operator queries tool invocation audit
- **THEN** records are available with filtering by session ID, tool name, and
  time range
