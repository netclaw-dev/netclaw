## ADDED Requirements

### Requirement: MCP server profile configuration

The system SHALL support named MCP server profiles in configuration.

#### Scenario: Disabled by default

- **WHEN** no MCP profile is enabled
- **THEN** no MCP tools are loaded

### Requirement: Policy-gated MCP invocation

The system SHALL apply ACL and grants before invoking MCP tools.

#### Scenario: Missing grant denies MCP tool

- **WHEN** an MCP tool is requested without grant
- **THEN** invocation is denied with a policy reason code
