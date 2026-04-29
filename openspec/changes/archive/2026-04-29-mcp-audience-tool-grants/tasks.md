## 1. Config Model

- [x] 1.1 Add `McpServerToolGrants` property (`Dictionary<string, List<string>>?`) to `ToolAudienceProfile` in `src/Netclaw.Configuration/ToolAudienceProfiles.cs`
- [x] 1.2 Add `McpServerToolGrants` to `ToolAudienceProfile` definition in `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`

## 2. Enforcement

- [x] 2.1 Add `IsMcpToolAllowed(string serverName, string toolName, TrustAudience audience)` method to `ToolAudienceProfileResolver` in `src/Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs`
- [x] 2.2 Update `IsToolExposed` in `ToolAccessPolicy` to call `IsMcpToolAllowed` after `IsMcpServerAllowed` for `McpToolAdapter` tools
- [x] 2.3 Update `AuthorizeInvocation` in `ToolAccessPolicy` to deny with `mcp_tool_not_allowed_for_audience_profile` when per-tool grant check fails

## 3. Tool Change Detection

- [x] 3.1 Add tool drift logging in `McpClientManager.ConnectAsync` — compare discovered tools against grants from all audience profiles, warn on ungranted tools and stale grants

## 4. Doctor Advisory

- [x] 4.1 Add info-level advisory in `ToolAudienceProfilesDoctorCheck` for enabled servers with no `McpServerToolGrants` on any audience profile

## 5. Tests

- [x] 5.1 Unit tests for `IsMcpToolAllowed`: null grants = all pass, empty list = none pass, populated list = only matches pass, server not in grants = all pass
- [x] 5.2 Unit tests for `IsToolExposed` integration: server allowed + tool granted = exposed, server allowed + tool not granted = blocked, server blocked = blocked regardless of grants
- [x] 5.3 Unit tests for `AuthorizeInvocation`: verify correct deny reason code `mcp_tool_not_allowed_for_audience_profile`
- [x] 5.4 Test `search_tools` does not return tools blocked by `McpServerToolGrants`
- [x] 5.5 Doctor check test: advisory fires for servers with no grants, does not fire when grants exist

## 6. CLI: `netclaw mcp tools`

- [x] 6.1 Add `tools` subcommand routing in `McpCommand.RunAsync` and help text
- [x] 6.2 Implement CLI list mode (`netclaw mcp tools <server>`) — query daemon for discovered tools, display per-audience grant status table
- [x] 6.3 Implement `--snapshot` flag — populate `McpServerToolGrants` from discovered tools and write to `netclaw.json`

## 7. TUI: Interactive Tool Permissions

- [x] 7.1 Create `McpToolPermissionsViewModel` with server list, tool list, audience selector, and toggle state
- [x] 7.2 Create `McpToolPermissionsPage` with Termina layout: server list → tool grid with checkboxes → save
- [x] 7.3 Wire TUI mode in `McpCommand` (no server arg) and `Program.cs` routing

## 8. Spec Sync

- [x] 8.1 Sync delta specs to main specs via `/opsx-sync` after implementation is verified
