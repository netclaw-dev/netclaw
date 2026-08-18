## MODIFIED Requirements

### Requirement: Tool and data grants

The system SHALL enforce explicit grants for tool and data access. Grants SHALL
be organized into specific tool grant categories: `shell`, `web_search`,
`web_fetch`, `github`, `mcp:{server_name}`, `config_write`, and
`schedule_write`. Each grant SHALL specify the allowed senders and channels to
which it applies.

For MCP tools, the system SHALL support an additional per-tool grant layer via
`McpServerToolGrants` on each audience profile. This per-tool check SHALL
execute after the server-level `AllowedMcpServers` check. The per-tool check
SHALL be posture-aware and follow the audience `McpServersMode`.

- When `McpServersMode` is `Allowlist`, the grant list SHALL be closed. Only a
  tool that the list names SHALL pass the audience check. A tool that the list
  does not name SHALL be denied. This keeps least-trust audiences fail-closed.
- When `McpServersMode` is `All`, the grant list SHALL be additive. A tool that
  the list names SHALL pass. A tool that the list does not name SHALL also pass
  and SHALL inherit the server default approval posture.

Each `ToolAudienceProfile` SHALL support an optional `ApprovalPolicy` of type
`ToolApprovalConfig`. The `ApprovalPolicy` SHALL define a `DefaultMode` (Auto,
Approval, Deny) and per-tool overrides via `ToolOverrides`. The approval check
SHALL execute after the tool access grant check passes. Tools in Approval mode
SHALL surface approval context for the executor, and the executor SHALL consult
`IToolApprovalService` before execution. Tools in Deny mode SHALL be blocked
without an approval prompt.

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

#### Scenario: MCP tool blocked by per-tool grant in allowlist posture

- **GIVEN** the session's audience `McpServersMode` is `Allowlist`
- **AND** the audience allows `memorizer` server via `AllowedMcpServers`
- **AND** `McpServerToolGrants` for this audience lists `["search_memories", "get"]`
- **WHEN** the agent invokes `memorizer/store`
- **THEN** the invocation is denied with reason `mcp_tool_not_allowed_for_audience_profile`

#### Scenario: MCP tool allowed by per-tool grant in allowlist posture

- **GIVEN** the session's audience `McpServersMode` is `Allowlist`
- **AND** the audience allows `memorizer` server via `AllowedMcpServers`
- **AND** `McpServerToolGrants` for this audience lists `["search_memories", "get"]`
- **WHEN** the agent invokes `memorizer/search_memories`
- **THEN** the invocation is allowed

#### Scenario: New MCP tool passes the per-tool check in open posture

- **GIVEN** the session's audience `McpServersMode` is `All`
- **AND** `McpServerToolGrants` for this audience lists `["search_memories", "get"]`
- **AND** the server adds a new tool `store` that the list does not name
- **WHEN** the agent invokes `memorizer/store`
- **THEN** the per-tool check passes
- **AND** the invocation inherits the server default approval posture

#### Scenario: New MCP tool stays fail-closed in allowlist posture

- **GIVEN** the session's audience `McpServersMode` is `Allowlist`
- **AND** `McpServerToolGrants` for this audience lists `["search_memories", "get"]`
- **AND** the server adds a new tool `store` that the list does not name
- **WHEN** the agent invokes `memorizer/store`
- **THEN** the invocation is denied with reason `mcp_tool_not_allowed_for_audience_profile`

#### Scenario: Tool granted but requires approval

- **GIVEN** the session has a grant for `shell_execute`
- **AND** the Personal `ApprovalPolicy` sets `shell_execute` to Approval mode
- **AND** the command pattern `git push` is not already approved in `IToolApprovalService`
- **WHEN** the agent invokes `shell_execute` with `git push origin main`
- **THEN** the grant check passes
- **AND** the approval check returns `RequiresApproval`
