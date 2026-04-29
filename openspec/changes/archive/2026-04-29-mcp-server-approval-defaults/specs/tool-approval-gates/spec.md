## MODIFIED Requirements

### Requirement: Tool approval configuration per audience

The system SHALL support per-audience tool approval configuration via
`ToolApprovalConfig` on `ToolAudienceProfile`. Each audience profile SHALL
independently specify a `DefaultMode` (Auto, Approval, Deny), per-tool
overrides in `ToolOverrides`, and per-MCP-server defaults in
`McpServerDefaults`.

Approval mode resolution SHALL use deterministic precedence:

1. Matcher-derived approval-mode key override (for example
   `file_write:control-plane`)
2. Base tool key override (for example `file_write` or `notion/create-pages`)
3. MCP server default from `McpServerDefaults[serverName]` when the tool
   name is `{serverName}/{toolName}` (MCP-namespaced tool)
4. Matcher fail-closed behavior for Personal audience
5. Audience `DefaultMode`

Both `ToolApprovalConfig.GetEffectiveMode` and
`ToolAccessPolicy.ResolveApprovalMode` SHALL implement this precedence
consistently. Runtime audience defaults SHALL NOT implicitly place
`shell_execute` in `Approval` mode. Instead, the init-generated Personal
config SHALL explicitly write
`ApprovalPolicy.ToolOverrides.shell_execute = Approval` as the recommended
shell-safe default.

#### Scenario: Shell requires approval in init-generated Personal config

- **GIVEN** a Personal audience session whose generated config explicitly sets
  `ApprovalPolicy.ToolOverrides.shell_execute` to `Approval`
- **WHEN** the agent invokes `shell_execute`
- **THEN** the system checks the approval cache before execution
- **AND** if the command pattern is not approved, an approval prompt is emitted

#### Scenario: Tool in Auto mode executes without approval

- **GIVEN** a tool whose approval mode is `Auto` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool executes immediately without an approval prompt

#### Scenario: Tool in Deny mode is always blocked

- **GIVEN** a tool whose approval mode is `Deny` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool is denied with reason `tool_denied_by_approval_policy`
- **AND** no approval prompt is offered

#### Scenario: Per-audience independence

- **GIVEN** Personal sets `shell_execute` to `Approval` and Team sets it to `Deny`
- **WHEN** a Personal session invokes `shell_execute`
- **THEN** the system checks approval cache and may prompt
- **AND** when a Team session invokes `shell_execute`
- **THEN** the system denies immediately without prompting

#### Scenario: Matcher-specific override key takes precedence over base tool key

- **GIVEN** `ApprovalPolicy.ToolOverrides.file_write = Auto`
- **AND** `ApprovalPolicy.ToolOverrides.file_write:control-plane = Approval`
- **WHEN** the agent invokes `file_write` targeting a control-plane path
- **THEN** the resolved mode is `Approval`
- **AND** the call is approval-gated unless already approved for that path pattern

#### Scenario: Base tool key applies when matcher-specific key is absent

- **GIVEN** `ApprovalPolicy.ToolOverrides.file_write = Approval`
- **AND** no override exists for `file_write:control-plane`
- **WHEN** the agent invokes `file_write` targeting a control-plane path
- **THEN** the resolved mode is `Approval`
- **AND** mode resolution does NOT fall directly to `DefaultMode`

#### Scenario: Exact MCP tool override beats server default

- **GIVEN** `ApprovalPolicy.McpServerDefaults.notion = Approval`
- **AND** `ApprovalPolicy.ToolOverrides["notion/search"] = Auto`
- **WHEN** the agent invokes `notion/search`
- **THEN** the resolved mode is `Auto`
- **AND** the call executes immediately without an approval prompt

#### Scenario: MCP server default applies when no exact override exists

- **GIVEN** `ApprovalPolicy.McpServerDefaults.notion = Approval`
- **AND** no entry for `notion/create-pages` exists in `ToolOverrides`
- **WHEN** the agent invokes `notion/create-pages`
- **THEN** the resolved mode is `Approval`
- **AND** the call is approval-gated

#### Scenario: MCP server default does not leak to non-MCP tools

- **GIVEN** `ApprovalPolicy.McpServerDefaults.notion = Approval`
- **AND** `ApprovalPolicy.DefaultMode = Auto`
- **WHEN** the agent invokes `shell_execute`
- **THEN** the resolved mode is `Auto`
- **AND** the MCP server default is not consulted for names without a `/` segment

#### Scenario: `GetEffectiveMode` and `ResolveApprovalMode` agree on precedence

- **GIVEN** a `ToolApprovalConfig` with both `McpServerDefaults` and
  `ToolOverrides` entries
- **WHEN** the same tool name is resolved through
  `ToolApprovalConfig.GetEffectiveMode` and through
  `ToolAccessPolicy.ResolveApprovalMode` with a default matcher
- **THEN** both paths return the same `ToolApprovalMode`

## ADDED Requirements

### Requirement: MCP server approval default inheritance

The system SHALL apply a per-audience `McpServerDefaults[serverName]` entry
to all tools exposed by that MCP server unless an exact entry in
`ToolOverrides` takes precedence. New tools discovered on the server at
later daemon startups SHALL automatically inherit the server default
without requiring per-tool enumeration in the config. Absence of
`McpServerDefaults[serverName]` SHALL NOT fail-close the tool — the
approval-mode resolution SHALL fall through to the fail-closed-on-Personal
matcher decision and the audience `DefaultMode` (preserving
backward-compatible behavior for configs written before this change).

#### Scenario: New tool inherits server default without config change

- **GIVEN** `ApprovalPolicy.McpServerDefaults.notion = Approval`
- **AND** the Notion server is extended with a new tool `notion/archive`
  between two daemon starts
- **WHEN** the agent invokes `notion/archive` after the restart
- **THEN** the resolved mode is `Approval`
- **AND** no config edit was required to cover the new tool

#### Scenario: Absent server default falls through to legacy resolution

- **GIVEN** a config has no `McpServerDefaults` entry for `notion`
- **AND** no exact override exists for any `notion/*` tool
- **WHEN** the agent invokes `notion/create-pages`
- **THEN** resolution proceeds to matcher fail-closed-on-Personal and then
  to audience `DefaultMode`
- **AND** no silent denial is introduced for pre-existing configs

#### Scenario: Removing the server default reverts tools to legacy resolution

- **GIVEN** `ApprovalPolicy.McpServerDefaults.notion = Deny`
- **WHEN** the operator removes the entry and restarts the daemon
- **THEN** subsequent invocations of `notion/*` tools without exact overrides
  fall through to matcher fail-closed-on-Personal and audience `DefaultMode`
