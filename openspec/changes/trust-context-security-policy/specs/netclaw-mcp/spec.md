## MODIFIED Requirements

### Requirement: MCP server profile configuration

The system SHALL support named MCP server profiles in configuration. Each profile SHALL specify a transport type (`stdio` or `SSE`), the command or URL for the server, optional environment variables to pass to the server process, and a capability classification used by trust-context policy. Audience profiles SHALL be able to allow or deny whole MCP servers independently of transport configuration.

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

The system SHALL apply ACL, the resolved audience profile, trust-context policy, and MCP capability classification before invoking MCP tools.

#### Scenario: Missing grant denies MCP tool

- **WHEN** an MCP tool is requested without grant
- **THEN** invocation is denied with a policy reason code

#### Scenario: Sensitive-read MCP denied in public context

- **GIVEN** an MCP server is classified as sensitive-read
- **WHEN** a `public` turn requests a tool from that server
- **THEN** invocation is denied regardless of transport health or discovery status

#### Scenario: Memory-safe MCP allowed in team context

- **GIVEN** an MCP server is classified as memory-safe
- **AND** the session has the matching `mcp:{server}` grant
- **WHEN** a `team` turn invokes a tool from that server
- **THEN** the invocation may proceed subject to the active trust context and memory policy

#### Scenario: Personal profile allows whole MCP server explicitly

- **GIVEN** the operator configures the `personal` audience profile to allow an MCP server
- **AND** a specific session has the necessary ACL grant for one of those servers
- **WHEN** a non-downgraded `personal` turn evaluates tool visibility
- **THEN** the runtime may expose tools from that MCP server subject to capability classification and runtime trust checks

#### Scenario: Public profile blocks MCP server despite broader personal allowance

- **GIVEN** the `personal` audience profile allows an MCP server
- **AND** the resolved `public` audience profile does not allow that server
- **WHEN** a `public` turn evaluates MCP discovery or invocation
- **THEN** the runtime hides and denies the server's tools regardless of the broader personal configuration

#### Scenario: Newly added remote tool remains bounded by server audience policy

- **GIVEN** an MCP server is allowed only for `personal`
- **AND** the remote operator adds a new tool to that server overnight
- **WHEN** a `team` or `public` turn evaluates discovery or invocation after reconnect
- **THEN** the runtime still hides and denies the new tool because the server itself is not allowed for that audience

#### Scenario: Missing server audience policy fails closed

- **WHEN** an MCP server is enabled without explicit audience-profile allowance
- **THEN** the runtime does not expose the server outside the strictest compatible defaults
- **AND** remote tool catalog changes do not widen access implicitly
