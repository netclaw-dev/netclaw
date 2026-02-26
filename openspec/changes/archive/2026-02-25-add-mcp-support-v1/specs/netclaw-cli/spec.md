## MODIFIED Requirements

### Requirement: Config and ACL validation

The CLI SHALL validate configuration and return actionable errors.

#### Scenario: MCP validation failure

- **WHEN** operator runs `netclaw mcp validate` and server handshake fails
- **THEN** command exits non-zero
- **AND** output includes remediation guidance for the failing server profile
