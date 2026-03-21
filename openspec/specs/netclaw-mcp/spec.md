# netclaw-mcp Specification

Research: `docs/research/dynamic-context-discovery.md`

## Purpose

Define MCP server integration, validation, policy enforcement, and diagnostics.

## Requirements

### Requirement: MCP server profile configuration

The system SHALL support named MCP server profiles in configuration. Each
profile SHALL specify a transport type (`stdio` or `SSE`), the command or URL
for the server, and optional environment variables to pass to the server
process.

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
- **THEN** the configured environment variables are set in the server process
  environment

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

#### Scenario: Daemon reports MCP auth failure

- **GIVEN** the daemon can reach the MCP server but authentication is rejected on the live runtime path
- **WHEN** the operator runs `netclaw mcp list` or `netclaw doctor`
- **THEN** the CLI reports `auth failed`
- **AND** remediation points to `netclaw mcp auth <name>` when OAuth is in use

#### Scenario: Doctor cannot verify OAuth auth offline

- **GIVEN** an HTTP/SSE MCP server uses OAuth
- **AND** the daemon is unavailable
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor may report offline connectivity evidence
- **BUT** it SHALL not claim the server is unauthorized unless the daemon runtime path has verified that auth failure

### Requirement: Memorizer as external memory tier

The Memorizer MCP server SHALL be the recommended first MCP server for Netclaw
deployments. Memorizer provides `store`, `search`, `get`, `delete`, and
`create_relationship` operations for persisting research findings and
cross-session learning. Memorizer is an external memory tier complementing
first-party local memory (personality, project registry, environment inventory).

#### Scenario: Store research finding via Memorizer

- **GIVEN** the `memorizer` MCP server is configured and reachable
- **AND** the session has `mcp:memorizer` grant
- **WHEN** the agent stores a research finding
- **THEN** the finding is persisted in Memorizer and retrievable in future
  sessions

#### Scenario: Search across sessions via Memorizer

- **GIVEN** prior sessions have stored findings in Memorizer
- **WHEN** the agent searches Memorizer for a topic
- **THEN** relevant findings from prior sessions are returned

### Requirement: Tool discovery and registration

On startup, the system SHALL discover tools from all enabled MCP server
profiles and register them as Microsoft.Extensions.AI (MEAI) tool definitions.
Tool discovery SHALL refresh on each session start to pick up newly added or
removed tools from MCP servers.

To avoid context window bloat with large tool catalogs (see
`docs/research/dynamic-context-discovery.md` §1–2), the system SHALL use a
two-layer discovery strategy: a compressed tool index injected into the system
prompt for agent awareness, and a `search_tools` meta-tool for on-demand
loading of full tool definitions. Core tools (shell, file operations) SHALL
remain always-loaded; MCP tools SHALL be deferred by default.

#### Scenario: Startup tool discovery

- **GIVEN** two MCP servers are enabled with a combined total of 5 tools
- **WHEN** the system starts
- **THEN** all 5 tools are discovered and registered as MEAI tool definitions

#### Scenario: Session-start tool refresh

- **GIVEN** an MCP server has added a new tool since last session start
- **WHEN** a new session actor initializes
- **THEN** the refreshed tool list includes the newly added tool

### Requirement: Graceful degradation

Tool calls to unavailable MCP servers SHALL return a clear error message to the
agent. The agent SHALL continue operating with remaining available tools. The
system SHALL attempt reconnection on the next tool call to a previously
unavailable server.

#### Scenario: Unavailable server returns clear error

- **GIVEN** a configured MCP server is unreachable
- **WHEN** the agent invokes a tool from that server
- **THEN** a clear error is returned indicating the server is unavailable
- **AND** the agent continues the conversation with remaining tools

#### Scenario: Reconnection on next call

- **GIVEN** an MCP server was previously unreachable
- **WHEN** the agent invokes a tool from that server again
- **THEN** the system attempts reconnection before returning an error

#### Scenario: Partial server availability

- **GIVEN** two MCP servers are configured and one is unreachable
- **WHEN** a session initializes
- **THEN** tools from the reachable server are available
- **AND** tools from the unreachable server are marked as unavailable
