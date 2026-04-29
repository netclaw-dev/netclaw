## Why

MCP server access is gated at the server level — if a server is allowed for an audience, all its tools are exposed. This creates a supply-chain risk (servers can add tools without operator review) and prevents per-audience tool scoping (no way to give Team `search+get` while Personal gets everything). Relates to PRD-002 (security posture) and GitHub issue #490.

## What Changes

- Add `McpServerToolGrants` dictionary to `ToolAudienceProfile` — per-server tool allowlists that vary by audience
- Enforce per-tool filtering in `ToolAccessPolicy` and `ToolAudienceProfileResolver` after the existing server-level gate
- Log warnings when MCP servers expose tools not granted to any audience (supply-chain detection)
- Add `netclaw mcp tools` CLI subcommand for viewing and managing per-server tool grants
- Add TUI mode for interactive tool permission configuration using Termina
- Add doctor advisory for servers with no tool grants configured on any audience

Default behavior is unchanged: Personal audience sees all tools from all servers. Tool grants are opt-in tightening — operators add them when they want to restrict exposure per audience.

## Capabilities

### New Capabilities

_None_ — this extends existing MCP and ACL capabilities rather than introducing a new spec domain.

### Modified Capabilities

- `netclaw-mcp`: Add per-tool audience filtering to tool discovery and registration. Tools not granted to the session's audience are excluded from `search_tools` results and `load_tool` availability.
- `netclaw-acl`: Add `McpServerToolGrants` as a second-stage filter after `AllowedMcpServers`. When a server has an entry in the grants dictionary, only listed tools pass the audience check.
- `netclaw-cli`: Add `netclaw mcp tools` subcommand (CLI list mode + TUI interactive mode + `--snapshot` for baselining).

## Impact

- **Config**: New `McpServerToolGrants` property on `ToolAudienceProfile` in `netclaw.json`. Nullable, backward-compatible.
- **Schema**: `netclaw-config.v1.schema.json` updated with new property in `ToolAudienceProfile` definition.
- **Enforcement**: `ToolAccessPolicy.IsToolExposed` and `AuthorizeInvocation` gain per-tool check for `McpToolAdapter` path.
- **Resolution**: `ToolAudienceProfileResolver` gains `IsMcpToolAllowed` method.
- **Diagnostics**: `McpClientManager` logs tool drift warnings. `McpServersDoctorCheck` adds advisory for ungated servers.
- **CLI**: `McpCommand` gains `tools` subcommand with CLI and TUI modes.
- **No breaking changes** — existing configs without `McpServerToolGrants` behave identically to today.
