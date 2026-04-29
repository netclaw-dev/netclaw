## MODIFIED Requirements

### Requirement: MCP tool permissions CLI

The system SHALL provide a `netclaw mcp tools` subcommand for viewing and
managing per-server tool grants across audience profiles. The system SHALL
additionally provide a `netclaw mcp permissions` subcommand as the canonical
alias that routes to the same interactive TUI host as bare `netclaw mcp
tools`. The CLI read-only listing (`netclaw mcp tools <server>`) SHALL append
a discoverability hint pointing operators to `netclaw mcp permissions` for
interactive editing.

The `netclaw mcp add` command SHALL be fail-closed by default: after
successfully writing the `McpServers[name]` entry, it SHALL write empty
`McpServerToolGrants[name] = []` entries and per-audience
`ApprovalPolicy.McpServerDefaults[name]` entries (per `netclaw-mcp` →
`Secure defaults for new MCP servers`) and print a post-add hint directing
the operator to run `netclaw mcp permissions`. `netclaw mcp add` SHALL
accept a `--grant-all` flag as a CI escape hatch that skips the grant
writes but still writes the approval defaults.

#### Scenario: List tools for a server

- **GIVEN** the daemon is running and `memorizer` is connected
- **WHEN** operator runs `netclaw mcp tools memorizer`
- **THEN** the CLI displays all discovered tools from `memorizer`
- **AND** each tool shows its grant status per audience (Public, Team, Personal)
- **AND** tools not granted to any audience are visually distinguished
- **AND** the output ends with a hint: `Edit interactively: netclaw mcp permissions`

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
- **AND** the help text references `netclaw mcp permissions` as the interactive editor

#### Scenario: `permissions` subcommand launches the TUI

- **GIVEN** the daemon is running with MCP servers connected
- **WHEN** operator runs `netclaw mcp permissions`
- **THEN** the TUI launches identically to `netclaw mcp tools` with no
  server argument
- **AND** the CLI reports no unrecognized-subcommand error

#### Scenario: Fail-closed `mcp add` emits post-add hint

- **WHEN** operator runs `netclaw mcp add --transport http notion https://mcp.notion.com/mcp`
- **THEN** the CLI prints the confirmation line naming the server
- **AND** the CLI prints a security note stating that 0 tools are granted
- **AND** the CLI prints an instruction to run `netclaw mcp permissions`

#### Scenario: `--grant-all` flag bypasses empty-grant writes

- **WHEN** operator runs `netclaw mcp add --grant-all --transport stdio trusted -- /usr/local/bin/trusted`
- **THEN** no `McpServerToolGrants.trusted` entry is written to `netclaw.json`
- **AND** `ApprovalPolicy.McpServerDefaults.trusted` entries are still
  written per audience (Personal=Approval, Team=Approval, Public=Deny)
- **AND** the post-add hint omits the "0 tools granted" note and mentions
  the legacy grant behavior

### Requirement: MCP tool permissions TUI

The system SHALL provide an interactive TUI mode for `netclaw mcp tools`
(invoked without a server name argument) that allows operators to browse
servers, view discovered tools, toggle per-tool grants per audience, edit
the per-audience MCP server approval default, and set per-tool approval
mode overrides. The `netclaw mcp permissions` subcommand SHALL route to
the same TUI host as an alias.

For each server/audience view, the TUI SHALL render a server-default row
showing the current
`ApprovalPolicy.McpServerDefaults[serverName]` value and a per-tool list
where each tool row shows the effective approval mode and a suffix
identifying whether the effective mode comes from inheritance (`(def)`)
or from an explicit `ToolOverrides` entry (`(override)`).

The TUI SHALL provide these keybindings for the per-audience server view:

- `Enter` — toggle the grant for the highlighted tool (existing)
- `A` — toggle all grants for the server (existing)
- `E` — toggle server access for the audience (existing)
- `←/→` — cycle the selected audience (existing)
- `S` — save pending changes (existing)
- `M` — cycle the server approval default through `Auto → Approval → Deny → Auto`
- `P` — cycle the highlighted tool's explicit override through
  `inherit → Auto → Approval → Deny → inherit` where `inherit` removes any
  existing `ToolOverrides[{server}/{tool}]` entry on save

`Save` SHALL persist `McpServerToolGrants`, `McpServerDefaults`, and
`ToolOverrides` changes atomically via `ConfigFileHelper.WriteConfigFile`.
Entries cycled back to `inherit` SHALL be removed from `ToolOverrides`
rather than written as `"Auto"`.

#### Scenario: Launch TUI without arguments

- **GIVEN** the daemon is running with MCP servers connected
- **WHEN** operator runs `netclaw mcp tools` (no server name)
- **THEN** the TUI launches showing a list of configured MCP servers

#### Scenario: Launch TUI via `permissions` alias

- **GIVEN** the daemon is running with MCP servers connected
- **WHEN** operator runs `netclaw mcp permissions`
- **THEN** the TUI launches showing the same server list as bare
  `netclaw mcp tools`

