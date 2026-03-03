## MODIFIED Requirements

### Requirement: Tool and data grants

The system SHALL enforce explicit grants for tool and data access. Grants SHALL
be organized into specific tool grant categories: `shell`, `web_search`,
`web_fetch`, `github`, `mcp:{server_name}`, `config_write`, and
`schedule_write`. Each grant SHALL specify the allowed senders and channels to
which it applies.

#### Scenario: Missing grant blocks tool call

- **WHEN** a tool call is attempted without a matching grant
- **THEN** execution is denied with a policy reason code

#### Scenario: Category-specific grant allows tool

- **GIVEN** ACL grants `web_search` for sender `U12345` on channel `C99999`
- **WHEN** sender `U12345` requests a web search in channel `C99999`
- **THEN** ACL evaluation returns allow for the `web_search` tool category

#### Scenario: MCP server-scoped grant

- **GIVEN** ACL grants `mcp:memorizer` for sender `U12345`
- **WHEN** sender `U12345` requests an MCP tool from the `memorizer` server
- **THEN** ACL evaluation returns allow
- **AND** MCP tools from other servers without explicit grants are denied

#### Scenario: Config write grant required for self-configuration

- **GIVEN** ACL does not grant `config_write` for the current sender
- **WHEN** the agent attempts to write configuration files through conversation
- **THEN** the write is denied with a policy reason code

## ADDED Requirements

### Requirement: Self-configuration prohibition

ACL and security policy files MUST NOT be modifiable by the agent through
conversation. These files SHALL only be modified through the CLI or direct file
edit by the operator. This prohibition SHALL be enforced regardless of any
grants in the ACL policy.

#### Scenario: Agent cannot modify ACL through conversation

- **WHEN** an agent session attempts to modify ACL policy files
- **THEN** the modification is denied regardless of active grants
- **AND** the denial reason indicates that ACL files require CLI or direct edit

#### Scenario: Agent cannot modify security policy through conversation

- **WHEN** an agent session attempts to modify gateway security policy files
- **THEN** the modification is denied regardless of active grants

### Requirement: Scheduled task tool grants

Each scheduled task definition SHALL specify the required tool grants for its
execution. At execution time, the system SHALL verify that all required tool
grants are still valid before running the task.

#### Scenario: Scheduled task with valid grants executes

- **GIVEN** a scheduled task requires `web_search` and `mcp:memorizer`
- **AND** both grants are present in the current ACL policy
- **WHEN** the scheduled task fires
- **THEN** the task executes with the granted tools available

#### Scenario: Scheduled task with revoked grant is blocked

- **GIVEN** a scheduled task requires `web_search`
- **AND** the `web_search` grant has been removed from ACL policy since the task
  was created
- **WHEN** the scheduled task fires
- **THEN** execution is denied with a policy reason code
- **AND** the task failure is recorded with the missing grant details
