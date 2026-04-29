## ADDED Requirements

### Requirement: MCP tool permissions CLI

The system SHALL provide a `netclaw mcp tools` subcommand for viewing and
managing per-server tool grants across audience profiles.

#### Scenario: List tools for a server

- **GIVEN** the daemon is running and `memorizer` is connected
- **WHEN** operator runs `netclaw mcp tools memorizer`
- **THEN** the CLI displays all discovered tools from `memorizer`
- **AND** each tool shows its grant status per audience (Public, Team, Personal)
- **AND** tools not granted to any audience are visually distinguished

#### Scenario: List tools when daemon is unavailable

- **GIVEN** the daemon is not running
- **WHEN** operator runs `netclaw mcp tools memorizer`
- **THEN** the CLI reports that tool discovery requires the daemon
- **AND** exits with a non-zero exit code

#### Scenario: Snapshot current tools as grants

- **GIVEN** the daemon is running and `memorizer` exposes 5 tools
- **WHEN** operator runs `netclaw mcp tools memorizer --snapshot`
- **THEN** the CLI populates `McpServerToolGrants` for all audience profiles that allow `memorizer`
- **AND** each profile's grant list contains all 5 currently discovered tool names
- **AND** the updated config is written to `netclaw.json`

#### Scenario: Help for tools subcommand

- **WHEN** operator runs `netclaw mcp tools --help`
- **THEN** the CLI displays usage, subcommand description, and available flags

### Requirement: MCP tool permissions TUI

The system SHALL provide an interactive TUI mode for `netclaw mcp tools`
(invoked without a server name argument) that allows operators to browse
servers, view discovered tools, and toggle per-tool grants per audience.

#### Scenario: Launch TUI without arguments

- **GIVEN** the daemon is running with MCP servers connected
- **WHEN** operator runs `netclaw mcp tools` (no server name)
- **THEN** the TUI launches showing a list of configured MCP servers

#### Scenario: Browse tools for a server

- **GIVEN** the TUI is showing the server list
- **WHEN** operator selects a server
- **THEN** the TUI shows all discovered tools for that server with descriptions
- **AND** each tool shows its current grant status for the selected audience

#### Scenario: Switch audience in TUI

- **GIVEN** the TUI is showing tools for a server
- **WHEN** operator presses Tab to switch audience
- **THEN** the tool grant checkboxes update to reflect the newly selected audience's grants

#### Scenario: Toggle tool grant in TUI

- **GIVEN** the TUI is showing tools for a server under the Team audience
- **WHEN** operator toggles a tool's checkbox
- **THEN** the tool is added to or removed from the Team profile's `McpServerToolGrants` for this server

#### Scenario: Save changes from TUI

- **GIVEN** the operator has toggled tool grants in the TUI
- **WHEN** operator presses the save key
- **THEN** the updated `McpServerToolGrants` are written to `netclaw.json`
- **AND** the TUI confirms the save

### Requirement: MCP doctor advisory for ungated servers

The `netclaw doctor` command SHALL include an advisory check for MCP servers
that have no `McpServerToolGrants` configured on any audience profile.

#### Scenario: Server with no tool grants triggers advisory

- **GIVEN** `memorizer` is enabled and connected
- **AND** no audience profile has `McpServerToolGrants` entries for `memorizer`
- **WHEN** operator runs `netclaw doctor`
- **THEN** an info-level advisory is reported for `memorizer`
- **AND** the message suggests adding tool grants for supply-chain protection

#### Scenario: Server with tool grants passes advisory

- **GIVEN** `memorizer` has `McpServerToolGrants` on at least one audience profile
- **WHEN** operator runs `netclaw doctor`
- **THEN** no tool grant advisory is reported for `memorizer`
