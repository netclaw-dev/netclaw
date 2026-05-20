## ADDED Requirements

### Requirement: Default audience tool-profile grants are monotonic

The built-in default audience profiles SHALL grant profile-managed tools
monotonically across the trust ladder: every profile-managed tool granted to
`Public` SHALL also be granted to `Team`, and every profile-managed tool
granted to `Team` SHALL also be granted to `Personal`.

The default `Public` profile SHALL set `AllowedTools` to exactly
`[file_read, file_list, attach_file]` — it SHALL grant no file-mutation,
outbound web, scheduling, skill, webhook, MCP, or shell tools.

The default `Team` profile SHALL set `AllowedTools` to exactly
`[file_read, file_list, file_write, file_edit, attach_file, web_search,
web_fetch, skill_manage, set_reminder, list_reminders, cancel_reminder,
get_reminder_history, set_working_directory]` — every profile-managed tool
except `shell_execute` and the webhook tools. The default `Team` profile SHALL
NOT enable any MCP server (`McpServersMode = Allowlist` with an empty
`AllowedMcpServers`).

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
