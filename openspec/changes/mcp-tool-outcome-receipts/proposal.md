## Why

Use the [Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms: tool receipt, outcome category, transport or session failure, application error, tool-declared error, and OAuth-managed server.

PRD-006 (MCP-005, MCP-008) requires that runtime diagnostics show recent MCP invocation failures and that failures degrade gracefully. Today an MCP tool call that ends in an exception becomes a tool result with no tool receipt. The dispatcher records it as a success, and the daemon log holds no failure line at the default level. The same path treats every HTTP error as a transport fault and reconnects. A server-side business error can also move a static-header server into `AuthFailed`. Epic #2058 tracks the three defects (#2055, #2056, #2057).

## What Changes

- Give an MCP tool-call exception a non-success outcome category in its tool receipt. The tool result text keeps its current shape.
- Log each MCP tool-call exception once at Warning. The line names the server, the tool, and the HTTP status when present.
- Classify an HTTP response that carries an application-level status code as an application error. Netclaw returns it without a reconnect. Only a missing status or a 404 session expiry counts as a transport or session failure.
- Move a server to `AuthFailed` on an HTTP 401 from a tool call, a catalog refresh, or `initialize`. Decide the remedy from the daemon's OAuth state, never from header names: `netclaw mcp auth` when the daemon holds OAuth tokens or the SDK reported a challenge, a credentials check otherwise. Demote from tool-declared error text only when the daemon holds OAuth tokens.
- Apply the same application-error classification to MCP prompt loads, so an HTTP error returns a failed load result instead of an exception.
- Remove the duplicate exception-to-string conversion in the client manager. The adapter owns that conversion.
- Add four MCP invocation terms to the engineering glossary: transport or session failure, application error, tool-declared error, and OAuth-managed server. Both modified capabilities use them.

In scope for MVP: the three defects above and their regression tests.

Out of scope: per-server rate-limit budgets, `Retry-After` propagation, approval gates for MCP tools that mutate state, changes to the MCP SDK discover probe, and the receipt category of a tool-declared `isError` result.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-tools`: Add machine-actionable outcomes for MCP tool calls that end in an exception.
- `netclaw-mcp`: Narrow reconnect to transport and session failures, require a Warning log for each failed invocation, and decide the auth remedy from the daemon's OAuth state on every path, never from header names.

## Impact

Code: `McpToolAdapter` (receipt completion), `McpClientManager` (failure classification, one Warning log, auth remedy from the OAuth token state), `McpServersDoctorCheck` (remediation text), and their tests. No public API, configuration property, durable record, or approval authority changes. The receipt stays call-local.

Security: a tool receipt grants no authority. The token-state rule removes a false `AuthFailed` state that pointed operators at `netclaw mcp auth` for a server with no OAuth, and it makes a rejected static credential visible in `netclaw mcp list` and `doctor`.

Operations: operators gain one Warning line per failed MCP call. Servers with request budgets receive fewer reconnect requests after an application error.
