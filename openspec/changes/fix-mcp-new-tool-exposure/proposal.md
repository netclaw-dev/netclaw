## Why

A remote MCP server can add a new tool after the operator sets custom per-tool
rules. Today the new tool disappears from the agent (issue #1959). The operator
expects the new tool to inherit the server default posture: available under
`Approval`, or available and auto-approved under `Auto`. A new tool must never
become silently unavailable.

## What Changes

- In open (`All`) posture, `McpServerToolGrants` becomes an additive layer, not
  a closed allow-list. A tool that is absent from the grant list passes the
  audience check and inherits the server default approval posture.
- `Allowlist` posture (Team, Public) keeps the closed allow-list. An unseen tool
  stays hidden. Least-trust audiences remain fail-closed.
- A tool with effective approval mode `Deny` is removed from the tool list that
  the model sees. Today a `Deny` tool is shown and then blocked at invocation.
  **BREAKING** for the exposed-tool surface: a `Deny` MCP tool is now hidden.
- The MCP Permissions TUI and the `netclaw mcp` CLI express "disable one tool" in
  open posture as `Deny`, not as omission from the allow-list.
- The daemon drift warning fires only for `Allowlist` posture. Open posture has
  no drift, because unseen tools are exposed by default.

Scope: this change applies "Deny hides the tool" to MCP tools only. Built-in
tools keep their current exposure logic.

## Capabilities

### New Capabilities

<!-- None. This change modifies existing capability requirements. -->

### Modified Capabilities

- `netclaw-acl`: the per-tool `McpServerToolGrants` layer changes from an
  always-closed allow-list to a posture-aware layer. `All` posture treats the
  list as additive; `Allowlist` posture keeps it closed.
- `tool-approval-gates`: a tool with effective approval mode `Deny` is removed
  from the exposed tool list, in addition to the existing invocation block.

## Impact

- `src/Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs` —
  `IsMcpToolAllowed` becomes posture-aware.
- `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs` — `IsToolExposed` hides a
  `Deny` MCP tool.
- `src/Netclaw.Cli/Mcp/McpToolPermissionsViewModel.cs`,
  `McpToolPermissionsPage.cs`, `McpCommand.cs` — checkbox and CLI map "disable"
  to `Deny` in open posture.
- `src/Netclaw.Daemon/Mcp/McpClientManager.cs` — drift warning scoped to
  `Allowlist` posture.
- Config migration: a pre-existing `All`-posture `McpServerToolGrants` snapshot
  becomes inert. No config rewrite. The daemon logs a one-time notice.
- Security: Team and Public stay fail-closed. Personal gains correct new-tool
  exposure under the server default posture.
- Skill `netclaw-operations` gains updated MCP permissions guidance.
