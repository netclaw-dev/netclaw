## MODIFIED Requirements

### Requirement: Channel and sender allow checks

The system SHALL evaluate source admission before turn dispatch using channel, sender, principal, and audience policy. Admission SHALL decide both whether the turn is accepted and what initial trust context it receives.

#### Scenario: Sender allowed, channel allowed

- **GIVEN** sender and channel are explicitly allowed
- **WHEN** a message arrives
- **THEN** ACL evaluation returns allow
- **AND** the evaluation records the source audience and principal classification used to derive the initial trust context

#### Scenario: Sender disallowed

- **WHEN** sender is not allowed by policy
- **THEN** ACL evaluation returns deny

#### Scenario: Teammate DM to personal bot is admitted with downgraded audience

- **GIVEN** a personal deployment allows direct messages from trusted teammates
- **WHEN** a teammate sends a DM to the bot
- **THEN** source admission may allow the turn
- **AND** the initial trust context is no broader than `team`
- **AND** the turn does not inherit owner-only or personal-only capability envelopes

### Requirement: Tool and data grants

The system SHALL enforce explicit grants for tool and data access. Grants SHALL be organized into specific tool grant categories: `shell`, `web_search`, `web_fetch`, `github`, `mcp:{server_name}`, `config_write`, and `schedule_write`. Matching grants SHALL be necessary but not sufficient for access: the effective trust context, audience policy, and capability classification SHALL also allow the action.

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

#### Scenario: Grant is present but trust context still denies execution

- **GIVEN** a matching `shell` grant exists for the session
- **AND** the active trust context has been downgraded by public-tainted or sensitive-read content
- **WHEN** the model requests shell execution
- **THEN** execution is denied with a policy reason indicating the active trust context is insufficient
