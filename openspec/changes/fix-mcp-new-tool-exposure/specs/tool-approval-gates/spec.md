## ADDED Requirements

### Requirement: MCP tool exposure honors Deny and server default

The system SHALL remove an MCP tool from the exposed tool list when the tool
effective approval mode is `Deny` for the session audience. The model SHALL NOT
receive a tool that the policy will block. This exposure rule SHALL apply in
addition to the existing invocation block for `Deny` tools.

An MCP tool that has no per-tool override SHALL inherit the server default
approval posture through the existing precedence (`ToolOverrides` ->
`McpServerDefaults` -> `DefaultMode`). A newly discovered MCP tool that the
operator never named SHALL therefore be exposed under the server default. It
SHALL be auto-approved when the server default is `Auto`. It SHALL be
approval-gated when the server default is `Approval`.

This exposure rule SHALL apply to MCP tools only. Built-in tool exposure logic
SHALL NOT change.

#### Scenario: MCP tool in Deny mode is removed from the exposed list

- **GIVEN** an MCP tool whose effective approval mode is `Deny` for the session's audience
- **WHEN** the runtime builds the tool list for the model
- **THEN** the tool is absent from the exposed tool list
- **AND** the tool is absent from the discoverable tool list

#### Scenario: New MCP tool is exposed under an Approval server default

- **GIVEN** the session's audience `McpServersMode` is `All`
- **AND** the server default approval mode for `dropbox` is `Approval`
- **AND** the server adds a new tool `get_upload_url` that no override names
- **WHEN** the runtime builds the tool list for the model
- **THEN** `dropbox/get_upload_url` is present in the exposed tool list
- **AND** the effective approval mode for `dropbox/get_upload_url` is `Approval`

#### Scenario: New MCP tool is exposed under an Auto server default

- **GIVEN** the session's audience `McpServersMode` is `All`
- **AND** the server default approval mode for `dropbox` is `Auto`
- **AND** the server adds a new tool `get_upload_url` that no override names
- **WHEN** the agent invokes `dropbox/get_upload_url`
- **THEN** the tool executes immediately without an approval prompt
