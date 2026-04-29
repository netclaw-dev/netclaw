## MODIFIED Requirements

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
