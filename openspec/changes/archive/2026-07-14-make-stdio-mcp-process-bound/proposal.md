## Why

Local STDIO MCP servers are daemon-owned child processes, but Netclaw currently gives Playwright a second, session-scoped lifecycle that retains an unused discovery process and can launch one additional process per session. This multiplies heavyweight browser process trees and makes MCP process ownership depend on Slack thread identity instead of the configured server.

Source PRD: PRD-006.

## What Changes

- Remove Playwright-specific session-scoped MCP clients and process fan-out.
- Treat every configured MCP server as one daemon-owned client connection; a local STDIO profile therefore owns at most one child process per daemon.
- Keep the process and its state shared by every session authorized to invoke that server.
- Stop adding Playwright's `--isolated` argument implicitly; configured command arguments pass through unchanged.
- Preserve existing startup discovery, reconnect, diagnostics, authorization, and daemon-shutdown behavior.
- Document that MCP server state is daemon-scoped rather than a Netclaw session-isolation boundary.

In scope: MCP client ownership and invocation behavior for configured local STDIO servers, focused regression tests, and operator/agent guidance.

Out of scope: lazy startup, idle shutdown, per-session browser contexts, client pools, queues, new lifecycle configuration, remote transport changes, and changes to the Playwright MCP server.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-mcp`: Define one daemon-owned client per configured server and make local STDIO state shared across authorized Netclaw sessions.

## Impact

- Code: `McpClientManager` becomes smaller by deleting Playwright detection, scoped-client storage, scoped cleanup, and the alternate invocation path.
- Tests: focused MCP manager coverage proves calls from different session identities reuse one client/process path and configured arguments are not rewritten.
- Security: authorization remains enforced before MCP invocation, but an authorized MCP server's internal state is shared daemon-wide; sessions are not an isolation boundary for that state.
- Operations: one configured local STDIO server produces at most one root child process per daemon and is disposed during reconnect or daemon shutdown.
- Configuration/schema: unchanged.
- Dependencies and public APIs: unchanged.
