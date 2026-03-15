## MODIFIED Requirements

### Requirement: MCP server profile configuration

The system SHALL support named MCP server profiles in configuration. Each profile SHALL specify a transport type (`stdio` or `SSE`), the command or URL for the server, optional environment variables to pass to the server process, and a capability classification used by trust-context policy.

#### Scenario: Disabled by default

- **WHEN** no MCP profile is enabled
- **THEN** no MCP tools are loaded

#### Scenario: Stdio transport profile

- **GIVEN** an MCP profile is configured with transport type `stdio`
- **WHEN** the profile is loaded
- **THEN** the system launches the server using the configured command
- **AND** communicates via stdio transport

#### Scenario: SSE transport profile

- **GIVEN** an MCP profile is configured with transport type `SSE`
- **WHEN** the profile is loaded
- **THEN** the system connects to the configured URL via SSE transport

#### Scenario: Environment variables passed to server

- **GIVEN** an MCP profile specifies environment variables
- **WHEN** the server process is launched (stdio transport)
- **THEN** the configured environment variables are set in the server process environment

#### Scenario: Capability class omitted falls back to strict handling

- **WHEN** an MCP profile is enabled without an explicit capability classification
- **THEN** runtime policy treats the server as the most restrictive compatible class until the operator classifies it

### Requirement: Policy-gated MCP invocation

The system SHALL apply ACL, trust-context policy, and MCP capability classification before invoking MCP tools.

#### Scenario: Missing grant denies MCP tool

- **WHEN** an MCP tool is requested without grant
- **THEN** invocation is denied with a policy reason code

#### Scenario: Sensitive-read MCP denied in public context

- **GIVEN** an MCP server is classified as sensitive-read
- **WHEN** a `public` or `community` turn requests a tool from that server
- **THEN** invocation is denied regardless of transport health or discovery status

#### Scenario: Memory-safe MCP allowed in team context

- **GIVEN** an MCP server is classified as memory-safe
- **AND** the session has the matching `mcp:{server}` grant
- **WHEN** a `team` turn invokes a tool from that server
- **THEN** the invocation may proceed subject to the active trust context and memory policy
