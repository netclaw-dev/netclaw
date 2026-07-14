## Context

`McpClientManager` already owns one client per configured MCP server in `_clients`. Playwright additionally enters a second path selected by command/name heuristics: the manager retains the startup client for discovery, creates a `ScopedClientHandle` per `ToolExecutionContext.SessionId`, and scans those handles for idle cleanup only during later scoped invocations. This makes process count proportional to recent Netclaw sessions and embeds Playwright-specific behavior in the generic MCP manager.

MCP authorization is enforced before calls reach the invoker. Session actors and persistence do not own MCP processes and require no changes.

## Goals / Non-Goals

**Goals:**

- Make configured MCP server identity the sole MCP client/process ownership key.
- Reuse the existing shared client invocation and reconnect path for Playwright.
- Delete the alternate scoped-client lifecycle and Playwright command rewriting.
- Preserve clear invocation failures, diagnostics, and deterministic daemon shutdown.

**Non-Goals:**

- Lazy startup or idle process reclamation.
- Per-session browser contexts or state isolation.
- New lifecycle configuration, pools, queues, or background maintenance.
- Changes to actor boundaries, persisted state, grants, or remote transports.

## Decisions

### One client per configured server

`_clients[McpServerName]` remains the sole live-client collection. `InvokeAsync` always uses the existing shared invocation path. This matches the configured-resource model used by other MCP harnesses and bounds a local STDIO profile to one root child process per daemon.

Alternative: retain session-scoped clients but cap them. Rejected because it preserves two lifecycle models, ownership state, cleanup scans, and Playwright-specific classification.

### Share server-internal state across authorized sessions

Netclaw session identity will not select or partition MCP clients. Authorization remains the access boundary; state held inside an MCP server is daemon-scoped. For Playwright, authorized callers may observe or affect the same browser context.

Alternative: multiplex Playwright contexts through per-session HTTP connections. Rejected because the STDIO tool surface exposes no context-selection primitive and per-session connection management recreates the lifecycle machinery being removed.

### Pass configured STDIO arguments unchanged

The manager will not recognize Playwright or append `--isolated`. Operators and canonical browser configuration own server arguments. This removes hidden product-specific behavior and makes the launched process match persisted configuration.

### Preserve startup and shutdown behavior

This change does not add lazy creation or idle teardown. Enabled servers still connect and discover tools at daemon startup, reconnect through the existing failure path, and dispose on daemon shutdown. Those behaviors provide a smaller, independently reviewable baseline; on-demand residency can be considered separately if process evidence still justifies it.

## Risks / Trade-offs

- **Authorized sessions share browser state** → Document that MCP state is daemon-scoped and keep existing audience/server grants as the access boundary.
- **Concurrent calls may contend inside a stateful server** → Preserve the existing shared invocation behavior; add synchronization only if a reproducible server/client failure proves it necessary.
- **Removing implicit `--isolated` changes profile persistence** → Pass the canonical configured arguments exactly and test that contract; operators can explicitly configure `--isolated` when desired.
- **Startup residency remains** → Accept for this focused correction; the immediate unbounded multiplier is removed without adding a maintenance loop.

## Migration Plan

No configuration or persisted-state migration is required. Deploying the change collapses Playwright from a retained discovery client plus per-session clients to the single configured client. Rollback restores the former process model without data migration.

## Open Questions

None for this change.
