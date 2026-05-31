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
three-step discovery strategy: a compressed tool index injected into the system
prompt for agent awareness, a `search_tools` meta-tool for browsing available
tools (names and descriptions only), and a `load_tool` meta-tool for
on-demand activation of individual tool definitions. `search_tools` SHALL NOT
load tool schemas into the session — it SHALL return a discovery menu only.
The agent SHALL call `load_tool` to activate each tool it needs. Core tools
(shell, file operations) SHALL remain always-loaded; MCP tools SHALL be
deferred by default.

When an LLM call fails after tools have been dynamically loaded, the system
SHALL evict all discovered tools from the session's available tool set. This
prevents a tool set that caused the failure (e.g., oversized schemas) from
poisoning subsequent turns.

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

### Requirement: Per-tool audience filtering for MCP servers

The system SHALL support per-server tool allowlists on each audience profile
via `McpServerToolGrants`. When a server has an entry in the grants dictionary,
only tools whose bare name appears in the list SHALL be exposed to that
audience. When a server has no entry (or grants is null), all tools from that
server SHALL be exposed (backward-compatible default).

Tool grants compose with the existing `AllowedMcpServers` gate: a tool must
pass both the server-level check AND the per-tool grant check to be exposed.

#### Scenario: Tool granted to audience is exposed

- **GIVEN** audience profile has `McpServerToolGrants: { "memorizer": ["search_memories", "get"] }`
- **AND** the `memorizer` server is in `AllowedMcpServers`
- **WHEN** the session resolves available tools for this audience
- **THEN** `memorizer/search_memories` and `memorizer/get` are exposed
- **AND** other tools from `memorizer` are not exposed

#### Scenario: No grants for server exposes all tools

- **GIVEN** audience profile has `McpServerToolGrants` that does not contain a `memorizer` entry
- **AND** the `memorizer` server is in `AllowedMcpServers`
- **WHEN** the session resolves available tools for this audience
- **THEN** all tools from `memorizer` are exposed

#### Scenario: Null grants exposes all tools from allowed servers

- **GIVEN** audience profile has `McpServerToolGrants: null`
- **AND** servers are allowed via `AllowedMcpServers` or `McpServersMode: All`
- **WHEN** the session resolves available tools
- **THEN** all tools from all allowed servers are exposed

#### Scenario: Empty tool list blocks all tools from server

- **GIVEN** audience profile has `McpServerToolGrants: { "memorizer": [] }`
- **AND** the `memorizer` server is in `AllowedMcpServers`
- **WHEN** the session resolves available tools
- **THEN** no tools from `memorizer` are exposed

#### Scenario: Server not in AllowedMcpServers is blocked regardless of grants

- **GIVEN** audience profile has `McpServerToolGrants: { "memorizer": ["search_memories"] }`
- **BUT** `memorizer` is NOT in `AllowedMcpServers` and `McpServersMode` is `Allowlist`
- **WHEN** the session resolves available tools
- **THEN** no tools from `memorizer` are exposed

#### Scenario: Different audiences see different tools from same server

- **GIVEN** Team profile has `McpServerToolGrants: { "memorizer": ["search_memories", "get"] }`
- **AND** Personal profile has `McpServersMode: All` with no `McpServerToolGrants`
- **WHEN** a Team session resolves tools
- **THEN** only `search_memories` and `get` are exposed
- **AND** when a Personal session resolves tools, all `memorizer` tools are exposed

### Requirement: Tool grant enforcement in search_tools

`search_tools` and `load_tool` SHALL enforce the same effective audience and
feature gates as direct MCP tool exposure. A session MUST NOT be able to use
these discovery/load paths to enumerate or activate tools that are blocked by
deployment-wide runtime switches, audience allowlists, or per-server per-tool
grants.

#### Scenario: Public session cannot discover blocked MCP capabilities

- **GIVEN** a session has audience `Public`
- **AND** Public does not have access to a given MCP server or tool
- **WHEN** the session calls `search_tools`
- **THEN** blocked servers and tools do not appear in results
- **AND** the response does not reveal hidden tool names for blocked internals

#### Scenario: Public session cannot activate blocked MCP tool through load_tool

- **GIVEN** a session has audience `Public`
- **AND** the requested MCP tool is not exposed to Public
- **WHEN** the session calls `load_tool`
- **THEN** the tool is not activated
- **AND** the result follows the generic denied/not-found path without leaking
  hidden capability inventory

#### Scenario: Disabled subsystem hides discovery inventory for all audiences

- **GIVEN** a deployment-wide feature switch disables the relevant MCP-backed
  subsystem
- **WHEN** a Team session calls `search_tools`
- **THEN** tools from that disabled subsystem are absent from discovery results
- **AND** `load_tool` cannot activate them

### Requirement: Tool change detection logging

At MCP server connect time, the system SHALL compare discovered tools against
tool grants configured across all audience profiles. The system SHALL log
warnings for tools that appear on the server but are not granted to any
audience, and for granted tool names that do not exist on the server.

#### Scenario: New tool discovered but not granted

- **GIVEN** `memorizer` server exposes tools `[search_memories, store, get, archive]`
- **AND** across all audience profiles, only `[search_memories, store, get]` are granted
- **WHEN** the daemon connects to `memorizer`
- **THEN** a warning is logged identifying `archive` as discovered but not granted to any audience

#### Scenario: Granted tool not found on server

- **GIVEN** Team profile grants `["search_memories", "old_tool"]` from `memorizer`
- **AND** `memorizer` does not expose a tool named `old_tool`
- **WHEN** the daemon connects to `memorizer`
- **THEN** a warning is logged identifying `old_tool` as granted but not found on the server

#### Scenario: No grants configured skips change detection

- **GIVEN** no audience profile has `McpServerToolGrants` entries for `memorizer`
- **WHEN** the daemon connects to `memorizer`
- **THEN** no tool change detection warnings are logged for that server
