## ADDED Requirements

### Requirement: Secure defaults for new MCP servers

The `netclaw mcp add` command SHALL configure new MCP servers with
fail-closed defaults across all audience profiles. After writing the
`McpServers[name]` entry, the command SHALL write an explicit empty
`McpServerToolGrants[name] = []` entry to every audience profile under
`Tools.AudienceProfiles` (Public, Team, Personal). This empty list SHALL
cause the existing per-tool grant check at `ToolAudienceProfileResolver`
to deny every tool from the server until the operator explicitly grants
specific tools.

The command SHALL also write `ApprovalPolicy.McpServerDefaults[name]`
entries per audience with the following defaults:

- Personal: `Approval` (operator prompts themselves)
- Team: `Approval` (trusted members can approve in team context)
- Public: `Deny` (public users cannot grant trust)

The `ApprovalPolicy` section SHALL be created if missing, reusing the
existing `ConfigFileHelper.GetOrCreateSection` pattern. The command SHALL
print a post-add hint identifying that zero tools are currently granted
and directing the operator to `netclaw mcp permissions` to review.

The command SHALL accept a `--grant-all` flag as a CI/automation escape
hatch. When passed, the command SHALL skip writing `McpServerToolGrants`
entries (leaving `McpServerToolGrants[name]` absent, which preserves the
legacy "null grants = all pass" behavior for the new server). The
`McpServerDefaults` writes SHALL still occur even with `--grant-all` so
that new servers always have an explicit approval default, regardless of
grant configuration.

The command SHALL NOT retroactively modify existing `McpServers` entries
or their corresponding audience-profile state. Only the newly added
server's name SHALL be touched.

#### Scenario: Fail-closed writes for new server

- **GIVEN** a fresh `netclaw.json` with default audience profiles
- **WHEN** operator runs `netclaw mcp add --transport http notion https://mcp.notion.com/mcp`
- **THEN** `Tools.AudienceProfiles.Public.McpServerToolGrants.notion` is `[]`
- **AND** `Tools.AudienceProfiles.Team.McpServerToolGrants.notion` is `[]`
- **AND** `Tools.AudienceProfiles.Personal.McpServerToolGrants.notion` is `[]`
- **AND** `Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults.notion` is `Approval`
- **AND** `Tools.AudienceProfiles.Team.ApprovalPolicy.McpServerDefaults.notion` is `Approval`
- **AND** `Tools.AudienceProfiles.Public.ApprovalPolicy.McpServerDefaults.notion` is `Deny`
- **AND** the CLI output includes a hint pointing to `netclaw mcp permissions`

#### Scenario: `--grant-all` preserves approval defaults but skips grants

- **GIVEN** a fresh `netclaw.json` with default audience profiles
- **WHEN** operator runs `netclaw mcp add --grant-all --transport stdio trusted-server -- /usr/local/bin/trusted`
- **THEN** no `McpServerToolGrants.trusted-server` entry is written
- **AND** `Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults.trusted-server` is `Approval`
- **AND** `Tools.AudienceProfiles.Public.ApprovalPolicy.McpServerDefaults.trusted-server` is `Deny`

#### Scenario: Pre-existing servers are not mutated

- **GIVEN** a `netclaw.json` that already contains an MCP server `old-server`
  with `McpServerToolGrants = null` and no `McpServerDefaults` entry
- **WHEN** operator runs `netclaw mcp add --transport http new-server https://new.example/mcp`
- **THEN** no `McpServerToolGrants` or `McpServerDefaults` entries are
  written for `old-server`
- **AND** only `new-server` receives the fail-closed defaults

#### Scenario: Missing `ApprovalPolicy` section is created

- **GIVEN** a `netclaw.json` whose audience profiles do not yet include
  `ApprovalPolicy` sections
- **WHEN** operator runs `netclaw mcp add new-server …`
- **THEN** each affected audience profile receives an `ApprovalPolicy`
  section with the appropriate `McpServerDefaults[new-server]` entry
- **AND** no existing keys in `ApprovalPolicy` (if present) are removed

#### Scenario: Personal `McpServersMode = All` does not bypass empty grants

- **GIVEN** Personal profile has `McpServersMode = All`
- **WHEN** operator runs `netclaw mcp add notion …` (without `--grant-all`)
- **AND** the daemon connects to the new Notion server after restart
- **THEN** Personal-audience tool resolution blocks every Notion tool with
  `mcp_tool_not_allowed_for_audience_profile` until explicit grants are added
- **AND** the server-level check still passes, but the empty grant list
  short-circuits the per-tool check
