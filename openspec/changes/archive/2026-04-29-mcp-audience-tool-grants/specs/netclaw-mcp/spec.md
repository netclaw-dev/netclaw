## ADDED Requirements

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

Tools blocked by `McpServerToolGrants` SHALL NOT appear in `search_tools`
results for the requesting session's audience. The compressed tool index
injected into system prompts SHALL also reflect per-tool grant filtering.

#### Scenario: Blocked tool absent from search_tools

- **GIVEN** Team profile grants only `["search_memories"]` from `memorizer`
- **WHEN** a Team session calls `search_tools` with query matching `store`
- **THEN** `memorizer/store` does NOT appear in results

#### Scenario: Blocked tool absent from load_tool

- **GIVEN** Team profile grants only `["search_memories"]` from `memorizer`
- **WHEN** a Team session calls `load_tool` for `memorizer/store`
- **THEN** the tool is denied with a policy reason

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
