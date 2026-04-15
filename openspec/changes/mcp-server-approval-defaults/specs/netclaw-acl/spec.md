## MODIFIED Requirements

### Requirement: Tool and data grants

The system SHALL enforce explicit grants for tool and data access. Grants SHALL
be organized into specific tool grant categories: `shell`, `web_search`,
`web_fetch`, `github`, `mcp:{server_name}`, `config_write`, and
`schedule_write`. Each grant SHALL specify the allowed senders and channels to
which it applies.

For MCP tools, the system SHALL support an additional per-tool grant layer via
`McpServerToolGrants` on each audience profile. When configured for a server,
only explicitly listed tools SHALL pass the audience check. This per-tool
check SHALL execute after the server-level `AllowedMcpServers` check.

Each `ToolAudienceProfile` SHALL support an optional `ApprovalPolicy` of type
`ToolApprovalConfig`. The `ApprovalPolicy` SHALL define a `DefaultMode` (Auto,
Approval, Deny), per-tool overrides via `ToolOverrides`, and per-MCP-server
defaults via `McpServerDefaults`. The approval check SHALL execute after the
tool access grant check passes and SHALL resolve the effective mode via the
precedence defined in `tool-approval-gates` (exact override → MCP server
default → matcher fail-closed on Personal → `DefaultMode`). Tools in Approval
mode SHALL consult the approval cache (session-scoped and persistent) before
execution. Tools in Deny mode SHALL be blocked without an approval prompt.

#### Scenario: Missing grant blocks tool call

- **WHEN** a tool call is attempted without a matching grant
- **THEN** execution is denied with a policy reason code

#### Scenario: Category-specific grant allows tool

- **GIVEN** ACL grants `web_search` for sender `U12345` on channel `C99999`
- **WHEN** sender `U12345` requests a web search in channel `C99999`
- **THEN** ACL evaluation returns allow for the `web_search` tool category

#### Scenario: MCP server-scoped grant

- **GIVEN** ACL grants `mcp:memorizer` for sender `U12345`
- **WHEN** sender `U12345` requests an MCP tool from the `memorizer` server
- **THEN** ACL evaluation returns allow
- **AND** MCP tools from other servers without explicit grants are denied

#### Scenario: MCP tool blocked by per-tool grant

- **GIVEN** the session's audience allows `memorizer` server via `AllowedMcpServers`
- **AND** `McpServerToolGrants` for this audience lists `["search_memories", "get"]`
- **WHEN** the agent invokes `memorizer/store`
- **THEN** the invocation is denied with reason `mcp_tool_not_allowed_for_audience_profile`

#### Scenario: MCP tool allowed by per-tool grant

- **GIVEN** the session's audience allows `memorizer` server via `AllowedMcpServers`
- **AND** `McpServerToolGrants` for this audience lists `["search_memories", "get"]`
- **WHEN** the agent invokes `memorizer/search_memories`
- **THEN** the invocation is allowed

#### Scenario: Tool granted but requires approval

- **GIVEN** the session has a grant for `shell_execute`
- **AND** the Personal `ApprovalPolicy` sets `shell_execute` to Approval mode
- **AND** the command pattern `git push` is not in the approval cache
- **WHEN** the agent invokes `shell_execute` with `git push origin main`
- **THEN** the grant check passes
- **AND** the approval check returns `RequiresApproval`

#### Scenario: Tool granted with approval already cached

- **GIVEN** the session has a grant for `shell_execute`
- **AND** `git push` is in the session or persistent approval cache
- **WHEN** the agent invokes `shell_execute` with `git push origin main`
- **THEN** both the grant check and approval check pass
- **AND** the tool executes immediately

#### Scenario: MCP tool approval mode inherited from server default

- **GIVEN** Personal profile allows `notion` via `AllowedMcpServers` (or `McpServersMode = All`)
- **AND** `Tools.AudienceProfiles.Personal.McpServerToolGrants.notion = ["create-pages"]`
- **AND** `Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults.notion = Approval`
- **AND** no entry for `notion/create-pages` exists in `ToolOverrides`
- **WHEN** the agent invokes `notion/create-pages`
- **THEN** the grant check passes
- **AND** the approval check resolves the effective mode to `Approval`
- **AND** the call is approval-gated

#### Scenario: MCP tool exact override beats server default at the grant gate

- **GIVEN** `Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults.notion = Deny`
- **AND** `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides["notion/search"] = Auto`
- **AND** `Tools.AudienceProfiles.Personal.McpServerToolGrants.notion = ["search"]`
- **WHEN** the agent invokes `notion/search`
- **THEN** the grant check passes
- **AND** the approval check resolves the effective mode to `Auto`
- **AND** the call executes immediately

#### Scenario: Config write grant required for self-configuration

- **GIVEN** ACL does not grant `config_write` for the current sender
- **WHEN** the agent attempts to write configuration files through conversation
- **THEN** the write is denied with a policy reason code
