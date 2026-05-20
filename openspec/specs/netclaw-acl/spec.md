# netclaw-acl Specification

## Purpose

Define the access-control model for channels, audiences, tool grants, and
approval policy enforcement across Netclaw.

## Requirements

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
- **AND** the command pattern `git push` is not already approved in `IToolApprovalService`
- **WHEN** the agent invokes `shell_execute` with `git push origin main`
- **THEN** the grant check passes
- **AND** the approval check returns `RequiresApproval`

#### Scenario: Tool granted with approval already cached

- **GIVEN** the session has a grant for `shell_execute`
- **AND** `git push` is already approved through `IToolApprovalService`
- **WHEN** the agent invokes `shell_execute` with `git push origin main`
- **THEN** both the grant check and approval check pass
- **AND** the tool executes immediately

#### Scenario: Config write grant required for self-configuration

- **GIVEN** ACL does not grant `config_write` for the current sender
- **WHEN** the agent attempts to write configuration files through conversation
- **THEN** the write is denied with a policy reason code

### Requirement: Default audience tool-profile grants are monotonic

The built-in default audience profiles SHALL grant profile-managed tools
monotonically across the trust ladder: every profile-managed tool granted to
`Public` SHALL also be granted to `Team`, and every profile-managed tool
granted to `Team` SHALL also be granted to `Personal`.

The default `Public` profile SHALL set `AllowedTools` to exactly
`[file_read, file_list, attach_file]` and SHALL grant no file-mutation,
outbound web, scheduling, skill, webhook, MCP, or shell tools.

The default `Team` profile SHALL set `AllowedTools` to exactly
`[file_read, file_list, file_write, file_edit, attach_file, web_search,
web_fetch, skill_manage, set_reminder, list_reminders, cancel_reminder,
get_reminder_history, set_working_directory]`, covering every
profile-managed tool except `shell_execute` and the webhook tools. The default
`Team` profile SHALL NOT enable any MCP server (`McpServersMode = Allowlist`
with an empty `AllowedMcpServers`).

The default `Personal` profile SHALL retain `ToolsMode = All`.

#### Scenario: Public default excludes file mutation and outbound web tools

- **WHEN** the default `Public` audience profile is resolved
- **THEN** `AllowedTools` contains `file_read`, `file_list`, and `attach_file`
- **AND** `AllowedTools` does not contain `file_write`, `file_edit`,
  `web_search`, or `web_fetch`

#### Scenario: Team default grants file, web, and scheduling tools but not shell or webhooks

- **WHEN** the default `Team` audience profile is resolved
- **THEN** `AllowedTools` contains `file_write`, `file_edit`, `file_list`,
  `web_search`, `web_fetch`, `skill_manage`, and the four reminder tools
- **AND** `AllowedTools` does not contain `shell_execute`, `set_webhook`,
  `list_webhooks`, or `delete_webhook`

#### Scenario: Default grants are monotonic across audiences

- **GIVEN** the default `Public`, `Team`, and `Personal` profiles
- **WHEN** their effective profile-managed tool grants are compared
- **THEN** every tool granted to `Public` is also granted to `Team`
- **AND** every tool granted to `Team` is also granted to `Personal`

### Requirement: File-editing tools are audience-gated

`file_edit` SHALL be a profile-managed tool, gated by the audience profile
`AllowedTools` allowlist exactly as `file_write` is. A profile-managed file
tool absent from the resolved audience's `AllowedTools` SHALL be hidden from
the tool list exposed to the model and SHALL be denied at invocation with
reason `tool_not_allowed_for_audience_profile`.

#### Scenario: file_edit denied for the Public audience by default

- **GIVEN** a session resolved to the default `Public` audience profile
- **WHEN** the agent invokes `file_edit`
- **THEN** the invocation is denied with reason
  `tool_not_allowed_for_audience_profile`

#### Scenario: file_edit allowed for the Team audience by default

- **GIVEN** a session resolved to the default `Team` audience profile
- **WHEN** the agent invokes `file_edit` on a path within the session directory
- **THEN** the audience allowlist check passes

### Requirement: Outbound web tools are audience-gated

`web_search` and `web_fetch` SHALL be profile-managed tools, gated by the
audience profile `AllowedTools` allowlist. They SHALL be granted to the
default `Team` and `Personal` profiles and SHALL NOT be granted to the
default `Public` profile, so a Public-audience session cannot make outbound
web requests. This is independent of the deployment-wide search feature flag,
which continues to disable the tools for every audience when off.

#### Scenario: web tools denied for the Public audience by default

- **GIVEN** a session resolved to the default `Public` audience profile
- **WHEN** the agent invokes `web_search` or `web_fetch`
- **THEN** the invocation is denied with reason
  `tool_not_allowed_for_audience_profile`

#### Scenario: web tools allowed for the Team audience by default

- **GIVEN** a session resolved to the default `Team` audience profile
- **WHEN** the agent invokes `web_search` or `web_fetch`
- **THEN** the audience allowlist check passes
