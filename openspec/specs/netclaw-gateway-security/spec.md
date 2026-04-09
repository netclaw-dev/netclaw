# netclaw-gateway-security Specification

## Purpose

Define security controls for inbound handling, exposure modes, approvals, and
audit behavior.

## Requirements

### Requirement: Default-deny policy

The system SHALL deny interactions unless explicitly allowed by ACL.

#### Scenario: Unknown sender blocked

- **WHEN** an unknown sender triggers an interaction
- **THEN** the interaction is denied

### Requirement: Fail-closed startup

The system SHALL fail startup if security-critical configuration is invalid.

#### Scenario: Invalid ACL prevents startup

- **WHEN** ACL schema is invalid
- **THEN** runtime start fails

### Requirement: Controlled exposure modes

The system SHALL support explicit exposure modes with secure defaults. Host-
network reachable daemon access SHALL require authenticated users. `Public`
deployment posture remains a chat-audience concept and SHALL NOT be
interpreted as permission for anonymous network access. Audience types and
exposure modes are parallel controls: audience governs chat interaction, while
exposure mode governs daemon network reachability.

#### Scenario: Default local mode

- **WHEN** no exposure mode is configured
- **THEN** the system binds loopback-only

#### Scenario: Internet-reachable mode requires authenticated users

- **GIVEN** exposure mode is internet-reachable (`tailscale-funnel` or
  `cloudflare-tunnel`)
- **WHEN** access policy prerequisites are missing
- **THEN** configuration validation fails

### Requirement: Privileged action approval

The system SHALL require explicit approval for privileged operations.

#### Scenario: Privileged request requires approval

- **WHEN** a privileged operation is requested
- **THEN** the system requires trusted operator approval before execution

### Requirement: Security audit visibility

The system SHALL expose policy denies and exposure status in diagnostics.

#### Scenario: Audit events visible in diagnostics

- **WHEN** policy allow/deny decisions occur
- **THEN** diagnostics include timestamped records with reason codes

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

### Requirement: Fail-closed reminder write validation

Reminder write surfaces SHALL validate reminder audience server-side before
persisting or importing a reminder definition. This applies to REST, admin,
CLI, and import paths in addition to conversational tool calls. Invalid
audience values, missing required authority context, or requested audiences
that exceed the caller's source authority SHALL be rejected with clear error
messages. Execution may trust the stored reminder audience because minting-time
validation is mandatory.

#### Scenario: REST create rejects invalid audience value

- **GIVEN** a REST reminder create request provides `audience: "superuser"`
- **WHEN** the server validates the request
- **THEN** the request is rejected with a clear validation error
- **AND** no reminder definition is persisted

#### Scenario: Admin import rejects over-privileged reminder

- **GIVEN** an admin or import request is authenticated with source audience `Team`
- **WHEN** the request submits a reminder definition with stored audience `Personal`
- **THEN** the server rejects the request with a clear over-privilege error
- **AND** the reminder is not written to disk

#### Scenario: Write path fails closed without authority context

- **GIVEN** a non-conversational reminder write path cannot determine the caller's source audience / authority
- **WHEN** the request attempts to create or import a reminder definition
- **THEN** the server rejects the request
- **AND** the error states that reminder audience authorization context is required

#### Scenario: Execution trusts stored audience after validated minting

- **GIVEN** a reminder definition was accepted by the server's minting validation
- **WHEN** the reminder executes later on a timer
- **THEN** the execution path uses the stored audience as authoritative
- **AND** no deployment-default fallback broadens that audience
