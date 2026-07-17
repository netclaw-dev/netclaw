## Why

Stdio MCP child processes are only torn down by `McpClientManager.StopAsync`
(`src/Netclaw.Daemon/Mcp/McpClientManager.cs:73-90`), which is registered as an
`IHostedService` before `AddAkka`. .NET's LIFO hosted-service stop ordering
means `AkkaHostedService.StopAsync` runs first, and it awaits
`CoordinatedShutdown.Run()` to full completion while ignoring the host's
shutdown `CancellationToken` (`Akka.Hosting.AkkaHostedService.StopAsync`).
Session drain (`SessionDrainHelper.DrainAsync`, bounded to
`DaemonConfig.BoundedDrainTimeout` = 190s by #1673) therefore consumes nearly
all of the shutdown budget before `McpClientManager.StopAsync` gets to run at
all — and today's per-server teardown there is sequential (10s graceful + 10s
kill wait, per configured server), so with multiple servers it can still
overrun whatever budget remains. The CLI force-kill path
(`netclaw daemon stop`, #1673) SIGKILLs only the main daemon PID, so
`McpClientManager.StopAsync`/`Dispose` never runs at all on that path —
orphaning the MCP child tree in any non-systemd deployment (container
entrypoint, manual `netclawd`, dev shell). Production evidence: idle
Playwright MCP children observed still alive 90+ seconds into a drain,
reaped only by systemd's cgroup sweep after the main PID was force-killed.

#1673 fixed the two race conditions in the session-drain/CLI-kill budget
layering (#1664, #1665) but did not touch MCP teardown sequencing or ordering
— it is a distinct defect, tracked as #1667. The prior MCP ownership change
(#1636, `2026-07-14-make-stdio-mcp-process-bound`) explicitly preserved
shutdown behavior as out of scope; this change is the first to address it.

Source issue: netclaw-dev/netclaw#1667. Related: #1664, #1665, #1636.

## What Changes

- Trigger MCP client/child-process teardown from
  `IHostApplicationLifetime.ApplicationStopping` (fires before any
  `IHostedService.StopAsync`, including `AkkaHostedService`'s), so MCP
  teardown runs concurrently with session drain instead of strictly after it.
- Make `McpClientManager` teardown idempotent: the `ApplicationStopping`
  callback and the existing `StopAsync`/`Dispose` path converge on one
  teardown operation; whichever runs second observes already-disposed state
  and does not log spurious warnings or attempt to reconnect.
- Guard reconnect attempts (`ConnectAsync`/`TryReconnectAsync`) once teardown
  has started, so neither an in-flight tool call's retry-after-failure path
  nor `McpReconnectionService`'s periodic background poll can launch a new
  MCP child process during shutdown.
- Tear down configured servers in parallel instead of today's sequential
  `foreach`, so total MCP teardown time is bounded by the slowest single
  server's dispose instead of the sum across all configured servers.
- Preserve the existing dispose behavior per client (graceful wait, then
  process-tree kill) and the existing clean-tool-error path
  (`McpToolAdapter.ExecuteAsync` already converts any invoker exception into
  `"Error: MCP tool '{Name}' failed: {ex.Message}"`) for calls in flight when
  teardown begins.

Out of scope: lazy MCP startup, idle-process teardown, per-session MCP
lifecycle, CLI-side child-PID tracking or cleanup (the CLI force-kill path
still cannot reach children of an already-SIGKILLed main PID from outside the
process; see design.md for why this residual gap is not solved here), and any
change to the per-client SDK dispose timeout or process-tree kill mechanism.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-mcp`: the "daemon shutdown owns local child cleanup" behavior
  changes from "runs after the full actor-system shutdown completes" to
  "starts at `ApplicationStopping`, runs concurrently with session drain, is
  idempotent, and does not reconnect once started"; per-server teardown
  changes from sequential to parallel.

## Impact

- Code: `McpClientManager` gains an `ApplicationStopping` hook, an
  idempotency guard shared with `StopAsync`/`Dispose`, a stopping flag
  consulted by `ConnectAsync`/`TryReconnectAsync`, and parallel (rather than
  sequential) per-server disposal. `Program.cs`'s MCP service registration
  gains the lifecycle wiring; no hosted-service registration order changes
  are required (ordering was never the fix — the new trigger is).
- Tests: MCP teardown tests exercising real spawned child processes
  (`McpSmokeHarness`, extending `McpProcessBoundStdioTests.cs`) prove
  idempotent double-teardown, no reconnect after teardown starts, and
  parallel (not summed) multi-server teardown latency.
- Security: no ACL/policy change. An in-flight MCP tool call that loses its
  transport during shutdown fails loudly and immediately as a clean tool
  error, rather than hanging — this is a desired, documented behavior change,
  not a silent degradation.
- Operations: MCP children are reclaimed sooner during a graceful `daemon
  stop`/SIGTERM, reducing (but not eliminating, on non-systemd deployments)
  the window in which a force-kill of the main PID orphans MCP child
  processes.
- Configuration/schema: unchanged.
- Dependencies and public APIs: unchanged.