#### Scenario: Browse tools for a server

- **GIVEN** the TUI is showing the server list
- **WHEN** operator selects a server
- **THEN** the TUI shows all discovered tools for that server
- **AND** each tool shows its current grant status for the selected audience
- **AND** the top of the view shows a `Server default:` row

#### Scenario: Cycle audience in TUI

- **GIVEN** the TUI is showing tools for a server
- **WHEN** operator presses left/right arrow to cycle audience
- **THEN** the tool grant checkboxes update to reflect the selected audience's grants
- **AND** the server-default row updates to reflect that audience's
  `McpServerDefaults` entry (or `Auto` if absent)
- **AND** per-tool rows re-resolve their effective mode against the new audience

#### Scenario: Toggle tool grant in TUI

- **GIVEN** the TUI is showing tools for a server under the Team audience
- **WHEN** operator toggles a tool's checkbox
- **THEN** the tool is added to or removed from the Team profile's `McpServerToolGrants` for this server

#### Scenario: Toggle server access in TUI

- **GIVEN** the TUI is showing tools for a server not allowed for the Team audience
- **WHEN** operator presses the enable/disable key
- **THEN** the server is added to the Team profile's `AllowedMcpServers`
- **AND** all tools start unchecked (secure by default)

#### Scenario: Cycle server approval default

- **GIVEN** the TUI shows Notion tools under the Personal audience with
  `Server default: [Auto]`
- **WHEN** operator presses `M`
- **THEN** the server-default display advances to `[Approval]`
- **AND** per-tool rows whose effective mode is inherited update to show
  `[Approval] (def)`

#### Scenario: Cycle per-tool override

- **GIVEN** the TUI shows a Notion tool `notion/delete-page` with an
  inherited effective mode of `[Approval] (def)`
- **WHEN** operator highlights the tool and presses `P`
- **THEN** the effective mode advances to `[Auto] (override)` on the first
  press, then `[Approval] (override)`, then `[Deny] (override)`, then back
  to the inherited `[Approval] (def)` on the fourth press

#### Scenario: Save changes from TUI

- **GIVEN** the operator has toggled tool grants, cycled a server default, and
  cycled tool overrides in the TUI
- **WHEN** operator presses the save key
- **THEN** `McpServerToolGrants`, `ApprovalPolicy.McpServerDefaults`, and
  `ApprovalPolicy.ToolOverrides` are written to `netclaw.json`
- **AND** tool overrides cycled back to `inherit` are removed from
  `ToolOverrides` rather than written as `"Auto"`
- **AND** the TUI confirms the save and prompts the operator to restart
  the daemon

## ADDED Requirements

### Requirement: MCP doctor warning for missing approval defaults

The `netclaw doctor` command SHALL emit a warning-severity diagnostic for
each enabled MCP server whose Personal audience profile has
`McpServersMode = All` AND no entry in
`Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults[serverName]`
AND no `ToolOverrides` entries whose key starts with `{serverName}/`. The
warning SHALL direct the operator to run `netclaw mcp permissions` to
configure an approval default. The warning SHALL NOT auto-remediate the
missing entry. The existing info-level null-grants advisory (`MCP doctor
advisory for ungated servers`) SHALL remain unchanged and continue to fire
independently.

#### Scenario: Server missing Personal approval default triggers warning

- **GIVEN** `notion` is enabled in `McpServers`
- **AND** Personal profile has `McpServersMode = All`
- **AND** Personal profile has no `ApprovalPolicy.McpServerDefaults.notion`
- **AND** Personal profile has no `notion/*` entries in `ToolOverrides`
- **WHEN** operator runs `netclaw doctor`
- **THEN** a warning-severity diagnostic is reported for `notion`
- **AND** the message directs the operator to run `netclaw mcp permissions`

#### Scenario: Server with server default passes warning

- **GIVEN** `notion` is enabled in `McpServers`
- **AND** Personal profile has
  `ApprovalPolicy.McpServerDefaults.notion = Approval`
- **WHEN** operator runs `netclaw doctor`
- **THEN** the missing-approval-default warning does NOT fire for `notion`

#### Scenario: Server with per-tool overrides passes warning

- **GIVEN** `notion` is enabled in `McpServers`
- **AND** Personal profile has `McpServersMode = All`
- **AND** Personal profile has no `McpServerDefaults` entry for `notion`
- **AND** Personal profile has an explicit override
  `ApprovalPolicy.ToolOverrides["notion/create-pages"] = Approval`
- **WHEN** operator runs `netclaw doctor`
- **THEN** the missing-approval-default warning does NOT fire for `notion`

#### Scenario: Warning does not change exit code alone

- **GIVEN** the missing-approval-default warning is the only diagnostic
- **WHEN** operator runs `netclaw doctor`
- **THEN** the command exits with code `2` (warnings only)
- **AND** does NOT exit with code `1` (errors)
