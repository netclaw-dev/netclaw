# netclaw-mcp Specification

## Purpose

Define MCP server integration, validation, policy enforcement, and diagnostics.

## Requirements

### Requirement: MCP server profile configuration

The system SHALL support named MCP server profiles in configuration.

#### Scenario: Disabled by default

- **WHEN** no MCP profile is enabled
- **THEN** no MCP tools are loaded

### Requirement: MCP validation

The system SHALL validate MCP server connectivity and discovery.

#### Scenario: Validate server

- **WHEN** operator runs MCP validation
- **THEN** output indicates handshake status and discovered tool count

### Requirement: Policy-gated MCP invocation

The system SHALL apply ACL and grants before invoking MCP tools.

#### Scenario: Missing grant denies MCP tool

- **WHEN** an MCP tool is requested without grant
- **THEN** invocation is denied with a policy reason code

### Requirement: MCP diagnostics visibility

The system SHALL expose MCP server health in diagnostics.

#### Scenario: Server becomes unavailable

- **WHEN** a configured MCP server is unreachable
- **THEN** diagnostics mark it degraded or unavailable with last error timestamp
