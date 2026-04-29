## Context

MCP tools are currently gated at the server level: `ToolAudienceProfile.AllowedMcpServers` lists server names, and if a server is allowed, all its tools pass through. `ToolAccessPolicy.IsToolExposed` delegates to `ToolAudienceProfileResolver.IsMcpServerAllowed` which checks server name only. There is no per-tool check.

Tools are registered at startup by `McpClientManager.ConnectAsync` → `ToolRegistrationExtensions.WithMcpTools`, which wraps each discovered tool as an `McpToolAdapter` with name format `{serverName}/{toolName}` and grant category `mcp:{serverName}`.

The enforcement path for MCP tools:
1. `IsToolExposed(INetclawTool, TrustAudience)` → checks `IsMcpServerAllowed(serverName, audience)`
2. `AuthorizeInvocation(INetclawTool, ToolExecutionContext)` → same server-level check
3. `FilterDiscoverableTools` / `FilterExposedTools` → calls `IsToolExposed` per tool

The `McpToolAdapter` already exposes `ServerName` and the bare tool name, so per-tool filtering can be added without changing the adapter.

## Goals / Non-Goals

**Goals:**
- Per-audience per-server tool allowlists via `McpServerToolGrants` on `ToolAudienceProfile`
- Backward-compatible: null/omitted grants = all tools exposed (current behavior)
- Supply-chain visibility: log warnings when servers expose tools not granted to any audience
- CLI + TUI for viewing and managing tool grants (`netclaw mcp tools`)
- Doctor advisory for servers with no tool grants

**Non-Goals:**
- Server-level `AllowedTools` on `McpServerEntry` — single enforcement layer at the audience profile is sufficient
- Webhook/notification alerts for tool changes (future Phase 3)
- Per-tool filtering for first-party tools (already handled by `IsProfileManagedTool`)
- Hot-reload of tool grants (config changes already require daemon restart)

## Decisions

### 1. Single enforcement layer at audience profile, not server entry

**Decision**: `McpServerToolGrants` lives on `ToolAudienceProfile`, not `McpServerEntry`.

**Rationale**: If every audience has scoped tool grants, there's no unreviewed path for a tool to reach a session. A second layer on `McpServerEntry` would be redundant config surface. Operators who want a global cap set the same grants on all three profiles.

**Alternative considered**: Two layers (server-level global cap + audience-level scoping). Rejected — adds config complexity for no additional security if audience profiles are properly configured.

### 2. Enforce at access-policy time, not registration time

**Decision**: All discovered tools are still registered in `ToolRegistry` via `WithMcpTools`. Filtering happens in `ToolAccessPolicy.IsToolExposed` and `AuthorizeInvocation`.

**Rationale**: Registration is a global, audience-independent operation — the daemon registers tools once for all sessions. Per-audience filtering must happen per-session at access time, which is where `ToolAccessPolicy` already operates. This also means `search_tools` correctly filters by the requesting session's audience.

**Alternative considered**: Filter at registration time in `WithMcpTools`. Rejected — registration is not audience-aware; would need to register tools multiple times or maintain parallel registries.

### 3. `McpServerToolGrants` is a nullable dictionary, not a mode enum

**Decision**: `Dictionary<string, List<string>>?` where keys are server names and values are tool name lists. Null means no per-tool filtering.

**Rationale**: This composes naturally with `AllowedMcpServers`. A server must pass the server gate first, then the tool grant check. Servers not in the dictionary expose all tools — backward-compatible and ergonomic for operators who only want to restrict one server.

### 4. Tool change detection via startup logging, not persistent state

**Decision**: `McpClientManager` compares discovered tools against grants from all audience profiles at connect time and logs warnings. No persistent "last known tools" storage.

**Rationale**: Config is the source of truth. If an operator has configured grants, any tool not in any audience's grants is a signal. This requires no new persistence, no new actor state, and works on first startup.

### 5. TUI fits in `netclaw mcp tools`, not a standalone command

**Decision**: Add `tools` as a subcommand of the existing `netclaw mcp` family.

**Rationale**: The feature is MCP-specific and the MCP command family already handles server lifecycle (`add`, `remove`, `auth`, `list`, `enable`, `disable`). Tool permissions are a natural extension.

## Risks / Trade-offs

**Risk: Stale grants** — If an MCP server renames a tool, the old name in grants silently stops matching.
→ *Mitigation*: Log warning for grant entries not found on the server. `netclaw mcp tools --snapshot` lets operators re-baseline.

**Risk: Empty grants confusion** — Operator sets `McpServerToolGrants: { "memorizer": [] }` and wonders why no tools work.
→ *Mitigation*: Doctor advisory. Log message at startup: "MCP server 'memorizer' has empty tool grants for [audience] — no tools will be exposed."

**Risk: Case sensitivity** — MCP tool names are case-sensitive per protocol. Grants must match exactly.
→ *Mitigation*: Use `StringComparer.Ordinal`. Document in CLI help. The `--snapshot` command captures exact names.

**Trade-off: No hot-reload** — Changing grants requires daemon restart. Acceptable because MCP server config changes already require restart. Future `netclaw-config-hot-reload` spec could address this.
